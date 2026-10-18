// State that survives a reload: which sidebar folders are open, and where the
// player was. PlaybackMemory.cs owns the playback record's shape; this module
// only keeps its position current, because the position has to be saved on
// pagehide, where an async round trip into .NET cannot be relied on.

const PLAYBACK_KEY = 'playback:v1';
const SAVE_EVERY_MS = 5000;

/** Whether a sidebar folder is open right now, or null if it is not rendered. */
export function folderOpen(name) {
    const el = document.querySelector(`details[data-folder="${CSS.escape(name)}"]`);
    return el ? el.open : null;
}

const read = () => {
    try {
        const record = JSON.parse(localStorage.getItem(PLAYBACK_KEY) ?? 'null');
        return record && record.v === 1 ? record : null;
    } catch {
        return null;
    }
};

const write = (record) => {
    try { localStorage.setItem(PLAYBACK_KEY, JSON.stringify(record)); } catch { /* storage full or blocked */ }
};

export function readPlayback() {
    try { return localStorage.getItem(PLAYBACK_KEY); } catch { return null; }
}

export function forgetPlayback() {
    fileId = null;
    try { localStorage.removeItem(PLAYBACK_KEY); } catch { /* nothing to forget */ }
}

let audio = null;
let fileId = null;
let lastSave = 0;
// A restored track starts at 0 until the seek to its saved position lands; a
// save in between would overwrite the very position being restored.
let armed = false;

/** Saves the element's position into the record, at most every 5 s unless forced. */
function save(force) {
    if (!audio || !fileId || !armed) return;
    if (audio.readyState < 1) return; // HAVE_METADATA: currentTime is meaningless before it
    const now = Date.now();
    if (!force && now - lastSave < SAVE_EVERY_MS) return;

    const record = read();
    if (!record || record.fileId !== fileId) return;
    record.position = audio.ended ? 0 : audio.currentTime;
    if (isFinite(audio.duration) && audio.duration > 0) record.duration = audio.duration;
    record.savedAt = now;
    write(record);
    lastSave = now;
}

function watch(element) {
    if (!element || element === audio) return;
    audio = element;
    audio.addEventListener('timeupdate', () => save(false));
    audio.addEventListener('pause', () => save(true));
    audio.addEventListener('ended', () => save(true));
    audio.addEventListener('seeked', () => { armed = true; save(true); });
    audio.addEventListener('playing', () => { armed = true; });
    window.addEventListener('pagehide', () => save(true));
}

/**
 * Starts remembering a track. `restored` means the element is being cued to a
 * saved position, so saving waits for that seek (or for playback) to land.
 */
export function rememberPlayback(json, element, restored) {
    watch(element);
    const record = JSON.parse(json);
    fileId = record.fileId;
    armed = !restored;
    lastSave = 0;
    if (!restored) write(record);
}

/** Position from a Cast device, which this page's element knows nothing about. */
export function savePlaybackPosition(seconds) {
    const record = read();
    if (!record || record.fileId !== fileId || !isFinite(seconds)) return;
    record.position = seconds;
    record.savedAt = Date.now();
    write(record);
}
