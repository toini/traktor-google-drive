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

    audio.addEventListener('loadstart', () => report('loading'));
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
    audio.addEventListener('pause', () => report('paused'));
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
    audio.src = currentUrl;
    if (play) {
        audio.play().catch(playFailed);
    } else {
        audio.addEventListener('loadedmetadata', () => report('paused'), { once: true });
    }
}

async function onError() {
    const id = currentId;
    const url = currentUrl;
    if (!id || !url) { report('error'); return; }

    // Where it stopped, before anything reloads: the position to resume at.
    const at = pendingStart ?? audio.currentTime;
    const wasPlaying = wantPlaying;

    const status = await probe(url);
    if (currentId !== id) return; // another track started meanwhile
    if (status !== 401 && status !== 403) { report('error'); return; }

    if (!retriedAuth) {
        retriedAuth = true;
        const token = await (window.authRefreshToken?.() ?? Promise.resolve(null));
        if (currentId !== id) return;
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
 * Plays `fileId`. `startAt`/`stopAt` apply only when this loads a new file: a
 * resume of the same file keeps its position and its preview bound.
 */
export async function play(fileId, url, startAt, stopAt) {
    if (!audio) return;
    if (currentId !== fileId) {
        pendingStart = startAt > 0 ? startAt : null;
        endAt = stopAt ?? null;
        audio.src = url;
        currentUrl = url;
        currentId = fileId;
        stalled = null;
        retriedAuth = false;
    } else if (stalled) {
        resume();
        return;
    }
    wantPlaying = true;
    try {
        await audio.play();
    } catch (e) {
        playFailed(e);
    }
}

/**
 * Autoplay rejection or a load failure — surface it rather than hanging the
 * button in a permanent "loading" state. A failed load also fires the
 * element's error event, whose handler knows whether it was the token; and an
 * AbortError only means a newer load replaced this one.
 */
function playFailed(e) {
    if (e?.name === 'NotAllowedError') { report('blocked'); return; }
    if (audio?.error || e?.name === 'AbortError') return;
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
    audio.src = url;
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
