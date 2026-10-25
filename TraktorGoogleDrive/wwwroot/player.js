// One audio element plays for the whole app. Two tracks playing at once used
// to be possible because every AudioTrack owned its own Audio(); with a single
// active element it is unrepresentable.
//
// A second, hidden element holds the next track loaded and decoded at its start
// point ("primed"), so advancing swaps elements and plays at once instead of
// loading a new source. Only the active element's events count: the spare's are
// ignored until it becomes the active one, and it never plays while spare.

import * as cache from './prefetch.js';

let audio = null;   // the active element
let spare = null;   // the other one: empty, or primed with the next track
let dotnet = null;
let currentId = null;

// Gig preview (ADR 20261018-47 §4). Checked here on every timeupdate (~4 Hz)
// rather than in .NET, whose progress callback is throttled to whole seconds.
let endAt = null;      // seconds; null = play to the natural end
let pendingStart = null; // seek once metadata is known

// Token expiry. The proxy URL carries no token: auth.js keeps the current one
// in a cookie, so every range slice is fetched with a fresh token. A 401 can
// still happen (a renewal the browser blocked, a revoked grant); then the
// element errors at the slice it could not fetch, and this reloads the same
// URL at the same position once a renewed token is in the cookie.
let currentUrl = null;
let wantPlaying = false;   // the user's intent, which an error does not change
let retriedAuth = false;   // one silent renewal per failure, not a loop
let stalled = null;        // { at, wasPlaying } while waiting for a sign-in

// Every new source is a new load. Skipping quickly through tracks supersedes
// loads before they finish, and whatever a superseded load still raises (its
// play() rejecting, an error event, the pause the switch causes) is about a
// track nobody is waiting for any more. It used to surface as "Playback
// failed", count as a dead track in gig preview, and auto-advance.
let generation = 0;
let switching = false; // a playing element is being given a new source

// Where the element's bytes come from. 'stream' is the proxy URL, fetched in
// slices as it plays; 'cache' is the whole file, downloaded by prefetch.js,
// which needs no network at all. A track starts streaming and moves to the
// cache, at the same position, once its download completes.
let source = 'stream';
let swapping = false;           // moving the loaded track from stream to cache
const cacheFailed = new Set();  // files whose cached copy would not play: stream them

// A network drop while streaming. The element gives up on a failed slice (or
// waits on one forever); this reloads at the same position, with backoff, until
// it plays again or the download finishes and takes over.
let reconnect = null;           // { attempt, at, timer }
let waitingSince = null;        // the element has been starved of data since
let waitTimer = null;
const SHOW_BUFFERING_MS = 1_500;
const STALL_RELOAD_MS = 15_000;
const MAX_RECONNECT_MS = 15_000;
let net = '';                   // '' | 'buffering' | 'reconnecting'
let nextId = null;              // the planned next track, for the "ready offline" sign

function load(url) {
    generation++;
    switching = !audio.paused;
    audio.src = url;
}

const report = (state) => dotnet?.invokeMethodAsync('OnPlaybackStateChanged', state, currentId);

// timeupdate fires ~4x/second; that is already a lot of interop, so only push
// when the whole second changes.
let lastWholeSecond = -1;
const reportProgress = (force) => {
    if (!audio) return;
    const t = Math.floor(audio.currentTime);
    if (!force && t === lastWholeSecond) return;
    lastWholeSecond = t;
    dotnet?.invokeMethodAsync('OnProgress', audio.currentTime, audio.duration || 0);
};

export function init(dotNetRef) {
    dotnet = dotNetRef;
    audio = wire(new Audio());
    spare = wire(new Audio());

    cache.subscribe(onCacheChanged);
    addEventListener('online', () => { if (reconnect) fireReconnect(); });
}

