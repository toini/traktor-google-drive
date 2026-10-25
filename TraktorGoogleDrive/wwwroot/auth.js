// Google Identity Services token flow. Tokens are ~1h and there is no refresh
// token in the implicit flow, so we persist the expiry alongside the token and
// treat "expired" as "signed out" rather than letting it surface as opaque 401s.

const TOKEN_KEY = 'access_token';
const EXPIRY_KEY = 'access_token_expires_at';
const CLIENT_ID = '219145501841-n6pki0jbvkue0u3vusmnguld6m4fugp9.apps.googleusercontent.com';
// drive.appdata: the app's own hidden folder in Drive, where Discover keeps its
// event logs (ADR 20261025-54 §8). It cannot see or touch any other file.
const SCOPE = 'https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/drive.appdata';

// Treat a token as expired slightly early so a request started now does not
// land after the real expiry.
const SKEW_SECONDS = 60;

// The audio proxy reads the token from this cookie, so <audio> fetches every
// slice with whatever token is current, and no URL (or Cloud Run request log)
// ever holds it. Scoped to the proxy path so no other request carries it.
// Same exposure as sessionStorage: page script can read both.
const PROXY_COOKIE = 'drive_token';
const PROXY_PATH = '/api/proxy/';

// Start renewing this long before expiry. Long enough that a few blocked tries
// still leave time for one inside a click.
const RENEW_LEAD_MS = 10 * 60 * 1000;
const RENEW_CHECK_MS = 30 * 1000;
// Spacing between attempts, so a blocked popup is not retried every tick.
const UNATTENDED_RETRY_MS = 2 * 60 * 1000;
const GESTURE_RETRY_MS = 60 * 1000;
// GIS calls neither callback while a popup sits open; never wait forever.
const REQUEST_TIMEOUT_MS = 60 * 1000;

const expiresAt = () => Number(sessionStorage.getItem(EXPIRY_KEY) ?? 0);

function writeProxyCookie(token, untilMs) {
    const secure = location.protocol === 'https:' ? '; Secure' : '';
    if (!token) {
        document.cookie = `${PROXY_COOKIE}=; Path=${PROXY_PATH}; Max-Age=0; SameSite=Strict${secure}`;
        return;
    }
    // A token seeded without an expiry (tests, manual injection) gets the full hour.
    const maxAge = untilMs ? Math.max(0, Math.floor((untilMs - Date.now()) / 1000)) : 3600;
    document.cookie = `${PROXY_COOKIE}=${encodeURIComponent(token)}; Path=${PROXY_PATH}; Max-Age=${maxAge}; SameSite=Strict${secure}`;
}

function store(tokenResponse) {
    const expiresIn = Number(tokenResponse.expires_in ?? 3600);
    const until = Date.now() + expiresIn * 1000;
    sessionStorage.setItem(TOKEN_KEY, tokenResponse.access_token);
    sessionStorage.setItem(EXPIRY_KEY, String(until));
    writeProxyCookie(tokenResponse.access_token, until);
}

window.authGetToken = () => {
    const token = sessionStorage.getItem(TOKEN_KEY);
    if (!token) return null;

    const until = expiresAt();
    // A token seeded without an expiry (tests, manual injection) is trusted.
    if (until && Date.now() > until - SKEW_SECONDS * 1000) {
        window.authSignOut();
        return null;
    }
    return token;
};

window.authSignOut = () => {
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(EXPIRY_KEY);
    writeProxyCookie(null);
};

window.authTokenExpiresAt = () => expiresAt() || null;

/**
 * Puts the stored token into the proxy cookie. Run at load (a reload, or a
 * token seeded straight into sessionStorage) and before every local play, so
 * the cookie can never lag the token a sign-in or a renewal just stored.
 */
window.authSyncProxyCookie = () => {
    const token = window.authGetToken();
    writeProxyCookie(token, token ? expiresAt() : 0);
    return token != null;
};

// Resolves with the token, or null if the user dismissed the picker or the
// browser refused the popup. Replaces the old fire-and-forget +
// poll-sessionStorage-100-times approach.
const requestToken = (options) =>
    new Promise((resolve) => {
        if (!window.google?.accounts?.oauth2) {
            console.error('Google Identity Services not loaded');
            resolve(null);
            return;
        }

        let settled = false;
        const settle = (value) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            resolve(value);
        };
        const timer = setTimeout(() => settle(null), REQUEST_TIMEOUT_MS);

        const client = google.accounts.oauth2.initTokenClient({
            client_id: CLIENT_ID,
            scope: SCOPE,
            callback: (tokenResponse) => {
                if (tokenResponse?.access_token) {
                    store(tokenResponse);
                    settle(tokenResponse.access_token);
                } else {
                    console.error('Token response invalid:', tokenResponse);
                    settle(null);
                }
            },
            // popup_failed_to_open is what a renewal outside a click gets: the
            // token flow always opens a popup, and browsers block popups that no
            // user gesture started.
            error_callback: (err) => {
                console.warn('Google token request failed:', err?.type ?? err);
                settle(null);
            },
        });

        client.requestAccessToken(options);
    });

window.googleLogin = () => requestToken();

// One renewal at a time, whoever asks: the scheduler below, a click, the Cast
// sender and the player's 401 recovery all share it, so they never stack popups.
let renewing = null;

/**
 * A fresh token without a consent screen (prompt ''), or null. Google's own
 * guidance is to call this from a user gesture: outside one the browser
 * normally blocks the popup and this resolves null. Callers must cope.
 */
window.authRefreshToken = () => {
    renewing ??= requestToken({ prompt: '' }).finally(() => { renewing = null; });
    return renewing;
};

/* ---------- proactive renewal ---------- */

let lastUnattendedTry = 0;
let lastGestureTry = 0;

const dueForRenewal = () => {
    const until = expiresAt();
    // No token, or one with no known expiry: nothing to renew.
    return !!sessionStorage.getItem(TOKEN_KEY) && until > 0 && until - Date.now() < RENEW_LEAD_MS;
};

/**
 * Renews the token while it still works, so playback never meets the expiry.
 * Exposed so tests can run a check without waiting for the timer.
 */
window.authCheckRenewal = () => {
    if (!dueForRenewal() || renewing) return Promise.resolve(null);
    if (Date.now() - lastUnattendedTry < UNATTENDED_RETRY_MS) return Promise.resolve(null);
    lastUnattendedTry = Date.now();
    // Looked up at call time, so a stub replaces it everywhere.
    return window.authRefreshToken();
};

// The reliable path: inside a click the popup is allowed. Once consent exists
// it opens and closes by itself. Pointer only: a popup opening mid-keystroke
// would swallow typing.
document.addEventListener('pointerdown', () => {
    if (!dueForRenewal() || renewing) return;
    if (Date.now() - lastGestureTry < GESTURE_RETRY_MS) return;
    lastGestureTry = Date.now();
    window.authRefreshToken();
}, { capture: true });

setInterval(() => window.authCheckRenewal(), RENEW_CHECK_MS);
// Background tabs throttle timers to about once a minute; catch up on return.
document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') window.authCheckRenewal();
});

window.authSyncProxyCookie();
