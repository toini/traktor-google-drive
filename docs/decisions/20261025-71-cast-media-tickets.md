# Cast media tickets: the Google token out of the TV's URL

Date: 2026-10-25

## Status

**Proposed**, implemented on `toni/fix/cast-token-out-of-urls` in the same PR as
this ADR, on top of `master` at `448b3b0`. It replaces §4 of
[adr-001](../adr-001-tv-playback.md) ("Re-arm the token": re-issue `LOAD` with a
new token in the URL) and closes the item that the remote-control ADR
([20261018-63](20261018-63-remote-control.md)) left under "Deliberately not done":
the audio proxy's token in Cloud Run request logs.

## Context

- Since PR 6, local playback sends the Google token in the `drive_token` cookie. A
  Chromecast has no cookie of ours and fetches the media itself, so the Cast URL
  still was `/api/proxy/drive/{id}?token=<Google access token>`. **Cloud Run logs
  full request URLs**, so every 8 MiB slice the TV fetched wrote a bearer token
  for the user's *whole Drive* (`drive.readonly`) into the request log.
- The proxy must still call Drive with the user's token. The server has never
  stored one.
- A set runs one to two hours, and a Google token lives about one hour. Renewing
  meant giving the TV a new URL: a fresh `LOAD` at the current position, which
  rebuffers.
- The receiver fetches with `Range`. It sends no `Origin`, `Referer` or cookie.
- Cloud Run runs this service with `max-instances=1` and session affinity, but it
  still restarts the instance on every deploy, and can recycle it at any time.
- `RemoteAuth__TicketSecret` already comes from Secret Manager.

## Options

| Option | What it costs | What it gives up |
|---|---|---|
| **1. In-memory opaque ticket.** The sender POSTs its token (Authorization header), the server stores token + fileId under a random id with a TTL, and the URL carries the id. | Tokens live in memory. Strict scope is a lookup. Renewals can update the same entry, so the TV's URL never changes. | **A restart loses every entry**, and the TV's URL is dead until the sender reloads it with a new ticket. It relies on one instance. |
| **2. Encrypted ticket in the URL.** AES-GCM of {token, fileId, exp} under an HKDF key. | No server state at all, and it survives restarts and a second instance. | **The (encrypted) Google token is in the logs**, a bearer credential until `exp`, and the key plus old logs give back old tokens. A renewed token means a new ciphertext, so **every renewal is still a new URL and a rebuffer**. No revocation. |
| **3. Signed handle, token in memory, re-registrable** (chosen). The URL carries an HMAC-signed `{grantId, fileId, exp}`; the token stays in memory under `grantId`. The sender pushes renewed tokens to the same grant, and a heartbeat re-registers it after a restart. | Tokens live in memory, as in 1. The scope and expiry checks are stateless (the signature), as in 2. One more endpoint (renew) and a 60 s heartbeat from the sender. | A restart still drops the token. The TV's next slice fails if it comes before the sender's next heartbeat (≤60 s, and the TV usually has a slice buffered). With the sender gone (tab closed), the TV plays until the stored token's hour runs out, as before. |
| 4. Server-side authorization-code flow with a refresh token. | Fully silent renewal, no sender needed. | A client secret, durable token storage and a consent change. Much larger, and already rejected in adr-001 for the same reasons. |
| 5. A custom Web Receiver that fetches with a header. | No credential in any URL. | A registered receiver app, its hosting, and the Default Media Receiver's maturity given up. adr-001 rejected it. |

## Decision

**Option 3.** It keeps option 1's property that the Google token never leaves
memory, in any form, and fixes option 1's real weakness: the ticket is
self-validating, so a restart only drops the token, and the sender puts it back
without the TV changing URL. It also removes the rebuffer that both option 2 and
today's code pay on every renewal.

### How it works

- **Mint:** `POST /api/cast/tickets`, same-origin, `Authorization: Bearer <token>`,
  body `{fileId, tokenExpiresAt}`. The server returns `{ticket, renewKey, expiresAt}`
  and stores `{fileId, token, tokenExpires}` under a random 128-bit `grantId`.
- **Ticket:** `base64url("c1|grantId|fileId|exp") . base64url(HMAC-SHA256(k, payload))`,
  where `k = HKDF-SHA256(RemoteAuth:TicketSecret, info="traktor-google-drive/cast-ticket/v1")`.
  The secret is never used raw, and the hub's tickets and these cannot be swapped
  for each other.
