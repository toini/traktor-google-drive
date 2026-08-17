// One audio element for the whole app. Two tracks playing at once used to be
// possible because every AudioTrack owned its own Audio(); with a single
// element it is unrepresentable.

let audio = null;
let dotnet = null;
let currentId = null;

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
    audio.addEventListener('timeupdate', () => reportProgress(false));
    audio.addEventListener('durationchange', () => reportProgress(true));
    audio.addEventListener('seeked', () => reportProgress(true));
    audio.addEventListener('playing', () => report('playing'));
    audio.addEventListener('pause', () => report('paused'));
    audio.addEventListener('ended', () => report('ended'));
    audio.addEventListener('error', () => {
        // MEDIA_ERR_SRC_NOT_SUPPORTED (4) is what a 401/403 body looks like here.
        report(audio.error?.code === 4 ? 'unauthorized' : 'error');
    });
}

export async function play(fileId, url) {
    if (!audio) return;
    if (currentId !== fileId) {
        audio.src = url;
        currentId = fileId;
    }
    try {
        await audio.play();
    } catch (e) {
        // Autoplay rejection or a load failure — surface it rather than hanging
        // the button in a permanent "loading" state.
        report(e?.name === 'NotAllowedError' ? 'blocked' : 'error');
    }
}

export function pause() {
    audio?.pause();
}

export function stop() {
    if (!audio) return;
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    currentId = null;
    report('idle');
}

export function seek(seconds) {
    if (!audio || !isFinite(seconds)) return;
    audio.currentTime = Math.max(0, seconds);
}

export function position() {
    return audio ? { current: audio.currentTime, duration: audio.duration || 0 } : null;
}

export function dispose() {
    stop();
    audio = null;
    dotnet = null;
}