/** Gives an element the player's listeners, which act only while it is the active one. */
function wire(el) {
    // Read ahead as far as the browser will; prefetch.js does the rest.
    el.preload = 'auto';
    const on = (type, fn) => el.addEventListener(type, (e) => { if (el === audio) fn(e); });

    // A swap to the cached copy is the same track at the same place: no spinner.
    on('loadstart', () => { switching = false; if (!swapping) report('loading'); });
    on('loadedmetadata', () => { swapping = false; });
    on('canplay', () => {
        report('ready');
        reportProgress(true);
        if (!wantPlaying) settleNetwork();
    });
    on('waiting', () => { if (wantPlaying) startWaiting(); });
    on('timeupdate', () => {
        if (waitingSince != null && !audio.paused && audio.readyState >= 3) settleNetwork();
        if (endAt != null && audio.currentTime >= endAt) {
            endAt = null; // once per track
            markAdvance();
            report('previewEnd');
        }
        reportProgress(false);
        primeIfDue();
    });
    // Seeking past the end is a deliberate "let me hear this bit": the seek wins
    // and the track plays on, rather than jumping straight to the next one.
    on('seeking', () => {
        if (endAt != null && audio.currentTime > endAt) endAt = null;
    });
    on('loadedmetadata', () => {
        if (pendingStart != null) {
            audio.currentTime = pendingStart;
            pendingStart = null;
        }
    });
    on('durationchange', () => reportProgress(true));
    on('seeked', () => reportProgress(true));
    on('playing', () => { retriedAuth = false; settleNetwork(); markPlaying(); report('playing'); });
    // Switching tracks pauses the old one on the way; that is not the user pausing.
    on('pause', () => { if (!switching) report('paused'); });
    on('ended', () => { markAdvance(); report('ended'); });
    on('error', () => { onError(); });
    return el;
}

/* ---------- the primed next track ---------- */

// What to prime (from plan): { id, url, start }. And what is primed in `spare`:
// { id, start, source, ready }. Primed as soon as the next track's download is
// complete; a track that streams is primed PRIME_LEAD_S before this one ends,
// so the spare does not fetch through the proxy any earlier than it must.
let primeTarget = null;
let primed = null;
let plannedCurrent = null;
let startedPrimed = null; // the loaded track came from the primed element (diagnostics)
const PRIME_LEAD_S = 30;

// 'off' turns priming off: every advance loads a new source, as before. For
// measuring the difference, and a manual escape hatch.
function primingEnabled() {
    try { return localStorage.getItem('player:prime') !== 'off'; } catch { return true; }
}

function discardPrimed() {
    if (!primed) return;
    primed = null;
    spare.removeAttribute('src');
    spare.load();
}

/** Primes the next track when it is ready to be (see above), unless it already is. */
function primeIfDue() {
    const t = primeTarget;
    if (!t || !audio || t.id === currentId || !primingEnabled()) return;
    if (primed?.id === t.id && primed.start === t.start) return;
    // Primed for the track that is about to start: play() takes it.
    if (primed && primed.id === plannedCurrent && primed.id !== currentId) return;
    const local = cacheFailed.has(t.id) ? null : cache.objectUrl(t.id);
    if (!local) {
        // Still downloading, or too big to: stream it, but only near the end.
        const end = endAt ?? audio.duration;
        if (!isFinite(end) || end - audio.currentTime > PRIME_LEAD_S) return;
        const s = cache.status(t.id);
        if (s && s.state !== 'skipped' && s.state !== 'failed' && end - audio.currentTime > 5) return;
    }
    discardPrimed();
    const p = { id: t.id, start: t.start, source: local ? 'cache' : 'stream', ready: false };
    primed = p;
    const ready = () => { if (primed === p) p.ready = true; };
    spare.addEventListener('loadedmetadata', () => {
        if (primed !== p) return;
        if (p.start > 0) {
            spare.addEventListener('seeked', ready, { once: true });
            spare.currentTime = p.start;
        } else {
            if (spare.readyState >= 3) ready();
            else spare.addEventListener('canplay', ready, { once: true });
        }
    }, { once: true });
    spare.addEventListener('error', () => { if (primed === p) discardPrimed(); }, { once: true });
    spare.src = local ?? t.url;
}

