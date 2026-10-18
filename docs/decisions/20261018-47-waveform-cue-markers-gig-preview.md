# Waveforms for every format, Traktor cue markers on them, and a gig-preview play mode

Date: 2026-10-18

## Status

Accepted 2026-10-18 by Toni, with the choices recorded under
§"Decisions Toni made" below. It is implemented on `toni/feat/gig-preview` in the
same change as this ADR, on top of `master` at `4d35512`, which already includes
the playlist-columns work (playlist index, track colours, Comment 2).

**Status note (implementation).** Everything in §1–§4 is implemented, except the
deferred items listed under "Deliberately not done here". Coverage is e2e only:
`tests/e2e/gig-preview.spec.ts` adds 12 tests, `player.spec.ts` has one rewritten
test, and the fixture gained a "Gig Prep" playlist with `CUE_V2` cues. There is no
xUnit project; see Consequences.

**Where this file lives, and why.** The repo's only earlier ADR is
[`docs/adr-001-tv-playback.md`](../adr-001-tv-playback.md), named with a running
number. This one follows agent-farm's convention instead (`docs/decisions/`,
`yyyymmdd-<2 random digits>-slug.md`). The random digits exist so that parallel
branches never pick the same number, and this repo now has parallel agent branches
too. Toni chose to leave `adr-001` where it is and unchanged: ADRs are append-only,
and moving it would be churn unrelated to this change. A later move to
`docs/decisions/20260817-01-tv-playback.md` would be a pure `git mv`.

## Decisions Toni made

1. **Waveform for compressed files: decode in the browser** (§1, option C), with a
   150 MB cap. This is the approach the repo used before `c8db8cc`, done properly.
   Back then `wavesurfer-blazor.js` handed WaveSurfer `url: wavUrl` and let it fetch
   and decode in the browser. But it fetched through the proxy, and with no
   `peaksUrl` it drew a hard-coded fake peak array, which is why it was removed. The
   idea is the same now. The download goes direct to Drive (no Cloud Run egress, no
   second proxy download), and the peaks are real and cached. The `AUDIO_ID` spike
   (option F) is a follow-up, not part of this change.
2. **No loop cue: play to the end.**
3. **No load cue: start at 0.**
4. **Auto-advance is a "Continuous" toggle in the player bar**, off by default and
   remembered in `localStorage` (`player:continuous-play`). **Gig preview always
   advances**, whatever the toggle says. This replaces §3's "auto-advance only in
   gig preview" default.
5. **Markers: WaveSurfer's Regions plugin** (§2).
6. **`adr-001` stays where it is.** Tests are e2e only.

## Context

Toni: *"I tried to wire up WaveSurfer so we could see the waveform when playing. What
happened to that? It's just a line now. Can we bring the waveform back and include
markers from NML too? I want an autonomous play mode that starts at the first cue
point of type 'load', stops at the end of the last loop cue point, then switches to
the next track, so I can preview the set I'm planning to play at a gig."*

### Why the waveform is a line

This was checked against the code and the running app, not inferred from the
history.

