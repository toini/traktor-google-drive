# ADR-001: Getting recorded sets onto the television

**Date:** 2026-08-17

**Status:** **Accepted** for Google Cast, which is implemented on this branch. The
native Android TV app is **rejected for now** — not on taste, but because the
auth mechanism the obvious design depends on does not support the scope this app
needs, and because building it needs a toolchain that is not on this machine.
Whether to install that toolchain is Toni's call, not mine; see
§"Deliberately not done".

## Context

The app streams a Traktor collection out of a personal Google Drive: recorded DJ
sets, 1–2 GB each, WAV and MP3. It runs as Blazor WebAssembly on Cloud Run, and
audio reaches the browser through a same-origin proxy,
`GET /api/proxy/drive/{fileId}?token={googleAccessToken}`, which exists only
because `<audio src>` cannot attach an `Authorization` header.

The want is to hear these on a Philips television running Google TV. Its built-in
browser is close to unusable, so "open the site on the TV" is not an answer.

Four properties of what already exists constrain every option below:

- **The proxy answers range requests in ≤8 MiB slices.** Cloud Run caps a
  non-streamed response at 32 MiB, and setting `Content-Length` makes the
  response buffered rather than chunked, so forwarding the real length used to
  500 every large file. `Content-Range` still reports the true total, so clients
  can seek. Anything that plays these files has to tolerate a server that
  answers `bytes=0-` with a short 206.
- **The access token lives about an hour; a set runs one to two.** Auth is the
  Google Identity Services *implicit* token flow with `drive.readonly`
  (`wwwroot/auth.js`). There is no refresh token, and `authGetToken()`
  deliberately treats "expired" as "signed out".
- **The token travels in the URL query string.** Whatever fetches the audio
  holds a bearer token inside a URL that goes stale on the hour.
- **The proxy rejects callers whose `Origin`/`Referer` names a foreign host,**
  and allows requests carrying neither. It compares host only, because Cloud Run
  terminates TLS.

And one property of this workstation:

- **There is no Android toolchain here.** Verified, not assumed: `java` and
  `javac` resolve to the macOS stubs and `java -version` fails with "Unable to
  locate a Java Runtime"; `kotlin`, `kotlinc`, `gradle`, `sdkmanager` and
  `flutter` are all absent; only `adb` is installed, from Homebrew. Standing one
  up is several GB of JDK plus Android SDK.

## Options

### 1. Google Cast from the existing web app

A Cast sender is plain JavaScript on the page. The phone or laptop stays the
remote; the television fetches and decodes the audio itself. The brief flagged
WAV support as the likeliest blocker, so that got checked first.

