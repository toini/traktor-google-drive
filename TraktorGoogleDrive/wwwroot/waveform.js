// Waveform peaks for every format, without ever downloading audio through the
// proxy twice. See docs/decisions/20261018-47-waveform-cue-markers-gig-preview.md.
//
// Uncompressed (WAV/AIFF): a 1-2 GB WAV cannot be decoded in the browser, since
// decodeAudioData expands PCM to Float32 and a 1.4 GB file needs ~2.8 GB of RAM.
// But the amplitude at any point is readable straight from the bytes at that
// offset, so a few hundred small range reads (~1 MB total) give the picture.
//
// Compressed (MP3/FLAC/AAC): has to be decoded, so it is fetched once straight
// from Drive (not the Cloud Run proxy, which bills egress per GiB) and decoded at
// 8 kHz, which keeps a 7-minute track at ~27 MB of samples. The caller caps the
// file size, so a 2-hour MP3 set never gets here.
//
// Either way the peaks are cached in IndexedDB, so this happens once per file.
// And when the player is downloading the whole file anyway (prefetch.js), both
// read that copy instead of fetching their own.

import * as cache from './prefetch.js';

const DB_NAME = 'traktor-waveforms';
const STORE = 'peaks';
const VERSION = 1;
const BUCKETS = 400;      // horizontal resolution
const WINDOW_BYTES = 2048; // bytes sampled per bucket
const CONCURRENCY = 12;
const DECODE_RATE = 8000; // Hz; plenty for a 400-bucket envelope

/* ---------- IndexedDB cache ---------- */

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(DB_NAME, VERSION);
        req.onupgradeneeded = () => {
            if (!req.result.objectStoreNames.contains(STORE)) req.result.createObjectStore(STORE);
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

async function cacheGet(key) {
    try {
        const db = await openDb();
        return await new Promise((resolve) => {
            const r = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
            r.onsuccess = () => resolve(r.result ?? null);
            r.onerror = () => resolve(null);
        });
    } catch { return null; }
}

async function cachePut(key, value) {
    try {
        const db = await openDb();
        // Await the transaction, not just the request: a reload immediately
        // after sampling would otherwise lose peaks that looked written.
        await new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readwrite');
            tx.objectStore(STORE).put(value, key);
            tx.oncomplete = resolve;
            tx.onerror = () => reject(tx.error);
            tx.onabort = () => reject(tx.error);
        });
    } catch { /* private mode or quota — recompute next time */ }
}

/** Peaks already computed for this file, or null. Never fetches anything. */
export async function cachedPeaks(fileId) {
    const cached = await cacheGet(fileId);
    return cached?.version === VERSION ? cached.peaks : null;
}

/* ---------- WAV parsing ---------- */

/**
 * A token to retry with after Drive refused `usedToken` (401/403), or null.
 * The same recovery the player does on a mid-set 401: if the browser already
 * holds a newer token, use it; otherwise renew once. auth.js runs one renewal at
 * a time, so a burst of refused reads shares a single renewal, and it rewrites
 * the proxy cookie when it lands.
 */
async function tokenAfterRefusal(usedToken) {
    const current = window.authGetToken?.() ?? null;
    if (current && current !== usedToken) return current;
    return (await (window.authRefreshToken?.() ?? Promise.resolve(null))) || null;
}

const refused = (status) => status === 401 || status === 403;

async function readRange(url, start, end, fileId) {
    // Already downloaded whole for playback (prefetch.js): read it from memory.
    const local = fileId ? cache.blob(fileId) : null;
    if (local) return new DataView(await local.slice(start, end + 1).arrayBuffer());

    const headers = { Range: `bytes=${start}-${end}` };
    // The proxy takes the token from the cookie; note which one this read used.
    const used = window.authGetToken?.() ?? null;
    let res = await fetch(url, { headers });
    if (refused(res.status) && await tokenAfterRefusal(used)) {
        window.authSyncProxyCookie?.();
        res = await fetch(url, { headers });
    }
    if (!res.ok) throw new Error(`range ${start}-${end}: HTTP ${res.status}`);
    return new DataView(await res.arrayBuffer());
}

function ascii(view, offset) {
    return String.fromCharCode(
        view.getUint8(offset), view.getUint8(offset + 1),
        view.getUint8(offset + 2), view.getUint8(offset + 3));
}