/**
 * Makes the primed spare the active element for `fileId`, if it holds exactly
 * that file at `start` and is ready. The old element is emptied. Returns false
 * when the normal load has to happen instead.
 */
function takePrimed(fileId, start) {
    const p = primed;
    if (!p || p.id !== fileId || !p.ready || Math.abs(p.start - start) > 0.25 || spare.error) {
        discardPrimed();
        startedPrimed = null;
        return false;
    }
    primed = null;
    startedPrimed = fileId;
    const old = audio;
    audio = spare;
    spare = old;
    generation++;           // whatever the old element still raises is stale
    switching = false;
    source = p.source;
    old.pause();
    old.removeAttribute('src');
    old.load();
    lastWholeSecond = -1;
    // WaveSurfer follows the element that plays.
    dispatchEvent(new CustomEvent('player:element', { detail: audio }));
    return true;
}

/** What is primed, for tests and diagnostics. */
export function primedState() {
    return primed ? { id: primed.id, start: primed.start, source: primed.source, ready: primed.ready } : null;
}

/* ---------- whole-file cache ---------- */

/** The cached copy when there is one (and it has not failed to play), else the stream. */
function sourceFor(fileId, streamUrl) {
    const local = cacheFailed.has(fileId) ? null : cache.objectUrl(fileId);
    source = local ? 'cache' : 'stream';
    return local ?? streamUrl;
}

/**
 * The current and next track to have downloaded, from PlayerService:
 * `{ id, bytes }` each, or null. `bytes` is the NML's estimate, so a set too
 * big for the cache is never started. `next` also carries `url` (its stream)
 * and `start` (0, or its load cue in gig preview), to prime it with.
 */
export function plan(current, next) {
    nextId = next?.id ?? null;
    plannedCurrent = current?.id ?? null;
    cache.setPlan([current, next].filter(Boolean).map(({ id, bytes }) => ({ id, bytes })));
    primeTarget = next?.id && next.url ? { id: next.id, url: next.url, start: next.start ?? 0 } : null;
    // PlayerService plans before it plays: the primed track may be the one
    // about to start, which play() then takes.
    const keep = primed && (primed.id === plannedCurrent
        || (primed.id === primeTarget?.id && primed.start === primeTarget?.start));
    if (!keep) discardPrimed();
    primeIfDue();
    reportBuffer(true);
}

/**
 * Moves the loaded track onto its downloaded copy at the same position and in
 * the same play state. After this a dropped connection cannot touch it.
 */
function swapToCache() {
    if (!audio || !currentId || source === 'cache' || stalled || cacheFailed.has(currentId)) return;
    const local = cache.objectUrl(currentId);
    if (!local) return;
    // Mid-seek the position is not settled yet; swap once it is.
    if (audio.seeking) { audio.addEventListener('seeked', swapToCache, { once: true }); return; }

    const at = pendingStart ?? reconnect?.at ?? audio.currentTime;
    const play = wantPlaying;
    clearReconnect();
    swapping = true;
    source = 'cache';
    pendingStart = at > 0 ? at : null;
    load(local);
    const gen = generation;
    if (play) audio.play().catch((e) => playFailed(e, gen));
    reportBuffer(true);
}

function onCacheChanged(id) {
    if (currentId && source === 'stream' && cache.blob(currentId)) swapToCache();
    if (id && id === primeTarget?.id) primeIfDue();
    reportBuffer(false);
}

/* ---------- buffering and reconnecting ---------- */

