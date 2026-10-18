// Browser-side helpers for remote control (ADR 20261018-63): who this tab is,
// what to call it, and whether it is visible. The connection itself lives in
// RemoteService.cs.

const ENABLED_KEY = 'remote:enabled';
const DEVICE_KEY = 'remote:device-id';
const NAME_KEY = 'remote:device-name';
const TAB_KEY = 'remote:tab-id';

const get = (storage, key) => { try { return storage.getItem(key); } catch { return null; } };
const set = (storage, key, value) => { try { storage.setItem(key, value); } catch { /* blocked */ } };

const uuid = () => (crypto.randomUUID?.() ?? Math.random().toString(36).slice(2) + Date.now().toString(36));

/** Off until switched on per browser, while the feature is new. */
export function isEnabled() {
    return get(localStorage, ENABLED_KEY) === '1';
}

export function setEnabled(on) {
    set(localStorage, ENABLED_KEY, on ? '1' : '0');
}

/**
 * One id per browser (shared by its tabs) and one per tab. sessionStorage is
 * copied when a tab is duplicated, so a duplicated tab would share its parent's
 * tab id; the hub keys on the connection, so that only affects the label.
 */
export function identity() {
    let deviceId = get(localStorage, DEVICE_KEY);
    if (!deviceId) { deviceId = uuid(); set(localStorage, DEVICE_KEY, deviceId); }
    let tabId = get(sessionStorage, TAB_KEY);
    if (!tabId) { tabId = uuid(); set(sessionStorage, TAB_KEY, tabId); }
    return { deviceId, tabId, name: get(localStorage, NAME_KEY) || defaultName() };
}

/** "Mac · Chrome", "Android · Chrome". The browser cannot know "MacBook". */
export function defaultName() {
    const ua = navigator.userAgent;
    const platform =
        /iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) ? 'iPad'
        : /iPhone/.test(ua) ? 'iPhone'
        : /Android/.test(ua) ? (/Mobile/.test(ua) ? 'Android phone' : 'Android tablet')
        : /Mac OS X|Macintosh/.test(ua) ? 'Mac'
        : /Windows/.test(ua) ? 'Windows'
        : /CrOS/.test(ua) ? 'Chromebook'
        : /Linux/.test(ua) ? 'Linux'
        : 'Browser';
    const browser =
        /Edg\//.test(ua) ? 'Edge'
        : /Firefox\//.test(ua) ? 'Firefox'
        : /Chrome\//.test(ua) ? 'Chrome'
        : /Safari\//.test(ua) ? 'Safari'
        : 'Browser';
    return `${platform} · ${browser}`;
}

let visibilityHandler = null;

/** Reports visibility changes to .NET; returns the current visibility. */
export function watchVisibility(dotnetRef) {
    if (visibilityHandler) document.removeEventListener('visibilitychange', visibilityHandler);
    visibilityHandler = () =>
        dotnetRef.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible')
            .catch(() => { /* app torn down */ });
    document.addEventListener('visibilitychange', visibilityHandler);
    return document.visibilityState === 'visible';
}

/**
 * Idle thresholds, in ms. Overridable for the e2e suite only, so a test does
 * not have to wait 15 minutes to see a player go away.
 */
export function idleTimeouts() {
    const read = (key, fallback) => {
        const v = Number(get(localStorage, key));
        return Number.isFinite(v) && v > 0 ? v : fallback;
    };
    return {
        playerMs: read('remote:test-player-idle-ms', 15 * 60 * 1000),
        controllerMs: read('remote:test-controller-idle-ms', 60 * 1000),
    };
}