/** Walks the RIFF chunk list to locate fmt and data. */
function parseHeader(view) {
    if (ascii(view, 0) !== 'RIFF' || ascii(view, 8) !== 'WAVE') throw new Error('not a RIFF/WAVE file');

    let offset = 12;
    let fmt = null;
    while (offset + 8 <= view.byteLength) {
        const id = ascii(view, offset);
        const size = view.getUint32(offset + 4, true);
        if (id === 'fmt ') {
            fmt = {
                format: view.getUint16(offset + 8, true),
                channels: view.getUint16(offset + 10, true),
                sampleRate: view.getUint32(offset + 12, true),
                bits: view.getUint16(offset + 22, true),
            };
        } else if (id === 'data') {
            if (!fmt) throw new Error('data chunk before fmt');
            return { ...fmt, dataOffset: offset + 8, dataSize: size };
        }
        offset += 8 + size + (size % 2); // chunks are word-aligned
    }
    throw new Error('no data chunk in the first bytes');
}

/** Peak amplitude in one window, 0..1. */
function peakOf(view, header) {
    const { bits } = header;
    const step = bits / 8;
    let peak = 0;

    for (let i = 0; i + step <= view.byteLength; i += step) {
        let v;
        if (bits === 16) v = view.getInt16(i, true) / 32768;
        else if (bits === 24) {
            const b = view.getUint8(i) | (view.getUint8(i + 1) << 8) | (view.getUint8(i + 2) << 16);
            v = (b & 0x800000 ? b - 0x1000000 : b) / 8388608;
        } else if (bits === 32) v = view.getFloat32(i, true);
        else return 0;
        const a = Math.abs(v);
        if (a > peak) peak = a;
    }
    return Math.min(peak, 1);
}

async function mapLimit(items, limit, fn) {
    const out = new Array(items.length);
    let next = 0;
    await Promise.all(Array.from({ length: Math.min(limit, items.length) }, async () => {
        while (true) {
            const i = next++;
            if (i >= items.length) return;
            out[i] = await fn(items[i], i);
        }
    }));
    return out;
}

/* ---------- public API: peaks ---------- */

export async function computePeaks(url, fileId, dotNetRef) {
    const cached = await cachedPeaks(fileId);
    if (cached) return cached;

    const header = parseHeader(await readRange(url, 0, 4095, fileId));
    if (header.format !== 1 && header.format !== 3) throw new Error(`unsupported WAV format ${header.format}`);

    const frame = (header.bits / 8) * header.channels;
    const usable = Math.max(0, header.dataSize - WINDOW_BYTES);

    // Never ask for more windows than the file has distinct bytes to give: a
    // short file would otherwise get hundreds of overlapping reads of the same
    // audio, which is pure waste and hammers the proxy for no extra detail.
    const buckets = Math.max(1, Math.min(BUCKETS, Math.floor(header.dataSize / WINDOW_BYTES)));

    const offsets = Array.from({ length: buckets }, (_, i) => {
        const raw = header.dataOffset + Math.floor((usable * i) / buckets);
        return raw - (raw % frame); // stay on a frame boundary or samples shear
    });

    let done = 0;
    const peaks = await mapLimit(offsets, CONCURRENCY, async (start) => {
        const view = await readRange(url, start, start + WINDOW_BYTES - 1, fileId);
        const p = peakOf(view, header);
        if (++done % 40 === 0) dotNetRef?.invokeMethodAsync('OnPeakProgress', done / offsets.length);
        return p;
    });

    await cachePut(fileId, { version: VERSION, peaks });
    return peaks;
}

/** Reads a response body while reporting progress against its length. */
async function readWithProgress(res, onProgress) {
    const total = Number(res.headers.get('content-length')) || 0;
    if (!res.body || !total) return await res.arrayBuffer();

    const reader = res.body.getReader();
    const out = new Uint8Array(total);
    let received = 0;
    let lastReport = 0;
    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        // A server that under-reports its length must not overflow the buffer.
        if (received + value.length > out.length) return await concatRest(out, received, value, reader);
        out.set(value, received);
        received += value.length;
        if (received - lastReport > total / 20) { lastReport = received; onProgress(received / total); }
    }
    return out.buffer.slice(0, received);
}

