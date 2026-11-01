# Server-held refresh token: no more sign-in popups on an idle tab

Date: 2026-11-01

## Status

**Proposed**, implemented on `toni/feat/server-refresh-token` in the same PR as
this ADR, on top of `master`. Toni chose the direction (option A below) before
implementation. It reverses the part of [adr-001](../adr-001-tv-playback.md) that
rejected a server-side authorization-code flow, and it builds on the Cast media
tickets ADR ([20261025-71](20261025-71-cast-media-tickets.md)) and the
remote-control ADR ([20261018-63](20261018-63-remote-control.md)).

The code ships **switched off**. It turns on only when the server has both
`GoogleOAuth__ClientSecret` and `AuthSession__CookieSecret`. Until then the page
behaves exactly as it does today (see "Feature flag").

## Context

- `wwwroot/auth.js` uses Google Identity Services' **token model**
  (`initTokenClient`). It gives an access token (~1 h) and **no refresh token**.
  Renewing means `requestAccessToken({prompt: ''})`, which always opens a popup.
  Browsers allow a popup only inside a click.
- So the page renews on a timer (blocked unless the site may open popups), inside
  the next click (pointer hook), and on a 401 (PR 6's single-flight renewal, with
  pause and "Sign in again" when it fails). If a tab stays open and idle, Toni
  sometimes gets **a Google login popup with no click behind it**. That happens
  when popups are allowed, or when the timer fires inside a stray activation.
  Otherwise playback stops at the hour.
- The token reaches the server three ways today:
  - the `drive_token` cookie, which auth.js writes for `/api/proxy/` (PR 6);
  - the `Authorization` header on Cast ticket mint and renew (PR 15);
  - the remote-control ticket exchange (`RemoteAuth`: tokeninfo plus `about.get`,
    allowlist `RemoteAuth__AllowedUsers`).
