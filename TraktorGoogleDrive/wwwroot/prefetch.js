// Whole-file cache for local playback: the current track and the next one are
// downloaded in full, so a patchy connection (a train) does not break up the
// audio. Once a file is complete the player plays it from memory, at the same
// position, and needs no network until the track after it.
//
// Downloads go straight to googleapis.com with the token as a header, like the
// waveform's: no Cloud Run egress. Only if that fails in a way the network
// does not explain (CORS, a blocker) does a download fall back to the proxy.
//
// A dropped connection resumes from the last byte received (Range), after an
// exponential backoff or as soon as the browser says it is back online. A
// refused token waits for the renewed one: auth.js renews before expiry, and
// the player's own 401 path asks the user when it has to. A download never
// opens a sign-in popup of its own.
//
// Bounded: one file over CAP_BYTES is never cached (a multi-hour WAV set); it
// streams through the proxy, where player.js reconnects after drops. Finished
// files beyond the plan stay for a quick Previous, least recently used first
// out, within BUDGET_BYTES.

// A one-hour 320 kbps set (~145 MB) fits; a two-hour one (~300 MB) streams.
export const CAP_BYTES = 256 * 1024 * 1024;
export const BUDGET_BYTES = 512 * 1024 * 1024;
// Received bytes are folded into a Blob this often, so the JS heap never holds
// more than this of a download: Chrome keeps Blob data out of the heap, and
// spills large blobs to disk.
const FOLD_BYTES = 4 * 1024 * 1024;
const MAX_BACKOFF_MS = 30_000;
const TOKEN_POLL_MS = 1_000;
// Turns the cache off ('off'). For tests that exercise pure streaming, and a
// manual escape hatch.
const DISABLED_KEY = 'player:prefetch';

const directUrl = (id) => `https://www.googleapis.com/drive/v3/files/${encodeURIComponent(id)}?alt=media`;
const proxyUrl = (id) => `/api/proxy/drive/${encodeURIComponent(id)}`;

/** @type {Map<string, Entry>} */
const entries = new Map();
let plan = [];               // file ids, most wanted first; [0] is the current track
let active = null;           // the one download running; they never compete for bandwidth
let directWorked = false;    // a direct download succeeded in this page
let directBlocked = false;   // direct fails while the network is up: use the proxy
const listeners = new Set();

export function enabled() {
    try { return localStorage.getItem(DISABLED_KEY) !== 'off'; } catch { return true; }
}

class Entry {
    constructor(id, bytesHint) {
        this.id = id;
        this.bytesHint = bytesHint ?? null;
        /** queued | downloading | waiting | complete | skipped | failed */
        this.state = 'queued';
        this.received = 0;
        this.total = null;
        this.type = '';
        this.parts = [];
        this.pending = [];
        this.pendingBytes = 0;
        this.blob = null;
        this.url = null;
        this.lastUsed = Date.now();
        this.controller = null;
        this.cancelled = false;
        this.waiters = [];
        this.reason = null;
    }
}

function notify(entry) {
    for (const fn of listeners) {
        try { fn(entry?.id ?? null); } catch { /* a listener's problem, not ours */ }
    }
}

/** Calls `fn(fileId)` whenever a download progresses or changes state. Returns an unsubscribe. */
export function subscribe(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
}

/**
 * What to have on hand, most wanted first: `[{ id, bytes }]`, the current track
 * then the next. Downloads for anything no longer wanted stop; finished files
 * stay until the budget needs their room.
 */
export function setPlan(items) {
    const wanted = (items ?? []).filter((i) => i?.id);
    plan = enabled() ? wanted.map((i) => i.id) : [];

    for (const [id, e] of entries) {
        if (!plan.includes(id) && e.state !== 'complete') drop(e);
    }
    if (!enabled()) { notify(null); return; }

    for (const { id, bytes } of wanted) {
        let e = entries.get(id);
        if (!e || e.state === 'failed') {
            e = new Entry(id, bytes);
            entries.set(id, e);
        }
        e.lastUsed = Date.now();
        if (e.state === 'queued' && tooBig(e.bytesHint)) skip(e, 'larger than the cache cap');
    }
    evict();
    pump();
    notify(null);
}

const tooBig = (bytes) => bytes != null && bytes > CAP_BYTES;

function skip(e, reason) {
    e.state = 'skipped';
    e.reason = reason;
    abort(e);
    e.parts = [];
    e.pending = [];
    settle(e);
}

