// One audio element for the whole app. Two tracks playing at once used to be
// possible because every AudioTrack owned its own Audio(); with a single
// element it is unrepresentable.

let audio = null;
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
    audio = new Audio();
    audio.preload = 'metadata';

    audio.addEventListener('loadstart', () => { switching = false; report('loading'); });
    audio.addEventListener('canplay', () => { report('ready'); reportProgress(true); });
    audio.addEventListener('timeupdate', () => {
        if (endAt != null && audio.currentTime >= endAt) {
            endAt = null; // once per track
            report('previewEnd');
        }
        reportProgress(false);
    });
    // Seeking past the end is a deliberate "let me hear this bit": the seek wins
    // and the track plays on, rather than jumping straight to the next one.
    audio.addEventListener('seeking', () => {
        if (endAt != null && audio.currentTime > endAt) endAt = null;
    });
    audio.addEventListener('loadedmetadata', () => {
        if (pendingStart != null) {
            audio.currentTime = pendingStart;
            pendingStart = null;
        }
    });
    audio.addEventListener('durationchange', () => reportProgress(true));
    audio.addEventListener('seeked', () => reportProgress(true));
    audio.addEventListener('playing', () => { retriedAuth = false; report('playing'); });
    // Switching tracks pauses the old one on the way; that is not the user pausing.
    audio.addEventListener('pause', () => { if (!switching) report('paused'); });
    audio.addEventListener('ended', () => report('ended'));
    audio.addEventListener('error', () => { onError(); });
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
function reload(at, play) {
    if (!audio || !currentUrl) return;
    pendingStart = at > 0 ? at : null;
    wantPlaying = play;
    load(currentUrl);
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
    const at = pendingStart ?? audio.currentTime;
    const wasPlaying = wantPlaying;

    // Also how an undecodable file (MEDIA_ERR_SRC_NOT_SUPPORTED, the same code a
    // 401 body gives) is told apart from an expired token: the proxy says 200.
    const status = await probe(url);
    if (gen !== generation) return; // another load started meanwhile
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
        pendingStart = startAt > 0 ? startAt : null;
        endAt = stopAt ?? null;
        currentUrl = url;
        currentId = fileId;
        stalled = null;
        retriedAuth = false;
        load(url);
    } else if (stalled) {
        resume();
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
    load(url);
    // Registered after init's listener, so the pending seek has been applied.
    audio.addEventListener('loadedmetadata', () => {
        if (currentId === fileId && audio.paused) report('paused');
    }, { once: true });
}

export function pause() {
    wantPlaying = false;
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
    report('idle');
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
    dotnet = null;
}
