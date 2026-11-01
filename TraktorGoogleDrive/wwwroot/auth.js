// Google sign-in, two ways (ADR 20261101-38).
//
// Code flow (the server has GoogleOAuth__ClientSecret): sign in once with GIS's
// authorization-code popup. The server keeps the refresh token, sealed in an
// HttpOnly cookie this script cannot read, and every later access token comes
// from POST /api/auth/token, with no popup. A popup appears again only when the
// refresh token is revoked or expired, and only from a click.
//
// Token model (the fallback while the server has no secret): GIS's implicit
// token client, as before. No refresh token: renewal opens a popup, which
// browsers allow only inside a click, hence the timers and the pointer hook.
//
// Either way the access token (~1h) sits in sessionStorage with its expiry, and
// the rest of the app sees the same API: authGetToken, authRefreshToken,
// authSyncProxyCookie, authTokenExpiresAt, authSignOut, googleLogin.

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
// Same exposure as sessionStorage: page script can read both. With a server
// session the proxy prefers the server's own token and this is the fallback.
const PROXY_COOKIE = 'drive_token';
const PROXY_PATH = '/api/proxy/';

// Token model: start renewing this long before expiry. Long enough that a few
// blocked tries still leave time for one inside a click.
const RENEW_LEAD_MS = 10 * 60 * 1000;
// Code flow: the server refreshes once its cached token has under 5 minutes
// left (SessionTokens.RefreshLead), so asking at 4 always gets a fresh hour.
const SERVER_RENEW_LEAD_MS = 4 * 60 * 1000;
const RENEW_CHECK_MS = 30 * 1000;
// Spacing between attempts, so a blocked popup is not retried every tick.
const UNATTENDED_RETRY_MS = 2 * 60 * 1000;
const GESTURE_RETRY_MS = 60 * 1000;
// GIS calls neither callback while a popup sits open; never wait forever.
const REQUEST_TIMEOUT_MS = 60 * 1000;

// Set after a code-flow sign-in that left a session cookie. Only a hint (the
// cookie is HttpOnly): it says which way to renew, and the server has the final word.
const SESSION_HINT_KEY = 'auth:server-session';
const AUTH_API = '/api/auth/';

const expiresAt = () => Number(sessionStorage.getItem(EXPIRY_KEY) ?? 0);

/* ---------- which flow ---------- */

// The client dev server (and an old host) has no /api/auth/config and answers
// with index.html: anything but JSON saying codeFlow: true means the token model.
let codeFlowOn = false;
const codeFlowReady = fetch(`${AUTH_API}config`, { headers: { Accept: 'application/json' }, cache: 'no-store' })
    .then((r) => (r.ok && (r.headers.get('content-type') ?? '').includes('application/json') ? r.json() : null))
    .then((c) => { codeFlowOn = c?.codeFlow === true; return codeFlowOn; })
    .catch(() => false);

const hasServerSession = () => {
    try { return localStorage.getItem(SESSION_HINT_KEY) === '1'; } catch { return false; }
};
const setServerSession = (on) => {
    try {
        if (on) localStorage.setItem(SESSION_HINT_KEY, '1');
        else localStorage.removeItem(SESSION_HINT_KEY);
    } catch { /* private mode: the hint just stays off */ }
};

// True while a click's transient activation lasts: the only time a popup may open.
const userActive = () => navigator.userActivation?.isActive === true;

/* ---------- storage ---------- */

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
        if (codeFlowOn && hasServerSession()) {
            // Not signed out: the server has a fresh one. Callers that can wait
            // use authEnsureToken; this one is synchronous, so start the renewal.
            window.authCheckRenewal();
            return null;
        }
        window.authSignOut();
        return null;
    }
    return token;
};

/** Forgets the token in this tab. Not a logout: the server session stays (see authLogout). */
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

/* ---------- code flow: the server ---------- */

const postAuth = (path, body) =>
    fetch(AUTH_API + path, {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        // The server refuses all three POSTs without this header (CSRF).
        headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
        body: JSON.stringify(body ?? {}),
    });

// After the server said there is no session, stop asking until a sign-in.
let serverSignedOut = false;

/**
 * A token from the server's session, or null. `force`: Drive just refused the
 * one we have, so the server must not hand back its cached copy.
 */
async function tokenFromServer(force) {
    let response;
    try {
        response = await postAuth('token', { force: !!force });
    } catch {
        return null; // offline: keep the session, try again on the next tick
    }
    if (response.ok) {
        const json = await response.json().catch(() => null);
        if (!json?.access_token) return null;
        store(json);
        setServerSession(true);
        serverSignedOut = false;
        return json.access_token;
    }
    if (response.status === 401 || response.status === 403) {
        // signed_out, invalid_grant (revoked, or unused for six months) or
        // not_allowed. The cookie is gone; the next click may sign in again.
        const json = await response.json().catch(() => null);
        console.warn('Server session ended:', json?.error ?? response.status);
        setServerSession(false);
        serverSignedOut = true;
        return null;
    }
    if (response.status === 503) codeFlowOn = false; // switched off since load
    return null; // 429 / 502: transient
}

/**
 * Google's authorization-code popup, then the server's exchange. Resolves with
 * the access token, or null if the popup was closed, blocked or refused.
 */