- **The TV's URL** is `/api/proxy/cast/{fileId}?ticket=…`. The proxy checks the
  caller (the same `IsAllowedCaller` as before), then the signature, the expiry
  and that `fileId` equals the ticket's file. It answers **403** to all of those,
  and **401 + `X-Cast-Ticket: no-token`** when the ticket is good but no token is
  held for it. It **ignores cookies**, so the ticket is the only credential on that
  route. Range requests go through the same sliced relay as the browser's proxy.
- **Renew:** `POST /api/cast/tickets/renew`, `Authorization: Bearer <new token>`,
  body `{ticket, renewKey}`. `renewKey = HMAC(k, "renew|" + grantId)` is returned
  only in the mint response body, so it is never in a URL or a log. Answers 204,
  **410** for a ticket past its cap, or 403. It also creates the grant if it is
  missing, which is how a restart heals.
- **Revoke:** `POST /api/cast/tickets/revoke` when the sender stops, disconnects,
  goes idle or loads another track.
- **Lifetimes:** a stored token is held until the expiry the sender reports, capped
  at **1 h**. A ticket's hard cap is **4 h**. There are at most 256 grants, and
  expired ones are swept on every write.

### Sender (`CastService`)

On each "playing" tick it pushes a token when one of these holds: auth.js renewed
the token elsewhere, the token is within 5 minutes of expiry (it renews it first,
silently, as before), or 60 s have passed since the last push. **None of these
touch the TV.** The TV gets a new `LOAD` at the current position in only two
cases: the ticket is within 5 minutes of its 4 h cap, or the server answered a
renewal with 410/403 (for example, a changed secret). An unreachable server is
retried after 10 s and leaves the TV alone.

### The `?token=` fallback is gone

`/api/proxy/drive/{fileId}` now reads only the `drive_token` cookie. Cast was its
last user. Nothing in the client builds a `?token=` URL any more.

## Consequences

**Positive**

- No Google token in any URL or request log, for local playback or Cast.
- What a request log does hold is a ticket for **one file**, valid only while the
  sender keeps its grant alive (at most an hour after the sender goes away) and at
  most 4 h. Before, it held a token for the whole Drive.
- Renewals no longer rebuffer the TV. A 2 h set sees no reload at all.
- Forged, expired and wrong-file tickets are refused with no state lookup.

**Cost / risk**

- **A ticket in a log is still a bearer credential for its file** while its grant
  lives. That is inherent: the TV can present nothing else. Revoking on stop and on
  the next track shortens it.
- **Restart gap:** after a restart the TV's next slice can 401 until the sender's
  next heartbeat (≤60 s). The TV normally has a slice (8 MiB, about 45 s of WAV or
  minutes of MP3) in hand. If it does hit the gap, the receiver goes idle with an
  error, and the user presses play again. Not measured on a real device.
- **Without `RemoteAuth__TicketSecret`** (development), the key is random per
  process, so a restart invalidates the tickets themselves. The renewal then gets a
  403 and the sender reloads with a new ticket.
- **A second instance** would not have the grant. With `max-instances=1` that only
  happens briefly during a deploy, and it heals like a restart.
- Mint does not validate the token with Google. It does not need to: a ticket only
  ever relays the token it was minted with, so a bogus one gets nothing.
- **No new config or secret.** The key is derived from the existing
  `RemoteAuth__TicketSecret`.

## Tests

- `tests/server/proxy-origin.sh`, against the real server: the ticket works for its
  own file and is refused for another one; forged, garbage, expired (correctly
  signed) and missing tickets are refused, including when a `drive_token` cookie is
  sent; `?token=` is no longer accepted; mint refuses other origins, no token and a
  malformed file id; renew refuses a wrong renew key and says 410 for an expired
  ticket; revoke empties the grant; and **after a server restart the ticket holds
  no token until one renewal restores it.**
- `tests/e2e/cast.spec.ts`, against a stand-in for the ticket endpoints: no `LOAD`
  URL contains the token (initial load, renewal, next track, ticket replacement),
  and the token travels only in an Authorization header. A token renewal reaches
  the server **without a new `LOAD`**, and the TV keeps playing. A ticket at its cap
  is replaced at the same position, a 410 renewal reloads with a fresh ticket, and
  stopping revokes.
- `tests/e2e/remote.spec.ts` runs the Cast sender against the real server: the URL
  it hands the TV gets past the ticket check for its own file and is refused (403)
  for another one.

Not tested: the 60 s heartbeat itself (no fake clock in the e2e suite), and any
real Chromecast.