- The scopes are `drive.readonly` and `drive.appdata` (Discover's event log).
- The OAuth client `219145501841-n6pk…` lives in a **different GCP project**
  (219145501841) from the Cloud Run service (`traktor-toni-2025`).
- The service runs `max-instances=1`. There is no database, and
  `RemoteAuth__TicketSecret` already comes from Secret Manager.

## Options

| Option | What it costs | What it gives up |
|---|---|---|
| **A. The server keeps a refresh token** (chosen by Toni). GIS **code** flow; the server exchanges the code with the client secret and keeps the refresh token sealed in an HttpOnly cookie. The page gets each access token from `POST /api/auth/token`. | A client secret and a cookie key in Secret Manager. The consent screen must be "In production" (refresh tokens die after 7 days in Testing). About 400 lines of server code. | The server now holds a long-lived Drive credential, though only inside a cookie it can decrypt. Logout revokes the whole grant, on every device. |
| B. Keep the token model, renew earlier and more often. | Nothing new. | Does not fix it. Without a click the popup is either blocked or shown, and being shown is the bug. |
| C. GIS `prompt: 'none'` in a hidden iframe (the old `gapi.auth2` trick). | No server change. | GIS does not support it, and third-party-cookie blocking breaks it in Chrome and Safari. |
| D. The code flow, with the refresh token stored in a database (Firestore) keyed by a session id. | Server-side revocation and a list of sessions. | A database, its IAM, and its cost. That is a lot for one user and a few devices. A sealed cookie needs none of it. |
| E. The code flow, with the refresh token held by the page (localStorage). | No cookie work. | The refresh token is readable by any script on the page. One XSS gives the attacker months of Drive access instead of an hour. Google also discourages it for confidential clients. |

## Decision

**Option A**, with the refresh token **sealed in a cookie** (not option D's
database). It removes the popup at its root: there is nothing to open once the
server can mint access tokens on its own. The cookie keeps the "no database"
property this service has today.

### 1. Sign-in (`POST /api/auth/code`)

- `googleLogin()` asks `/api/auth/config` (fetched at load) whether the code flow
  is on. If it is, it opens GIS `initCodeClient({ux_mode: 'popup', scope, state})`
  with the same two scopes. GIS's popup mode makes `postmessage` the redirect URI.
  The code is meant to be exchanged server-side for an access token and a refresh
  token. Only a real sign-in can prove that Google returns the refresh token here
  (see "Not tested" and the `persistent: false` path below).
  No `openid`/`email` scope is added: the server identifies the user the way
  `RemoteAuth` already does (tokeninfo plus `about.get`).
- auth.js checks the `state` it generated, then POSTs `{code}` to
  `/api/auth/code`. The server exchanges the code at Google's token endpoint with
  `client_secret` and `redirect_uri=postmessage`.
- **Allowlist at the door:** the new access token goes through
  `RemoteAuth.IdentifyAsync`. It must be issued to our client, carry
  `drive.readonly`, and belong to a user in `RemoteAuth__AllowedUsers`. A refused
  user gets 403, **no cookie**, and the grant they just gave is **revoked**.
- Success sets the session cookie and returns `{access_token, expires_in,
  persistent: true}`. The refresh token is never in a response body.
- **No refresh token in the exchange** happens because Google returns one only
  for a new grant. Example: a second browser where the app was never signed out,
  so the grant is still live. The server then answers `persistent: false` and sets
  no cookie. That sign-in works for its hour, the way the token model does. The app's
  Sign out cannot fix it, because without a cookie the server has nothing to revoke.
  Remove the app's access at myaccount.google.com (Security → Third-party apps) and
  sign in again, and the new grant comes with a refresh token. The server logs a
  warning when it happens.

### 2. Renewal (`POST /api/auth/token`)

- Body `{force?: bool}`. Answers `{access_token, expires_in}`, or:
  - **401 `signed_out`**: no cookie, or one that does not decrypt;
  - **401 `invalid_grant`**: revoked, or unused for six months. The cookie is cleared;
  - **403 `not_allowed`**: the cookie is cleared;
  - **502 `upstream`**: Google unreachable, with no usable cached token;
  - **503 `code_flow_unavailable`**: the flag is off.
- **Sliding lifetime:** every successful answer re-issues the cookie for another
  **180 days**, resealed under the current key. A browser that stays away longer
  than that, or longer than Google's six-month idle limit, signs in again.

### 3. The cookie

- `tgd_session`, **`HttpOnly; Secure; SameSite=Strict; Path=/api/`**, Max-Age 180
  days. Path `/api/` covers the auth endpoints and the audio proxy, and nothing
  else. That is why it cannot be a `__Host-` cookie, which requires `Path=/`.
- Value: `s1.{kid}.{base64url(nonce ‖ AES-256-GCM(payload) ‖ tag)}`. The payload
  is `{rt, iat}`.
- **Key:** `HKDF-SHA256(AuthSession:CookieSecret, info="traktor-google-drive/session-cookie/v1/key")`.
  It comes from a secret of its own, never the raw secret, and never
  `RemoteAuth__TicketSecret`.
- **`kid`:** 4 bytes of HKDF with another info string, so each secret names
  itself. The version and kid are associated data, so swapping either fails the tag.
- **Rotation:** set the new secret as `AuthSession__CookieSecret` and the old one
  in `AuthSession__PreviousCookieSecrets`. Old cookies still open, and each is
  resealed under the new key on its next token request. Drop the old secret after
  a few days. A cookie that has not been used by then needs a new sign-in.

### 4. Server-side access tokens (`SessionTokens`)

- In memory, keyed by SHA-256 of the refresh token. A cached access token is
  served until it has **under 5 minutes** left. Then one caller refreshes it with
  Google, and concurrent callers wait for the same refresh (single flight).
- `force: true` (the page just got a 401 from Drive) skips the cache, except
  within 30 s of the last refresh. A burst of 401s therefore makes one refresh.
- If Google is unreachable, a cached token that has not expired is still served.
- **Every token served** passes `RemoteAuth.IdentifyAsync`, which caches per access
  token. So the allowlist is checked on every request, and that costs Google about
  one tokeninfo call an hour.
- A restart empties the cache. The next request refreshes. Nothing is lost.

### 5. The page (`auth.js`)

- The API the app uses is unchanged: `authGetToken`, `authRefreshToken`,
  `authSyncProxyCookie`, `authTokenExpiresAt`, `authSignOut`, `googleLogin`. It
  still serves player.js, waveform.js, prefetch.js, CastService, RemoteBridge and
  Discover's appdata calls. The access token still sits in sessionStorage with its
  expiry, and still goes into `drive_token`.
- **Code flow, with a session:**
  - The timer asks `/api/auth/token` **4 minutes** before expiry. That is inside
    the server's 5-minute lead, so the answer is always a fresh hour.
  - It is a plain request, so every 30 s tick may retry, and a background tab
    catches up on `visibilitychange`.
  - `authRefreshToken()` (the player's 401, Cast, the waveform) asks with
    `force: true`.
  - **No timer, 401 or Cast renewal ever opens a popup.** The pointer hook is off.
- **Code flow, no session** (revoked, expired, or a tab signed in before the flag
  was turned on): `authRefreshToken()` opens the code popup **only inside a click**
  (`navigator.userActivation.isActive`). Otherwise it returns null. This keeps
  PR 6's behaviour: playback pauses on "Sign in again", that click signs in, and
  playback resumes at the same position.
- **New `authEnsureToken()`:** `AuthService.GetTokenAsync` now calls it. When the
  stored token has run out (a reload after the hour, a new tab, a laptop back from
  sleep), it asks the server first, so the app no longer lands on `/login` while a
  session exists. If auth.js is stale, it falls back to `authGetToken`.
- **New `authLogout()`:** the Sign out button calls it via
  `AuthService.LogOutAsync`. `authSignOut()` stays local: `HandleExpiredAsync` uses
  it, and it must not revoke anything.
- A localStorage hint (`auth:server-session`) records whether a session exists, so
  the page knows which way to renew. The cookie itself is HttpOnly, so the page
  cannot check it. The server always has the final word.

### 6. `drive_token` and the audio proxy

`/api/proxy/drive/{id}` **prefers the server session.** If the request carries a
`tgd_session` that decrypts, the proxy relays with the server's own access token
for it. Otherwise it reads `drive_token` as before. auth.js still writes
`drive_token` in both modes, so:

- with the flag off, nothing changes;
- a tab signed in before the flag was turned on keeps playing;
- if the session dies mid-set, the proxy falls back to the page's token, and the
  normal 401 path takes over.

`drive_token` is **kept for now**, not replaced. Removing it is a follow-up for once
the flag has been on in production for a while. Even then it is worth keeping
while a token-model fallback exists.

The proxy never writes the session cookie. `/api/proxy/cast/` still **ignores
cookies**, so the ticket stays its only credential.

### 7. Cast tickets: unchanged

The sender still mints and renews tickets with the page's access token. It now
gets that token from the silent server renewal, so Cast renewal no longer depends
on a popup, which was the only problem it had. Letting a grant draw tokens from
the server session would make a ticket outlive the sender. The Cast ADR keeps the
opposite property on purpose: when the sender goes away, the TV stops within the
hour. So this is not done.

### 8. Feature flag

`GoogleOAuth.IsAvailable` is true only with **both** `GoogleOAuth__ClientSecret`
and an `AuthSession__CookieSecret` of at least 32 characters. Without them:

- `GET /api/auth/config` answers `{codeFlow: false}`, and the three POSTs answer
  503 (logout still just clears a cookie);
- auth.js keeps the token model exactly as before, timers and pointer hook
  included;
- the proxy reads `drive_token` only.

The client dev server has no `/api/auth/config` and answers with `index.html`.
auth.js treats any answer that is not JSON as "off". So every existing e2e suite
runs, unchanged, against the fallback.

### 9. CSRF, origin and rate limits

- **All three POSTs** require the following, or they answer 403:
  - an `Origin` that names this host. This is `RemoteAuth.IsSameOrigin`: the header
    must be present, so `null` and missing are refused;
  - `X-Requested-With: XMLHttpRequest`. No cross-site form or simple request can
    send that header, and a cross-site `fetch` with it needs a preflight we never
    answer.
- The cookie is `SameSite=Strict`, so a cross-site request does not carry it at
  all. Each of the three layers alone stops classic CSRF.
- **Rate limit:** a fixed window of 30 requests per minute per client address
  (`X-Forwarded-For`, via `UseForwardedHeaders`), on the three POSTs. The page
  asks about once an hour per tab. The setting is `AuthSession:RateLimitPerMinute`.
- Responses are `Cache-Control: no-store`.

### 10. Logging

- Nothing logs a token, a code, a cookie or a Google response body.
- Google's error code (`invalid_grant`, …) and HTTP status are logged.
- A session appears in logs only as the first 8 hex characters of SHA-256 of its
  refresh token.
- ASP.NET's request logging does not log cookies.

## Threat model

| Threat | Before | After |
|---|---|---|
| **XSS on our origin** | The script reads the access token from sessionStorage or `drive_token`: an hour of Drive. | The script still gets access tokens, by calling `/api/auth/token` like the page does. Same-origin and the header check do not stop script running *on* the origin. It **cannot read the refresh token** (HttpOnly, sealed). Once the XSS is gone, or after Sign out (which revokes), the attacker's tokens die within the hour. XSS stays the main risk, as before. This change adds no script source. |
| **CSRF** | n/a | Three independent layers: SameSite=Strict, strict Origin, and the custom header. A cross-site page can neither carry the cookie nor read a response. |
| **Replay of a stolen cookie** (copied from disk, a backup or malware) | n/a | A copy works from another machine until the grant is revoked. The cookie is not bound to a device, and sliding re-issue does not invalidate old copies, because there is no server state. Mitigations: Sign out (revokes the grant, so every copy is dead within the hour and refreshes fail at once), revoking at myaccount.google.com, and the allowlist (a copy is only useful as Toni). Accepted for a one-user app. Option D's database would allow per-session revocation, at the cost of a database. |
| **Stolen or unlocked laptop** | A tab that stays signed in for the rest of the hour. | **Up to 180 days of access from that browser profile**, the same as any "stay signed in" site. Mitigations: Sign out on any device, or revoke at Google, and every session ends. Disk encryption and the OS lock screen protect the cookie at rest. This is the real cost of the feature. |
| **Server compromise / leaked secrets** | The server only ever saw tokens in flight. | The client secret plus the cookie secret plus a cookie gives a refresh token. Without a cookie, the secrets alone give nothing: no refresh tokens are stored server-side. To rotate the cookie secret, see §3. To rotate the client secret, reset it in GCP project 219145501841. |
| **Redirect or endpoint swap** | n/a | `GoogleOAuth:TokenEndpoint/RevokeEndpoint` are a Development-only test seam. The server **refuses to start** with them set in any other environment, like `RemoteAuth:FakeUsers`. |
| **A user off the allowlist** | Can use the app with their own Drive, but not remote control. | Can still use the token-model fallback only while the flag is off. With it on, they get no session, and their grant is revoked at sign-in. |

## Consequences

**Positive**

- An idle tab never shows a Google popup. Renewal is a same-origin request every
  hour, with no click needed. A reload or a new tab after the hour signs in
  silently instead of landing on `/login`.
- The refresh token is never readable by page script, and never in a log or URL.
- PR 6's pause-and-resume still covers the cases that genuinely need the user:
  revoked, expired, or a session from before the flag.
- Deploying before the secrets exist changes nothing (§8).

**Cost / risk**

- Two new secrets, and a consent-screen change (Testing to In production). In
  Testing, refresh tokens expire after **7 days**. In production with an
  unverified app, `drive.readonly` (a restricted scope) shows Google's "unverified
  app" warning at sign-in, and the app is capped at **100 users**. Both are fine
  for Toni.
- **Logout revokes the grant, so it signs out every device.** Google revokes per
  grant, not per token. The phone remote signs out too.
- **Replacing a session does not revoke the old refresh token**, for the same
  reason: revoking it would kill the new one too. Signing in again in the same
  browser leaves the previous refresh token valid at Google until it idles out.
  Google caps live refresh tokens per user and client at 100 and drops the oldest.
- A revocation reaches the server at its next refresh (within the hour), or at
  once if Drive refuses the token. Google also kills the access tokens.
- `persistent: false` sign-ins (§1) fall back to hourly behaviour until the grant is
  removed at myaccount.google.com and the user signs in again. This has not been observed yet. **Watch for the server warning on
  the first real sign-in.**
- `max-instances=1` makes the in-memory cache and the rate limiter exact. A second
  instance would only refresh more often. The cookie works on any instance.

## Tests

- **`tests/server-unit/AuthSessionTests.cs`** (xUnit):
  - the cookie: round trip, every flipped character rejected, garbage, swapped
    kid, wrong secret, rotation (old opens, reseals under new, dies once dropped),
    the derived key not equal to the secret, cookie options;
  - `SessionTokens` on a manual clock: cached until the 5-minute lead and refreshed
    at 4, restart, `invalid_grant` not cached, the force debounce, single flight
    under concurrency, the allowlist per token, Google down with and without a
    usable cached token;
  - the flag: on, and off without either secret;
  - the endpoint seam refused in Production;
  - the client secret sent on refresh.
- **`tests/server-unit/AuthEndpointTests.cs`** (the real host, with Google and Drive
  faked at the HTTP handler):
  - flag off: config `false`, 503s, and the proxy still on `drive_token`;
  - sign-in sets an `HttpOnly; Secure; SameSite=Strict; Path=/api/` 180-day cookie,
    and the refresh token appears in neither body nor cookie;
  - token from the session with a sliding cookie, and a refresh near expiry;
  - `invalid_grant` clears the cookie; a forged cookie means signed out;
  - allowlist refusal revokes; a refused code is a 400; no refresh token means
    `persistent: false`;
  - the Origin and header checks on all three POSTs;
  - logout revokes and kills the cookie;
  - the proxy prefers the session, falls back to `drive_token`, and the Cast route
    ignores the session;
  - the rate limit gives 429.
- **`tests/e2e/auth-session.spec.ts`** (`playwright.auth.config.ts`: the real host
  with the flag on, Google's token endpoint replaced by `fake-google.mjs`, GIS faked
  with Chrome-like popup blocking):
  - first sign-in: one code popup, the cookie is HttpOnly and Strict, and neither
    page storage nor `document.cookie` holds the session;
  - a simulated hour: four renewals, each a request to the server and a refresh
    at Google, the token changes, and **zero popups of either kind**; then a reload
    with an expired token and a new tab both sign in silently;
  - revoked: a reload lands on `/login` with the cookie cleared and no popup, and
    one click restores the session;
  - revoked mid-set: playback pauses on "Sign in again", nothing pops up by itself,
    and the click signs in and resumes at the same position;
  - Sign out: revoked at Google, cookie gone, and the next visit needs a sign-in;
  - a user off the allowlist: no session, and their grant is revoked.
- Every other suite runs with the flag off and so covers the fallback:
  `playwright.config.ts` (the client dev server) and
  `playwright.remote.config.ts` (the host without secrets).

**Not tested:**

- real Google: whether GIS's popup code flow returns a refresh token on the first
  sign-in, and Google's actual `invalid_grant` timing;
- the 180-day sliding expiry itself;
- Safari.

The first real sign-in after Toni's manual steps is the check: look for the
`tgd_session` cookie, and for **no** "returned no refresh token" warning in the
logs.