async function concatRest(head, used, value, reader) {
    const chunks = [head.subarray(0, used), value];
    while (true) {
        const { done, value: v } = await reader.read();
        if (done) break;
        chunks.push(v);
    }
    return await new Blob(chunks).arrayBuffer();
}

/**
 * The bytes the player's whole-file download (prefetch.js) fetched for this
 * track, once it finishes, or null when it is not downloading it. Then the file
 * crosses the network once, for both playback and the picture.
 */
async function downloadedForPlayback(fileId, onProgress) {
    if (!cache.status(fileId)) return null;
    const unsubscribe = cache.subscribe((id) => {
        const s = id === fileId ? cache.status(fileId) : null;
        if (s?.total) onProgress(Math.min(1, s.received / s.total));
    });
    try {
        const blob = await cache.whenComplete(fileId);
        return blob ? await blob.arrayBuffer() : null;
    } finally {
        unsubscribe();
    }
}

async function downloadForWaveform(directUrl, token, onProgress) {
    let res = await fetch(directUrl, { headers: { Authorization: `Bearer ${token}` } });
    if (refused(res.status)) {
        const renewed = await tokenAfterRefusal(token);
        if (renewed) res = await fetch(directUrl, { headers: { Authorization: `Bearer ${renewed}` } });
    }
    if (!res.ok) throw new Error(`download for waveform: HTTP ${res.status}`);
    return await readWithProgress(res, onProgress);
}

/**
 * Peaks for a compressed file: one direct download from Drive, decoded at a low
 * sample rate. `directUrl` is the googleapis.com media URL, which takes the
 * token as a header — unlike the proxy it costs no Cloud Run egress.
 */
export async function computeCompressedPeaks(directUrl, token, fileId, dotNetRef) {
    const cached = await cachedPeaks(fileId);
    if (cached) return cached;

    // Download is the slow half; report it as the first 90%.
    const onProgress = (f) => dotNetRef?.invokeMethodAsync('OnPeakProgress', f * 0.9);
    const bytes = await downloadedForPlayback(fileId, onProgress)
        ?? await downloadForWaveform(directUrl, token, onProgress);

    // OfflineAudioContext resamples to its own rate while decoding, which is
    // what keeps memory bounded: 8 kHz is ~1/5 of the samples at 44.1 kHz.
    const ctx = new OfflineAudioContext(1, 1, DECODE_RATE);
    const audio = await ctx.decodeAudioData(bytes);

    const channels = Array.from({ length: audio.numberOfChannels }, (_, c) => audio.getChannelData(c));
    const length = audio.length;
    const buckets = Math.max(1, Math.min(BUCKETS, length));
    const peaks = new Array(buckets).fill(0);
    for (let b = 0; b < buckets; b++) {
        const from = Math.floor((length * b) / buckets);
        const to = Math.floor((length * (b + 1)) / buckets);
        let peak = 0;
        for (const data of channels) {
            for (let i = from; i < to; i++) {
                const a = data[i] < 0 ? -data[i] : data[i];
                if (a > peak) peak = a;
            }
        }
        peaks[b] = Math.min(peak, 1);
    }

    await cachePut(fileId, { version: VERSION, peaks });
    return peaks;
}

/* ---------- rendering ---------- */

// WaveSurfer does the drawing, cursor and click-to-seek. Given `media` plus
// `peaks` and `duration` it renders from the data we computed and never fetches
// or decodes the file itself, which is the only reason it can be used on a
// 1-2 GB set at all. Without `media` (casting) it is a picture whose cursor is
// moved by setTime, and clicks are handed back to .NET to seek the TV.

// Traktor's own palette, near enough: blue cues, orange fades, yellow load,
// green loops.
const COLORS = {
    cue: '#3d8bfd',
    fade: '#ff8c1a',
    load: '#ffd21f',
    loop: 'rgba(63, 191, 90, 0.28)',
    loopLabel: '#3fbf5a',
    outside: 'rgba(10, 12, 15, 0.62)',
};

let ws = null;
let regions = null;
let followsPlayer = false; // mounted on the player's element, not a picture for the TV

// The player swaps to a second, primed element when it advances (player.js).
// The cursor and click-to-seek move with it.
addEventListener('player:element', (e) => {
    if (!ws || !followsPlayer || !e.detail) return;
    try { ws.setMediaElement(e.detail); } catch { /* torn down meanwhile */ }
});

