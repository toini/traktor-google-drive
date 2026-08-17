// Google Identity Services token flow. Tokens are ~1h and there is no refresh
// token in the implicit flow, so we persist the expiry alongside the token and
// treat "expired" as "signed out" rather than letting it surface as opaque 401s.

const TOKEN_KEY = 'access_token';
const EXPIRY_KEY = 'access_token_expires_at';
const CLIENT_ID = '219145501841-n6pki0jbvkue0u3vusmnguld6m4fugp9.apps.googleusercontent.com';
const SCOPE = 'https://www.googleapis.com/auth/drive.readonly';

// Treat a token as expired slightly early so a request started now does not
// land after the real expiry.
const SKEW_SECONDS = 60;

function store(tokenResponse) {
    const expiresIn = Number(tokenResponse.expires_in ?? 3600);
    sessionStorage.setItem(TOKEN_KEY, tokenResponse.access_token);
    sessionStorage.setItem(EXPIRY_KEY, String(Date.now() + expiresIn * 1000));
}

window.authGetToken = () => {
    const token = sessionStorage.getItem(TOKEN_KEY);
    if (!token) return null;

    const expiresAt = Number(sessionStorage.getItem(EXPIRY_KEY) ?? 0);
    // A token seeded without an expiry (tests, manual injection) is trusted.
    if (expiresAt && Date.now() > expiresAt - SKEW_SECONDS * 1000) {
        window.authSignOut();
        return null;
    }
    return token;
};

window.authSignOut = () => {
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(EXPIRY_KEY);
};

window.authTokenExpiresAt = () => Number(sessionStorage.getItem(EXPIRY_KEY) ?? 0) || null;

// Resolves with the token, or null if the user dismissed the picker. Replaces
// the old fire-and-forget + poll-sessionStorage-100-times approach.
const requestToken = (options) =>
    new Promise((resolve) => {
        if (!window.google?.accounts?.oauth2) {
            console.error('Google Identity Services not loaded');
            resolve(null);
            return;
        }

        const client = google.accounts.oauth2.initTokenClient({
            client_id: CLIENT_ID,
            scope: SCOPE,
            callback: (tokenResponse) => {
                if (tokenResponse?.access_token) {
                    store(tokenResponse);
                    resolve(tokenResponse.access_token);
                } else {
                    console.error('Token response invalid:', tokenResponse);
                    resolve(null);
                }
            },
            error_callback: (err) => {
                console.error('Google login failed:', err);
                resolve(null);
            },
        });

        client.requestAccessToken(options);
    });

window.googleLogin = () => requestToken();

// A Cast device holds the token inside its media URL, so a set longer than the
// ~1h token lifetime needs a replacement minted without a consent popup.
window.authRefreshToken = () => requestToken({ prompt: '' });