function drop(e) {
    abort(e);
    entries.delete(e.id);
    if (e.url) URL.revokeObjectURL(e.url);
    settle(e);
    if (active === e) active = null;
}

function abort(e) {
    e.cancelled = true;
    try { e.controller?.abort(); } catch { /* already done */ }
}

function settle(e) {
    const waiters = e.waiters;
    e.waiters = [];
    for (const w of waiters) w(e.state === 'complete' ? e.blob : null);
}

/** Starts the most wanted download that is not done, if none is running. */
function pump() {
    if (active && entries.get(active.id) === active && !active.cancelled) return;
    active = null;
    for (const id of plan) {
        const e = entries.get(id);
        if (!e || e.state !== 'queued') continue;
        // The current track always gets its room; a prefetch only if it fits.
        if (id !== plan[0] && !fits(e)) { skip(e, 'no room in the cache budget'); continue; }
        active = e;
        download(e).finally(() => {
            if (active === e) active = null;
            pump();
        });
        return;
    }
}

function fits(e) {
    const size = e.total ?? e.bytesHint ?? 0;
    let held = 0;
    for (const other of entries.values()) {
        if (other !== e && plan.includes(other.id)) held += other.blob?.size ?? other.total ?? other.bytesHint ?? 0;
    }
    return held + size <= BUDGET_BYTES;
}

/** Finished files outside the plan go, oldest use first, while over budget. */
function evict() {
    let held = 0;
    for (const e of entries.values()) held += e.blob?.size ?? e.received;
    const spare = [...entries.values()]
        .filter((e) => e.state === 'complete' && !plan.includes(e.id))
        .sort((a, b) => a.lastUsed - b.lastUsed);
    while (held > BUDGET_BYTES && spare.length) {
        const e = spare.shift();
        held -= e.blob?.size ?? 0;
        drop(e);
    }
}

/* ---------- the download ---------- */

function fold(e) {
    if (!e.pending.length) return;
    e.parts.push(new Blob(e.pending));
    e.pending = [];
    e.pendingBytes = 0;
}

function sleep(ms, e) {
    return new Promise((resolve) => {
        const done = () => {
            clearTimeout(timer);
            removeEventListener('online', done);
            clearInterval(check);
            resolve();
        };
        const timer = setTimeout(done, ms);
        // Back online is the moment to try, whatever the backoff says.
        addEventListener('online', done);
        const check = setInterval(() => { if (e.cancelled) done(); }, 250);
    });
}

function backoffMs(attempt) {
    const base = Math.min(MAX_BACKOFF_MS, 1000 * 2 ** Math.max(0, attempt - 1));
    return base * (0.75 + Math.random() * 0.5);
}

/** Waits until auth.js holds a token other than the one Drive refused. */
async function tokenOtherThan(refused, e) {
    while (!e.cancelled) {
        const t = window.authGetToken?.() ?? null;
        if (t && t !== refused) return t;
        await sleep(TOKEN_POLL_MS, e);
    }
    return null;
}

let lastNotify = 0;
function progress(e) {
    const now = performance.now();
    if (now - lastNotify < 250) return;
    lastNotify = now;
    notify(e);
}