function startWaiting() {
    if (waitingSince != null) return;
    waitingSince = performance.now();
    clearTimeout(waitTimer);
    waitTimer = setTimeout(() => {
        if (waitingSince == null) return;
        setNet(reconnect ? 'reconnecting' : 'buffering');
        // A connection that hangs instead of failing never raises an error.
        waitTimer = setTimeout(() => {
            if (waitingSince == null || !wantPlaying || source === 'cache') return;
            scheduleReconnect(audio.currentTime);
        }, STALL_RELOAD_MS - SHOW_BUFFERING_MS);
    }, SHOW_BUFFERING_MS);
}

/** Data is flowing again (or nobody is waiting for it). */
function settleNetwork() {
    waitingSince = null;
    clearTimeout(waitTimer);
    clearReconnect();
    setNet('');
}

function clearReconnect() {
    if (reconnect) clearTimeout(reconnect.timer);
    reconnect = null;
}

/** Reloads the stream at `at` after a backoff; the browser coming online cuts it short. */
function scheduleReconnect(at) {
    const attempt = (reconnect?.attempt ?? 0) + 1;
    if (reconnect) clearTimeout(reconnect.timer);
    const delay = Math.min(MAX_RECONNECT_MS, 500 * 2 ** (attempt - 1));
    reconnect = { attempt, at, timer: setTimeout(fireReconnect, delay) };
    setNet('reconnecting');
}

function fireReconnect() {
    const r = reconnect;
    if (!r || !audio || !currentId) return;
    clearTimeout(r.timer);
    // The download may have finished meanwhile: then the network is moot.
    if (cache.blob(currentId) && !cacheFailed.has(currentId)) { swapToCache(); return; }
    // Kept until the element plays (or has data, when paused), so a reload that
    // fails again backs off further. One that hangs is retried regardless.
    reconnect = { ...r, timer: setTimeout(() => { if (reconnect) scheduleReconnect(r.at); }, STALL_RELOAD_MS) };
    reload(r.at, wantPlaying, true);
}

function setNet(value) {
    if (net === value) return;
    net = value;
    reportBuffer(true);
}

let lastBuffer = '';
function reportBuffer(force) {
    if (!dotnet) return;
    const s = currentId ? cache.status(currentId) : null;
    const state = !currentId ? 'none'
        : source === 'cache' ? 'cached'
        : !s || s.state === 'skipped' || s.state === 'failed' ? 'stream'
        : s.state === 'complete' ? 'cached'
        : 'downloading';
    const fraction = state === 'cached' ? 1 : s?.total ? Math.min(1, s.received / s.total) : 0;
    const nextReady = !!nextId && nextId !== currentId && cache.blob(nextId) != null;
    // Two-percent steps are plenty for a progress line, and keep interop quiet.
    const key = `${currentId}|${state}|${Math.floor(fraction * 50)}|${nextReady}|${net}`;
    if (key === lastBuffer && !force) return;
    lastBuffer = key;
    dotnet.invokeMethodAsync('OnBufferChanged', state, fraction, nextReady, net);
}

/** What the cache and the connection look like now, for tests and diagnostics. */
export function bufferState() {
    return { source, net, reconnecting: !!reconnect, cache: cache.debug() };
}

/**
 * What the proxy says about this URL now. A media element never exposes the
 * status of the request that failed: a 401 on a mid-file slice surfaces as
 * MEDIA_ERR_NETWORK, the same as a dropped connection, so ask once.
 */
async function probe(url) {
    try {
        const res = await fetch(url, { method: 'GET', headers: { Range: 'bytes=0-0' }, cache: 'no-store' });
        res.body?.cancel?.();
        return res.status;
    } catch {
        return 0;
    }
}

/** Loads the same URL again at `at`. The preview bound (endAt) is left alone. */
function reload(at, play, keepReconnect) {
    if (!audio || !currentUrl) return;
    if (!keepReconnect) clearReconnect();
    pendingStart = at > 0 ? at : null;
    wantPlaying = play;
    load(sourceFor(currentId, currentUrl));
    const gen = generation;
    if (play) {
        audio.play().catch((e) => playFailed(e, gen));
    } else {
        audio.addEventListener('loadedmetadata', () => report('paused'), { once: true });
    }
}

