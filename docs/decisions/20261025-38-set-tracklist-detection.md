# Now playing inside a recorded set: a tracklist timeline from history, playlist and audio

Date: 2026-10-25

## Status

**Proposed. Not yet decided by Toni.** Design only. Written on
`toni/docs/set-tracklist-detection`, cut from `origin/master` at `d249a93`. Nothing
is implemented. Following this repo's rule, the ADR and its implementation will go
in one PR together. This branch carries the ADR only, so that the direction can be
agreed first.

Every number under §"What the real collection shows" was measured on 2026-10-25
from the parser repo's test collection
(`traktor-nml-parser/tests/TraktorNmlParser.Tests/test-data/collection.nml`,
Traktor Pro 4, 1,545 entries, 173 playlists). That file is a 2025 snapshot, so it is
evidence about Toni's habits, not about every set he has recorded since.

## Decisions (Toni, 2026-10-25)

- **Keylock is on.** Toni plays with Master Tempo on, so pitch stays put when the
  tempo changes. The matcher does not search pitch shifts (open question 2). The
  ±2 circular chroma shifts in §3a are only a safety margin.
- **±15 s is precise enough** for a boundary (open question 5). "Now playing" is the
  goal, not publishable tracklists, so a blend is shown at its midpoint and the nudge
  UI stays coarse.
- **Spike first, on `Z19-2025 Sentimony 2025-02-23.wav`** (135 min, a 39-track `.rec`
  playlist and a 51-track base playlist; the base playlist has 60 entries, 9 of which
  are `====` separators). Open question 4.
- **History files exist** (open question 1). Toni's Google Drive "Native Instruments"
  folder holds 141 `history_*.nml` files: 50 unique sessions from 2025-02-23 to
  2026-08-17. See §"Spike findings".

## Context

Toni: *"Make a plan on how to show the currently playing track in a set, even when
we don't have a history playlist stored for the set. I.e. we would need to detect
when a track changes using the audio signal. The good thing is that most often
there is a playlist per recorded set (with similar names) which contains the
tracks played in the set. But we can't rely on them being in the correct order
(most often they are). The playlist often has more tracks than what ended up in
the set."*

### What exists today, checked against the code

- **Recording to playlist pairing.** `Services/SetMatcher.cs` groups playlists into
  families by `SetName.Key` (it strips variant suffixes such as `.rec`, `-orig`,
  `-edit`, and trailing dates or years). It ranks a family by year, then `.rec`
  first, then by shortest name. The user's pick is stored per Drive file id in
  `localStorage` (`set-match:<fileId>`), so it lives on **this device only**.
  `Pages/Playlist.razor` `IsRecording` counts a file as a set when its path
  contains `/Setit/`, `/Gig preparation/` or `/Recordings/`.
- **`Components/SetDetail.razor`** is the expander on a set row. It shows the chosen
  playlist as a plain `<ol>` of title, artist and full track length. It has no
  times inside the set and no link to playback.
- **Playlist entries.** `Models/PlaylistEntry.cs` (`PlaylistEntries.Read`) reads
  every `ENTRY` straight from the NML `XDocument`. That includes separators and
  streaming entries, which the parser package drops. Tracks are keyed by
  `VOLUME+DIR+FILE`, with a volume-less, NFC-normalised, lower-cased fallback key.
  `CollectionService` keeps the parsed `XDocument`, so any other NML attribute
  (`AUDIO_ID`, `MUSICAL_KEY`, `EXTENDEDDATA`) can be read from it without parsing
  the file twice.
- **Waveform and peaks pipeline** (`wwwroot/waveform.js`, ADR 20261018-47 §1).
  WAV and AIFF files are sampled with about 400 parallel range reads of 2 KB each,
  with no full download. Compressed files are fetched **directly from
  googleapis.com** (no Cloud Run egress) and decoded with `OfflineAudioContext` at
  8 kHz. Files over `DriveAudio.CompressedDecodeCapBytes` (150 MB) are not decoded,
  which in practice means every MP3 or M4A of a whole set. Peaks are cached in
  IndexedDB (`traktor-waveforms`, per file id). At 400 buckets, a 2-hour set gets
  one bucket per 18 s. `scripts/` has a 73,259-point peaks JSON of a real set
  (`Z17.1 Psytech 2024-03-17.wav`, about 10 points per second) from an earlier
  experiment.
- **Markers.** `Models/CueMarkers.cs` turns `CUE_V2` into WaveSurfer Regions
  (`waveform.js` `addMarkers`). Clicking a region seeks to its start. Regions
  already support `drag` and `resize`. Today both are switched off.
- **Position.** `PlayerService.CurrentTime` is already one property for three
  sources: the local `<audio>` (`OnProgress`, pushed when the whole second changes,
  so about 1 Hz), Cast (`CastService` ticks, about 1 Hz), and a remote player
  (`RemoteService.RemotePosition`, interpolated between snapshots). Anything that
  maps time to a track can read that property and works on all three paths.
- **Remote control** (ADR 20261018-63). The player publishes a `PlayerSnapshot`
  (ids and display text only, never a token) on every state change and every 10 s
  while playing. Controllers interpolate the position between snapshots. A
  snapshot is re-published when its change key differs (`RemoteBridge`).
- **Cast.** `cast.js` `load()` sets `MusicTrackMediaMetadata` `title`/`artist` once
  per `LOAD`. The Default Media Receiver has no message that updates metadata
  without a new `LOAD`.
- **Scope.** `auth.js` asks for `drive.readonly` only. The app cannot write anything
  to Drive today, not even to `appDataFolder`.
- **Server.** `Dockerfile` is plain `aspnet:9.0` with no ffmpeg. Cloud Run runs one
  instance with 1 vCPU and 512 MiB, a 3600 s request timeout, and egress billed
  per GiB.

### What the real collection shows

**Sets and their pairing.**