const codeLogin = () =>
    new Promise((resolve) => {
        if (!window.google?.accounts?.oauth2?.initCodeClient) {
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
        const state = Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, '0')).join('');

        const client = google.accounts.oauth2.initCodeClient({
            client_id: CLIENT_ID,
            scope: SCOPE,
            ux_mode: 'popup',
            state,
            callback: async (codeResponse) => {
                if (!codeResponse?.code || codeResponse.state !== state) {
                    console.error('Code response invalid:', codeResponse?.error ?? 'state mismatch');
                    settle(null);
                    return;
                }
                try {
                    const response = await postAuth('code', { code: codeResponse.code });
                    const json = await response.json().catch(() => null);
                    if (!response.ok || !json?.access_token) {
                        console.warn('Sign-in refused by the server:', json?.error ?? response.status);
                        settle(null);
                        return;
                    }
                    store(json);
                    // No refresh token (Google returns one only for a new grant):
                    // this sign-in works as the token model did, for its hour.
                    setServerSession(json.persistent === true);
                    serverSignedOut = false;
                    settle(json.access_token);
                } catch (err) {
                    console.warn('Sign-in exchange failed:', err);
                    settle(null);
                }
            },
            error_callback: (err) => {
                console.warn('Google code request failed:', err?.type ?? err);
                settle(null);
            },
        });

        client.requestCode();
    });

/* ---------- token model: GIS's token client ---------- */

// Resolves with the token, or null if the user dismissed the picker or the
// browser refused the popup.
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

/* ---------- the API the app uses ---------- */

/** The sign-in button. Called from a click, so either popup may open. */
window.googleLogin = async () => {
    // Fetched at load, so normally settled long before a click; awaiting a
    // settled promise does not cost the click its activation.
    if (await codeFlowReady) return codeLogin();
    return requestToken();
};

// One renewal at a time, whoever asks: the timer, a click, the Cast sender and
// the player's 401 recovery all share it, so they never stack popups or requests.
let renewing = null;
const once = (work) => {
    renewing ??= work().finally(() => { renewing = null; });
    return renewing;
};

/**
 * A fresh token, or null. Code flow: from the server, forced past its cache
 * (callers ask because Drive refused the token they had), never a popup unless
 * there is no session and this runs inside a click. Token model: a popup
 * without a consent screen, which outside a click the browser normally blocks.
 * Callers must cope with null.
 */
window.authRefreshToken = () => once(async () => {
    if (await codeFlowReady) {
        if (hasServerSession() && !serverSignedOut) {
            const token = await tokenFromServer(true);
            if (token || hasServerSession()) return token; // got one, or a transient failure
        }
        // No session (revoked, expired, or a tab from before the code flow).
        // Only a click may open Google's popup; a timer or a 401 must not.
        return userActive() ? codeLogin() : null;
    }
    return requestToken({ prompt: '' });
});

/**
 * The current token, renewing from the server first when it has run out. What
 * the Blazor side awaits (AuthService.GetTokenAsync), so a reload or a new tab
 * after the hour picks up a token silently instead of landing on /login.
 */
window.authEnsureToken = async () => {
    const token = window.authGetToken();
    if (token) return token;
    if (!(await codeFlowReady) || serverSignedOut) return null;
    return once(() => tokenFromServer(false));
};

/**
 * Signs out for real: the server revokes the refresh token at Google (which
 * ends the grant, so every device's session) and clears its cookie.
 */
window.authLogout = async () => {
    if (await codeFlowReady) {
        try { await postAuth('logout'); } catch { /* still sign this tab out */ }
    }
    setServerSession(false);
    serverSignedOut = true;
    window.authSignOut();
};

/* ---------- proactive renewal ---------- */

let lastUnattendedTry = 0;
let lastGestureTry = 0;

const serverMode = () => codeFlowOn && hasServerSession() && !serverSignedOut;

const dueForRenewal = () => {
    const until = expiresAt();
    const lead = serverMode() ? SERVER_RENEW_LEAD_MS : RENEW_LEAD_MS;
    // No token, or one with no known expiry: nothing to renew.
    return !!sessionStorage.getItem(TOKEN_KEY) && until > 0 && until - Date.now() < lead;
};

/**
 * Renews the token while it still works, so playback never meets the expiry.
 * Exposed so tests can run a check without waiting for the timer.
 */
window.authCheckRenewal = () => {
    if (!dueForRenewal() || renewing) return Promise.resolve(null);
    // Code flow: a plain request, so every tick may try; no popup, ever.
    if (serverMode()) return once(() => tokenFromServer(false));
    // Code flow without a session: nothing silent to try. The next click or 401
    // decides (authRefreshToken); a timer never opens Google's popup.
    if (codeFlowOn) return Promise.resolve(null);
    if (Date.now() - lastUnattendedTry < UNATTENDED_RETRY_MS) return Promise.resolve(null);
    lastUnattendedTry = Date.now();
    // Looked up at call time, so a stub replaces it everywhere.
    return window.authRefreshToken();
};

// Token model only. Inside a click the popup is allowed; once consent exists it
// opens and closes by itself. Pointer only: a popup opening mid-keystroke
// would swallow typing.
document.addEventListener('pointerdown', () => {
    if (codeFlowOn || !dueForRenewal() || renewing) return;
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