**WAV is a supported container.** [Supported Media for Google
Cast](https://developers.google.com/cast/docs/media) lists the container formats
as MP2T, MP3, MP4, OGG, **WAV**, WebM — a flat list, not scoped to a device
class. WAV (LPCM) also appears in the audio-codec list.

That said, the codec list is not the unqualified endorsement it looks like: it is
introduced as "Chromecast Audio, Google Home, and Google Home Mini support the
following list of codecs", and the page gives **no** audio-codec list for the
video devices (Chromecast with Google TV, Google TV Streamer, Chromecast
built-in televisions). `audio/wav` also never appears in the page's "Media type
strings" tables, which only cover MP4, WebM and passthrough. So for a Google TV,
WAV is documented at the container level and silent at the codec level. It is
almost certainly fine — the receiver is Chromium and Chromium decodes WAV — but
it is the one claim here I cannot source cleanly, and it is why the sender ships
Drive's own reported MIME type rather than a hardcoded string.

**CORS is not required for what we do.** The same page lists "Progressive
download without adaptive switching" as a supported delivery method, and puts the
CORS requirement only on adaptive-bitrate protocols ("With adaptive bitrate
streaming protocols, you must implement CORS") and on subtitle resources ("Your
subtitle resources must implement CORS"). One file, one URL, no subtitles: no
preflight, no `Access-Control-Allow-Origin`.

**Can the device reach the proxy?** This is where the brief's assumption needed
testing rather than accepting. The brief says a Cast device sends neither
`Origin` nor `Referer`, which the proxy already allows. Half of that is solid and
half is not:

- *`Origin` is absent, and there is a mechanism for why.* App id `CC1AD845`
  resolves — via `https://clients3.google.com/cast/chromecast/device/app?a=CC1AD845`
  — to `https://www.gstatic.com/cast/sdk/default_receiver/1.0/app.html`. That
  page's body is a bare `<cast-media-player>` element with no `crossorigin`
  attribute, and the CAF framework only mirrors `crossorigin` onto the media
  element when the author sets it, or when text tracks are loaded. No text
  tracks, no `crossorigin`, no `Origin`.
- *`Referer` is expected to be present, and this is the likely blocker.* Seven
  independent header captures across `CrKey/1.17`–`1.56`
  ([go-chromecast#101](https://github.com/vishen/go-chromecast/issues/101),
  [#210](https://github.com/vishen/go-chromecast/issues/210),
  [AirConnect#479](https://github.com/philippe44/AirConnect/issues/479),
  [#492](https://github.com/philippe44/AirConnect/issues/492),
  [app-aircast#73](https://github.com/hassio-addons/app-aircast/issues/73),
  [UMS#1373](https://github.com/UniversalMediaServer/UniversalMediaServer/issues/1373),
  [LMS-Cast#2](https://github.com/philippe44/LMS-Cast/issues/2), the last a
  tcpdump) show progressive loads with **no `Origin` and no `Referer`**, plus
  `Range: bytes=0-` and `Accept-Encoding: identity;q=1, *;q=0`. But every one of
  those media servers was plain **`http://`**, and under
  `strict-origin-when-cross-origin` Chrome sends no `Referer` at all on an
  HTTPS→HTTP downgrade. That explains the absence without saying anything about
  our case. We are HTTPS→HTTPS, where the same policy sends the bare origin. So
  `Referer: https://www.gstatic.com/` is **inferred, not observed** — nobody
  appears to have captured a Chromecast hitting an HTTPS media server.

  The proxy would have 403'd that. It now allows `www.gstatic.com` explicitly and
  logs `Origin`, `Referer` and `User-Agent` on every rejection, so if the
  inference is wrong the first real cast says so in one Cloud Run log line
  instead of presenting as "casting is broken".

**Ranged slicing is fine.** The `Accept-Encoding: identity;q=1, *;q=0` in those
real captures matches Blink's `resource_multi_buffer_data_provider.cc` exactly,
which confirms the device runs Chromium's normal media path — the same path the
desktop browser already uses successfully against this proxy. A short 206 makes
it request the next range; the requirement is that the `Content-Range`
denominator is the true total, which the proxy satisfies by forwarding Drive's
own header verbatim.

**No registration and no fee** are needed to use the Default Media Receiver.

**Token expiry mid-playback is the genuine cost.** The device holds the token
inside the media URL it was handed, and there is no receiver API for "here is a
fresh URL, keep playing". When the hour is up, the next range request 401s and
playback stops; a failed progressive load surfaces as
`detailedErrorCode: 104` / `MEDIA_SRC_NOT_SUPPORTED`, which is exactly what
go-chromecast#101 ends in. A sender *can* issue a new `LOAD` with
`currentTime` set to the current position, which is a reload rather than a
seamless swap — a short rebuffer, not a restart. That is the mechanism this
branch implements.

### 2. Native Android TV app (Kotlin, Compose for TV or Leanback, Media3/ExoPlayer)

ExoPlayer genuinely is better at ranged streaming than a browser, and a real
leanback UI beats a phone-as-remote. The brief asks me to confirm that the OAuth
device-code flow supports `drive.readonly` and describe it concretely.

**It does not, and that is the finding that decides this ADR.**
[OAuth 2.0 for TV and Limited-Input Device
Applications](https://developers.google.com/identity/protocols/oauth2/limited-input-device)
enumerates the scopes the flow supports, and the list is short:

| API | Scopes the device flow allows |
|---|---|
| OpenID Connect | `email`, `openid`, `profile` |
| Drive | `https://www.googleapis.com/auth/drive.appdata`, `https://www.googleapis.com/auth/drive.file` |
| YouTube | `https://www.googleapis.com/auth/youtube`, `.../youtube.readonly` |

`drive.readonly` is absent, and neither permitted Drive scope can do this job:

- **`drive.appdata`** exposes only a hidden per-application folder. The Traktor
  collection is not in it and never will be.
- **`drive.file`** is per-file access to files *the app created* or *the user
  explicitly opened with the app*. "Opened with the app" means the Google Picker
  or Drive's "Open with", both of which are web UIs needing a browser. On a
  television whose browser is the reason we are here, the app would be able to
  enumerate exactly nothing.

The device flow itself is otherwise a good fit and worth stating for the record,
because it is the right tool for a different problem: the app POSTs to
`https://oauth2.googleapis.com/device/code` with a **TVs and Limited Input
devices** client id, gets back `device_code`, `user_code`, a verification URL and
a poll `interval`, shows the code on screen, and polls
`https://oauth2.googleapis.com/token` with
`grant_type=urn:ietf:params:oauth:grant-type:device_code` until the user has
approved it on a phone. Codes last `expires_in` (1800 s by default), and
"refresh tokens are always returned for devices" — which would have solved the
1-hour-token problem outright. All of that is real. It just cannot ask for
`drive.readonly`.

Google's own page even offers, as its motivating example, that "a TV application
could use OAuth 2.0 to obtain permission to select a file stored on Google
Drive". That is the `drive.file` + Picker story, and it does not survive contact
with a TV that cannot run a Picker.

So a native app needs a different auth story. Two exist:

- **(a) Play Services native authorization.**
  `Identity.getAuthorizationClient()` / `AuthorizationClient.authorize()` (the
  successor to Google Sign-In) renders account selection and consent as native
  activities via Play Services, returns a `PendingIntent` when a grant is
  missing, and is not restricted to the device flow's scope list — so
  `drive.readonly` is askable, with no browser and no device code. This is the
  standard Android path and it is probably the answer. **I could not verify it
  works on a television.** There is an Android TV Community thread titled "Issue
  with Google Sign-In on Android TV using CredentialManager and
  AuthorizationClient" and issuetracker 258270061 "Google Sign-In error occurs on
  Android TV apps using Google…", and both are behind pages that did not render
  for me. The existence of those two titles is weak evidence that the consent UI
  on a TV is at least awkward. Marking this **unverified** rather than asserting
  it, because asserting it is exactly the mistake this ADR is documenting in the
  device-flow case.
- **(b) Move OAuth server-side.** Convert the browser app to the
  authorization-code flow, keep a refresh token in Cloud Run, and expose an
  authenticated API so the TV app never speaks to Google at all. This is the
  principled fix, and it happens to delete the token-in-URL problem for casting
  too. It is also a much larger change: a client secret in Cloud Run, somewhere
  to persist a refresh token in a service that scales to zero, and a pairing step
  between the television and the account.

Either way, **it cannot be built here.** See the toolchain note in Context.

### 3. Cheaper off-the-shelf routes

Investigated so we are not writing software to solve a problem someone already
solved. Verdicts are marked **verified** (sourced below), **reasoned** (follows
from a mechanism I am confident about but did not test) or **unverified**.

- **`catt` (Cast All The Things)** — **verified to exist and be maintained, and it
  is the same mechanism as Option 1 driven from a CLI.**
  `catt -d "<TV name>" cast "https://…/api/proxy/drive/<id>?token=…"` hands a URL
  to a Chromecast-built-in device. Last commit 2026-08-14, and recent commits are
  specifically about audio ("fix: Audio MIME types…", "feat: Surface
  artist/album/track/series metadata…") —
  [source](https://github.com/skorokithakis/catt). It guesses the MIME type from
  the URL basename, so a URL not ending in `.wav`/`.mp3` needs
  `--force-default`.

  This is not a competitor to Option 1 — it is the **cheapest possible test of
  Option 1's riskiest assumptions**, with no code and no UI. See §"Unverified".
- **Kodi with the Google Drive add-on** — **verified to work, verified
  unmaintained, and its sign-in is worth understanding.** `plugin.googledrive` by
  cguZZman is in the **official** Kodi repos for both Kodi 21 and 22
  ([Omega](https://mirrors.kodi.tv/addons/omega/plugin.googledrive/),
  [Piers](https://mirrors.kodi.tv/addons/piers/plugin.googledrive/)),
  declares `<provides>image audio video</provides>` so it appears under Music,
  and resolves audio to the direct Drive API URL with a `|Authorization=Bearer …`
  header rather than downloading first — so it streams and Drive's ranged
  downloads make seeking work. Kodi itself is a genuine leanback app
  (`android.software.leanback` + `LEANBACK_LAUNCHER`, touchscreen
  `required="false"` in
  [AndroidManifest.xml.in](https://github.com/xbmc/xbmc/blob/master/tools/android/packaging/xbmc/AndroidManifest.xml.in))
  and installs on a Google TV from the Play Store.

  **Its answer to browserless OAuth is a QR code**, and it is the best idea in
  this whole ADR that we are not using: the add-on POSTs to
  `<sign-in-server>/pin`, renders a QR encoding `<sign-in-server>/signin/<pin>`
  on the television, and polls `/pin/<pin>` until tokens arrive
  ([signin.py](https://github.com/cguZZman/script.module.clouddrive.common/blob/matrix/clouddrive/common/remote/signin.py),
  [addon.py](https://github.com/cguZZman/script.module.clouddrive.common/blob/matrix/clouddrive/common/ui/addon.py)).
  Scan with a phone, consent in the phone's browser, done. No typing on a remote,
  and no dependence on the device-flow scope list that killed Option 2 — because
  the add-on is not the OAuth client at all. The default server
  (`drive-login.herokuapp.com`) was probed live and still issues PINs that
  redirect to Google's real consent screen.

  Two reasons not to adopt it. First, **it is unmaintained**: last commit
  2023-01-21, 37 open issues. Second, that shared OAuth client asks for
  `drive.readonly`, which Google classes as a **restricted scope** needing an
  annual security assessment and imposing a user cap on unverified apps
  ([verification
  docs](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification)) —
  and issues
  [#341](https://github.com/cguZZman/plugin.googledrive/issues/341),
  [#344](https://github.com/cguZZman/plugin.googledrive/issues/344) and
  [#345](https://github.com/cguZZman/plugin.googledrive/issues/345) are users
  hitting "This app is blocked", unresolved. The escape hatch is real — the
  sign-in server is [open
  source](https://github.com/cguZZman/drive-login) and reads its client id and
  secret from env vars, so it could run on Cloud Run with *our* OAuth client,
  which also keeps our refresh token off a stranger's server. It holds PINs in an
  in-process cache, so it would need `max-instances=1`.

  And it still does not do this job: it knows nothing about `collection.nml`, so
  browsing by Traktor playlist and matching a recording to the set it came from —
  the point of this app — is gone. Note also that its Bearer token is baked into
  the resolved URL at play-start, so **it has exactly the same 1-hour expiry
  problem as casting**. That is not a Cast quirk; it is inherent to any client
  that receives a pre-signed URL, which is the strongest argument for the
  server-side-OAuth follow-up.
- **Kodi with a `Player.Open` push from this app** — **the strongest fallback,
  and it needs no Drive auth on the television at all.** Install Kodi, enable its
  HTTP interface (Chorus2 has shipped built in since v17, on port 8080), and
  `POST /jsonrpc` a `Player.Open` naming our proxy URL
  ([JSON-RPC API](https://kodi.wiki/view/JSON-RPC_API)). The collection browsing
  stays in this app, where `collection.nml` already lives, and the TV is just an
  output. Caveat: Kodi treats any `http(s)://` path as an internet stream and
  will not add it to the music library, so playback works and library browsing
  does not — which is fine, because we do not want Kodi's browser. Worth keeping
  on the shelf if Cast turns out not to play WAV.
- **DIAL** — **verified not applicable.** "DIAL is not a bit-streaming or screen
  mirroring API, it enables apps on a second-screen device to find and launch
  apps on a Fire TV (with an optional payload)" — [Amazon's DIAL
  docs](https://developer.amazon.com/docs/fire-tv/dial-integration.html), and the
  [spec site](https://www.dial-multiscreen.org/) agrees. The app must already be
  installed and registered in the DIAL registry. It carries no media and defines
  no playback control.
- **DLNA / UPnP** — **verified not applicable to a cloud-hosted source.** rclone
  states the constraint plainly for its own DLNA server: it "relies on UDP
  multicast packets (SSDP), which will thus only work on LANs"
  ([docs](https://rclone.org/commands/rclone_serve_dlna/)). Multicast discovery
  does not route, so a Cloud Run server can never be a DLNA source for the
  television. This is the exact inverse of Cast, where the *sender* must share
  the LAN but the device fetches over the internet — which is why Cast works from
  Cloud Run and DLNA cannot. Whether a Philips Google TV is a renderer out of the
  box is unverified and moot. (Also worth recording: **BubbleUPnP no longer
  supports Google Drive** — its current listing names only Box, Dropbox and
  OneDrive — so the advice still circulating on forums is stale.)
- **Chrome "Cast tab" from a laptop** — **verified to work, with caveats.**
  Google's own [support
  page](https://support.google.com/chromecast/answer/3228332) says to cast the
  *tab* rather than the screen so the audio plays on the TV, and notes that macOS
  15+ needs Chrome granted access in system settings. It is real-time mirroring
  of a rendered tab, so for 1–2 GB WAV it is the worst-quality option here and
  pins the laptop for the duration. Fallback, not a plan.
- **LocalCast (phone as sender)** — **verified Drive support.** Its listing
  states it streams from Google Drive to "Chromecast — all generations, Ultra,
  Google TV". No server, no re-upload. Costs: ads plus in-app purchases, WAV is
  not among the named formats, and it transcodes on the phone when the TV cannot
  play something — which for a 2 GB WAV is not a good place for transcoding to
  happen.
- **File Manager Plus** — **verified Drive *and* Android TV.** The only app in
  the file-manager category whose own listing claims both ("Cloud storage: Google
  Drive™…" and "Supported devices : Android TV, phone and tablet"), free. Whether
  it streams or pre-downloads a 1–2 GB file, and whether its player handles WAV,
  are both unverified — but it is a five-minute test on the TV itself.
- **A media server with a Drive mount (Plex / Jellyfin / Emby + rclone)** —
  **the easy path is verified gone.** Plex Cloud *was* "Plex + Google Drive" and
  it shut down on 2018-11-30 because Plex could not deliver it "at a reasonable
  cost" ([announcement](https://forums.plex.tv/t/plex-cloud-to-be-discontinued-nov-30-2018/306986)).
  Since then every variant needs an always-on machine at home running an rclone
  mount. That is a server to own in exchange for playback Cast gives us for free,
  and still no `collection.nml`.
- **YouTube Music uploads** — **verified to exclude WAV.** Uploads do play on the
  Android TV app, but the supported upload formats are "FLAC, M4A, MP3, OGG, and
  WMA" ([help
  page](https://support.google.com/youtubemusic/answer/9716522)) — no WAV — and
  uploading cannot be done from the mobile app. So the MP3 sets would work and
  the WAVs would need transcoding to FLAC and re-uploading by the gigabyte, in
  exchange for losing any DJ-set-shaped browsing.
- **The Google Drive Android app, sideloaded onto the TV** — **unverified.** No
  evidence either way of a leanback build. A related verified negative: the Drive
  *mobile* app has no Chromecast support at all. Even if it ran, it browses raw
  folders, which is the thing this app exists to avoid.

**Not investigated, and stated so rather than guessed:** VLC for Android, Solid
Explorer, FX File Explorer, Nova Video Player, Just Player and MX Player were on
the list to check for Drive support and none of them got confirmed either way.
Jellyfin and Emby specifics likewise. If any of those matter, they are unchecked.

None of these beats Option 1 once you count what it gives up. Four are worth
remembering: **`catt`**, because it tests Option 1 for free; **Kodi +
`Player.Open`**, because it is a real fallback that keeps the collection UI here;
**Kodi's QR pairing**, because it is the only off-the-shelf answer to Drive auth
on a browserless device; and **tab casting**, because it needs no code at all.

## Decision

**Google Cast from the existing web app, using the Default Media Receiver.**

It needs no new toolchain, no app registration, no fee, and no second copy of the
collection-browsing UI. The device does the fetching and decoding, so a 1–2 GB
file never passes through the browser that is acting as the remote. And unlike
the native app, every hard question about it could be answered from documentation
or from other people's packet captures before writing code.

Five sub-decisions are worth recording.

### 1. The Default Media Receiver, not a custom one

A custom Web Receiver would let us handle the token ourselves and would remove
the `www.gstatic.com` guesswork. It also means registering an application in the
Cast Developer Console, hosting a receiver page, and registering the device for
development. For one household and one listener, the default receiver's
constraints are cheaper than that. Revisit only if the receiver turns out not to
play WAV.

### 2. The play button routes; there is no second set of controls

`PlayerService` already existed as "the single place components ask to play".
While a Cast session is connected it delegates to `CastService`; when it is not,
it drives the local `<audio>` exactly as before. So `AudioTrack` did not need to
learn about casting — it still calls `Player.ToggleAsync` — and neither did the
playlist's "currently playing" row highlight. The alternative, a second cast
button on every row, adds a column and makes the user choose an output per track.
Cast's own UX guidance puts one button in the app bar and routes playback to it;
that is also the smaller change.

Connecting silences the local element, because a set playing in two places at
once is the obvious failure mode.

### 3. An absolute URL, and Drive's own MIME type

`DriveAudio` is documented as the one place that knows how audio reaches a Drive
file, so it gained `AbsoluteUrlFor` rather than anyone building a URL by hand: a
relative path means nothing to a device fetching from elsewhere on the network.
`contentType` is required on `MediaInformation`, and `FileEntry.DriveFileMimeType`
already carries what Drive itself reports, so that is what gets sent —
`audio/wav` only as a fallback for a file Drive did not type.

### 4. Re-arm the token rather than let the set die at the hour

`CastService` watches the position ticks it already receives. Five minutes before
the stored expiry it asks `authRefreshToken()` — a new silent
`requestAccessToken({ prompt: '' })` in `auth.js` — for a fresh token, then
re-issues `LOAD` with `currentTime` set to the current position. A short rebuffer
mid-set, instead of playback stopping an hour in.

If the silent refresh is refused (no live Google session in the sender browser,
third-party cookies blocked), it says so once and stops trying. Reporting the
same refusal once a second for the rest of a two-hour set is not a useful log.

### 5. The proxy's caller check had to change, and now says what it rejected

`IsSameOrigin` became `IsAllowedCaller`: it additionally accepts
`www.gstatic.com`, it runs **before** the token/fileId validation so a rejection
never depends on the rest of the request being well formed, and it logs all three
of `Origin`, `Referer` and `User-Agent` when it rejects. The logging is the point
— who a Cast device claims to be is only knowable from a real device's first
attempt, and one log line beats another afternoon of searching.

Verified by hand against the Server project on `localhost:5399` (no token, so
`400` means the caller check passed and `403` means it rejected):

| Request | Status |
|---|---|
| no `Origin`, no `Referer` | 400 |
| `Referer: http://localhost:5399/playlist/x` | 400 |
| `Referer: https://www.gstatic.com/` | 400 |
| `Origin: https://www.gstatic.com` | 400 |
| `User-Agent: CrKey/1.56.500000`, no `Origin`/`Referer` | 400 |
| `Referer: https://evil.example/` | **403** |
| `Origin: https://evil.example` | **403** |

The two rejections logged
`Rejected proxy request. Origin= Referer=https://evil.example/ UserAgent=curl/8.7.1`
and its `Origin` counterpart.

This does widen the endpoint: a page on `www.gstatic.com` can now embed our proxy
URL. Weighed against the existing posture — the proxy relays with the *caller's*
token, so it can reach nothing the caller could not already reach, and a request
with no headers at all was always allowed — the check is defence against casual
third-party embedding rather than a load-bearing control. Adding one Google-owned
host to it does not change what an attacker would need, which is the token.

## Consequences

**Good:**

- Playback on the television needs no new build tooling, no APK, no sideloading
  and no Play Store account.
- The 1–2 GB stream goes Cloud Run → television. The laptop or phone acting as
  remote does not carry the audio.
- A cast session survives navigation: browsing to another playlist stops the
  local element and leaves the TV playing, which is what you want while queueing
  up the next set.
- The whole sender is testable without hardware. 13 new Playwright tests drive a
  fake Cast SDK (`tests/e2e/cast-mock.ts`) and assert what actually goes to the
  device — absolute URL, content type, `BUFFERED` stream type, Traktor title and
  artist — plus pause-not-reload, error surfacing, and the token re-arm.
- The proxy tells us why it rejected something, which it did not before.

**Costs and risks:**

- **Chrome or Edge on the sender.** The Cast sender SDK is Chromium-only;
  Firefox and Safari cannot start a session. The button stays visible and
  explains itself rather than disappearing.
- **The sender tab must stay open** for any set longer than the token, because
  the re-arm runs in the page. Close the tab an hour in and the set stops when
  the token does.
- **`Referer: https://www.gstatic.com/` is an inference.** If the real device
  sends something else, the first cast 403s. The mitigation is a log line, not a
  fix, and the fix is one string.
- **WAV on a Google TV is documented at the container level only.** If the
  receiver refuses `audio/wav`, the fallbacks in order are: a custom Web
  Receiver, transcoding, or Kodi driven by a `Player.Open` push from this app
  (§Options 3) — that last one keeps the collection UI here and is the cheapest
  of the three.
- **The re-arm is a reload.** Expect a rebuffer around the 55-minute mark of a
  long set, and a resume that is accurate to the position the receiver last
  reported, not to the sample.
- **Silent token refresh is not guaranteed.** `prompt: ''` fails if the sender
  browser has no live Google session. It degrades to one honest error.

**Deliberately not done:**

- **The native Android TV app.** The auth premise it needed does not hold
  (Option 2), and building it means installing a JDK and the Android SDK on a
  machine that has neither. That is a decision for Toni; I stopped rather than
  installing several gigabytes on a hunch. If it is wanted, the sequence is:
  prove `AuthorizationClient` consent is navigable with a remote on the actual
  Philips set **before** writing any UI, or commit to server-side OAuth first.
- **Server-side OAuth with a refresh token.** This is the change that deletes
  both the token-in-URL problem and the sender-tab-must-stay-open problem, and it
  is a prerequisite for a native app that does not do its own OAuth. Out of scope
  here; it changes the auth model for the whole app. Worth noting how general the
  problem is: Kodi's Drive add-on bakes its Bearer token into the resolved URL
  the same way and has the same one-hour cliff, so this is not a Cast quirk but
  something every client handed a pre-signed URL inherits. That makes it the
  highest-value follow-up on this list.
- **A custom Web Receiver**, queueing a whole playlist to the device, and volume
  control from the casting bar. All cheap follow-ups once one file is proven to
  play.
- **No automated test of the proxy's caller check.** The Playwright harness runs
  the *client* project and intercepts `/api/proxy/drive/**`, so the real proxy is
  not exercised by any test in this repo — that predates this change. Adding a
  second `webServer` for the Server project would fix it, and was not done here
  because both projects building concurrently contend on the client's `obj/`. The
  table above is hand-verified instead. A small server-side test project is the
  right follow-up.

**Unverified, and stated as such:**

**No audio has ever reached a television.** Everything above is documentation,
other people's packet captures, and a fake SDK. The three things a single real
cast would settle, in the order they would fail:

1. Whether the proxy sees `Referer: https://www.gstatic.com/` — and therefore
   whether the allowlist added here is the right one, or whether the `Referer`
   check should simply not apply to this endpoint.
2. Whether the receiver plays a 1–2 GB `audio/wav` served in 8 MiB slices.
   Google documents progressive download as supported but publishes no size
   limit and no server-side requirements for it, so this is the one with no
   paper answer at all.
3. Whether the re-arm actually resumes rather than restarting.

**Items 1 and 2 can be settled without any of the code on this branch.** `catt`
(§Options 3) casts a bare URL from the command line:

```
pip install catt
catt -d "<TV name>"  cast "https://traktor-google-drive-…run.app/api/proxy/drive/<id>?token=<token>"
```

That exercises the identical path — same receiver, same proxy, same slicing —
with no sender code involved, and the Cloud Run log answers item 1 either way.
Do that before debugging anything in `cast.js`: if a bare `catt` cast fails, the
problem is the receiver or the proxy, not the sender. Item 3 needs the branch,
because the re-arm is the only part `catt` has no equivalent for.

Also unverified: that this machine can even run the Server project as shipped.
It targets `net9.0` and only the .NET 10 runtime is installed here, so
`dotnet run` fails with "You must install or update .NET to run this
application" unless `DOTNET_ROLL_FORWARD=LatestMajor` is set. The proxy probe
above was run that way. Cloud Run builds its own image, so this affects local
work only, but it is a trap worth knowing about.