| Measure | Value |
|---|---|
| Recordings by the app's `IsRecording` rule | 132, of which **73 are set-length (≥ 30 min)**. The rest are label tracks under a `/Recordings/` path, and 0–1 min Traktor test recordings. |
| Set length | median **116 min**, p10 63, p90 165, max 322 |
| Set file size (`FILESIZE`) | median **432 MB**, max 2.0 GB |
| Set formats (all 132) | 93 WAV, 29 MP3, 5 M4A, 5 FLAC |
| Sets paired to a playlist by the `SetName` rules | **58 of 73 (79%)**. That agrees with the "~81%" in `SetName.cs`. |
| …of which the best match is a `.rec` playlist | 19 (P2-2022 and most sets from 2022-12 on; the 2023-07 P3.1, 2024-03 Z17.1 and 2024-12 kanelikestit sets have a `.rec` playlist that the name rules miss) |
| Unpaired | 15: Traktor's default names (`2022-02-19_20h12m36.wav`, `Recording_2024-08-11_01h30m47s.wav`), `Psy01*`, `C01-…`, `Juvani - Off the Grid-…`, `P3.1-2023-07-03 (no levels)` |

Name differences seen. Codes reuse dashes and dots (`Z6 2020-3 2020-05-17` against
`Z6 2020`, and `H12-1-2020-03-08` against `H12`). Playlists carry the year and the
recordings carry the date (`P4.1-2024.rec` against `P4.1-2024-07-29.wav`).
Recordings carry suffixes (`-orig`, `-poista`, `-edit2`, `.treeni`,
`- gain+limiter by Golan`). Playlists carry suffixes too (`.all`, `.rec1`,
`- karsittu`). And a raw Traktor recording such as
`Recording_2024-08-11_01h30m47s.wav` belongs to `P4.2 2024-08-11`, but only the
date says so. A typo in a playlist name breaks pairing too: the kanelikestit set's
playlists are named `2024-12-2024 Menomenninkäisten kanelikestit(.rec)`. One likely mismatch: in my Python port of the rules,
`Z17.1 Psytech 2024-03-17.wav` falls back by prefix to the playlist **`Z1`**, not to
`Z17-2024 Psytech(.rec)`. That needs checking against the C# before it is called a
bug. It is out of scope here, but it is the kind of error the audio match would
expose.

**What a `.rec` playlist is, measured against its base playlist.** The 11 `.rec`
playlists that have a base sibling are **strict subsets** of it (one exception adds
a single track). Their size against the base:

- 44 of 172
- 19 of 138
- 20 of 56
- 16 of 25
- 43 of 114
- 15 of 23
- 29 of 66
- 64 of 114
- 35 of 58
- 44 of 59
- 43 of 60

The **base order is a weak prior**: the longest common in-order run (LIS) of the
`.rec` inside its base ranges from 100% (3 sets) down to **14 of 43** (P2-2022) and
**40 of 64** (Z15.4). So "most often in order" holds for the `.rec` list (that is
what it is for), but **not** for the base list.

**Playlist size against what fits in the recording.** The ratio is the sum of the
full lengths of the playlist's tracks divided by the recording's length:

| Pairing | Median ratio | Spread |
|---|---|---|
| `.rec` | **1.77** | 1.5 – 2.25 |
| base / non-`.rec` | **2.57** | up to 14 (`Z8 2020` against a 41-minute recording) |

Read the other way, in a `.rec` set each track is audible for about **3.5–4.6
minutes** (Z19: 39 tracks in 135 min, P4.2: 29 in 134, Z15.4: 46 in 191, H15: 14
in 55), against a median full length of 7.3 min. The rest is cut or overlapped in
blends. So:

- a `.rec` list is close to "exactly the tracks played, in order";
- a base list holds roughly **1.5–5×** more candidates than were played;
- a reasonable expected track count is `recording minutes / 4`.

**History playlists, with their timestamps.** Almost none exist in this snapshot:

- **One** `HISTORY` playlist at the root, 23 entries, all with
  `<EXTENDEDDATA EXTENDEDTYPE="HistoryData" DECK STARTDATE STARTTIME DURATION
  PLAYEDPUBLIC>`. `STARTDATE` packs the date as `year<<16 | month<<8 | day`
  (132449813 is 2021-06-21). `STARTTIME` is seconds since local midnight.
  `DURATION` is seconds on the deck. `PLAYEDPUBLIC=1` means the track reached the
  master output; `0` means it was only pre-listened or loaded (9 of the 23 entries).
  **No recording is dated 2021-06-21**, so this history pairs with no set.
- `History 2020-10-25` (113 entries, with repeats) does pair with
  `Z8 2020-2 2020-10-25.mp3`, and `history_2020y01m27d_23h31m59s` (35 entries) and
  `Z7 from history` exist too. **None of them has `EXTENDEDDATA`**: copying a history
  into an ordinary playlist drops the timestamps.
- So **0 of 73 sets have a timed history in `collection.nml`.** Traktor archives
  each session as a separate `History/history_YYYYyMMmDDd_HHhMMmSSs.nml` file in its
  root folder (normally `~/Documents/Native Instruments/Traktor x.y/History`), not
  in the collection. Whether those files exist, and whether they are on Drive, is
  unknown and is **open question 1**. It decides how much the exact path is worth.

**Cues on the recordings.** None that help. Each set carries only the AutoGrid
`TYPE=4` anchor and placeholder `TYPE=0` cues named `AutoGrid` or `n.n.`. No set
has a user-named cue, so nobody has hand-marked track changes in Traktor.

**Per-track features available from the NML**, across the 9,282 playlist entries
that resolve to a track:

- `TEMPO BPM` on 9,280. Median 134, p10–p90 120–146. In a single `.rec` set it
  spans **96–155** (psytrance with chill-out openers), so tracks are played well
  off their native tempo.
- `MUSICAL_KEY` on 8,571, as 24 values. A `.rec` set uses **8–19 distinct keys
  across 12–46 tracks**, so key alone separates neighbours only partly.
- `AUDIO_ID` on 9,272.