async function download(e) {
    e.state = 'downloading';
    e.cancelled = false;
    notify(e);
    let attempt = 0;
    let viaProxy = directBlocked;

    while (!e.cancelled) {
        if (e.total != null && e.received >= e.total) break;

        const headers = {};
        // Always a range through the proxy: it answers in 8 MiB slices (Cloud Run
        // caps a buffered response), which this loop strings together.
        if (e.received > 0 || viaProxy) headers.Range = `bytes=${e.received}-`;
        let token = null;
        if (viaProxy) {
            window.authSyncProxyCookie?.();
        } else {
            token = window.authGetToken?.() ?? null;
            if (!token) { e.state = 'waiting'; notify(e); token = await tokenOtherThan(null, e); continue; }
            headers.Authorization = `Bearer ${token}`;
        }

        e.controller = new AbortController();
        let res;
        try {
            res = await fetch(viaProxy ? proxyUrl(e.id) : directUrl(e.id),
                { headers, signal: e.controller.signal, cache: 'no-store' });
        } catch {
            if (e.cancelled) return;
            attempt++;
            // Direct never got through, yet the browser is online: CORS or a
            // blocker, not the network. The proxy can still serve it.
            if (!viaProxy && !directWorked && navigator.onLine && attempt >= 2) {
                directBlocked = viaProxy = true;
                attempt = 0;
                continue;
            }
            e.state = 'waiting';
            notify(e);
            await sleep(backoffMs(attempt), e);
            continue;
        }

        if (res.status === 401 || res.status === 403) {
            res.body?.cancel?.();
            e.state = 'waiting';
            notify(e);
            await tokenOtherThan(viaProxy ? window.authGetToken?.() : token, e);
            continue;
        }
        if (!res.ok) {
            res.body?.cancel?.();
            if (res.status === 404 || res.status === 400 || res.status === 416) {
                e.state = 'failed';
                e.reason = `HTTP ${res.status}`;
                settle(e);
                notify(e);
                return;
            }
            attempt++;
            e.state = 'waiting';
            notify(e);
            await sleep(backoffMs(attempt), e);
            continue;
        }

        // 206 picks up where we are; a 200 is the whole file again.
        if (res.status === 206) {
            const m = /bytes (\d+)-\d+\/(\d+|\*)/.exec(res.headers.get('content-range') ?? '');
            if (m) {
                if (Number(m[1]) !== e.received) {
                    res.body?.cancel?.();
                    e.parts = []; e.pending = []; e.pendingBytes = 0; e.received = 0;
                    continue;
                }
                if (m[2] !== '*') e.total = Number(m[2]);
            } else if (e.total == null) {
                // Cross-origin, Content-Range is only readable if the server
                // exposes it. A 206 to "bytes=N-" is the file from N to its end.
                const length = Number(res.headers.get('content-length'));
                if (length > 0) e.total = e.received + length;
            }
        } else {
            e.parts = []; e.pending = []; e.pendingBytes = 0; e.received = 0;
            const length = Number(res.headers.get('content-length'));
            e.total = length > 0 ? length : null;
        }
        e.type ||= res.headers.get('content-type') ?? '';

        if (tooBig(e.total)) {
            res.body?.cancel?.();
            skip(e, 'larger than the cache cap');
            notify(e);
            return;
        }

        e.state = 'downloading';
        notify(e);
        try {
            const reader = res.body.getReader();
            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                e.pending.push(value);
                e.pendingBytes += value.byteLength;
                e.received += value.byteLength;
                if (e.pendingBytes >= FOLD_BYTES) fold(e);
                progress(e);
            }
        } catch {
            fold(e);
            if (e.cancelled) return;
            attempt++;
            e.state = 'waiting';
            notify(e);
            await sleep(backoffMs(attempt), e);
            continue;
        }
        fold(e);
        if (!viaProxy) directWorked = true;
        attempt = 0;
        // No length to check against: the body ending is the end.
        if (e.total == null) break;
    }
    if (e.cancelled) return;

    e.blob = new Blob(e.parts, e.type ? { type: e.type } : undefined);
    e.parts = [];
    e.total = e.blob.size;
    e.url = URL.createObjectURL(e.blob);
    e.state = 'complete';
    settle(e);
    evict();
    notify(e);
}

/* ---------- reading the cache ---------- */

/** An object URL for the whole file when it is downloaded, else null. */
export function objectUrl(id) {
    const e = entries.get(id);
    if (e?.state !== 'complete') return null;
    e.lastUsed = Date.now();
    return e.url;
}

/** The downloaded file, or null. Never fetches. */
export function blob(id) {
    const e = entries.get(id);
    return e?.state === 'complete' ? e.blob : null;
}

/**
 * The whole file once downloaded, or null when it will not be (too big, failed,
 * dropped from the plan, or never planned). Never starts a download itself.
 */
export function whenComplete(id) {
    const e = entries.get(id);
    if (!e) return Promise.resolve(null);
    if (e.state === 'complete') return Promise.resolve(e.blob);
    if (e.state === 'skipped' || e.state === 'failed') return Promise.resolve(null);
    return new Promise((resolve) => e.waiters.push(resolve));
}

/** `{ state, received, total }` for a file, or null when it is not in the cache. */
export function status(id) {
    const e = entries.get(id);
    if (!e) return null;
    return { state: e.state, received: e.received, total: e.total ?? e.bytesHint ?? null };
}

/** Stops every download; finished files stay. */
export function stopAll() {
    setPlan([]);
}

/** For tests and diagnostics. */
export function debug() {
    return [...entries.values()].map((e) => ({
        id: e.id, state: e.state, received: e.received, total: e.total, size: e.blob?.size ?? null, reason: e.reason,
    }));
}
