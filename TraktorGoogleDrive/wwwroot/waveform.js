// Waveform peaks for very large uncompressed audio, without downloading it.
//
// A 1-2 GB WAV cannot be decoded in the browser: decodeAudioData expands PCM to
// Float32, so a 1.4 GB file needs ~2.8 GB of RAM and kills the tab. But WAV is
// uncompressed, so the amplitude at any point is readable straight from the
// bytes at that offset — a few hundred small range reads (~1 MB total) give a
// pixel-accurate picture. Peaks are then cached so it only ever happens once.

const DB_NAME = 'traktor-waveforms';
const STORE = 'peaks';
const VERSION = 1;
const BUCKETS = 400;      // horizontal resolution
const WINDOW_BYTES = 2048; // bytes sampled per bucket
const CONCURRENCY = 12;

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

/* ---------- WAV parsing ---------- */

async function readRange(url, start, end) {
    const res = await fetch(url, { headers: { Range: `bytes=${start}-${end}` } });
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

/* ---------- public API ---------- */

export async function computePeaks(url, fileId, dotNetRef) {
    const cached = await cacheGet(fileId);
    if (cached?.version === VERSION) return cached.peaks;

    const header = parseHeader(await readRange(url, 0, 4095));
    if (header.format !== 1 && header.format !== 3) throw new Error(`unsupported WAV format ${header.format}`);

    const frame = (header.bits / 8) * header.channels;
    const usable = Math.max(0, header.dataSize - WINDOW_BYTES);
    const offsets = Array.from({ length: BUCKETS }, (_, i) => {
        const raw = header.dataOffset + Math.floor((usable * i) / BUCKETS);
        return raw - (raw % frame); // stay on a frame boundary or samples shear
    });

    let done = 0;
    const peaks = await mapLimit(offsets, CONCURRENCY, async (start) => {
        const view = await readRange(url, start, start + WINDOW_BYTES - 1);
        const p = peakOf(view, header);
        if (++done % 40 === 0) dotNetRef?.invokeMethodAsync('OnPeakProgress', done / offsets.length);
        return p;
    });

    await cachePut(fileId, { version: VERSION, peaks });
    return peaks;
}

export function draw(canvas, peaks, position) {
    if (!canvas || !peaks?.length) return;

    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    if (w === 0 || h === 0) return;

    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);

    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    const barWidth = w / peaks.length;
    const mid = h / 2;
    const playedUpTo = w * (position ?? 0);

    // Normalise: DJ recordings are mastered loud, but a quiet one should still
    // fill the box rather than render as a flat line.
    const loudest = Math.max(...peaks, 0.01);

    for (let i = 0; i < peaks.length; i++) {
        const x = i * barWidth;
        const amp = Math.max(1, (peaks[i] / loudest) * mid);
        ctx.fillStyle = x + barWidth <= playedUpTo ? '#ff6600' : '#4a5058';
        ctx.fillRect(x, mid - amp, Math.max(barWidth - 0.5, 0.5), amp * 2);
    }
}

export function fractionFromClick(canvas, clientX) {
    const rect = canvas.getBoundingClientRect();
    return Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
}