**`AUDIO_ID` is a loudness envelope, now confirmed structurally.** It is always 256
bytes. **The first two bytes are the track length in whole seconds, big-endian**:
the header equals `INFO PLAYTIME` in **1,516 of 1,516** entries. The remaining 254
bytes are **508 four-bit loudness steps**, so one step covers `length / 508`
seconds: ~0.86 s for a 7-minute track and ~14 s for a 2-hour set. It is heavily
saturated. A median **76%** of steps are at the maximum `0xF`, so the information
sits almost entirely in intros, breakdowns and outros. That is the "F" hypothesis
from ADR 20261018-47, now with the header decoded. The visual comparison against
real peaks has still not been done. As a signature it is free (no download), coarse
and level-dependent. §3 treats it as one option among several.

**Download volume.** The median candidate track is **21.9 MB** (p90 97 MB). The
4,550 WAV, 4,023 MP3, 670 FLAC, 30 AIFF and 9 M4A playlist references make WAV and
MP3 the formats that matter. With 35 candidates per set (the median), downloading
every candidate whole is **~770 MB per set**. For a 140-track base list it is ~3 GB.

## Decision

### 0. The shape of the answer

The output of every path below is one artefact, a **set timeline**:

```jsonc
{
  "v": 1,
  "recording": { "fileId": "…", "name": "Z19-2025 Sentimony 2025-02-23.wav", "duration": 8102.4 },
  "playlistUuid": "…",                  // the candidate list it was built from
  "generator": { "tool": "set-tracklist", "version": "0.1.0", "method": "history|audio|manual" },
  "segments": [
    {
      "start": 0, "end": 301.5,          // seconds in the recording; end = next start
      "blendIn": null,                   // [from, to] while the previous track is still audible
      "blendOut": [268.0, 335.0],
      "track": { "key": "<NML PRIMARYKEY>", "title": "…", "artist": "…" },  // null = unknown
      "trackOffset": 42.1,               // where in the original track playback entered
      "tempoRatio": 0.962,               // played BPM / NML BPM, when audio produced it
      "confidence": 0.91,                // 0..1; history = 1, a user edit = 1
      "source": "history|audio|manual"
    }
  ]
}
```

A segment's `start`/`end` is the **midpoint of the blend**, so that "now playing" has
one answer at every instant. The blend itself is kept as a range (`blendIn`,
`blendOut`), because psytrance transitions run 16–64 bars (30–120 s) and a single
cut point would misrepresent them. Tracks are keyed by NML primary key with title
and artist alongside, so a moved library still resolves through
`PlaylistEntries`' loose key.

### 1. Sources of truth, and how they combine

| Source | What it gives | Cost | What it gives up | Coverage in this snapshot |
|---|---|---|---|---|
| **H. Timed Traktor history** (`History/history_*.nml`, or the root `HISTORY` playlist) | The exact load/start time and on-deck duration per track, plus `PLAYEDPUBLIC` to drop pre-listens. Deck overlap marks the blend. | Parsing one more NML. Needs **one time anchor**: history time is wall-clock, the recording starts at 0. The anchor comes from Traktor's own recording name (`Recording_2024-08-11_01h30m47s`, `2022-02-19_20h12m36`), else one user tap ("track 1 starts here"), else one audio match. | `STARTTIME` is when the deck **started**, not when the fader came up, so a blend's audible start is earlier or later by up to a phrase. Only sets that have a history file. | **0 of 73** sets (open question 1) |
| **R. `.rec` playlist** (ordered, all played) | The candidate set and, almost always, the order. | Free; it is already loaded. | No times. A blind proportional split would be wrong by minutes, because play lengths vary from 1.5 to 8 min. | 19 of 58 paired sets; a `.rec` exists for most sets since 2022-12 |
| **B. Base playlist** (superset, loose order) | The candidate set only. Order is a weak prior (LIS ranges from 33% to 100%). | Free. | It has 1.5–5× too many tracks, and the order is unreliable. | The other 39 paired sets |
| **A. Pure audio** | Boundaries without names ("Track 7, 41:12–45:50"). | Detection compute (§2). | The names. | All 73, but useful mainly for the 15 unpaired sets |
| **M. Manual** (tap "next track" while listening, drag a boundary, pick a track) | Exact, and it covers anything. | Toni's time: about one listen-through, or about 10 min of scrubbing with the waveform. | Toni's time. | All |

**How they combine: one alignment, with priors that depend on the source** (§4).
History segments are clamped as hard constraints. A `.rec` list sets a strong order
prior and a low skip probability. A base list sets a weak order prior and a high
skip probability. With no list, the alignment runs with an "unknown track" state
only. Manual edits are stored **separately** as overrides (§5) and applied last, so
a recompute never loses them.

**Confidence shown to the user:** four levels, not a raw number.

- **exact**: from history or a user edit. Solid.
- **high**: audio, with a clear margin over the runner-up and consistent offsets
  inside the track.
- **likely**: drawn normally with a small `?`.
- **unsure**: hatched, with the label "Unknown — 41:12" or "Maybe: <title>".

Options for combining:

| Option | Cost | Gives up | Verdict |
|---|---|---|---|
| **1a. One probabilistic alignment, priors by source** | One model to build and tune | Some explainability | **Recommended** |
| 1b. A cascade: history if present, else `.rec` order plus audio boundaries, else audio | Simple, with branches | A partial history cannot be mixed with audio. A `.rec` that is out of order in two places breaks the "order" branch. | Fallback if 1a is too much |
| 1c. Show only what is certain (history or manual) | Trivial | Every set without history stays empty | It is increment 1 (§9), not the end state |

### 2. Detecting track changes from audio

In a psytrance set, every transition keeps the tempo (that is the point of
beatmatching) and almost always keeps the kick. What changes is the bassline
pattern, the harmonic content and the timbre, and it changes gradually over 30–120
s. Breakdowns **inside** a track are often bigger events than the transition
itself. That is the core difficulty for any detector that has no candidate list.