- **The line is a deliberate fallback.** `Components/PlayerBar.razor` renders
  `<Waveform>` only when `Player.CurrentIsWav && !Player.IsCasting`. Otherwise it
  renders `<input type="range" class="player-seek">`, a seek slider, and that is the
  line. `DriveAudio.IsUncompressed` decides by filename: `.wav`, `.aif` and `.aiff` get
  a waveform and everything else gets the slider. That rule was added in `74a3ab2` on
  purpose ("Compressed recordings would have to be fully decoded, so they get a plain
  seek bar instead"), and `tests/e2e/player.spec.ts` asserts it ("falls back to a plain
  seek bar for a compressed recording").
- **Seen in the running app.** I ran the e2e suite on this base and it passed 70/70.
  The end-of-test screenshot for the `.mp3` recording shows the orange slider. The
  `.wav` one shows WaveSurfer bars.
- **It hits about half the collection.** The Traktor collection in the parser repo's
  test data (1,545 entries) has 693 `.wav` and 10 `.aif`/`.aiff`, but also **630
  `.mp3`, 186 `.flac` and 6 `.m4a`**. Every one of those 822 shows a line. Ordinary
  tracks in a gig playlist are mostly MP3 and FLAC, so a gig playlist is almost
  all line.
- **Casting always shows a line**, whatever the format, because of the
  `!Player.IsCasting` condition.
- **The deployed build is not stale.** `waveform.js`, `player.js` and `cast.js` on
  the Cloud Run service are byte-identical to `069f1dc`, and
  `/lib/wavesurfer/wavesurfer.esm.js` returns 200. So Toni's screenshot came from a
  build that already had WaveSurfer, and the line means either a compressed track or
  a cast session. Which one it was cannot be known without the track. See Open
  questions.
- The WaveSurfer setup before `c8db8cc` never drew real data either. It was called
  with 2 of its 4 arguments and drew a hard-coded 65-sample dummy array.

**The `player.spec.ts:43` failure is not WaveSurfer.** That test expects
`5:12` (Traktor's `PLAYTIME_FLOAT`), but the mock serves a **2-second** WAV
(`makeWav(seconds = 2)`). `PlayerService.OnProgress` replaces the NML duration with
the element's real one as soon as `durationchange` fires. Whenever metadata arrives
before the assertion's first poll, the bar reads `0:02`, which is the reported
failure. It is a race in the test, not in the app: it passed here 70/70 and 8/8
repeated, and the `0:02` is visible in the screenshots above. With real files the
two durations agree. Fixing it (a mock that honours `PLAYTIME`, or asserting
before metadata loads) is small but out of scope here.

### What the NML gives us

Each cue is an `ENTRY/CUE_V2` with `NAME, DISPL_ORDER, TYPE, START, LEN, REPEATS,
HOTCUE`. `START` and `LEN` are in **milliseconds** (traktor-nml tools multiply
seconds by 1000 when writing). Checked against the 1,545-entry collection:

| TYPE | Meaning | Count | `HOTCUE` |
|---|---|---|---|
| 0 | Cue | 2,752 | 0–7 |
| 1 | Fade-in | 7 | 1–3 |
| 2 | Fade-out | 30 | 2–7 |
| 3 | Load | 1,146 | 0–7 (990 of them on slot 1) |
| 4 | Grid | 1,543 | always `-1`, has a child `<GRID BPM>` |
| 5 | Loop | 1,167 | 1–7, `LEN` > 0 |

`HOTCUE` is a 0-based slot, so `0` is shown as hotcue 1, and `-1` means the cue is
not a hotcue. Traktor 4 adds `COLOR="#FFFFFF"` to some cues (1,500, all
`NAME="AutoGrid"`, TYPE 0). The e2e fixture `tests/e2e/fixtures/collection.nml`
has **no** `CUE_V2` at all, so tests will need cues added to it.

**The `TraktorNmlParser` package already exposes cues.** `0.0.11` (the version
pinned here, built from `toini/traktor-nml-parser@52c3bfa`) has
`Track.CuePoints : List<CuePoint>`, already **sorted by `Start`**, with
`Start, Type (CuePointType enum: Cue, FadeIn, FadeOut, Load, AutoGrid, Loop), Name,
Hotcue, Length, Order, Repeats`. Playlists hold the same `Track` objects, so cues reach
`FileEntry.Track` with no change to the package. It does not read cue
`COLOR`. If that is ever wanted, the playlist-columns branch already keeps the
parsed `XDocument` in `CollectionService` (it reads track `COLOR` from it), and cue
colours can be read from that same document without parsing the file twice. No
package change and no package publish is needed.

The cue shapes that matter for gig preview, in that same collection:

| Shape | Tracks |
|---|---|
| ≥1 load **and** ≥1 loop | 682 |
| load, no loop | 419 |
| loop, no load | 57 |
| neither | 387 |
| more than one load cue | 43 |
| last loop ends at or before the first load | 1 |
| "last loop by START" ≠ "loop ending latest" | 0 |

So "no loop" (806, 52%) and "no load" (444, 29%) are the common cases, not edge
cases. That collection is a 2025 snapshot, and a gig-prep playlist is probably
better annotated, but the defaults below must still be good ones.

### Constraints carried in

- **Cloud Run bills internet egress per GiB.** All audio goes through
  `/api/proxy/drive/{id}` in ≤8 MiB slices, so every byte the browser plays is
  billed. A typical MP3 here is ~16 MB (median `FILESIZE`, 7 min). A 20-track gig
  preview is ~320 MB through the proxy, and anything that downloads a track twice
  through the proxy doubles that.
- **The browser can already fetch Drive directly.** `DriveService` downloads the
  5 MB `collection.nml` from `googleapis.com/drive/v3/files/{id}?alt=media` with a
  Bearer header, from WASM. The proxy exists only because `<audio src>` cannot send
  that header (`DriveAudio`). `fetch()` can, so **a download that is not for
  `<audio src>` does not need the proxy and costs no Cloud Run egress.**
- **Recorded sets are 1–2 GB.** Decoding a whole set in the browser is not an
  option. `decodeAudioData` expands to Float32, so 1.4 GB of WAV needs ~2.8 GB.
- **The Cast path** ([adr-001](../adr-001-tv-playback.md)): the TV fetches media
  itself through the proxy with a token baked into the URL. The sender re-arms with a
  fresh `LOAD` at the current position before the hour. Position ticks reach the
  sender about once a second, and the sender tab must stay open.
- **Nothing advances to a next track today.** `PlayerService`, `player.js`,
  `cast.js`, `CastService` and `PlayerBar` have no next/previous logic. On `ended` the
  track stays loaded and paused, by design. So auto-advance is new, general
  capability, not something hidden inside gig preview.
- **Table order is changing under us.** The playlist-columns branch adds
  `FileEntry.PlaylistIndex` (Traktor's entry order) as the default sort, and the
  user can still re-sort. "Next track" has to follow whatever order the table shows.

## Decision

### 1. Compressed formats: decode in the browser from a direct Drive download, at a low sample rate

For a non-WAV track up to a size cap (proposed **150 MB**, using NML `FILESIZE`,
which is in KB), `waveform.js` will:

1. `fetch` the file **directly from `googleapis.com`** with the Bearer token, not
   through the proxy;
2. decode it with `OfflineAudioContext(…, sampleRate = 8000).decodeAudioData`,
   which resamples as it decodes;
3. reduce the result to the same peak array the WAV sampler produces, and cache it
   in the same IndexedDB store under the same key and a new `VERSION`;
4. hand it to the **existing** `mount(container, media, peaks, duration)`, so
   WaveSurfer stays attached to the one `<audio>` element and never fetches anything.

WAV/AIFF keeps the range-read sampler as it is. It is the only approach that works
on a 2 GB set, and it already works. Compressed files over the cap (in practice the
MP3 recordings of whole sets) get no picture. They show the cue markers from §2,
which need no audio at all, on a flat line.

Memory: a 7-minute stereo track at 8 kHz is ~27 MB of Float32 (at 44.1 kHz it would
be ~150 MB), plus the ~16 MB file, and it is freed after the peaks are taken.
Egress: **zero extra on Cloud Run**. The user's own connection downloads the track
once more from Google, and only on the first play, because of the cache.

Options considered:

| Option | What it costs | What it gives up |
|---|---|---|
| **A. Keep the slider** (status quo) | Nothing | The feature. Half the collection, and nearly all of a gig playlist, stays a line. |
| **B. WaveSurfer decodes it** (`url:` instead of `peaks:`) | A **second download through the proxy**, so double Cloud Run egress for every compressed track. WaveSurfer also decodes at the device rate (~150 MB for 7 min), and with `media` + `url` it points the element's `src` at that URL, which fights `PlayerService`'s ownership of the element. | It is the least code, and it is what Toni first tried. |
| **C. Browser decode from a direct Drive fetch** (chosen) | ~60 lines in `waveform.js`. One extra download per track on the user's connection (first play only). Needs a size cap. | Full detail for huge compressed sets. Those get their cue markers on a flat line instead. |
| **C′. As C, and also play from the downloaded blob** | Rewires `player.js` to play a `blob:` URL. Playback waits for the whole download (~1–3 s for 16 MB). The file is held in memory. Cast cannot use a blob URL, so it stays on the proxy anyway. | Nothing from C, and it **removes Cloud Run egress for local playback of normal tracks entirely**. It is the biggest cost lever on the table, but it changes how playback works, so it gets its own decision later rather than riding along here. |
| **D. Server-side peaks endpoint** (`/api/peaks/{id}`) | A decoder in the server: `ffmpeg` in the image (the image is plain `aspnet:9.0` today), or NLayer for MP3 plus separate FLAC and AAC handling. Cloud Run CPU and memory per first play, and a slow first request for large files. A new endpoint carrying the token, and there are no server tests in this repo (adr-001 §"Deliberately not done"). | Nothing on egress (peaks are a few KB), and it **covers huge compressed sets too**. Strongest coverage, heaviest change. |
| **E. Range-read sampler for compressed files** | MP3: find a frame sync at each offset and decode a few frames, with VBR offsets mapped through the Xing table of contents. FLAC: splice the STREAMINFO header in front of every window. M4A: not feasible without the `moov` index. High complexity, ~3–6 MB of reads per track. | Format coverage, and it is fragile. Its only win over C is very large MP3 sets. |
| **F. Traktor's own `AUDIO_ID`** | **Unverified.** Every analysed `ENTRY` carries `AUDIO_ID`, a 256-byte blob (1,535 of 1,545 entries). Decoded, it looks like a 512-step loudness envelope: `Zerror` shows a quiet intro, a saturated body, a breakdown and a fade to `00`s. It may instead be a fingerprint. | If it is an envelope: **zero downloads, instant, every format, any length, and it works while casting.** Resolution is ~1 s per step for an 8-minute track and ~14 s for a 2-hour set. |

**Chosen: C** (Toni, see §"Decisions Toni made"). Follow-ups, not part of this change:

- **F spike**: plot `AUDIO_ID` next to real peaks for three WAV tracks. If it
  matches, it becomes the instant placeholder drawn before real peaks arrive, the
  waveform for over-cap sets, and the waveform while casting.
- **C′**: the follow-up that actually lowers the bill.
- **D**: the fallback if full-detail waveforms for compressed sets are wanted.

How it is built: `waveform.js` `computeCompressedPeaks` makes one direct download
and decodes it with `OfflineAudioContext` at 8 kHz. The peaks go into the same
IndexedDB store as the WAV sampler's. `DriveAudio.CanDecodeInBrowser` applies the
cap from NML `FILESIZE`, or bitrate × length when that is missing. An entry with
neither is decoded only if it is shorter than 30 minutes. A file over the cap, or
one that fails to decode, still mounts WaveSurfer on a flat line, so markers and
click-to-seek work for every track. The old `<input type=range>` slider remains
only as a fallback for when no playlist row is loaded.

**While casting**, the bar mounts WaveSurfer with *no* media element
(`peaks` + `duration` only) and moves its cursor with `setTime` from the Cast
position ticks. Clicks go to `Player.SeekAsync`, which already routes to the TV.
It uses **cached peaks only**. adr-001's rule is that this browser never fetches
the audio while casting (`cast.spec.ts` asserts it), so a track never played
locally shows its markers on a flat line on the TV.

### 2. NML markers: WaveSurfer's Regions plugin, coloured by cue type

Read cues from `FileEntry.Track.CuePoints` (no package change). Draw them with
WaveSurfer's **Regions plugin**, vendored next to `wavesurfer.esm.js` at the
**same version**. `069f1dc` names regions as the reason WaveSurfer was chosen, and
Toni picked the library. Regions are read-only (`drag: false, resize: false`), and
clicking one seeks to its start.

| Cue | Drawn as | Colour (Traktor-like) |
|---|---|---|
| Cue (0) | line | blue `#3d8bfd` |
| Fade-in / fade-out (1, 2) | line | orange `#ff8c1a` |
| Load (3) | line | yellow `#ffd21f` |
| Loop (5) | region `START`→`START+LEN`, translucent | green `#3fbf5a` |
| Grid (4) | **hidden** (1,543 tracks have one, at the first beat, and it adds no information here) | — |

- **Label**: hotcue number `HOTCUE+1` when `HOTCUE ≥ 0`, plus `NAME` unless it is
  Traktor's placeholder (`n.n.`, `AutoGrid`). The loop's length is shown in beats
  when `TEMPO/BPM` is known (the parser exposes `Bpm`), otherwise in seconds.
- **Time base**: `START/1000` seconds against the same `duration` WaveSurfer is
  given. Markers past the end are clamped and drawn, not dropped.
- **Gig preview** also dims everything outside the preview range (§4) with two
  grey regions, so the range shows on the waveform itself.
- Markers are drawn even when there are no peaks (over the cap, decode failed,
  casting without a cache). In that case WaveSurfer is mounted with a flat peak
  array, so the cue layout is always visible.
- The vendored core turned out to be **wavesurfer.js 7.12.11**: its file is
  byte-identical to that release's `dist/wavesurfer.esm.js`. The Regions plugin is
  vendored from the same release, as `lib/wavesurfer/plugins/regions.esm.js`.

Options considered:

| Option | Cost | Gives up |
|---|---|---|
| **Regions plugin** (chosen) | One more vendored file, version-matched. The vendored `wavesurfer.esm.js` has no version banner, so it has to be identified from its source first. | — |
| Our own DOM/SVG overlay, positioned in % | ~50 lines and no dependency. It handles resize for free. | Duplicates what the chosen library already does. `069f1dc` exists because a hand-rolled renderer replaced Toni's library choice once already. |
| Painting into WaveSurfer's canvas via render hooks | Fragile across versions, and it redraws on every frame | — |

### 3. Auto-advance is a general `PlayerService` capability, fed by the table

- **The playlist page owns the order.** Whenever its rows or sort change, it calls
  `Player.SetQueue(SortedFiles())`, so the queue is always the order on screen,
  including the playlist-columns default (`PlaylistIndex` ascending). Rows with no
  `DriveFileId` are **skipped**, never queued. Navigating away already stops
  playback (`Playlist.Dispose`), and it clears the queue too.
- `PlayerService` gets `NextAsync()` / `PreviousAsync()` (the next or previous queued
  entry after the current file id; if the current file was removed by a re-sort, the
  entry nearest its old position) and `ContinuousPlay` (on or off). The player bar
  gets ⏮ ⏭ buttons. The queue is matched by `FileEntry` reference first, because
  the same Drive file can sit in a playlist twice.
- **Policy (Toni's decision 4): a "Continuous" checkbox in the player bar**, off by
  default and remembered in `localStorage`. With it off, a finished track stays
  loaded and paused, as before (`74a3ab2`: a finished *set* is one you want to
  scrub back into). Gig preview always advances, whatever the checkbox says.
- **Local** advance is driven by `ended` (or by the preview end, §4). **Cast**
  advance is driven by the sender: a finished item (or the preview end) makes
  `CastService` send a new `LOAD` for the next file, with `currentTime` set to its
  start. `cast.js` now reports `idleReason` (`FINISHED` vs `INTERRUPTED` vs
  `ERROR`), because `IDLE` alone means both "not started" and "done".
  `CastService` raises `Finished` on `FINISHED`. A receiver that reports no reason
  still counts as finished if it went idle within 1.5 s of the end.

Options considered for the order:

| Option | Cost | Gives up |
|---|---|---|
| **Page pushes the order on every change** (chosen) | One call in `Playlist.razor`, next to the sort code | — |
| Order snapshotted at the moment Play is pressed | Simplest | A re-sort mid-preview is silently ignored, which contradicts "follow the table's order" |
| `PlayerService` pulls the order through a callback into the page | No push calls | The service holds a reference into a page that may already be disposed |

Options considered for Cast:

| Option | Cost | Gives up |
|---|---|---|
| **Sender-driven `LOAD` per track** (chosen) | Needs the sender tab open, which adr-001 already requires for the token re-arm. Small gap between tracks (a rebuffer). | Truly autonomous TV playback |
| Cast queue: `QUEUE_LOAD` with per-item `startTime` + `playbackDuration` + `preloadTime` | The receiver would run the preview on its own, near-gaplessly. But every item's URL carries a token that dies within the hour, the re-arm logic is single-item, and it is **unverified** that the Default Media Receiver honours `playbackDuration`. | Simplicity. Worth a spike once a real TV has played anything at all (adr-001's "No audio has ever reached a television"). |

### 4. Gig preview: semantics and edge cases

`GigPreview.RangeFor(Track, double durationSeconds) → (Start, End, Notes)` is a pure
function in `Models/`, so all of the rules below sit in one testable place:

- **Start = the earliest load cue** (`TYPE=3`, by `START`; 43 tracks have more than
  one).
- **End = the end (`START+LEN`) of the last loop cue** (`TYPE=5`). "Last" is taken
  as *the loop that ends latest* (`max(START+LEN)`). In this collection that is the
  same loop as "the last by `START`" in every track (0 differ), and it does not
  depend on how loops overlap.
- **The end check runs in JS, not over interop.** `player.js`'s
  `play(fileId, url, startAt, stopAt, rebound)` arms the bound for a newly loaded
  file, or with `rebound` for the file already loaded (a preview starting on the
  current track, or a resume during a preview)
  (`setBounds(stopAt)` clears it) and checks `currentTime >= end` on every `timeupdate`
  (~4 Hz, so ≤250 ms late). `PlayerService.OnProgress` is throttled to whole
  seconds, so checking in C# would overshoot. On Cast the check runs on the ~1 Hz
  position ticks, so up to ~1 s late, which is fine for previewing.
- **Seek to the start once metadata is known** (`loadedmetadata`). The proxy's
  ranged slices already support seeking.

| Edge case | Options | Chosen |
|---|---|---|
| **No load cue** (29%) | (a) start at 0; (b) start at the first non-grid cue; (c) start at hotcue 1 | **(a) 0.** That is where Traktor itself puts the playhead when there is no load marker. (b) looks clever, but 1,080 tracks have an ordinary cue *before* their load marker, so "first cue" is not a reliable intent signal. |
| **No loop cue** (52%) | (a) play to the natural end; (b) play a fixed slice (e.g. 90 s) from start; (c) skip the track | **(a) to the end.** A gig preview should not silently cut tracks, and (c) loses tracks. Toni chose this (decision 2). A per-track cap is a one-line add if previews get too long. |
| **Last loop ends at or before the start** (1 track) | (a) ignore the end and play to the end; (b) skip; (c) swap start and end | **(a)**, with the reason shown in the player bar. It is almost certainly a stale loop. |
| **Loop end past the track's duration** | clamp, or ignore | **Treated as "to the end"**, so the element's own `ended` event advances, which is more reliable than waiting for a `timeupdate` at the exact last sample. |
| **User seeks past the end while previewing** | (a) the seek wins: disarm the end bound for this track and let it play on; (b) advance immediately; (c) clamp the seek to the end | **(a).** Seeking past the range is a deliberate "let me hear this bit". Jumping to the next track would fight the user. The bound re-arms on the next track. Seeking backwards, or before the start, just works. |
| **Track fails to load** | (a) stop the preview; (b) skip with a notice | **401/403 → no skip**: `player.js` asks the proxy for the status (a media element never exposes it), renews the token once, and if that needs the user it pauses there with "Sign in again to continue", preview mode kept (the token-refresh change). An undecodable file gives the same `MEDIA_ERR_SRC_NOT_SUPPORTED` a 401 body does, but the proxy says 200, so it is skipped like any other failure. **Any other error → skip** with `Errors.Info` naming the track. **3 failures in a row → stop.** Rows with no Drive file are never queued. **A superseded load is not a failure**, in preview or not: skipping quickly starts a new load before the last one finished, and whatever the old one still raises (`play()` rejecting with `AbortError`, an `error` event, the pause the switch causes) is ignored, by a load generation in `player.js`. Counting those used to show "Playback failed", skip an extra row and, after three, stop the preview. |
| **End of the list** | stop or loop | **Stop**, leaving the last track loaded and paused (today's end-of-track behaviour). |
| **Preview ends without the user** | vanish, or say why | **Say why.** End of the list, 3 failures, an expired session while casting, and leaving the playlist each show "Preview stopped: <reason>" in the player bar for 8 s. Only ✕ Stop preview ends it silently. |
| **Re-sort during a preview** | — | The next track comes from the new order (§3). The current track is not interrupted. |
| **Click Play on a row during a preview** | (a) jump the preview to that row; (b) leave preview mode | **(a)**, so "start the preview from track 7" is just a click. Leaving is an explicit control. Clicking the current row pauses and resumes it, and the end bound stays armed across the pause. |
| **Resume from before the load cue** | (a) keep the position; (b) jump to the load cue | **(b) only if this preview never started the track at its load cue**, which a cued or restored track has not. Otherwise an earlier position came from a seek, and the seek wins, like seeking past the end. |

**UI.** Options: (a) a toggle in the player bar; (b) a "Preview set" button in the
playlist header; (c) both. **Chosen: (b) plus a status chip in the bar.** "▶ Preview
set" in the header starts at the loaded track when it is one of the playlist's rows
(playing, paused, or restored after a reload), restarting it at its load cue, and
otherwise at the first playable row in table order. Its tooltip says which
("Preview from #6"). While it runs,
the bar shows **`PREVIEW · 3/18`**, the range (`1:52 → 4:13`, or the reason when a
default applied, e.g. "no loop — to end"), ⏮ ⏭, and **✕ Stop preview**. On the
waveform the range is lit and outside it is dimmed (§2). A bar-only toggle (a) hides
the feature where the playlist is not visible. Having both is redundant at this size.

**Cast.** The same `PlayerService` logic: `CastService.LoadAsync(file, token,
startTime)` always sends a fresh `LOAD` (`load` already accepted `startTime`), the end is checked on ticks, and the
next track is sent as a new `LOAD` (§3). Each `LOAD` uses a fresh token from
`authGetToken()`, so the hourly cliff only matters inside a single track, and
the existing re-arm already covers that.

## Consequences

**Positive**

- Every format gets a waveform up to the size cap, with no extra Cloud Run egress.
  Casting gets one too, from cached peaks.
- Cue markers need no audio at all, so even over-cap sets show their cue layout.
- Auto-advance and next/previous become ordinary player features. Gig preview is
  only a range rule and a policy on top of them.
- No change to `TraktorNmlParser` and no package publish.

**Cost / risk**

- **A second download on the user's connection** (first play of each compressed
  track only). It is free on Cloud Run, but on a phone over mobile data it is real.
- **Direct-to-Google fetches of audio** depend on Drive's CORS for `alt=media`. This
  is proven for `collection.nml` and expected to behave the same for audio, but not
  yet observed for audio.
- **Decode cost** is ~1–2 s of CPU for a 7-minute track on a laptop, more on a phone.
  The cap is there to bound the worst case. A malformed file is a waveform notice
  (`Errors.Info`), never a playback failure, the same as today.
- **The Regions plugin must match the vendored core's version** (7.12.11, recorded
  in §2). Upgrading one means upgrading both.
- **Gig preview on Cast** inherits adr-001's caveats: the sender tab stays open, and
  there is a short rebuffer between tracks.
- **No unit-test project.** `GigPreview.RangeFor` is pure and would be cheapest to
  test there, but the client is a Blazor WASM project, and a test project
  referencing it is not quite trivial. Toni asked for e2e only unless it was
  trivial, so every rule in §4 is instead exercised end to end through the "Gig
  Prep" fixture playlist: one track per edge case, each with a 2-second
  `PLAYTIME`, so the whole preview runs in seconds.
- **Not covered by tests:** a track that fails mid-preview (the mock fails every
  file at once or none), and seeking past the end while previewing. Both are
  implemented as the table above says.

**Deliberately not done here**

- C′ (play from the blob to cut egress), server-side peaks (D), the compressed
  range sampler (E), the Cast queue, and per-cue `COLOR`.
- Fixing the `player.spec.ts:43` race (described in Context). It did not
  reproduce in this branch's runs.
- The `AUDIO_ID` spike (option F).

## Open questions

1. **Which track was in Toni's screenshot?** If it was a `.wav` and *not* casting,
   there is a second bug that this change does not address. Every WAV path tested
   here draws.
2. **Unverified on real hardware:** direct-to-Drive CORS for audio, decode time on
   a phone, and Cast `idleReason` on the Default Media Receiver. All of them are
   mocked in the e2e suite.
