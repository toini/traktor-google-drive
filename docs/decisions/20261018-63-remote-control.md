# Remote control: one playing device, any number of controllers

Date: 2026-10-18

## Status

**Accepted 2026-10-18 by Toni**, with the choices recorded under §"Decisions Toni
made". Written on `toni/docs/remote-control-adr`, cut from `master` at `27a96dd`.
Increment 0 (the spike) is recorded under §"Spike findings". Increment 1, together
with its prerequisite (§5.1: the queue moves into the player and browsing never
stops the music), is implemented on the same branch, in the same PR as this ADR.

## Decisions Toni made

1. **Transport: A, a SignalR hub on the existing Cloud Run service** (§2).
   Firebase (C) stays the documented fallback.
2. **Idle-disconnect as proposed:** a player that is paused and hidden drops off
   the tablet's list after 15 minutes (§2.1, Open question 1).
3. **Browsing never stops the music.** The queue moves into the player (§5.1, Open
   question 3).
4. **The tablet is Android** (Open question 5). F1 (Android's system Cast controls)
   is therefore worth checking on the device.
5. **Local token re-arm is out of scope here.** It is being fixed separately on
   `toni/fix/local-playback-token-refresh`. §5.4 keeps only the remote-facing part:
   the snapshot reports `auth.needsSignIn`, and the tablet says so.

**Not answered, so the proposals stand as defaults:**

- **No volume control** for now (Open question 6).
- **No "listen on both":** a device is either the player or a controller (Open
  question 7).
- **The allowlist is on** (`REMOTE_ALLOWED_USERS`, §3), since the consent-screen
  mode is unknown (Open question 4).

## Context

Toni: *"I have the app running and playing music on my laptop. I want to be able to
open the site on my tablet and fast forward, pick another track or whatever. Like
with Spotify. Design a solution."*

Spotify Connect has three parts. **One device plays.** **Every other signed-in
device sees what is playing and can control it.** **Playback can be moved** from
one device to another. The account is the pairing: there is no code to type.

### What exists today, checked against the code

- **`PlayerService` is already a router.** It owns the one `<audio>` element
  (`player.js`) and, while a Cast session is connected, sends every call to
  `CastService` instead. `PlayerBar` never knows which output it is driving
  ([adr-001](../adr-001-tv-playback.md) §2: "The play button routes; there is no
  second set of controls"). A remote target fits that pattern as a third route.
- **The queue belongs to the playlist page, not the player.** `Playlist.razor`
  calls `Player.SetQueue(SortedFiles())` on every load and re-sort. Its `Dispose`
  calls `Player.ClearQueue()` and `Player.StopAsync()`, and switching playlists
  calls `StopAsync()`. So **on the laptop, browsing to another playlist stops the
  music**, and a remote "play row 4 of playlist X" has nothing to run against
  unless the laptop's UI happens to show playlist X. This is the biggest piece of
  existing code the design has to change (§5.1).
- **`CurrentUrl` embeds the Google access token**
  (`DriveAudio.UrlFor` → `/api/proxy/drive/{id}?token=…`). Anything that
  serialises `PlayerService` state for another device must leave it out.
- **Auth is the Google Identity Services token flow, scope `drive.readonly` only**
  (`wwwroot/auth.js`). There is no `openid`, `email` or `profile` scope, no ID
  token and no refresh token. The token sits in **`sessionStorage`**, so it is
  per tab, and it lives about an hour. `authRefreshToken()` (`prompt: ''`) exists,
  and only `CastService` calls it.
- **Local playback has no token re-arm.** The `<audio>` element keeps the URL it was
  given, and each ≤8 MiB slice is a new request carrying that token. A local set
  longer than the token's lifetime will therefore hit a 401 on the first slice
  fetched after expiry. Cast re-arms with a fresh `LOAD` (adr-001 §4), but local
  playback does not. Today someone is at the laptop to sign in again. Under
  remote control, nobody is (§5.4).
- **Gig preview's end check runs in `player.js`** on `timeupdate`, and on the
  ~1 Hz Cast ticks in `PlayerService.OnCastProgress`
  ([ADR 20261018-47](20261018-47-waveform-cue-markers-gig-preview.md) §4).
- **`PlaybackMemory`** stores `{fileId, title, isWav, duration, position}` in
  `localStorage` (`playback:v1`), never the token, and **cues** the track paused
  after a reload. It never autoplays.
- **Waveform peaks are 400 buckets** (`waveform.js` `BUCKETS = 400`), cached in
  IndexedDB per file id. That is about 2–3 KB as JSON. Computing them costs a lot
  more: WAV is sampled with range reads *through the proxy* (Cloud Run egress),
  and compressed files are downloaded in full from Drive and decoded in the
  browser.
- **The server is a stateless relay.** `TraktorGoogleDrive.Server/Program.cs` has
  static files, the `/api/proxy/drive/{fileId}` endpoint with a host-only
  `Origin`/`Referer` check, and nothing else: no database, no session, no identity.
  It never learns who the caller is, and only forwards the caller's token to Drive.

### Cloud Run, as deployed (read with `gcloud run services describe`, 2026-10-18)

| Setting | Value | Why it matters here |
|---|---|---|
| `maxScale` | 20 | In-memory state on one instance is invisible to the others |
| `minScale` | unset (0) | First connect after idle waits for a cold start |
| `containerConcurrency` | 80 | Plenty for one user's sockets plus proxy slices |
| `timeoutSeconds` | 300 | **A WebSocket or SSE stream is cut after 5 minutes.** The maximum is 3600 |
| CPU / memory | 1 vCPU / 512 MiB, request-based billing | **An open long-lived request keeps an instance billed** |
| Ingress | `all`, `--allow-unauthenticated` | Anyone can reach any endpoint, so the app has to authenticate every call itself |

Also: the OAuth client in `auth.js` (`219145501841-…`) belongs to GCP project
number **219145501841**, but the Cloud Run project `traktor-toni-2025` is
**194132977379**. That does not matter for a hub on Cloud Run. It does matter for
Firebase (§2, option C).

## Decision

### 1. Model: Spotify Connect, with the player as the only authority

**Roles.** A **device** is one browser tab running the app. At most one device in
a room is the **active player**. Every other device in the room is a
**controller** while it shows the remote state. A device is never both at once.
The **room** is the signed-in Google user (§3).

**The player is the source of truth, and the server only relays.** The server keeps
nothing it cannot lose: the roster of connected devices and the last snapshot, both
in memory. After a restart, deploy or instance swap, the player republishes its
full state on reconnect, and nothing is lost. That property is what makes Cloud
Run's scale-to-zero and instance churn acceptable (§2).

#### State the player publishes (`PlayerSnapshot`)

Sent **on every state change** (play, pause, seek, track change, queue change, mode
change, Cast change). It is also sent **every 10 s while playing** as a heartbeat
and drift correction, never every second. Controllers interpolate the position
locally between snapshots.

| Field | Content | Notes |
|---|---|---|
| `v`, `seq` | protocol version, monotonic sequence | Controllers drop stale or out-of-order snapshots |
| `deviceId`, `deviceName` | the publishing player | |
| `collectionFileId` | the `collection.nml` the player resolved | A controller that picked a different one warns (§4) |
| `track` | `fileId`, `playlistUuid`, `playlistIndex`, `title`, `artist`, `isWav`, `duration` | Ids are enough, because the controller resolves cues, colours and BPM from its own copy of the NML |
| `state` | `Idle / Loading / Playing / Paused / Error / Unauthorized / Blocked` | `PlaybackState`, plus `Blocked` (autoplay refused, §5.3) |
| `position`, `positionAt` | seconds, and the player's clock when sampled | The controller extrapolates from its own receive time, not from `positionAt`, so clock skew does not matter. The error is one-way latency, ~50 ms |
| `queue` | `playlistUuid`, `sort` (column, direction), `items: [{fileId, playlistIndex}]`, `index` | Only sent when it changes. A 300-row playlist is ~10 KB |
| `continuous` | bool | |
| `preview` | `{active, position, count, start, end, note}` | Mirrors `PreviewRange`. `note` is the reason a default applied, e.g. "no loop — to end" |
| `output` | `{kind: local / cast, castDeviceName, castState}` | "MacBook · Chrome → Living room TV" |
| `auth` | `{expiresAt, needsSignIn}` | So a controller can say *why* the player stopped (§5.4) |
| ~~`volume`~~ | — | **Left out.** `player.js` has no volume control, and on Cast the TV remote owns volume. See Open question 6 |

**Never in a snapshot:** `CurrentUrl` or any other value that contains a token.
Peaks are sent separately, on request (§4.6).

#### Commands controllers send

Every command carries `commandId` (for the ack) and the sender's `deviceId`. They
are **absolute where they can be** (`Seek(123.4)`, not "+10 s";
`SetContinuous(true)`, not "toggle"), so a retry after a reconnect is idempotent.

| Command | Player does | Increment |
|---|---|---|
| `Play`, `Pause` | `ResumeAsync` / `PauseAsync` | 1 |
| `Seek(seconds)` | `SeekAsync`. A "+15 s" button is computed on the controller from the interpolated position | 1 |
| `Next`, `Previous` | `NextAsync` / `PreviousAsync` | 1 |
| `RequestSnapshot` | republish now | 1 |
| `RequestPeaks(fileId)` | reply with cached peaks or `none` | 2 |
| `PlayRow(playlistUuid, fileId, playlistIndex, sort)` | load that playlist **into the player's own queue** in that sort, play that row | 3 |
| `SetContinuous(bool)` | `SetContinuousPlayAsync` | 3 |
| `StartPreview(playlistUuid, sort, fromFileId?)`, `StopPreview` | gig preview from the top or from a row | 3 |
| `TransferTo(deviceId)` | hand off (§4.3) | 4 |
| `StopCasting` | `CastService.DisconnectAsync`. *Starting* a cast cannot be remote, because the Cast picker needs a user gesture on the laptop | 4 |

The player answers each command with `Ack(commandId)` or
`Reject(commandId, reason)`, e.g. `playlist-not-found`, `needs-sign-in`,
`autoplay-blocked`, `not-active`. The controller shows the reason. It never
guesses.

#### Browse locally, or mirror the laptop's queue?

| Option | Cost | Gives up |
|---|---|---|
| **A. Mirror only.** The tablet shows the laptop's queue and can only pick from it | Smallest. The queue list is already in the snapshot | "Pick another track" from any other playlist. That is half of Toni's ask |
| **B. Browse only.** The tablet browses its own copy and sends "play this". No queue view | No queue list in the snapshot | Seeing what comes next. During a gig preview that is what you want to see |
| **C. Both** (proposed) | The tablet is the full app (sidebar, playlists, its own Drive token and NML), and sends ids. It also has a "Now playing" view rendered from the mirrored `queue` | Needs the queue to live in the player rather than in the page (§5.1). Needs both devices to resolve the same collection |

**Proposed: C.** The tablet already has everything it needs to browse: it signs in
to the same Drive and parses the same `collection.nml`. So the commands carry only
**ids**, never metadata or tokens, and the player looks up the rest itself. The
queue stays authoritative on the player, which is what keeps continuous play and
gig preview working when the tablet goes to sleep.

### 2. Transport

All costs use Cloud Run request-based pricing for a Tier 1 region at list price
(about $0.000024 per vCPU-second and $0.0000025 per GiB-second, with a free tier of
180,000 vCPU-s and 360,000 GiB-s a month, shared with the audio proxy). **Verify
against the current price sheet before deciding.** At this service's
1 vCPU / 512 MiB, **one hour with an instance held open costs about $0.09**:

| Hours a month with a socket open | ≈ cost after free tier |
|---|---|
| 90 (3 h a day) | ~$3.50 |
| 240 (8 h a day) | ~$17 |
| 730 (a tab left open around the clock) | ~$60 |

That table is the dominant cost of options A and B, so idle-disconnect (§2.1) is
part of the design, not polish.

**Latency, for perspective:** a seek on the player loads a new ≤8 MiB slice through
the proxy, and that takes ~0.3–1 s before the sound changes. So any transport
under ~150 ms is not the bottleneck. A transport at 1–5 s is.

#### A. SignalR hub on the existing ASP.NET server (WebSockets only)

- **How:** `MapHub<RemoteHub>("/api/remote/hub")` in `Program.cs`. Each room is a
  SignalR group keyed by the user. The client is `Microsoft.AspNetCore.SignalR.Client`
  in the WASM app, with `SkipNegotiation = true` and `Transports = WebSockets` so
  the connection is one request on one instance (negotiate followed by connect can
  otherwise land on two different instances).
- **Cloud Run:** WebSockets work. The settings it needs:
  - **`--timeout=3600`.** Cloud Run treats a socket as a request and cuts it at the
    timeout, today 300 s. 3600 is the ceiling, so **every socket is dropped at least
    once an hour by design** and `WithAutomaticReconnect` reconnects it.
  - **`--max-instances=1`**, plus **`--session-affinity`** as a belt. Both devices
    must reach the same in-memory group. With one user, one instance at
    concurrency 80 is plenty for the proxy too. But Cloud Run documents that it
    can briefly exceed max instances, e.g. during a deploy. Correctness therefore
    does not depend on it: a command whose room has no player *on this instance*
    is rejected with `no-player`, the controller reconnects, and the player
    republishes on its own reconnect. The worst case is a few seconds of "Looking
    for MacBook…".
  - **Scale to zero stays.** When no socket is open, nothing is billed.
- **Latency:** ~40–100 ms command to player (two hops through Google's front end;
  europe-north1 from the Nordics).
- **Cost:** about $0.09 per hour that any socket is open (table above). With
  idle-disconnect: a few dollars a month.
- **Complexity:** medium. Hub ~150 lines, ticket auth (§3), a client
  `RemoteService` ~300 lines, three `gcloud` flags. It adds a NuGet package to the
  WASM payload; measure its size in increment 1.
- **Offline / reconnect:** automatic reconnect with backoff. Presence comes free: a
  closed connection *is* "device gone", and keep-alives (15 s default) detect a
  dead tablet within ~30 s. Soft state is rebuilt by republishing.
- **Cold start:** the first connect after idle waits for a cold start (seconds;
  `startup-cpu-boost` is on). A side effect: while a socket is open the instance is
  warm, so the proxy's first slice is faster.

#### B. Server-Sent Events for state, POST for commands

- **How:** `GET /api/remote/events` streams `text/event-stream` (hand-written on
  .NET 9's minimal API, a few lines). `POST /api/remote/commands` relays a command
  to the player's stream.
- **Cloud Run:** **the same constraints as A.** The stream is a long request with
  the same 3600 s ceiling. A POST from the tablet and the player's stream must sit
  on the same instance, so it also needs `max-instances=1`.
- **Latency:** the same as A for state. Commands ~50–150 ms, a fresh request each.
- **Cost:** the same as A, because it is the open stream that costs.
- **Complexity:** low to medium. No new package. But presence (detect a dead stream
  from a failed heartbeat write), groups, reconnect (`Last-Event-ID`) and command
  routing are all hand-rolled. `EventSource` cannot send an `Authorization` header,
  so it needs the ticket in the URL (as A does) or a `fetch`-based stream reader.
- **Offline / reconnect:** `EventSource` reconnects on its own. POSTs made while
  offline need a retry policy.
- **Verdict:** a fair alternative. It gives up nothing on Cloud Run compared with A,
  and A gets bidirectional messaging, groups and presence for free.

#### C. Managed realtime store: Firestore or Firebase Realtime Database

- **How:** both devices read and write `rooms/{uid}/…` directly from the browser
  with the Firebase JS SDK through interop. Security rules:
  `request.auth.uid == uid`. Commands are documents the player consumes and
  deletes. RTDB's `onDisconnect()` gives presence.
- **Auth:** needs **Firebase Auth**, a second identity system. It can take the GIS
  credential (`GoogleAuthProvider.credential(null, accessToken)`), but that
  probably needs `openid email` on the token (unverified). Firebase lives in a GCP
  project, and **the OAuth client is in a different project (219145501841) from
  Cloud Run (194132977379)**, so either Firebase goes into the OAuth client's
  project or the client is recreated.
- **Latency:** RTDB ~50–150 ms, Firestore ~100–300 ms.
- **Cost:** ~$0 within the free tiers, **as long as the position is not written
  every second** (Firestore's free tier is 20k writes a day, and 1 Hz is 3,600 an
  hour). The snapshot design above writes only on change, plus every 10 s.
- **Complexity:** medium to high. Firebase project, rules deployment, ~100+ KB SDK,
  interop, a second sign-in, and rules that need their own tests (emulator).
- **Offline / reconnect:** best in class. The SDK buffers and resyncs, and there are
  no instance-affinity questions.
- **Cold start:** none. Cloud Run is not involved.
- **Gives up:** "nothing stored server-side". Track titles, device names and the
  Google uid are persisted in a database.

#### D. Polling a state file in the user's Drive `appDataFolder`

- **How:** the player writes `remote-state.json`, controllers write
  `remote-commands.json`. That is two files, so the two writers never race on one
  file. Both poll with `files.get` and an ETag.
- **Auth:** needs the **`drive.appdata` scope**. Today only `drive.readonly` is
  granted, so Toni consents once more.
- **Latency:** 1–5 s (poll interval plus ~300–800 ms per Drive write or read). There
  is no push: Drive's `changes.watch` needs an HTTPS webhook, which brings the
  server back.
- **Cost:** $0 on Cloud Run. Drive API quota: one poll per second per device is
  well inside the per-user limits, but it is constant background traffic on the
  tablet's battery.
- **Complexity:** low to medium. Presence is "heartbeat timestamp newer than 15 s".
- **Offline / reconnect:** trivial, it just polls again.
- **Gives up:** it does not feel live. A seek that lands two seconds later feels
  broken. **Worth keeping in mind for a different feature:** "resume on another
  device" (a `PlaybackMemory` record shared through `appDataFolder`), where
  seconds do not matter.

#### E. WebRTC data channel, peer to peer

- **How:** devices exchange an offer and answer through *some* signalling channel
  (A, C or D), then talk directly. STUN is free (Google's public servers). TURN is
  needed when the devices are on different networks behind symmetric NAT, and
  that means a TURN server to run or rent.
- **Latency:** ~10–50 ms on the same Wi-Fi.
- **Cost:** low once connected, but **signalling and discovery still need A, C or D**
  ("is there a player at all?" needs a rendezvous), so it adds to one of them
  rather than replacing it.
- **Complexity:** high. ICE, renegotiation on every network change, and two
  transports to test.
- **Gives up:** simplicity, for latency that is already not the bottleneck.

#### F. Other options

- **F1. Reuse Cast: the tablet joins the TV's Cast session.** The Default Media
  Receiver accepts several senders. **This only exists while the laptop is
  casting, there is no Cast receiver when it plays locally (a browser tab cannot be
  one), and the Cast Web Sender does not run on iPad Safari.** Even then the
  tablet gets transport controls only: the queue, continuous play and gig preview
  live in the laptop's `PlayerService`, and a tablet `LOAD` would bypass all of
  them. One thing is worth checking for free: **Android shows system media controls
  for Cast sessions on the same network**, so play, pause and seek on the TV may
  already work from an Android tablet today with zero code.
- **F2. Hosted pub/sub (Ably, Pusher, PartyKit, Cloudflare Durable Objects).**
  Free tiers, ~50–100 ms, presence built in. Durable Objects in particular is a
  very good fit (one object per room, WebSocket hibernation, so idle sockets cost
  nearly nothing). But each one is a second vendor, account and API key, and moves
  part of a .NET app onto another platform.
- **F3. Long polling** on Cloud Run: A or B's cost without its latency. Rejected.
- **F4. `BroadcastChannel` / Web Locks:** same browser only. Not a transport
  between devices, but it is the right tool for "two tabs on one machine" (§4.5).

#### Summary

| Option | Latency | Cost / month | Complexity | Reconnect / presence | Verdict |
|---|---|---|---|---|---|
| **A. SignalR hub** | 40–100 ms | ~$0.09 per socket-hour; a few $ with idle-disconnect | Medium | Automatic, presence free; hourly forced reconnect | **Recommended** |
| B. SSE + POST | 40–150 ms | Same as A | Low–medium, more hand-rolled | `EventSource` auto; presence by hand | Close second |
| C. Firestore / RTDB | 50–300 ms | ~$0 | Medium–high (Firebase Auth, rules, SDK, other GCP project) | Best (SDK resync, `onDisconnect`) | **Fallback** if socket-hours cost too much |
| D. Drive appData polling | 1–5 s | $0 | Low–medium, new scope | Trivial; presence by heartbeat | Reject for live control; keep for "resume elsewhere" |
| E. WebRTC | 10–50 ms (LAN) | Needs A/C/D anyway, plus TURN | High | Re-signal on every drop | Reject |
| F1. Cast multi-sender | Native | $0 | Low | Native | Only while casting, Android only, transport only. Check Android's built-in controls first |
| F2. Hosted pub/sub | 50–100 ms | $0 free tier | Medium + new vendor | Built in | Reject for now |

**Recommended: A**, for three reasons. It keeps everything in one C# codebase and
one deploy. It stores nothing server-side. And it can be tested end to end in the
existing Playwright setup against one server (§6). It costs a few dollars a month
and pins the service to one instance. **C is the fallback.** The client talks to a
small `IRemoteChannel` interface (`Publish`, `Send`, `OnSnapshot`, `OnCommand`,
`OnRoster`), so swapping A for C touches one class, not the player.

#### 2.1 Idle-disconnect: what keeps A cheap

- **Player:** connected while it has a track loaded **and** (it is playing, or the
  page is visible, or it was paused less than 15 min ago). It disconnects after that
  and reconnects on the next local action or `visibilitychange`.
- **Controller:** connected while its page is visible. It disconnects ~1 min after
  being hidden (tablet locked). Reconnecting and asking for a snapshot takes about a
  second when the tablet wakes.
- **Consequence:** a laptop that has been paused and hidden for 15 min disappears
  from the tablet's device list. See Open question 1.

### 3. Pairing and auth

**There is no pairing step.** Same Google account means same room, as in Spotify.

#### How the server learns who is calling

Today the server never validates the token. It only forwards it to Drive. The hub
has to know the user, and the options are:

| Option | What it costs | What it gives up |
|---|---|---|
| **3a. `tokeninfo` + Drive `about.get`** (proposed) | Per connect: `GET oauth2.googleapis.com/tokeninfo?access_token=…` to check that `aud`/`azp` is *our* client id (`219145501841-…`), that `exp` is in the future and that `scope` contains `drive.readonly`. Then `GET drive/v3/about?fields=user(permissionId)` for a stable user id. That works with `drive.readonly`, so **no scope change and no new consent**. Cache by token hash until `exp`: ~2 calls per device per hour | Two outbound calls per connect |
| 3b. Add the `openid` scope; `tokeninfo` / `userinfo` returns `sub` | One call instead of two. `sub` is the canonical Google user id | One new consent. **Unverified:** whether `tokeninfo` returns `sub` for a token with only `drive.readonly`. Expected no, so 3a uses Drive's `permissionId` instead. Check in increment 0 |
| 3c. Switch to ID tokens (GIS "Sign in with Google") | Verified offline against Google's JWKS, so no outbound call per connect | A **second sign-in flow** next to the token client, since GIS's token client never returns an ID token. More moving parts in `auth.js` for a one-user app |

**Why the `aud` check matters:** without it, an access token minted for *any* other
app the attacker controls would be accepted. In practice it would only open the
attacker's own room, but it is one line and the standard check.

#### The connection: a ticket, not the Google token, in the URL

Browsers cannot set headers on a WebSocket, so SignalR puts its token in the query
string (`?access_token=`), and **Cloud Run's request logs record full URLs.** The
audio proxy already writes tokens into those logs (`?token=` on every slice). That
is an existing gap and out of scope here, but the hub should not add another.
Instead:

1. `POST /api/remote/ticket` with `Authorization: Bearer <access token>` and
   `{deviceId}`. The server validates (3a) and returns a **ticket**:
   `HMAC(secret, userKey | deviceId | exp)` with a 60 s `exp`, base64url.
2. The WebSocket connects with `?ticket=…`. The hub checks the HMAC and the expiry,
   and joins group `userKey`. A ticket in the logs is dead a minute later.
3. It is **stateless**, so a ticket issued on one instance is valid on another. The
   HMAC secret comes from **Secret Manager** as an env var, and is never committed.

`userKey` = `HMAC(secret, permissionId)`. The raw Google id is never held as a group
name or logged. **The hub also checks `Origin`** against the host, the way
`IsAllowedCaller` does but strictly: no "neither header" pass, and no Cast receiver
host.

**Optional, defence in depth:** `REMOTE_ALLOWED_USERS`, a list of `userKey`s. With it
set, only Toni's account can open a room at all. Whether it is needed depends on
whether the OAuth consent screen is in "Testing" mode, limited to listed test users
(Open question 4).

#### What can never happen

- **A controller never gets the player's token.** Tokens are not in the protocol.
  Snapshots carry ids, the player builds its own URL from its own token, and the hub
  rejects any message over 64 KB or containing a field it does not know. A
  Playwright test asserts that no WebSocket frame on the controller contains the
  player's token (§6).
- **Nobody else controls Toni's player.** Every message is routed only inside the
  sender's own group, which is bound at connect time from a validated ticket. The
  room is never something the client chooses.
- **Nothing personal is stored server-side.** The roster (device ids and names), the
  last snapshot (track titles) and the token-validation cache all live in memory
  and die with the instance. Logs carry `userKey` prefixes and command names, never
  snapshots or tokens.

The hub connection's lifetime is bounded by Cloud Run's 3600 s timeout, so a
revoked or expired token stops being honoured within the hour: every reconnect
needs a new ticket, and that needs a valid token.

### 4. UX

#### 4.1 Discovery

When the tablet connects, the server sends the **roster**: `{deviceId, name, isActive,
state summary}` for each device in the room. If there is an active player, the
tablet's player bar switches to **remote mode** with a strip above it:
**"▶ Playing on MacBook · Chrome"** (or "… → Living room TV" when the player is
casting). This is the Spotify green bar. If nobody is playing, the tablet behaves
exactly as it does today.

#### 4.2 Device picker

A speaker icon in the player bar opens the list: **This tablet**, **MacBook · Chrome
(playing)**, and so on. Picking a device transfers playback to it (§4.3). Cast stays
on its own `CastButton` for now, because the Cast SDK's device list is the browser's
own dialog and cannot be embedded. Spotify merges the two lists, and merging is a
later polish step (increment 5).

#### 4.3 Transfer

`TransferTo(target)` is **make before break**:

1. The source sends a hand-off payload to the target: queue (playlist uuid, sort),
   track, position, continuous, preview. It is a superset of `PlaybackMemory`'s
   `Snapshot` record, so that shape is reused.
2. The target loads it and calls `play()`.
3. **Only when the target reports `Playing`** does the source pause and give up the
   active role. If the target reports `Blocked` (autoplay, §5.3), the source keeps
   playing and the target shows "Tap to continue here".

A **pull** ("play here", pressed on the tablet) always works, because the press is a
user gesture on the device that will play. A **push** from the tablet to the laptop
can be blocked if the laptop tab has had no interaction since it loaded.

#### 4.4 Conflicts

| Case | Behaviour |
|---|---|
| A controller presses play while a player is active | It is a command to that player, not local playback. Remote mode makes the play button route |
| Someone presses play *on the laptop* while the tablet is the player | **Last press wins**, as in Spotify. The laptop claims the active role, the server tells the old player to pause, and the tablet becomes a controller |
| Two claims within the same instant | The hub serialises them per room (one instance, one lock). Last one processed wins, and both get the result |
| Two players claim after a server restart | On reconnect, only a player that is *currently playing* re-claims. If two are, last wins and the other pauses |
| A command arrives for a player that is no longer active | `Reject(not-active)`, and the controller refreshes its roster |

#### 4.5 Device naming, and two tabs on one machine

- **Name:** derived from the user agent (`navigator.userAgentData` where available):
  "Mac · Chrome", "Android · Chrome", "iPad · Safari". The browser cannot know
  "MacBook", so the name is **editable** in the picker and stored in `localStorage`
  (`remote:device-name`).
- **Identity:** `deviceId` is a UUID in `localStorage`, shared by every tab of that
  browser, plus `tabId` in `sessionStorage`. The hub keys connections by
  `deviceId/tabId`.
- **Two tabs:** only one tab per browser may be the player. It takes a **Web Lock**
  (`navigator.locks.request('traktor-player', …)`). A second tab that finds the
  lock held starts in remote mode, controlling the first, through the hub like any
  other controller. When the first tab closes, the lock is released and the second
  tab can take over on its next play. The roster lists the browser once, as
  "MacBook · Chrome (2 tabs)".

#### 4.6 What the controller shows

**The full player bar**, the same component, driven by remote state:

- **Title, transport, time, Continuous, the preview chip and range, "Stop
  preview".** All read from the snapshot, and every button sends a command.
- **Playlist rows highlight the playing track**, because `RowClass` asks
  `Player.CurrentFileId`, which in remote mode answers from the snapshot.
- **Waveform with cue markers.** Cues come from the tablet's own NML, so no protocol
  is needed. Peaks:

  | Option | Cost | Gives up |
  |---|---|---|
  | **Share over the hub** (proposed) | `RequestPeaks(fileId)` → the player answers from its IndexedDB, ~2–3 KB of JSON for 400 buckets. The tablet caches the answer in its own IndexedDB under the same key and `VERSION` | Nothing, if the player has them. Otherwise a flat line with markers until it does |
  | Recompute on the tablet | No protocol | WAV: range reads through the proxy, i.e. Cloud Run egress, for a picture. Compressed: a full download and decode on the tablet's CPU and connection |
  | Flat line with markers only | Nothing | The picture |

  The `Waveform` component already has a mode for this: **while casting it mounts
  WaveSurfer with no media element and moves the cursor with `setTime`**
  (ADR 20261018-47 §1). Remote mode is the same thing: generalise `Casting` into
  "no local media", and send clicks as `Seek` commands.

### 5. Interaction with existing features

#### 5.1 Prerequisite: the queue moves from the page into the player

Today `Playlist.razor` owns the queue and **stops playback when it is disposed or
switched**. With a remote, the laptop's screen and what it plays come apart: the
tablet can start playlist X while the laptop shows playlist Y.

- **The `PlayerService` queue gains its own loader.** `LoadQueueAsync(playlistUuid,
  sort)` uses `CollectionService.GetPlaylistByUuid` + `Drive.ResolveTracksAsync`,
  the same calls the page makes. The page then **shows** the player's queue when it
  is on that playlist, and pushes re-sorts as today, rather than **owning** it.
- **Browsing no longer stops playback.** Leaving a playlist does not clear the
  queue. Playing a row on another playlist replaces it. That is a visible change to
  today's "switching playlists stops the music" (Open question 3).

#### 5.2 Cast

The laptop casting to the TV while the tablet controls it **needs no special
path**: commands reach the laptop's `PlayerService`, which already routes them to
`CastService`. The snapshot's `output` shows the TV. The constraints carried over
from adr-001 stay: **the laptop tab must stay open**, since it re-arms the token and
drives auto-advance, and the tablet **cannot start** a cast, because the picker needs
a gesture on the laptop. It can stop one (`StopCasting`).

#### 5.3 Autoplay: the one thing a remote cannot command

Browsers refuse `audio.play()` on a page that has had no user interaction since it
loaded. `player.js` already reports this as `blocked`. So a laptop that was just
reloaded (with `PlaybackMemory` having cued the last track, paused) **cannot be
started from the tablet** until someone clicks anywhere on the laptop's page once.
After that the permission sticks for the page's lifetime.

- The snapshot carries `state: Blocked`, and the tablet shows "MacBook needs a click
  before it can play".
- The laptop shows a banner: "Click anywhere to allow remote control".
- **Unverified:** Chrome's Media Engagement Index may allow autoplay on a site with a
  playback history, in which case this rarely bites. Measure in increment 0.

#### 5.4 Token expiry on the player while it is controlled remotely

This is where remote control makes an existing gap real (Context: local playback has
no token re-arm).

- **Before expiry:** the player calls `authRefreshToken()` (`prompt: ''`) 5 minutes
  ahead, the way `CastService` does. **Unverified:** whether GIS can complete it
  without a user gesture in a background tab, because popups without a gesture are
  normally blocked. `CastService` already has a "Silent token refresh was refused"
  path, which suggests it has been refused at least sometimes.
- **Local playback re-arm** (new, needed anyway): on refresh, swap the element's
  `src` to the fresh URL at the current position, as Cast's re-`LOAD` does.
- **If the refresh fails:** the snapshot carries `auth.needsSignIn: true`, and the
  tablet shows **"MacBook needs you to sign in again"**. The tablet *cannot* fix it.
  Lending the tablet's own token to the player through the hub would work
  technically, but it puts tokens into the protocol, which §3 rules out. Rejected.
- The hub connection does not need the token after connect. The player stays
  visible on the tablet even while it cannot fetch audio.

#### 5.5 PlaybackMemory restore

- **On the player:** unchanged. A restored track is published as `Paused`, so the
  tablet sees "MacBook · paused · Track X at 23:10" right after the laptop reloads.
  §5.3 applies to resuming it remotely.
- **On a controller:** the restore is **skipped while in remote mode**, so the
  tablet does not cue its own remembered track behind the remote bar. When the
  tablet becomes the player (transfer), the hand-off payload wins over its own
  memory.
- PlaybackMemory keeps observing only local playback, so a remote session does not
  overwrite the tablet's memory.

#### 5.6 Gig preview and continuous play

Nothing moves. The range rule, the end check (`player.js` locally, Cast ticks
remotely) and the advance all stay on the player. The tablet only sends
`StartPreview`/`StopPreview`/`SetContinuous`/`Next`/`Previous`, and mirrors the
chip and range. Because the queue lives on the player (§5.1), a preview keeps
advancing while the tablet is asleep.

### 6. Rollout, in increments

Everything is behind a flag (`remote:enabled` in `localStorage`, toggled from a
settings menu) until increment 3 has been used for real.

| # | Increment | What Toni can do after it |
|---|---|---|
| 0 | **Spike, half a day, no product code.** Verify with real tokens and devices: what `tokeninfo` returns for a `drive.readonly` token, `about.get` → `permissionId`, a WebSocket on Cloud Run with `--timeout=3600 --max-instances=1`, whether remote `play()` after a reload is blocked on laptop Chrome, silent `authRefreshToken()` in a hidden tab, and Android's system media controls for the TV (F1) | Know which assumptions hold |
| 1 | **See and nudge.** Hub, ticket auth, rooms, roster, `PlayerSnapshot`, `Play/Pause/Seek/Next/Previous/RequestSnapshot`, remote mode in `PlayerBar` (seek bar, no waveform yet), the "Playing on …" strip, idle-disconnect, `gcloud` flags | Open the tablet, see what the laptop plays, pause, seek, skip |
| 2 | **Waveform on the tablet.** `RequestPeaks`, the waveform's "no local media" mode, cue markers from the tablet's NML | Seek by cue on the tablet |
| 3 | **Pick another track.** Queue moved into `PlayerService` (§5.1), `PlayRow`, `SetContinuous`, `Start/StopPreview`, the "Now playing" queue view | Browse on the tablet, play any row of any playlist on the laptop, run a gig preview from the sofa |
| 4 | **Transfer and conflicts.** Device picker, `TransferTo` make-before-break, last-press-wins, Web Lock for tabs, device rename, `StopCasting` | Move playback between devices like Spotify |
| 5 | **Polish.** Token-expiry banners and the local re-arm (§5.4), the autoplay banner (§5.3), merging the Cast list into the picker | Leave it running for a whole set |

#### Testing

- **Playwright, two browser contexts against one server.**
  `browser.newContext()` twice gives two devices with separate `localStorage`,
  `sessionStorage` and IndexedDB, which is exactly the real split. Drive stays
  mocked per context with the existing `drive-mock.ts` routes.
- **Required change to the test harness:** `playwright.config.ts` runs the **client**
  dev server (`TraktorGoogleDrive.csproj`), and there is no ASP.NET host in the
  tests today. The hub lives in `TraktorGoogleDrive.Server`, so the remote specs need
  that project as their `webServer`. That is either a second Playwright project or a
  switch for the whole suite, and the existing specs mock `/api/proxy` with
  `page.route` either way.
- **Test seam for identity:** the server calls Google's `tokeninfo` and Drive, which
  `page.route` cannot intercept. A `RemoteAuth:FakeUsers` setting maps fixture tokens
  to user keys. It is **only honoured when `ASPNETCORE_ENVIRONMENT=Development`** and
  must fail startup if set in Production.
- **Specs:**
  1. The controller sees "Playing on …" and the title within 2 s of the player
     starting.
  2. Pause, seek, next and previous from the controller change the player's
     `<audio>` (asserted with `page.evaluate` in the player context).
  3. **Room isolation:** a third context with a different fake user sees no devices,
     and a crafted command from it does not reach the player.
  4. **No token leaks:** collect every WebSocket frame on the controller
     (`page.on('websocket')` → `framereceived`) and assert that none contains the
     player's fixture token or `token=`.
  5. **Reconnect:** `context.setOffline(true/false)` on the player. The controller
     shows "Looking for …" and recovers with the right state.
  6. **Conflict:** play on both, and the first one pauses.
  7. **Cast:** the player context with `cast-mock.ts`, and the controller shows
     "→ TV". A remote pause reaches the mocked receiver.
  8. **Autoplay:** run one spec with Chromium's `--autoplay-policy=user-gesture-required`
     and assert `Blocked` and the banner.
- **Hub unit tests** would be cheapest for routing and ticket validation, but the repo
  has no server test project (adr-001 §"Deliberately not done").
  `TraktorGoogleDrive.Server` is a plain ASP.NET project, so an xUnit project
  referencing it is straightforward. Proposed, for the ticket and room logic only.

## Consequences

**Positive**

- Spotify-style control with the account as the pairing. No codes, no QR.
- It fits the existing architecture: `PlayerService` already routes local vs Cast,
  and remote becomes a third route. The waveform already has a no-media mode.
- No tokens in the protocol, nothing personal persisted, and room membership is
  derived from a validated Google identity.
- Gig preview, continuous play and Cast keep working unchanged on the player.

**Cost / risk**

- **Socket-hours are billed.** About $0.09 per hour that any device is connected.
  Idle-disconnect keeps it to a few dollars a month. A forgotten open tab without
  idle-disconnect would cost ~$60 a month.
- **`max-instances` goes from 20 to 1.** That is fine for one user. Correctness under
  a brief second instance relies on republish-on-reconnect, not on the setting.
- **The request timeout goes from 300 s to 3600 s.** That also applies to the proxy,
  which is harmless because its slices are short.
- **The queue moves out of the playlist page.** That is a refactor of code that gig
  preview just shipped, and it changes "switching playlists stops the music".
- **Autoplay and token expiry are real limits**, not bugs: the tablet cannot click
  for the laptop. Both surface as explicit states rather than silent failures.
- **Payload:** the SignalR client adds to the WASM download. Size unmeasured.

**Deliberately not done here**

- Implementation of any kind.
- Fixing the audio proxy's tokens in Cloud Run request logs (noted in §3; it is an
  existing issue and deserves its own change).
- Volume (Open question 6), merging Cast into the picker (increment 5), and
  "resume on another device" through `appDataFolder` (option D's better use).

## Open questions

1. **Cost vs always-visible.** With idle-disconnect (§2.1), a laptop that has been
   paused and hidden for 15 minutes vanishes from the tablet's list. Without it, an
   open tab costs ~$0.09 an hour. Is 15 minutes right, or should the laptop stay
   visible whenever its tab is open? If always-visible matters more than a few
   dollars, option C (Firebase) becomes the better transport.
2. **A or C?** The recommendation is A (SignalR on Cloud Run). C has better
   reconnect, no instance pinning and ~$0 cost, but brings Firebase Auth, rules, an
   SDK, persisted data and a different GCP project. Agree with A?
3. **May browsing on the laptop stop playback?** Today switching playlists stops the
   music. Under remote control that becomes surprising (the tablet starts a set, and
   someone clicking around on the laptop silences it). Proposed: browsing never
   stops playback, and only playing something else does (§5.1).
4. **Is the OAuth consent screen in "Testing" mode** (limited to listed test users)?
   If not, anyone with a Google account can sign in to the app, and the
   `REMOTE_ALLOWED_USERS` allowlist is worth turning on.
5. **Is the tablet Android or iPad?** Android may already give TV transport controls
   for free through its system Cast controls (F1). An iPad gets nothing from Cast,
   and its Safari's autoplay and background rules are stricter.
6. **Volume:** wanted? There is no volume control in the app today. It would be a
   small `player.js` addition locally and `setVolumeLevel` on Cast.
7. **Should a controller also be able to play locally while in remote mode**
   (Spotify's "listen on this device too")? Proposed: no. A device is either the
   player or a controller, and switching is a transfer.