| Option | How | Cost | Gives up | Verdict |
|---|---|---|---|---|
| **2a. Novelty on a self-similarity matrix of beat-synchronous features** (Foote checkerboard kernel over chroma + MFCC, one vector per beat or bar) | Beat-track (trivial on a steady 4/4 kick), average the features per bar, build the SSM, convolve with a 32–64-bar kernel, pick peaks | Moderate DSP. About 2–5 min of CPU per 2-hour set in Python. | Fires on breakdowns and drops as readily as on transitions, and gives one point, not a blend range | **Use as the no-playlist fallback and as a boundary refiner**, not as the primary detector |
| 2b. Tempo or beat-grid discontinuities | Track BPM over time | Cheap | Beatmatched sets have none by construction. Tempo **ramps** across a set, so this barely helps. | Reject as a detector. Keep the local tempo as an input to §3. |
| 2c. Energy or RMS envelope | From the peaks we already compute | ~Free | Breakdowns dominate it, and a blend has no energy dip | Reject alone. It is the feature behind 3d. |
| 2d. A learned boundary model | Fine-tune a segmentation net on DJ mixes (published datasets exist) | Training data, GPU, a model in the pipeline | Over-engineered for one user's ~73 sets | Reject for now |
| **2e. Boundaries fall out of identification** (§3 + §4): wherever the best-matching candidate changes, there is a transition, and where two candidates both match, there is a blend | No separate detector | — | Needs a candidate list | **Primary**, whenever a playlist exists (58 of 73) |