async function onError() {
    // A new load resets `error`, so an event left over from a superseded load
    // finds none.
    if (!audio.error) return;
    const gen = generation;
    const id = currentId;
    const url = currentUrl;
    if (!id || !url) { report('error'); return; }

    // Where it stopped, before anything reloads: the position to resume at.
    const at = pendingStart ?? reconnect?.at ?? audio.currentTime;
    const wasPlaying = wantPlaying;
    const code = audio.error.code;

    // The downloaded copy would not play: stream it instead, from here.
    if (source === 'cache') {
        cacheFailed.add(id);
        swapping = false;
        reload(at, wasPlaying);
        return;
    }

    // Also how an undecodable file (MEDIA_ERR_SRC_NOT_SUPPORTED, the same code a
    // 401 body gives) is told apart from an expired token: the proxy says 200.
    const status = await probe(url);
    if (gen !== generation) return; // another load started meanwhile

    // The network, not the file: no answer at all, or a fetch that failed on a
    // file the proxy still serves. Reconnect at the same place, however long
    // the tunnel. (A bad file fails as MEDIA_ERR_SRC_NOT_SUPPORTED instead.)
    if (status === 0 || (code === MediaError.MEDIA_ERR_NETWORK && (status < 400 || status >= 500))) {
        scheduleReconnect(at);
        return;
    }
    if (status !== 401 && status !== 403) { report('error'); return; }

    if (!retriedAuth) {
        retriedAuth = true;
        const token = await (window.authRefreshToken?.() ?? Promise.resolve(null));
        if (gen !== generation) return;
        if (token) { reload(at, wasPlaying); return; }
    }

    // Could not renew without the user. Hold the position; resume() continues
    // from it after a sign-in.
    stalled = { at, wasPlaying };
    wantPlaying = false;
    dotnet?.invokeMethodAsync('OnProgress', at, audio.duration || 0);
    report('unauthorized');
}

/**
 * Continues after a sign-in, from exactly where a 401 stopped it. Returns
 * false when nothing was stalled, so the caller can fall back to a plain play.
 */
export function resume() {
    if (!stalled) return false;
    const { at } = stalled;
    stalled = null;
    retriedAuth = false;
    reload(at, true);
    return true;
}

/**
 * Plays `fileId`. `startAt`/`stopAt` apply when this loads a new file. For the
 * file already loaded they apply only with `rebound`: then `stopAt` replaces the
 * preview bound and a non-null `startAt` seeks. Without it, a resume of the same
 * file keeps its position and its preview bound.
 */
export async function play(fileId, url, startAt, stopAt, rebound) {
    if (!audio) return;
    if (currentId !== fileId) {
        const primedNow = takePrimed(fileId, startAt > 0 ? startAt : 0);
        pendingStart = primedNow || !(startAt > 0) ? null : startAt;
        endAt = stopAt ?? null;
        currentUrl = url;
        currentId = fileId;
        stalled = null;
        retriedAuth = false;
        swapping = false;
        settleNetwork();
        if (primedNow) reportProgress(true);
        else load(sourceFor(fileId, url));
        reportBuffer(true);
    } else if (stalled) {
        resume();
        return;
    } else if (reconnect || audio.error) {
        // Paused while reconnecting, or a dead stream: continue from where it was.
        if (rebound) endAt = stopAt ?? null;
        const at = rebound && startAt != null ? startAt : reconnect?.at ?? audio.currentTime;
        reload(at, true);
        return;
    } else if (rebound) {
        // The file is already loaded: the bounds were passed on purpose, for gig
        // preview starting on (or resuming) the current track.
        endAt = stopAt ?? null;
        if (startAt != null) {
            // currentTime cannot be set before metadata; a cued track may lack it.
            if (audio.readyState >= 1) { pendingStart = null; audio.currentTime = Math.max(0, startAt); }
            else pendingStart = startAt > 0 ? startAt : null;
        }
    }
    wantPlaying = true;
    const gen = generation;
    try {
        await audio.play();
    } catch (e) {
        playFailed(e, gen);
    }
}