function labelElement(marker, index) {
    const el = document.createElement('span');
    el.textContent = marker.label ?? '';
    el.dataset.kind = marker.kind;
    el.dataset.cueIndex = String(index);
    if (marker.label) el.dataset.label = marker.label;
    const color = marker.kind === 'loop' ? COLORS.loopLabel : COLORS[marker.kind];
    el.style.cssText =
        `display:inline-block;padding:0 3px;font:600 9px/12px system-ui,sans-serif;`
        + `color:#111;background:${color};border-radius:2px;white-space:nowrap;`
        + (marker.label ? '' : 'min-width:4px;min-height:4px;padding:0;');
    return el;
}

function addMarkers(markers, range, duration) {
    if (!regions) return;
    regions.clearRegions();

    // Dim what gig preview will skip, so the range is visible on the waveform.
    if (range) {
        if (range.start > 0) {
            regions.addRegion({ id: 'preview-before', start: 0, end: range.start, color: COLORS.outside, drag: false, resize: false });
        }
        if (range.end != null && range.end < duration) {
            regions.addRegion({ id: 'preview-after', start: range.end, end: duration, color: COLORS.outside, drag: false, resize: false });
        }
    }

    (markers ?? []).forEach((m, i) => {
        // Clamped, not dropped: a cue past Traktor's PLAYTIME is still a cue.
        const start = Math.max(0, Math.min(m.start, duration));
        const params = {
            id: `cue-${m.kind}-${i}`,
            start,
            drag: false,
            resize: false,
            content: labelElement(m, i),
        };
        if (m.kind === 'loop' && m.end != null) {
            params.end = Math.max(start, Math.min(m.end, duration));
            params.color = COLORS.loop;
        } else {
            params.color = COLORS[m.kind] ?? COLORS.cue;
        }
        regions.addRegion(params);
    });
}

export async function mount(container, mediaElement, peaks, duration, markers, range, dotNetRef) {
    const [{ default: WaveSurfer }, { default: RegionsPlugin }] = await Promise.all([
        import('./lib/wavesurfer/wavesurfer.esm.js'),
        import('./lib/wavesurfer/plugins/regions.esm.js'),
    ]);

    destroy();
    regions = RegionsPlugin.create();

    const options = {
        container,
        peaks: [peaks],
        duration,
        height: 56,
        waveColor: '#4a5058',
        progressColor: '#ff6600',
        cursorColor: '#ffaa44',
        cursorWidth: 1,
        barWidth: 2,
        barGap: 1,
        barRadius: 1,
        // All-zero peaks (markers only) would divide by zero.
        normalize: peaks.some((p) => p > 0),
        plugins: [regions],
    };
    if (mediaElement) options.media = mediaElement;

    ws = WaveSurfer.create(options);
    followsPlayer = !!mediaElement;
    // Mounted on an element the player swapped away from while this was loading.
    const active = mediaElement ? (await import('./player.js')).element() : null;
    if (active && active !== mediaElement) ws.setMediaElement(active);

    regions.on('region-clicked', (region, e) => {
        // A marker is a place to jump to; without this the click would also
        // fall through to the waveform and seek to the pixel instead.
        e.stopPropagation();
        if (mediaElement) ws.setTime(region.start);
        else dotNetRef?.invokeMethodAsync('OnSeekRequested', region.start);
    });

    if (!mediaElement) {
        ws.on('interaction', (t) => dotNetRef?.invokeMethodAsync('OnSeekRequested', t));
    }

    // Regions need the renderer's width, which exists once WaveSurfer is ready.
    const draw = () => addMarkers(markers, range, duration);
    if (ws.getDuration() > 0) draw();
    ws.once('ready', draw);
    ws.once('redraw', draw);
    container.dataset.markers = String((markers ?? []).length);
    return true;
}

/** Moves the cursor without a media element, for a track playing on the TV. */
export function setTime(seconds) {
    if (!ws || !isFinite(seconds)) return;
    try { ws.setTime(seconds); } catch { /* not ready yet */ }
}

export function destroy() {
    if (!ws) return;
    try { ws.destroy(); } catch { /* already torn down with its container */ }
    ws = null;
    regions = null;
}