A blend is modelled explicitly. Where the incoming track's match score rises while
the outgoing one's falls, the blend is `[first window where incoming > threshold,
last window where outgoing > threshold]`, and the segment boundary is where their
scores cross.

### 3. Identifying which track is playing

This is **closed-set** identification: 12–140 known candidates, each with an NML
BPM. It is not an open search. What breaks naive matching is **tempo ±8%** (more in
practice: a 155 BPM track can be played at 145), **pitch** (shifted with the tempo
unless Master Tempo, Traktor's keylock, is on; open question 2), **EQ**
(a low cut during blends), filters and effects, and **two tracks summed** in every
blend.

| Option | How | Download per set | Robustness | Verdict |
|---|---|---|---|---|
| **3a. Beat-synchronous chroma + MFCC, subsequence alignment per candidate** | Turn the recording and each candidate into one feature vector per beat (or per bar). Tempo then disappears by construction. Pitch shift is handled by the best of ±2 circular chroma shifts. Slide each candidate over the recording window (subsequence DTW or plain correlation in the beat domain). | Recording: the whole file. Candidates: excerpts (below), **~70–350 MB**, or whole files at ~770 MB | Good against tempo, pitch and EQ (chroma ignores level; MFCC loses some to EQ). Blends lower the score of both tracks, which is exactly the blend signal §2e needs. This is the method published research on real DJ mixes uses (beat-synchronous chroma/MFCC with mix-to-track subsequence DTW; Kim et al., ISMIR 2020; check the reference before relying on it). | **Recommended** |
| 3b. Chromaprint / AcoustID fingerprints | `fpcalc` on candidates and on recording windows; compare bit-error rates | Same as 3a | Chromaprint uses a fixed frame rate and absolute pitch classes, so a few percent of tempo change decorrelates it. You would have to time-stretch each candidate to every tempo you hypothesise. A WASM build exists, but nothing official. | Reject as primary. Worth 30 min in the spike as a baseline. |
| 3c. Landmark fingerprints that tolerate tempo and pitch change (Panako-style) | Constellation hashes with ratio-invariant timing | Same as 3a | Designed for exactly this case, but there is no maintained .NET or JS implementation, and Panako is Java | Keep as the fallback if 3a is weak on heavy-effects sets |
| **3d. `AUDIO_ID` envelope against the recording's envelope** | Take each candidate's 508-step loudness curve, already in the NML. Stretch it by `tempoRatio` and slide it over a dense envelope of the recording. A WAV recording costs about 29 MB of range reads at 2 points per second. | **≈ 30 MB, and zero candidate downloads** | Weak: 4-bit, 76% saturated, the mastering differs from the recording's chain, and a blend sums two envelopes. Only the placement of breakdowns, intros and outros carries information. | **Spike it.** If it reaches even "likely" on `.rec` sets, it is the cheapest path by an order of magnitude and runs in the browser at once. Unproven. |
| 3e. Key + BPM from the NML | Estimate the local key and tempo from the recording and score candidates | Recording only | 8–19 keys per set and tempos ramped to the set's tempo, so it only rules candidates out | **Use as a prior inside 3a/3d**, never alone |
| 3f. Pretrained audio embeddings (e.g. Essentia's Discogs-EffNet, CLAP) | Cosine similarity of window embeddings | Same as 3a, plus a 20–300 MB model | Robust to EQ and effects, good at "same track" | Overkill unless 3a fails. Keep for the offline path only. |

**Candidate excerpts, not whole tracks.** Matching needs 2–4 windows of 20–30 s from
each candidate, not the whole file, at 22 kHz mono for chroma:

- **WAV/AIFF:** the existing range-read code reads three 30 s slices at about 5 MB
  each, so ~15 MB per track.
- **MP3:** a byte range snapped to a frame sync decodes with `decodeAudioData` on
  its own (the bit reservoir costs a few milliseconds of glitch), so ~1.2 MB per 30
  s at 320 kbps.
- **FLAC/M4A:** need the whole file. That is the fragile case from ADR 20261018-47
  option E.

So the realistic download is **~3–15 MB per candidate**, which is 100–500 MB for a
35-track list. All of it comes direct from googleapis.com, so it costs **zero Cloud
Run egress**.

### 4. Alignment: order as a prior, not a constraint

Two levels:

1. **Scores.** Split the recording into windows of 8 bars (~14 s at 140 BPM). For
   each window `w` and candidate `c`, compute a match score `s(c,w)` and the matched
   position inside the track `o(c,w)` (3a or 3d, with 3e as a prior).
2. **Path.** Run Viterbi over windows. The states are the candidates plus `UNKNOWN`:
   - **Emission:** `s(c,w)`, plus a bonus when `o(c,w)` keeps advancing at the
     window rate times `tempoRatio`. A track that "plays" at the right speed is far
     stronger evidence than any single window.
   - **Transitions:** *stay* is high. *Next in the list* uses the prior's strength
     (strong for `.rec`, weak for base). *Skip ahead k* decays with `k` (low for
     `.rec`, high for base). *Go back* is low.
   - **Revisits:** each track is played at most once, which a plain HMM cannot
     express. Use a penalty pass: after decoding, if one track holds two separate
     segments, keep the stronger one and re-decode with that candidate masked out
     of the weaker span. This takes 1–2 passes in practice.
   - **Reordering:** a candidate out of list order costs a "go back" or a large
     skip. With a weak prior (base list) that cost is small, which is what lets the
     P2-2022 case (LIS 14/43) still come out right.
   - **Expected length:** a soft prior on segment length (median ~4 min, rarely
     under 1.5 min, rarely over 9 min) to stop flicker.

Options:

| Option | Cost | Gives up | Verdict |
|---|---|---|---|
| **4a. Window scores + Viterbi over candidates, with the revisit penalty pass** | ~200 lines. Candidates × windows is about 60 × 600, which is trivial. | The single-play rule is approximate | **Recommended** |
| 4b. DTW of the ordered list against the recording (LCS-style, with skips) | Simple and exact for an ordered list | **Cannot reorder**, which breaks on base lists | Use it as the `.rec`-only fast path if 4a's tuning drags |
| 4c. An HMM with in-track position states | Exact offsets | States grow from candidates to candidates × beats. Too slow in the browser. | Reject |
| 4d. Greedy: best candidate per window, then smoothing | Trivial | Flicker during blends, and nothing to resolve ambiguity | Baseline for the spike only |

### 5. Where the compute runs, and where the timeline is stored

**Compute:**

| Option | Download | Cloud cost | Gives up | Verdict |
|---|---|---|---|---|
| **5a. Offline CLI on Toni's laptop** (Python: librosa or Essentia, `fpcalc`) | **None** when Drive for desktop has the files locally. The NML paths say `/Users/toni/Google Drive/Musiikki/Traktor/…` (open question 3). Otherwise a direct Drive download, which is still not through Cloud Run. | €0 | A manual step per new set (about one a month; a folder watcher could run it), and it runs only on the laptop | **Recommended for the spike and for the first automated increment** |
| 5b. In the browser (Web Worker + WASM: Essentia.js has beat tracking, HPCP chroma and MFCC; or hand-written JS for 3d) | Recording: WAV by streamed range reads (432 MB median, 2 GB max). MP3 by frame-aligned chunked decode. M4A/FLAC sets whole, so the decoder cap applies. Plus 100–500 MB of candidate excerpts. | €0 (direct Drive) | Several minutes of CPU, and the tab must stay open. Not realistic on a phone. A 2 GB WAV over home broadband takes minutes. | **Later, and only if 3d proves out.** Then it needs about 30 MB and seconds. |
| 5c. Inside the Cloud Run service | Drive into Cloud Run is free ingress | Pennies of CPU | One instance at 1 vCPU / 512 MiB **shares its CPU with the audio proxy**, so playback stutters during analysis. No ffmpeg and no MP3/FLAC decoder in the image. A 2-hour float buffer alone exceeds 512 MiB unless streamed. | **Reject** |
| 5d. A Cloud Run **Job** (its own container, e.g. 2 vCPU / 4 GiB, ffmpeg + Python) | Drive ingress is free | About €0.01–0.05 per set | New infrastructure: image, job, IAM, a trigger endpoint. The job needs the user's Google token, which **expires within the hour**, and a long run needs a refresh token held server-side, which is a new secret to store. | Keep as the path if analysis must ever start from the tablet |

**Storage of the timeline:**

| Option | Who can read it | Needs | Gives up | Verdict |
|---|---|---|---|---|
| **5e. Sidecar JSON next to the recording on Drive** (`<recording>.tracklist.json`), written by the CLI | Every device signed into the account, **with today's `drive.readonly` scope** | Nothing new in the app | The app cannot write it, so edits go elsewhere | **Recommended for the computed timeline** |
| **5f. Drive `appDataFolder`** (`tracklists/<fileId>.json`) | Every device of the account, through this app's OAuth client | The **`drive.appdata` scope** (not a sensitive scope) and one re-consent | Hidden from Toni, and the CLI cannot read it | **Recommended for manual corrections.** The `set-match:` overrides could move there too, which fixes their current device-locality. |
| 5g. IndexedDB / `localStorage` | This browser only | Nothing | The tablet never sees edits | Cache only. Corrections live here only until 5f lands. |

The effective timeline is the sidecar (or the history), plus the overrides from
`appDataFolder`, merged in the client. A CLI re-run never touches the overrides. An
override is stored against a segment's track key and a time, not against a segment
index, so it survives a recompute that shifts the indices.

**Cost summary for one median set** (116 min, 432 MB WAV, 35-track list):

| Path | Drive download | Cloud Run egress | Compute |
|---|---|---|---|
| History + manual only | ~0 (a few-KB history NML) | 0 | 0 |
| CLI (5a), files local | 0 | 0 | ~3–8 min of laptop CPU (estimate; the spike measures it) |
| CLI (5a), files not local | 432 MB + ~770 MB of whole candidates | 0 | as above |
| Browser 3a (5b) | 432 MB + ~100–500 MB of excerpts | 0 | several minutes in a worker (estimate) |
| Browser 3d (5b) | ~30 MB of WAV range reads; candidates 0 | 0 | seconds |
| Server (5c) | ~1.2 GB of ingress | 0, but the proxy CPU is contended | rejected |

### 6. UX

- **Now playing in the player bar** while a set plays: a second line under the set
  title, `▸ 7/39 · Artist – Title`, with `?` when the segment is *likely*. During a
  blend it reads `Artist A → Artist B`. It is derived from
  `PlayerService.CurrentTime`, so it works locally, while casting and as a remote
  controller with no extra plumbing (§7).
- **Segments on the waveform:** a WaveSurfer region per segment in alternating
  translucent bands, labelled with the track number. The blend span is a darker
  overlap band. *Unsure* segments are hatched. The existing cue-marker layer stays.
  The `region-clicked` handler already seeks to `start`.
- **Tracklist in `SetDetail`:** the `<ol>` gains a start time per row (`41:12`), a
  confidence mark, and rows of **"not played"** candidates folded at the bottom
  (base lists). Clicking a row seeks the player there and starts the set when it is
  not loaded. The row playing now is highlighted.
- **Manual correction** (all saved as overrides, §5):
  - **"Mark next track" (tap)** while listening sets the next segment's start at
    the current position, minus a configurable 2 s reaction offset. It is the
    fastest way to annotate a set with no history, and it doubles as a way to
    collect ground truth (§8).
  - **Nudge** the start: ±1 s and ±5 s buttons on a row.
  - **Drag** a boundary on the waveform, using Regions `resize` in an explicit
    "Edit tracklist" mode. On a 56 px, 400-bucket bar, one pixel is 10–20 s, so
    dragging is for coarse moves and nudge is for precision. Denser peaks for sets
    (at least 4,000 buckets) are a cheap follow-up.
  - **Pick a different track** for a segment from the candidate list (or "All
    playlists"), or mark it "Unknown".
  - **Split or merge** segments.
  - **Reset to computed.**
- **Export:**
  - **Text tracklist**: `00:00 Artist – Title`, one per line, copied to the
    clipboard. This is the Mixcloud/SoundCloud format.
  - **CUE sheet**: `FILE "…"` + `TRACK nn AUDIO` + `PERFORMER`/`TITLE` +
    `INDEX 01 mm:ss:ff` at 75 frames per second.
  - Optional **JSON**, the timeline itself.
- The UI never shows an empty state just because detection has not run. It shows
  the candidate list as today, plus "Tap ▸| while listening to mark track changes,
  or run the tracklist tool".

### 7. Remote control and Cast

| Option | How | Cost | Gives up | Verdict |
|---|---|---|---|---|
| **7a. The player publishes now-playing in its snapshot** | Add an optional `RemoteNowPlaying(Index, Count, Title, Artist, Confidence, SegmentStart, SegmentEnd)?` to `PlayerSnapshot`. It goes into `RemoteBridge`'s change key, so a track change publishes at once. The controller interpolates the position as today and knows when the segment ends. | Small. A trailing nullable field keeps older clients working, so `Version` stays 1. | The controller cannot show the whole tracklist | **Recommended first** |
| 7b. The controller loads the timeline itself | The tablet reads the sidecar and overrides from Drive with its own token, and maps the interpolated position locally | More code on the controller, and a stale copy is possible right after an edit | — | **Add when the tablet needs a tracklist to tap through** (seeking uses the existing `seek` command with seconds, so no protocol change) |

**Cast:** the TV keeps showing the set's title from the `LOAD`. The Default Media
Receiver cannot update metadata without a new `LOAD`, and a new `LOAD` means a
rebuffer gap, so per-track metadata on the TV would need a custom receiver. That is
out of scope. The **sender's** player bar (and a remote tablet, through 7a) shows
now-playing from the Cast position ticks, which needs no new work because
`CurrentTime` is already unified.

### 8. Evaluation

**Ground truth, best first:**

1. A timed history file for a recorded set, if Toni has any (open question 1).
2. **Tap-marked truth:** Toni marks one `.rec` set with the §6 tap UI, or with a
   minimal CLI that plays and records keypresses. That takes one listen, or about
   10 min of scrubbing. Precision is about ±3 s, which is enough.
3. **Synthetic mixes:** a script builds 60-minute "sets" from real candidate tracks,
   with known boundaries. It applies tempo changes of ±3–8% (with and without
   keylock), 32–64-bar crossfades with an EQ low-cut on the outgoing track, and
   omits 30–60% of the list on purpose, to mimic a base list. The truth is exact,
   and it isolates each difficulty.

**Metrics:**

| Metric | Definition | Target for "good enough to ship" |
|---|---|---|
| Time-weighted ID accuracy | % of recording seconds whose labelled track is correct (blends count for either track) | ≥ 90% on `.rec` sets, ≥ 80% on base lists |
| Boundary error | \|predicted − true\| per matched boundary; report the median and % within 15 s and 30 s | median ≤ 15 s; ≥ 85% within 30 s |
| Played / not-played detection (base lists) | Precision and recall of "this candidate was played" | F1 ≥ 0.9 |
| Order | Kendall tau of the predicted order against the true order | ≥ 0.95 |
| Cost | Wall time and bytes downloaded per set | CLI under 10 min per set; browser 3d under 60 s |

Every option the spike tries (3a, 3b, 3d, 4a against 4d) is reported against the
same table.

### 9. Increments

**Increment 0: the spike. Offline, a few days, no app code.**

1. **Find the history.** Look for `History/history_*.nml` in Toni's Traktor folder
   and on Drive. Parse one, and check `PLAYEDPUBLIC` and `STARTTIME` against a
   recording whose Traktor name carries its start time.
2. **Pick the set:** `Z19-2025 Sentimony 2025-02-23.wav`. It is 135 min, WAV, has a
   `.rec` of 39 tracks and a base of 60. Use `P2 2022-04-30_5h32m11.wav` as the
   hard case: its `.rec` is out of base order, 14 of 43 in order. Get ground truth
   for Z19 by history if it exists, otherwise by tap-marking.
3. **Build** the synthetic mixes (§8.3).
4. **Implement in Python:**
   - beat tracking;
   - beat-synchronous chroma and MFCC;
   - 3a scoring against excerpts;
   - the 3d `AUDIO_ID` envelope matcher;
   - 3b `fpcalc` as a baseline;
   - 4a Viterbi, with 4d as the baseline;
   - 2a novelty for the no-list case.
5. **Run** each against the `.rec` list, the base list, and with no list.
6. **Report** the §8 table, plus the CPU time and the bytes read.

**What the spike must prove:**

- (a) 3a reaches the targets on a real set with only a base list.
- (b) Excerpts are as good as whole tracks, which decides the download cost.
- (c) Whether 3d alone is usable, which decides whether the browser path is ever
  cheap.
- (d) Whether keylock matters for chroma.
- (e) That a timeline JSON in the §0 shape is enough for the UI.

If (a) fails, the product stops at increment 1 and the spike tries 3c or 3f.

**Increment 1: the timeline without any audio analysis.** It is useful on its own,
and it is the foundation for the rest.

- The `SetTimeline` model and the §0 JSON, merged from the sidecar and the
  overrides.
- Now playing in the player bar (§6), segments on the waveform with click-to-seek,
  times in `SetDetail`.
- Manual correction: tap to mark, nudge, pick a track, reset. Overrides go to
  `localStorage` first. The `drive.appdata` scope is a separate, small change.
- **The history importer, if increment 0 found history files:** parse
  `history_*.nml` from Drive, anchor it on the recording name's timestamp or on one
  tap, and filter `PLAYEDPUBLIC=1`.
- Export as text and CUE.
- `RemoteNowPlaying` in the snapshot (7a).
- e2e tests: a fixture recording with a fixture sidecar; the now-playing line
  changes at a boundary; tap-to-mark writes an override; export text.

**Increment 2: automatic timelines.** The spike's Python becomes the
`set-tracklist` CLI (5a). It writes `<recording>.tracklist.json` beside the
recording, and the app shows those segments with their confidence levels. Overrides
move to `appDataFolder` (5f), so the tablet sees them too.

**Increment 3 (optional):**

- The browser path (5b), but only if 3d proved out.
- Controller-side tracklist (7b).
- Denser set peaks for precise dragging.
- Pairing fixes that fall out of the audio, for example a recording that matches
  no playlist of its family but does match another playlist's tracks.

## Consequences

**Positive**

- One artefact (the timeline) serves the player bar, the waveform, the tablet, the
  sender while casting, and export.
- Manual correction is a first-class path, not a patch for a bad detector. It
  covers the 15 unpaired sets, and it produces ground truth.
- Nothing in the recommended path adds Cloud Run egress or Cloud Run CPU.
- `AUDIO_ID` is now understood well enough (length header plus 508 four-bit steps)
  to be used, here and for ADR 20261018-47's flat-line waveforms.

**Cost / risk**

- **The automatic path is unproven on Toni's material.** Psytrance blends are long,
  breakdowns are loud, and tempos ramp. The targets in §8 are aspirations until
  the spike measures them.
- **History may not exist at all.** Then the "best first" source contributes
  nothing, and increment 1 rests on tapping.
- **An offline CLI is a manual step,** and it is tied to the laptop having the files.
- **A new OAuth scope** (`drive.appdata`) means one re-consent, and it touches
  `auth.js`, which other work also edits.
- **More state to reconcile:** sidecar, overrides and history can disagree. The
  rule is override > history > audio, always.

**Deliberately not done here**

- Implementation of any kind, and the `SetMatcher` fixes (the `Z17.1` → `Z1`
  pairing; Traktor-named recordings paired by date).
- Per-track metadata on the TV (it needs a custom Cast receiver).
- Writing the tracklist back into Traktor (cue points in the NML). The app is
  read-only on the collection.

## Spike findings (increment 0)

Tools: `tools/set-tracklist/` (`pair_history.py`, `ground_truth.py`,
`download_list.py`; standard library only). Measured on 2026-10-25 against the copy of
Toni's "Native Instruments" folder (`Traktor 4.5.1/collection.nml` plus all history
files).

**How the history clock works.**

- `STARTTIME` is **local wall-clock time** on the laptop, the same clock that names
  Traktor's own recordings.
- A history file's name is when the session was **closed**, not opened. A session
  that ran for days holds entries dated days before its file name.
- Traktor copies the history folder per version (4.1.1, 4.2.0, 4.4.1, 4.5.1), so the
  141 files are 50 sessions. Copies with the same name have the same size.

**Recordings carry an exact anchor.** Toni renames Traktor's raw recording
(`Recording_2025-02-23_13h28m54s.wav`) into `Setit/` or `Gig preparation/`, and the
collection keeps both entries with the same `FILESIZE` (or the same length). The raw
name is the local **start** time. The raw entry's `MODIFIED_DATE/TIME` is stamped in
**UTC** when the file is closed, so start + length must equal it modulo whole hours.
It does, to within 1 s, for all four raw twins. That also shows the laptop was on
UTC−8 on 2025-02-23, UTC+2 in winter, UTC+3 in summer.

**Pairing** (`pair_history.py`). Recordings with no raw twin get a fitted start: a
window of the recording's length slid over the public plays, scored by coverage,
activity just outside the window, the dates in the name, and the F1 against the
recording's own playlist family.

| Recording | Min | Start (local) | How | Public tracks | Own playlist (F1) | Confidence |
|---|---|---|---|---|---|---|
| `Z19-2025 Sentimony 2025-02-23.wav` | 135 | 2025-02-23 13:28:54 | raw twin, same `FILESIZE`, end check −1 s | 39 | `Z19-2025 Sentimony 2025-02-23.rec` (0.99) | exact |
| `…-edit.wav`, `…-edit2.wav` (Z19 trims) | 135 | 2025-02-23 13:28:32 | fitted | 39 | same (0.99) | high |
| `2025-04-19 Golgatan farssi.prep.wav` | 98 | **2025-04-13** 11:22:47 | fitted; a restart of a 10:57 run, imported on the gig day | 20 | `Golgatan farssi.rec` (0.91) | high |
| `2025-08-09 Huhuu metsä vol1.prep1-liian lyhyt.wav` | 49 | 2025-08-01 09:16:56 | fitted inside a 5-hour practice session | 15 | `…preparation-1h.rec` (0.89) | medium |
| `2026-02-14-Ystavien tanssit.prep-2025-12-07.wav` | 95 | 2025-12-07 11:43:44 | raw twin, same length, end check 0 s | 21 | `Ystavien tanssit-2.prep.rec` (0.93) | exact; begins 9 min into the run |
| `2026-02-14-Ystavien tanssit.prep.3.3-2026-01-11.wav` | 103 | 2026-01-12 00:32:43 | raw twin, same length, end check −1 s | 22 | `Ystavien tanssit-3.3.rec` (0.94) | exact |
| `C4 2026 Dark Tek Chillout 2026-08-16.wav` | 45 | 2026-08-16 22:29:54 | raw twin, same `FILESIZE`, end check 0 s | 10 | `C4 2026 Dark Tek Chillout` (0.95) | exact; mid-session |

Two earlier guesses were wrong. Golgatan was not recorded on 2025-04-19: that day's
19:50 and 21:04 sessions are Toni playing the prep recording itself on a deck. And the
2025-12-07 prep is neither the 03:49 nor the 09:36 run but a third one from 11:34. The
Huhuu pairing is only medium: an incomplete collection entry,
`Recording_2025-08-01_10h34m43s.wav` (no length), could be its raw twin, and if so no
history covers it. One raw recording was never renamed:
`Recording_2025-02-23_08h43m07s.wav`, 134 min, most likely a Z19 run-through. History
starts at 10:06 that day, so it covers only 24% of it.

**Z19 ground truth** (`ground-truth/z19-2025-sentimony-2025-02-23.json`). 39 segments.
The played tracks are **exactly** the 39 tracks of the `.rec` playlist, in the same
order (in-order run 39 of 39). The base playlist's other 12 tracks were not played.
The first deck started 20 s before the recording did, and the last deck stopped 9 s
after it ended. On the history clock, the recording's t = 0 is 13:28:54. That is
**±2 s from the metadata**, but it rests on two clocks agreeing, so the spike's first
audio step must confirm it: cross-correlate the opening minute against the first
track. Deck starts are not audible entries. The segment boundaries are the midpoints
of the deck overlaps. Every one of the 38 transitions overlaps: median 58 s, range
7–90 s. So a boundary sits within about ±30 s of the true audible handover. That is
looser than the ±15 s Toni asked for, so audio has to tighten the ground truth itself
before it can grade a detector at ±15 s.

**Audio for the spike is not on this laptop.** The NML paths point to
`/Users/toni/Google Drive/…`, which does not exist here, and there is no Drive for
desktop mount. The spike needs the Z19 recording (1.33 GiB) and 51 tracks (39 played,
12 base-only; 50 FLAC, 1 WAV, 4.1 GiB): **5.51 GiB** in total.

**The audio pipeline, on synthetic mixes** (`synth.py`, `detect.py`, `evaluate.py`,
`run_synth.py`; librosa in a venv, pinned in `requirements.txt`).

Each mix is built like this:
- 8 played tracks out of 14 candidates;
- every track stretched by ±3–8 % with keylock;
- tracks entered on the bar grid, with bass-swap blends of 16–32 bars;
- the "hard" variant uses 16–32-bar plays, 32–64-bar blends and added noise.

The detector implements 3a (beat-synchronous chroma and MFCC, matched as
subsequences in the beat domain), 4a (Viterbi with order priors by list type plus the
one-run-per-track re-decode) and 4d (greedy) as the baseline. Over 4 seeds:

| Mix | List | Candidates | 4a accuracy | 4d accuracy | Boundary median |
|---|---|---|---|---|---|
| easy | `.rec`, base, or shuffled | whole or excerpts | 1.00 | 0.96–0.98 | 2–2.5 s |
| hard | `.rec` | excerpts (3 × 30 s) | 1.00 | 1.00 | 2.2 s |
| hard | base / shuffled | excerpts | 0.985 / 0.988 | 0.944 / 0.966 | 2.2 s |
| hard | base | whole tracks | 0.90 | 0.92 | 5 s |
| hard, **no keylock** | `.rec` | excerpts | 0.73 | 0.70 | 26 s |

Three lessons carry over to real audio:

- Every candidate scores about 0.8 against every window, because the kick and hats
  are shared. Identification only works once each score is set against the median
  over candidates. The detector uses a robust z-score of the score against the other
  candidates' scores, which also keeps the UNKNOWN level meaningful under noise.
- Excerpts beat whole tracks. A whole track's drum-only intro and outro match
  anything. This points to answer (b) "excerpts are enough", but only real tracks can
  confirm it.
- Without keylock, accuracy drops by a quarter, because chroma is not searched for
  pitch shifts. Toni plays with keylock on, so that path is not built.

Synthetic results prove the plumbing and the tuning direction, not the targets: the
tracks are far more distinct than real Sentimony psytrance. `run_real.py` runs the
same grid on Z19 once the audio is local. Its signed boundary median also checks the
anchor. Not built yet:
- 3d (`AUDIO_ID` envelopes);
- 3b (`fpcalc` is not installed);
- 2a (novelty for the no-list case).

## Open questions for Toni

1. **History files.** Does your Traktor `History/` folder exist (`history_*.nml`),
   how far back does it go, and is it on Drive? This decides whether the exact path
   is worth building first.
2. **Keylock.** Is Master Tempo on when you play sets? With it off, pitch moves
   with the tempo, and the matcher has to search pitch shifts.
3. **Local files.** Does this laptop have the set and track files locally through
   Drive for desktop (the NML paths say `~/Google Drive/Musiikki/Traktor/…`)? This
   decides whether the CLI downloads anything.
4. **The spike set and ground truth.** Is `Z19-2025 Sentimony 2025-02-23` a good
   choice? If no history exists for it, are you willing to tap-mark it once (one
   listen, or about 10 min of scrubbing)?
5. **Precision.** Is ±15 s fine for "now playing", or do you want publishable
   tracklists (Mixcloud), where a few seconds matter? This decides how much the
   blend modelling and the nudge UI matter.
6. **Scope.** OK to add `drive.appdata` so that corrections (and the `set-match:`
   picks) follow you to the tablet? Or keep them per device for now?
7. **Unpaired sets.** Do you care about the 15 unpaired sets (mostly
   `Recording_…` and `2022-…_20h12m36` names)? If so, pairing by date, and the
   unnamed-segment fallback (2a), come into scope.
8. **Going forward.** Will every new set have a `.rec` playlist, as for most sets since 2022-12?
   If so, the hard problems (skips, reordering) are mostly about older sets.
9. **Off the laptop.** Must analysis ever start from the tablet or phone without
   the laptop? Only then does the Cloud Run Job (5d) earn its infrastructure.