/**
 * Autoplay rejection or a load failure — surface it rather than hanging the
 * button in a permanent "loading" state. A failed load also fires the
 * element's error event, whose handler knows whether it was the token. An
 * AbortError only means a newer load or a pause overtook this play(), e.g. Next
 * clicked while the track was still buffering; and anything raised once another
 * load has started is stale the same way.
 */
function playFailed(e, gen) {
    if (gen !== generation || e?.name === 'AbortError') return;
    if (e?.name === 'NotAllowedError') { report('blocked'); return; }
    if (audio?.error) return;
    report('error');
}

/**
 * Loads a track without playing it, at a saved position. The seek waits for
 * metadata: currentTime cannot be set on a fresh source before it.
 */
export function cue(fileId, url, position) {
    if (!audio) return;
    // No preview bound: a restored track plays on to its natural end.
    endAt = null;
    pendingStart = position > 0 ? position : null;
    currentId = fileId;
    currentUrl = url;
    stalled = null;
    retriedAuth = false;
    wantPlaying = false;
    swapping = false;
    settleNetwork();
    discardPrimed();
    load(sourceFor(fileId, url));
    reportBuffer(true);
    // Registered after init's listener, so the pending seek has been applied.
    audio.addEventListener('loadedmetadata', () => {
        if (currentId === fileId && audio.paused) report('paused');
    }, { once: true });
}

export function pause() {
    wantPlaying = false;
    // A pending reconnect stays, so play continues from its position; the
    // "buffering" sign goes, since nothing is waiting for data any more.
    waitingSince = null;
    clearTimeout(waitTimer);
    if (!reconnect) setNet('');
    audio?.pause();
}

export function stop() {
    if (!audio) return;
    generation++;
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    currentId = null;
    currentUrl = null;
    stalled = null;
    wantPlaying = false;
    endAt = null;
    pendingStart = null;
    swapping = false;
    source = 'stream';
    nextId = null;
    primeTarget = null;
    discardPrimed();
    settleNetwork();
    // Downloads stop; finished files stay, for a quick replay.
    cache.stopAll();
    report('idle');
    reportBuffer(true);
}

/** Arms or clears the preview end for the loaded file. */
export function setBounds(stopAt) {
    endAt = stopAt ?? null;
}

export function seek(seconds) {
    if (!audio || !isFinite(seconds)) return;
    audio.currentTime = Math.max(0, seconds);
}

/** The one audio element, so WaveSurfer can attach to it instead of loading its own. */
export function element() {
    return audio;
}

export function position() {
    return audio ? { current: audio.currentTime, duration: audio.duration || 0 } : null;
}

export function dispose() {
    stop();
    audio = null;
    spare = null;
    dotnet = null;
}

/* ---------- transition timing (tests and diagnostics) ---------- */

// The gap between one track ending (or reaching its preview end) and the next
// one playing: what a listener hears as silence between tracks.
const transitions = [];
let advanceFrom = null; // { id, at }

function markAdvance() {
    advanceFrom = { id: currentId, at: performance.now() };
}

function markPlaying() {
    if (!advanceFrom || advanceFrom.id === currentId) return;
    transitions.push({
        from: advanceFrom.id, to: currentId, gapMs: performance.now() - advanceFrom.at,
        primed: startedPrimed === currentId, at: audio.currentTime,
    });
    if (transitions.length > 20) transitions.shift();
    advanceFrom = null;
}

/** The last few track-to-track gaps, newest last. */
export function gaps() {
    return transitions.slice();
}
