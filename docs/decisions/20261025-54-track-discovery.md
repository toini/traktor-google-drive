# Track discovery: a Discover view that finds, plays and triages new releases from the labels and artists Toni already plays

Date: 2026-10-25

## Status

**Accepted 2026-10-25 by Toni**, with the choices recorded under §"Decisions Toni
made". Written on `toni/docs/track-discovery`, cut from `master` at `9865fab`.
Increment 1 is implemented on the same branch, in the same PR as this ADR. Where
the text below still says "recommended", the decisions list is what settles it.

### Increment 1 as built

- **Server** (`TraktorGoogleDrive.Server/Discover/`): `BandcampUrls` rebuilds every
  URL from its parts (a label or artist subdomain's `/music`, or `/album|track/<slug>`;
  nothing else, no other host, no query). `PoliteFetcher` serialises every Bandcamp
  request, ≥ 2 s apart, 500 a day (both capped in code, so config can only make it
  stricter), with the honest User-Agent. `BandcampSource` caches `/music` pages 24 h
  and release pages 7 days, refetching a release only when its stream URLs have
  under an hour left. `BandcampParser` is pure, tested on the S1 recordings.
  Endpoints: `GET /api/discover/bandcamp/releases?url=`, `GET /api/discover/bandcamp/release?url=`
  (track data without stream URLs), and the media URL
  `GET /api/proxy/discover/bandcamp/{trackId}?release=` (302 to the CDN, `?probe=1`
  for a status), all for the allowlisted user only (`RemoteAuth`; bearer header, or the
  `drive_token` cookie for the media URL).
- **Client**: a Discover entry per genre in the sidebar; `/discover/{genre}` with
  Listen, Shortlist and Seeds tabs. Seeds: "eti lisää" first, then a playlist's
  ≥ N★ tracks, the genre's top lists, by hand; Bandcamp URLs prefilled from the
  collection's "Visit …bandcamp.com" comments, or guessed on a click. A refresh walks
  the release list newest first, reading release pages until it passes the
  watermark (or 10 pages; "Load older" continues). Listen queues oldest first, never
  a heard or owned track; skip/maybe/like move on, 1–5 stars, Buy and a note stay
  on the card; undo; keyboard. Shortlist: liked tracks to decide, the buy list with
  Bandcamp links, "Copy links", Bought. "Heard through" is offered per seed when a
  month is done. The event log lives in `appDataFolder` (`DiscoverStore`), with
  unsaved events kept in localStorage; a token without the new scope keeps working
  on the device and shows "Allow saving to Drive".
- **Starter list** (`TraktorGoogleDrive/wwwroot/discover/starter.json`): Toni's
  triage from before the app was deployed, as versioned batches applied once each
  to his log, as ordinary events, and only after the log was read from Drive (so a
  device that cannot read it never re-applies a batch over a change made elsewhere).
  Batch `2026-10-25.1`: the Carnivor Records seed (H) heard through **2026-10-25**;
  heard releases *Feeding Generation Z II*, *Feeding Generation Z*, *Elegies*, *19th
  Century* (Bandcamp) and *Toxic Compilation 2025 V.1* (Beatport 4930160, kept under
  both its URL and `beatport:4930160`); picks *Satanas*, *I'm No Immigrant* and
  *Madtype (Shadym & T Y remix)* as **maybe**, known by URL until their release page
  is read (a seed refresh, or "Look up" on the shortlist row). New confirmations go
  into a new batch.
- **Watermarks are a month or a day.** OneNote marks are months ("2023-06", covering
  the whole month); Toni's Carnivor mark is a day, so releases later in October 2026
  still appear. A watermark is compared by the last day it covers.
- **Heard releases match across stores** (`ReleaseMatch`): by catalogue number when
  both sides have one (a leading "DS054 - " in a title counts, "ER31" equals
  "ER031"), else by normalised title ("V.1" = "Vol. 1") with label and artists
  agreeing when both sides know them; a title under 12 characters needs labels on
  both sides. A heard Beatport release therefore stays heard when Bandcamp or
  Discogs lists it.
- **Playback in the player bar** (built after PR 17, prefetch and buffering, merged):
  - **Item model.** The ADR recommended a `PlayableItem` interface (Q2). As built it
    is the Q1 shape with Q2's capability flags: a preview is a `FileEntry` with an
    `External` source (`PlayableItem.Discover(...)`), a synthetic id
    `discover:bandcamp:<trackId>`, and `CanCache`, `CanWaveform`, `CanCast` flags.
    PR 17 keyed the queue, prefetch, priming, waveform, Cast and remote code by
    `FileEntry`, so the flags kept every one of those paths unchanged for Drive
    files while each Drive-only step now asks before it runs.
  - **The Listen queue is the player's queue** (`discover:H`), offered like a
    playlist page's rows: Next/Previous, continuous play and a remote controller walk
    it. While a preview plays the card follows the player, a verdict starts the next
    track, and P plays or pauses.
  - **Stream only.** A preview is never planned for whole-file download
    (`stream` items are left out of `prefetch.js`'s plan, which also refuses
    `discover:` ids), gets no waveform (the plain seek bar instead), no buffer bar,
    and is not remembered for a reload. The next track is still primed by `src` on
    the spare element, which costs one redirect and a few kilobytes.
  - **Errors.** `player.js` probes a preview with `?probe=1` (a redirect cannot be
    read cross-origin) and reloads once on a failed stream with a good probe, which
    is what an expired signed URL looks like.
  - **Cast** refuses a preview with a message; the TV cannot use our cookie.
- **The collection** is read through `CollectionService.GetCollectionAsync`, so seeds
  and owned-track detection use PR 20's cached `CollectionSnapshot` rather than
  parsing the NML again. The snapshot's extras gained `PlayCount` and `LastPlayed`
  (for seed ranking), which bumps `CollectionSnapshot.CurrentVersion` to 2: the
  first load after deploy re-parses once.
- **Limits of the first cut**: "heard through" is offered only after a refresh has
  read the seed's whole list down to its watermark in this session, because the walk
  reads newest first and anything unread is older (a watermark above it would hide it
  unheard). Owned matching is exact on the normalised title plus one shared artist; near
  matches are not flagged yet.

## Decisions Toni made

1. **Bandcamp: B2, fetched only when Toni acts** (§1.2). A label's release list is
   fetched when he opens or refreshes that seed, a release page when its track is
   about to play or he opens it. At most 1 request per 2 s, a daily cap, cached, an
   honest User-Agent, never `/search` or `/api/`, **never in the background**.
   Accepted knowing the acceptable-use policy's wording.
2. **Hefty is not Toni.** Hefty stays a normal seed. There is no alias stop list;
   the stop-list mechanism stays for `Various Artists`, `NN` and the like (§3.1).
3. **Storage: S-A**, the Drive `appDataFolder` with append-only per-device logs
   (§8). Adding the `drive.appdata` scope, and the one re-consent it costs, is OK.
4. **First genre: H**, through the H16 playlist seed flow (§3.2).
5. **No YouTube, no Spotify** (§1.5).
6. **Never offer what Toni has already heard** (§4.3): everything the OneNote import,
   the Bandcamp wishlist import and the collection say he has heard is marked heard,
   whatever its verdict, and is never queued.
7. **"Eti lisää" comments are the top seed source** (§3.1). A collection track
   whose comment says "eti/etsi lisää" ("search for more", any case, with or
   without the umlauts) seeds its artist **and** its label in its genre, ahead of
   every rating-based seed. The first concrete seed is **Obwigszyh + Carnivor
   Records (H)**, from "1616 (Original Mix)", 5★, comment "KOVA LOPPU. eti lisaa".

## Context

Toni, lightly cleaned:

> Design a new view and feature in the same app to help me search for new tracks.
> Use artists or labels that I like, or that produce music in the genre I like, and
> search for other tracks by that producer or label. I play several genres and they
> must not be mixed: **Z** is dark prog, **H** is hard techno, **P** is psytrance,
> **C** is dark tek chillout. I used to keep a OneNote document listing the labels
> I'd found, with dates marking the period of releases I'd already listened through,
> so I don't listen to them twice. When I find a good track I paste its URL into
> OneNote, which is extremely inconvenient. After a round of finding tracks I make a
> second pass and mark each one with a rating or a word (skip, buy). Lately I use my
> Bandcamp wishlist instead, but I still have the old docs. I prefer Bandcamp (best
> for psy), but not all techno is there, so we need to search both. I want something
> that runs the searches and presents the results so I can listen and rate or
> discard, until I have a very curated list. I don't want to buy what I won't play.

Two additions from Toni, relayed by the coordinator:

1. **Seed from a playlist's well-rated tracks**: "take the H16 tracklist, find the
   tracks with a good rating and use their artist or label for searching". This is a
   first-class flow (§3.2), and H16 is the worked example.
2. **The app plays the found tracks itself**, in the existing player bar, with
   queue, next/previous, continuous play, and keyboard and remote control, so that
   triage feels like listening to a playlist (§2).

### The job, restated

Four loops, each of which the OneNote document did badly:

| Loop | Today | What the feature does |
|---|---|---|
| **Find**: which labels and artists to watch, per genre | A label list in OneNote, from memory | Seeds come from the collection (top lists, a playlist's good tracks), manual adds, imports and suggestions |
| **Fetch**: what they released since he last looked | Open each label page by hand, compare with the date mark | Adapters list releases per seed, and a per-seed **watermark** hides what he already heard |
| **Listen and judge** | Play in a browser tab, paste URLs into OneNote, a second pass with ratings or words | A queue in the player bar. Pass 1: like, maybe or skip. Pass 2: rate and decide buy |
| **Keep** | OneNote lists, then the Bandcamp wishlist | A per-genre "to buy" shortlist with purchase links. Owned tracks drop out automatically |

### What exists in the app, checked against the code

- **Layout** (`Layout/MainLayout.razor`): a sidebar with `FolderTree` (Traktor
  folders and playlists), a top row (sidebar toggle, `RemoteToggle`,
  `CastButton`, sign out), `CastBar`, `ErrorBanner`, the page body, and
  `PlayerBar` at the bottom. The sidebar is a drawer below 1000 px (the tablet in
  portrait).
- **Routes**: `/music` (collection picker), `/playlist/{Uuid}` (the table with
  sortable columns, `ColumnPicker`, a star rating column from `INFO RANKING`,
  51 per star). A Discover view is a new route, `/discover/{genre}`, beside them.
- **`CollectionService`** parses `collection.nml` from Drive in the browser with
  `TraktorNmlParser`, and exposes folders, playlists, entries, ranking, label and
  album per track. **Everything the seed and owned-track logic needs is already in
  the client.** The server never sees the collection.
- **`PlayerService`** owns the one `<audio>` element (`wwwroot/player.js`, which
  sets `audio.src = url`) and routes to Cast or to a remote player. The queue is
  offered by the page that shows it (`OfferQueue(playlistUuid, rows)`), and next,
  previous and continuous play (`player:continuous-play`) work on that queue. Gig
  preview ([ADR 20261018-47 — waveforms, cue markers and gig preview](20261018-47-waveform-cue-markers-gig-preview.md))
  plays a bounded range of each track and always advances.
- **Waveforms** (`waveform.js`) need the audio **bytes** (range reads through our
  proxy, or a full download from Drive), so they need either same-origin or CORS.
- **The server** (`TraktorGoogleDrive.Server`) is a relay: static files, the Drive
  proxy, the Cast ticket endpoints, and the remote-control hub. It has no database.
  `RemoteAuth.IdentifyAsync` turns a Google access token into a user and checks the
  `RemoteAuth:AllowedUsers` allowlist. Discover's endpoints reuse it (§9).
- **Auth** is the Google Identity Services token flow with the **`drive.readonly`**
  scope only. The app cannot write anything to Drive today.
  [ADR 20261025-38 — set tracklist detection](https://github.com/toini/traktor-google-drive/blob/toni/docs/set-tracklist-detection/docs/decisions/20261025-38-set-tracklist-detection.md)
  (on its own branch, not merged) proposes adding `drive.appdata` for manual
  corrections. Discover wants the same scope (§8), so one re-consent covers both.
- **Tests**: Playwright e2e against a Drive mock (`tests/e2e/drive-mock.ts`) and a
  Cast mock, plus shell tests against the real server (`tests/server/`). There is no
  xUnit project.

## Collection findings (the seed data)

Read from `~/Downloads/NativeInstruments-2026-10-25/Native Instruments/Traktor 4.5.1/collection.nml`
(read only), with a throwaway Python script outside the repo. **1,606 tracks, 193
playlists.**

### Top-level folders

| Folder | What it is | Playlists | Unique tracks | Rated | Rated ≥ 4★ | With a label |
|---|---|---|---|---|---|---|
| **Z** | dark prog | 50 | 678 | 607 | 362 | 521 (77%) |
| **H** | hard techno (also `H/T1`, `H/T1-3`) | 31 | 548 | 507 | 264 | 493 (90%) |
| **P** | psytrance | 13 | 229 | 211 | 119 | 167 (73%) |
| **C** | dark tek chillout | 9 | 66 | 62 | 43 | 47 (71%) |
| X | unknown (open question 1) | 10 | 166 | 163 | 107 | 129 |
| F | unknown, one playlist `F5-2024` | 1 | 23 | 8 | 1 | 16 |
| DNB | drum and bass | 1 | 23 | 2 | 2 | 23 |
| Gigs | one playlist per gig, plus `.rec`/`.bak` variants | 62 | — | | | |
| History | one Traktor history copy | 1 | — | | | |
| (root) | 15 loose playlists: `ZX3`, `Psybreaks-uudet`, `Downtempo`, `Techno 02`, `Recorded sets`, `Preparation`, histories… | 15 | — | | | |

234 tracks sit in no genre folder at all (only in Gigs, History or root
playlists).

Genre folders, not `INFO GENRE`, are the truth. The tag is store-supplied and
coarse: `Psy-Trance` 745, `Hardcore / Hard Techno` 296, none 208, `Techno` 140,
`Hard Techno` 75, `Psytech` 44. These are Beatport's genre names, which says where
many H tracks were bought. 33 tracks carry `COMMENT="Visit https://<x>.bandcamp.com"`,
which Bandcamp writes into its downloads: those are free, exact Bandcamp URLs for
their labels and artists. 856 tracks carry `RELEASE_DATE`, mostly year-only
(`2025/1/1`), and none of Padang, Another Psyde or Parasomnia's tracks have one.
So the collection **cannot** supply "listened through until" watermarks. Those
come from Toni or the OneNote import (§4).

### Genre overlap (unique tracks in both folders)

|   | Z | H | P | C | X |
|---|---|---|---|---|---|
| **Z** | 678 | 50 | **140** | 23 | 36 |
| **H** | 50 | 548 | 18 | 3 | **100** |
| **P** | 140 | 18 | 229 | 2 | 24 |
| **C** | 23 | 3 | 2 | 66 | 1 |

205 of the 1,301 tracks in Z, H, P or C (16%) are in more than one of them. The
big overlap is Z∩P (dark prog and psytrance share labels and artists), and X looks
like an H sibling (100 of its 166 tracks are also in H).

Entities overlap much more than tracks do:

- **Labels**: 227 distinct across Z/H/P/C, **46 in more than one genre, and only 20
  of those have ≥ 75% of their tracks in one genre.** Padang Records: Z 41, P 28,
  H 6. Erebos Records: C 21, Z 15, H 12, P 9. Zenon: Z 42, P 10.
- **Artists**: 561 distinct, **137 in more than one genre, 35 of them clearly in
  one.** Hefty: H 43, C 31, Z 24, P 3. Dragon: P 22, Z 16. Schoiroideairis: Z 17,
  P 17.

So a label or artist cannot be given one genre by majority without losing real
seeds (Erebos would be "C only"). §3.3 proposes the alternative.

### Top labels per genre

Counts are unique tracks in that genre's playlists. "≥ 4★" is tracks rated 4 or 5
stars over all tracks of that label in the genre. Plays sum `PLAYCOUNT`.

| Genre | By track count | By ≥ 4★ | By plays |
|---|---|---|---|
| **Z** | Another Psyde 62, Sentimony 50, Zenon 42, Padang 41, Pistolero 23, Parasomnia 22, Erebos 15, Intermind 14 | Padang 33/41, Another Psyde 31/62, Sentimony 19/50, Parasomnia 16/22, Zenon 15/42, Erebos 10/15, Visionary Shamanics 8/10, Occulta 7/9 | Padang 913, Another Psyde 721, Zenon 675, Sentimony 451, Parasomnia 449, Pistolero 296 |
| **H** | Industrial Philharmonics 58, Endzeit 43, Klangrecords 30, Darker Sounds 29, Hardwandler 24, NGRecords 22, Wicked Waves 21, Oxytech 20 | Industrial Philharmonics 22/58, Darker Sounds 20/29, Klangrecords 18/30, Endzeit 16/43, NGRecords 13/22, Wicked Waves 11/21, Oxytech 9/20, Erebos 8/12 | Industrial Philharmonics 855, Klangrecords 505, Darker Sounds 426, NGRecords 282, Oxytech 279, Hardwandler 262 |
| **P** | Padang 28, Inpsyde Media 11, Chronicle Of Mystery 10, Parvati 10, Zenon 10, Intermind 9, Erebos 9, Selva Digital 8 | Padang 16/28, Erebos 7/9, 2TO6 6/7, Inpsyde Media 5/11, Chronicle Of Mystery 5/10, Parasomnia 5/5 | Padang 541, Zenon 212, Parasomnia 169, Noise Kult 163, Intermind 162 |
| **C** | Erebos 21, DistroKid 5, Sentimony 5, Uprise Music 4 (13 labels in total) | Erebos 15/21, DistroKid 5/5, Sentimony 4/5, Uprise 4/4 | Erebos 388, Sentimony 54, DistroKid 52 |

### Top artists per genre

| Genre | By track count | By ≥ 4★ | By plays |
|---|---|---|---|
| **Z** | Hellquist 25, Hefty 24, Alchemy Circle 23, Schoiroideairis 17, Dragon 16, Antibody 13, Zentrix 12, Egomorph 12 | Hefty 17/24, Alchemy Circle 14/23, Dragon 11/16, Ivort 9/10, Hellquist 8/25, Zentrix 8/12, Deuteroz 6/6, Ritalin Child 6/7 | Hefty 579, Dragon 386, Alchemy Circle 376, Ivort 326, Ritalin Child 302 |
| **H** | Champas 69, Hefty 43, Grozdanoff 12, Tropar Flot 11, Timao 10, Noseda 10, Gladyshev 9, Kai Pattenberg 9 | Champas 27/69, Hefty 23/43, Grozdanoff 7/12, Tropar Flot 7/11, Predator Tekno 7/8, Noseda 6/10, Chefetage 6/7, Parallx 5/5 | Hefty 839, Champas 658, Chefetage 202, Tropar Flot 187, Timao 177 |
| **P** | Derango 28, Dragon 22, Schoiroideairis 17, Ka-Sol 7, Biollusion 7, Alchemized 5 | Dragon 15/22, Derango 11/28, Schoiroideairis 8/17, Tyamat 4/4, Ka-Sol 4/7 | Dragon 463, Derango 333, Ritalin Child 234, Schoiroideairis 207 |
| **C** | Hefty 31, ZABO 13, Rezz 4, MNGRM 3 (25 artists in total) | Hefty 17/31, ZABO 10/13, MNGRM 3/3, Rezz 3/4 | Hefty 494, ZABO 138, MNGRM 54, Rezz 48 |

**Hefty** is a top artist in every genre and has 99 tracks overall. It is not Toni
(decision 2), so it is an ordinary seed, and a strong one: Darker Sounds' newest
release (2025-11-09) is a Hefty EP.

### Recency

- **Newest imports**: H's latest batch is **2025-12-13** (Wicked Waves, Definition
  Of Hard Techno, RAW., Valhall, Carnivor, Occulta, Vision Serpent, Fresh Cut,
  Legion IX, Falsive, DEMIAN, Eternal Damnation): exactly the labels H16 holds. Z's
  latest is 2025-12-07 (Erebos), then 2025-02-08 (Sentimony). P's latest is
  **2024-07-29**, C's 2025-02-08. Only 9 tracks were imported in 2026.
- **Last played**: H and Z were played on **2026-10-11** (Industrial Philharmonics,
  Endzeit, Klangrecords, Darker Sounds, Erebos, Champas, Hefty…), P on 2026-10-10.

Recency matters for ranking seeds: a label he imported from last December and played
two weeks ago is a better seed than one he stopped buying in 2019.

### Worked example: seeding from `H/H16 2023/2025 Darker Sounds-2`

73 entries, **63 unique tracks**, imported 2022-01-30 to 2025-12-07. Ratings: 17 at
5★, 19 at 4★, 9 at 3★, 3 at 2★, **15 unrated**.

| Filter | Tracks | Artists | Labels |
|---|---|---|---|
| ≥ 5★ | 17 | 13 | 10 |
| **≥ 4★ (default)** | **36** | **23** | **14** |
| ≥ 3★ | 45 | 28 | 16 |

At ≥ 4★ that yields:

- **Labels (14)**: Darker Sounds 12, Erebos Records 7, RAW. 3, Valhall Records 2,
  Attack MuZik, Legion IX, Antibody, Falsive Records, Carnivor Records, Fresh Cut,
  Definition Of Hard Techno, Eternal Damnation, DEMIAN RECORDS, Vision Serpent (1
  each). Two tracks have no label.
- **Artists (23)**: Hefty 12, Noseda 3, Kristonov 2, Strasse Killer 2, NN 2, and
  Hydrosplifix, Algia, Xikwri Neyrra, Crytone, Rezystor, VINYA, Draugr, Antibody,
  Trust True, Obwigszyh, Mike Opani, Maze Runner, Hardtrax, Bernhard Von Zorn,
  Dave Black, CVNSUMED, David Phoenix, Various Artists (1 each).

After the stop list (`Various Artists`, `NN`), **21
artist seeds and 14 label seeds for H**, from one playlist. That is a good size for
one triage session, and much more focused than "H's top 20 by track count", which is
dominated by 2017–2019 labels (Industrial Philharmonics' newest dated track is
2019). This is why §10 puts the playlist seed in increment 1.

## Decision summary (recommended)

1. **Server-side source adapters** behind one interface, each declaring what it
   can do (release lists, dates, audio, tags, purchase links). Bandcamp, Discogs and
   link-out stores first. Beatport only if Beatport grants partner API access.
   YouTube and Spotify are out, by Toni's decision (§1.5).
2. **Bandcamp is fetched only at Toni's pace, never crawled in the background.** A
   label's release list is fetched when he opens or refreshes that seed, and a
   release page when its track comes up to play. It is slow, cached and
   identifiable. Bandcamp's own acceptable-use policy forbids automated access
   outright, so this is a deliberate, low-volume personal-use exception, and
   **Toni's call**, and he made it (decision 1). The ToS-clean fallback is listed
   with it.
3. **The player bar plays previews through our `<audio>` element**: the source is
   `/api/discover/audio/{source}/{trackId}`, which our server answers with a 302 to
   the source's current stream URL. Bytes go from the CDN to the browser, not through
   Cloud Run. Next/previous, continuous play, keyboard and remote control all keep
   working. The Bandcamp iframe is only a fallback. A release with no playable
   audio (some Discogs finds) is link-out only.
4. **Seeds are (entity, genre) pairs**, not entities with one genre. Auto-created
   when an artist or label has ≥ 2 tracks, or ≥ 1 track rated ≥ 4★, in that genre's
   playlists. **Releases are routed to a genre by their own tags**, and only fall back
   to the seed's genre. Manual moves win.
5. **Per-seed watermark ("heard through YYYY-MM")** plus a per-release and per-track
   state, so nothing comes up twice.
6. **Two-pass triage**: pass 1 is skip / maybe / like while the queue plays; pass 2
   is rate 1–5, buy or skip, and a note. **Skim mode** reuses gig preview's bounded
   range to jump to the drop and auto-advance.
7. **Owned tracks never appear**: every release track is matched against the parsed
   collection by normalised artist and title. "Bought" items turn into "owned" on
   the next collection sync.
8. **Storage in the Drive `appDataFolder`** as per-device append-only event logs, so
   the tablet and the laptop never overwrite each other. That needs the
   `drive.appdata` scope, shared with the set-tracklist ADR. Refresh is on demand,
   with no scheduler.
9. **No Bandcamp session cookie anywhere.** The wishlist is imported read-only, and
   "add to wishlist" is a link out.

## 1. Sources and how to query them

### 1.1 What each source gives

| Source | Release list per label/artist | Release dates | Audio the app can play | Genre or tags | Purchase link | Access |
|---|---|---|---|---|---|---|
| **Bandcamp** | Yes: `<label>.bandcamp.com/music` (HTML) | Yes, on the release page (`album_release_date`) | **Full tracks** (MP3 128k) for most releases, via expiring stream URLs in the release page's data | Free-form tags (`dark psytrance`, `hard techno`, `psytech`…) | Yes | No API. HTML pages only (§1.2) |
| **Beatport** | Yes (API v4 `/catalog/labels/{id}/releases`) | Yes, day-accurate | **2-minute sample MP3s** | Genre and sub-genre (Hard Techno, Psy-Trance…) | Yes | Partner API, by application only (§1.3) |
| **Discogs** | Yes (`/labels/{id}/releases`, `/artists/{id}/releases`) | Year, sometimes full date | **None** (§1.4 says what its releases fall back to) | Styles (Psy-Trance, Hard Techno, Industrial…) | Marketplace for vinyl, otherwise none | Real API, personal token, 60 requests/min |
| **MusicBrainz** | Yes (label → releases) | Yes | None | Folksonomy tags, sparse | None | Open API, 1 request/s, needs a real User-Agent |
| **SoundCloud** | Per account | Upload date | Full or preview, **iframe only** (Widget API, scriptable) | Free-form | Often a "buy" link | New API registrations effectively closed. Widget needs no key |
| **Juno, Traxsource** | Web pages | Yes | Samples on their pages | Genre | Yes | No public API. Link out only |

**Coverage, from the collection:** Z and P labels (Padang, Another Psyde, Sentimony,
Zenon, Parasomnia, Erebos) are Bandcamp-first. H labels (Industrial Philharmonics,
Darker Sounds, Klangrecords, Endzeit, Wicked Waves) mostly sell on Beatport, and some
also on Bandcamp. The `GENRE` tags in the NML are Beatport's. Spike S3 (§11)
measures how many of the top 20 per genre have a Bandcamp page and a Discogs entry
with dates.

### 1.2 Bandcamp: the options

Bandcamp has had no public API since 2016 or so. What its pages expose:

- **Label and artist pages** (`<x>.bandcamp.com/music`): the release grid, with
  links and often `data-item-id`s.
- **Release pages** (`/album/<slug>`, `/track/<slug>`): a `data-tralbum` JSON blob
  with the release date, the track list, durations, and `trackinfo[].file["mp3-128"]`,
  a **signed stream URL on `*.bcbits.com` that expires**. There are also the tags and
  the "you may also like" block.
- **Site search** (`/search?q=`), and the JSON endpoints its own pages use under
  `/api/` (fan collection and wishlist, discover, tag hubs).
- **The EmbeddedPlayer iframe** (`bandcamp.com/EmbeddedPlayer/album=<id>/track=<id>/…`),
  which Bandcamp offers to everyone. It needs numeric ids and has **no JavaScript
  API**: the page cannot play, pause, seek it or learn that it ended.

**The rules, read 2026-10-25:**

- Bandcamp's [Acceptable Use and Moderation Policy](https://get.bandcamp.help/en/articles/15263124-bandcamp-s-acceptable-use-and-moderation-policy)
  (dated 2026-06-10) says users agree "not to scrape any text, media, or other data
  or content from the site, including through the use of scripts, robots, bots,
  spiders, scrapers, crawlers, or other automated means", and not to "create
  fingerprints or any other identifying digital files of content on our site". The
  [Terms of Use](https://bandcamp.com/terms_of_use) (2026-05-07) incorporate that
  policy.
- [`robots.txt`](https://bandcamp.com/robots.txt) disallows `/search`, `/stream` and
  `/api/` (except three discover and tag endpoints) for every agent. Label and
  release pages are not disallowed.

So **any** automated fetch of Bandcamp is outside its policy, however polite. The
honest question is how far from a browser the app's behaviour is, and what
enforcement could cost: most likely a block of Cloud Run's egress IPs, which breaks
the feature. Toni's fan account is not at risk as long as no cookie of his is ever
sent (§9).

| Option | What it costs | What it gives | What it gives up / risk |
|---|---|---|---|
| **B1. Background crawler**: a scheduled job walks every watched label's `/music` page and every new release page daily | A scheduler, server-side storage (§8) | New releases are waiting when Toni opens the app | **Most clearly against the policy**: a bot on a timer. Volume grows with the watch list (Z alone has 111 labels). The likeliest to get blocked. **Rejected.** |
| **B2. User-paced fetch** (recommended): the server fetches a seed's `/music` page only when Toni opens or refreshes that seed, and a release page only when one of its tracks is about to play or he opens it. 1 request per 2 s per host, a burst of 3, a daily cap of 500, cache label pages for 24 h and release pages for 7 days, an honest User-Agent, no `/search`, no `/api/` | A server adapter, an HTML parser that will break when Bandcamp changes its markup | Back catalogues, dates, tags, and **full-length playable audio** | Still "automated means" by the letter. The volume is that of Toni browsing: a seed refresh is 1 request plus 1 per release he plays. Breakage risk is real (markup), and is contained to one adapter |
| **B3. Bandcamp's own notifications**: Toni follows the labels and artists on Bandcamp, and Bandcamp emails him each new release. The app reads those emails | Following ~50 labels by hand once. A way to read the emails: the Gmail API (`gmail.readonly` is a *restricted* scope), or an Apps Script in his own Google account that copies them into the app's storage | **New** releases with no fetching of Bandcamp at all, ToS-clean | No back catalogue, and no audio or ids by itself: playing still needs B2 (one release page) or the embed. Email formats change too. Spike S4 |
| **B4. Embed only**: the iframe for every Bandcamp release | Ids still come from somewhere (B2 or B3's links) | Bandcamp-sanctioned playback | **No player-bar control**: no next, no continuous play, no skim, no remote control, no keyboard. Toni presses play inside each iframe. That fails the "feels like a playlist" requirement |
| **B5. Manual**: Toni pastes release URLs, the app only links out | Nothing | Zero risk | No discovery at all. Today's OneNote with a nicer list |

**Recommendation: B2 now, and B3 later as the new-release feed** (increment 4), so
that "what's new" needs no Bandcamp fetching and B2 is left with "play this
release" and an occasional back-catalogue walk. B4 is the per-release fallback when
a stream URL cannot be resolved. **Never B1.** Seeds are resolved to Bandcamp URLs
without site search: from the 33 `Visit https://…bandcamp.com` comments in the
collection, from Discogs label profiles (their `urls` usually list the Bandcamp
page), or pasted once by Toni.

**Server or browser?** The browser cannot read Bandcamp pages (no CORS headers),
so the fetch must be server-side, from Cloud Run. The audio is the exception: an
`<audio>` element plays a cross-origin URL without CORS (§2).

### 1.3 Techno sources

| Option | What it costs | What it gives | Verdict |
|---|---|---|---|
| **Beatport partner API** | An application through Beatport's [partner portal](https://partnerportal.beatport.com/hc/en-us/categories/13076449714580-Developers). OAuth client credentials in Secret Manager. No self-serve keys, and Beatport can refuse or revoke. The default grant covers non-commercial informational use | The best H source: label/artist releases with exact dates, genres, 2-min sample MP3s, ISRCs, buy links | **Apply now (spike S2), build when granted.** Using the public client id lifted from Beatport's docs page, as many hobby tools do, is against its API terms. Not that |
| Scraping beatport.com pages (`__NEXT_DATA__` JSON) | A parser | The same data without access | Against Beatport's terms, and it breaks with their frontend. **Rejected** |
| **Discogs API** | A personal access token (Secret Manager), 60 requests/min | Release lists with year or date, styles, label profile URLs (incl. Bandcamp) | **Recommended** as the H release-list source and for resolving seeds to URLs. No audio of its own (fallbacks in §1.4). Coverage of digital-only techno is the open question (S3) |
| **SoundCloud** Widget API | None | Premieres, often a buy link | Optional, later: paste-a-URL only, since no discovery API |
| **MusicBrainz** | A User-Agent with contact, 1 request/s | Open ids, ISRCs for some releases | Optional, for dedupe (§6). Underground digital coverage is thin |
| **Juno, Traxsource** | URL templates | A search page per track | **Link out only** ("search on Juno" for a track found elsewhere) |

### 1.4 Adapter design

One interface, in the server, one implementation per source, registered by config,
so a source can be switched off (`Discover:Sources:Bandcamp:Enabled=false`) or added
without touching the rest:

```csharp
interface IReleaseSource
{
    string Id { get; }                         // "bandcamp", "discogs", "beatport"
    SourceCapabilities Capabilities { get; }   // Releases | Dates | Audio | Tags | Purchase | Search
    IReadOnlyList<string> AllowedHosts { get; } // the only hosts it may fetch (§9)

    Task<IReadOnlyList<SeedCandidate>> ResolveSeedAsync(SeedKind kind, string name, CancellationToken ct);
    Task<ReleasePage> ListReleasesAsync(SourceRef seed, DateOnly? newerThan, string? cursor, CancellationToken ct);
    Task<Release?> GetReleaseAsync(SourceRef release, CancellationToken ct);
    Task<PreviewTarget?> ResolvePreviewAsync(SourceRef track, CancellationToken ct);
}

abstract record PreviewTarget;
record DirectAudio(Uri Url, DateTimeOffset ExpiresAt, bool CorsReadable) : PreviewTarget; // <audio>
record Embed(string Kind, string Id) : PreviewTarget;                                      // "bandcamp" | "soundcloud"
record ExternalOnly(Uri Url) : PreviewTarget;                                              // link out
```

Each adapter gets its own named `HttpClient` with the outbound rate limiter and
cache in front of it (`System.Threading.RateLimiting` token buckets, keyed by host).
Parsing is a pure function from a recorded response to records, which is what the
tests exercise (§12).

**Audio for releases found on Discogs.** Discogs has release lists and dates but no
audio. When a release comes from Discogs, `ResolvePreviewAsync` tries, in order:

1. **A Bandcamp URL** for the release or its label, from the Discogs release or label
   profile (`urls`), or from a Bandcamp seed already known for that label. The
   Bandcamp adapter then resolves it as in §2 (one release-page fetch, at Toni's
   pace).
2. **A Beatport preview**, if Beatport access is granted: match the release by label,
   catalogue number, and artist and title, then play its `sample_url`.
3. **Link out only**: a "search on Beatport / Juno / Traxsource" link built from the
   artist and title. The item stays in the queue as **not playable**: continuous
   play skips it, and Toni can still judge it after listening in the store's page.

### 1.5 Rejected sources

- **YouTube and Spotify: out, by Toni's decision ("No need for youtube or
  spotify").** YouTube would only have played through its own iframe, with a small
  search quota. Spotify's development mode has needed Premium since February 2026
  and gives new apps no 30-second previews (since November 2024), so it would add only
  release lists, which Discogs and Beatport already give.

## 2. Playing the found tracks in the app's player

Toni's requirement: discovery previews play in the existing player bar, with queue,
next/previous, continuous play, and keyboard and remote control.

### 2.1 How audio reaches the player, per source

| Source | What the app plays | Through our `<audio>`? | Notes |
|---|---|---|---|
| Bandcamp | The `mp3-128` stream URL from the release page | **Yes**, via the redirect endpoint | URLs are signed and expire (lifetime unknown: S1). Whether `bcbits.com` checks `Referer` is unknown: S1. Full-length tracks. Bandcamp may limit repeated full plays of an unbought track (unverified, S1); triage plays each track once or twice |
| Beatport (if granted) | `sample_url`, a 2-minute MP3 | **Yes** | Samples start mid-track, which already skips the intro |
| SoundCloud (optional, later) | Widget API iframe | **No** | Scriptable (play, pause, `seekTo`, `FINISH` event) |
| Bandcamp fallback | EmbeddedPlayer iframe | **No** | **Not scriptable**: no auto-advance, no skim, no remote control |
| Discogs, MusicBrainz | nothing of their own | — | Via a Bandcamp URL or a Beatport preview when one is found, else link-out only (§1.4) |
| Juno, Traxsource | nothing | — | Link out only |

### 2.2 Options for direct audio

| Option | What it costs | What it gives | What it gives up |
|---|---|---|---|
| **P1. Embeds for everything** | Little | Each source's sanctioned player | Bandcamp's iframe is not scriptable, so no queue behaviour for the most important source. **Rejected as the main path** |
| **P2. Redirect resolver** (recommended): `<audio src="/api/discover/audio/bandcamp/{trackId}">`. The server resolves the current stream URL (cached until 5 min before it expires) and answers **302** to it | One endpoint. The same `<audio>`, so the player bar, queue, next/previous, continuous play, keyboard, Media Session keys and remote control all work unchanged. The browser fetches the bytes from the CDN directly: **no Cloud Run egress**, and we never re-serve anyone's audio. A seek after expiry re-requests our URL and gets a fresh redirect | **No waveform**: the bytes are not CORS-readable (unless S1 finds the CDN sends CORS headers). Cast needs more work (§2.5) |
| P3. Byte proxy through Cloud Run | Egress: a 7-minute 128k track is ~6.7 MB, and a triage session of 100 tracks is ~0.7 GB | Waveforms, Cast with the existing ticket scheme, full control over `Range` | Re-serving Bandcamp's audio from our server is the worst ToS position of all, and computing peaks from it brushes against the "no fingerprints" clause. **Rejected** |
| P4. Download to Drive and play from there | Writes to Drive, storage | Offline, waveforms | It is acquiring the audio without buying it. **Rejected** |

**Recommendation: P2 for direct audio (Bandcamp, Beatport samples), and the
Bandcamp iframe only as a fallback.**

### 2.3 One player bar, two kinds of queue

- **The queue owner** becomes a key, not just a playlist uuid: `OfferQueue` is
  called with `discover:H` by the Discover view, exactly as `Playlist.razor` calls it
  with a playlist uuid. Browsing elsewhere never stops the music (remote-control ADR
  decision 3) and that holds here too.
- **The item** today is a `FileEntry` (a Drive file id plus the NML track). Two ways:
  - **Q1. Widen `FileEntry`** with an `Origin` (Drive or Discover) and a source
    ref. Least code, but every Drive-specific path (`DriveAudio.UrlFor`, peaks,
    `PlaybackMemory`, Cast) needs an "if Drive" guard.
  - **Q2. A `PlayableItem` interface** with `DriveItem` and `DiscoverItem`
    implementations: `PlayUrl`, `Title`, `Artist`, `CanWaveform`, `CanCast`,
    `PreviewRange`. More refactoring up front, but each capability is explicit.
  - **Recommended: Q2**, introduced in increment 1. The capability flags are what
    the player bar renders from, so a discovery item hides the waveform and the
    Cast button greys out for it, instead of failing.
- **The player bar** shows the source (a small "Bandcamp" or "Beatport" badge), the
  release and label, and the triage buttons (§5) when the current item is a
  discovery item.
- **`PlaybackMemory`** remembers the source ref, never the stream URL (it carries a
  signed token and expires). A reload cues the item through the redirect endpoint,
  as it cues a Drive track today.
- **`player.js` `probe()`** issues a `Range: bytes=0-0` fetch after a media error to
  learn the status. For a redirect it would follow to the CDN and fail on CORS. The
  endpoint therefore also answers `?probe=1` with the status itself (200, 404 gone,
  410 expired and not refreshable, 429 rate-limited) instead of a redirect.
- **The Bandcamp iframe fallback** is not driven by the player bar: the bar says
  "playing in the Bandcamp player", and advancing is manual. A scriptable embed
  route (SoundCloud's Widget API) is possible later, but no increment needs it.
- **Link-out-only items** (§1.4) sit in the queue marked "not playable", and
  continuous play skips them.

### 2.4 Waveforms, gig preview and "jump to the drop"

- **Waveforms do not apply** to discovery items under P2: there are no readable
  bytes. Computing them from a proxied copy is P3, rejected above.
- **Gig preview's mechanism does apply.** It plays a bounded range of each track and
  always advances. **Skim mode** reuses that: each item starts at its *drop offset*,
  plays N seconds (default 60) and advances. Psy previews that start quietly are
  exactly what it is for.
- **Where the drop is**, without audio analysis (no bytes, and analysis is the
  fingerprinting the Bandcamp policy forbids):
  - a default offset per genre, as a fraction of the duration (proposed: Z and P
    35%, H 25%, C 20%; Toni tunes them);
  - keys to jump: `D` to the drop offset, `←`/`→` ±15 s, `Shift+→` +60 s, and
    `1`…`9` to 10%…90%, as many DJ previewers do;
  - **learned offsets**: when Toni seeks forward within the first 20 seconds of an
    item, the app records where he landed as a fraction, and the genre's default
    moves towards his median. That costs nothing and needs no audio.
- Beatport samples already start mid-track, so their offset is 0.

### 2.5 Keyboard, remote control and Cast

- **Keyboard**: the triage keys (§5.3) live on the Discover view. Space, the arrows
  and the Media Session keys come from the player bar and work for any item.
- **Remote control**: the snapshot already carries title, position and state. For a
  discovery item it carries the source ref and badge, never the stream URL. The
  controller's play, pause, seek, next and previous go through the hub unchanged. Two
  new commands, `triage(state)` and `rate(n)`, let the tablet judge what the laptop
  plays. That is the "listen on the big speakers, judge from the couch" case.
- **Cast**: off for discovery items in increment 1. The Default Media Receiver could
  follow a 302 to the CDN, but our redirect endpoint would need the Cast ticket
  scheme ([ADR 20261025-71 — Cast media tickets](20261025-71-cast-media-tickets.md))
  instead of the user's token. That is a later, small change, after S1 shows that
  the CDN does not check `Referer`.

## 3. Seeds

### 3.1 Where seeds come from

| Origin | How | When |
|---|---|---|
| **"Eti lisää" comments** (top priority) | A collection track whose `COMMENT` matches `et(s)?i lis(ää\|aa)` (case-insensitive) seeds its artist and its label in the genre of the folder its playlists sit in. Shown first in the seed list. Three exist today: Obwigszyh / Carnivor Records "1616 (Original Mix)" (H, "KOVA LOPPU. eti lisaa"); BADVOID remix of "Rockstar" by BIJOU on Do Not Duplicate Recordings ("3-1 Hullu. Eti lisää"); Muddy Monkeyz "Atomic Drink" on Selva Digital Records ("2-2 ETI LISÄÄ. …"). "lisää luuppi" ("add a loop") on Dragon's "Tetrakis" is **not** a marker: the regex needs "eti/etsi" before "lisää" | Increment 1 |
| **Playlist seed** | Pick a playlist, a rating threshold (default ≥ 4★), artists and/or labels. The genre comes from the playlist's top folder. A preview shows the resulting seeds with counts before anything is saved | Increment 1 (§3.2) |
| **Genre top lists** | Per genre, ranked by a score (below). Toni ticks which ones to watch | Increment 1 |
| **Manual** | Paste a Bandcamp, Discogs or Beatport URL of a label or artist, pick the genre | Increment 1 |
| **Imports** | OneNote label lists with date marks, the Bandcamp wishlist (§7) | Increment 4 |
| **Suggestions** | Entities that appear on releases Toni liked: label mates, remixers, compilation artists, Bandcamp's "you may also like" on pages already fetched, Discogs label rosters | Increment 5 |

**Score for top lists**: `3 × tracks rated ≥ 4★ + plays / 20 + recency`, where
recency is 5 if anything was imported or played in the last 12 months, falling to 0
at five years. Raw track counts reward old bulk imports (Industrial Philharmonics has
58 H tracks, none dated after 2019). The formula is a starting point to tune.

**Stop list**: `Various Artists`, `VA`, `NN`, `Unknown`, Traktor separator rows
(`==============`), store artefacts in artist fields (one C track's artist field
holds a pasted Bandcamp page), and `DistroKid` (a distributor, not a label). Toni
can add names to it from the seed list.

**Artist names are split** on `,`, `&`, `feat.`, `ft.`, `vs.`, `x`, so "A & B"
seeds A and B.

### 3.2 Seed from a playlist

1. On a playlist page, a "Seed discovery from this playlist" action (or a picker in
   the Discover view).
2. Options: minimum stars (default 4), "artists", "labels" (both on), include
   unrated (off).
3. The preview lists each candidate with its track count in the playlist, its
   overall counts in the genre, whether it is already a seed, and whether it is on
   the stop list. For H16 at ≥ 4★: 14 labels and 23 artists, 21 artists after the stop list.
4. Saving creates `(entity, H)` seeds with origin `playlist:H16`, so a later view can
   say why a seed exists.

### 3.3 Assigning a seed to a genre

| Option | How | Gives | Gives up |
|---|---|---|---|
| **Majority** | Each entity gets the genre holding most of its tracks | One clean genre per entity | Erebos would be C-only, Padang Z-only. 26 of 46 multi-genre labels have no 75% majority. **Loses real seeds** |
| **Any** | A seed in every genre where the entity has a track | Nothing lost | One stray track drags a label into a genre (Padang has 6 H tracks, Hefty 3 P tracks), and its releases flood that queue |
| **Threshold per genre** (recommended) | A seed `(entity, genre)` exists when the entity has **≥ 2 tracks, or ≥ 1 track rated ≥ 4★**, in that genre | Erebos is a seed in C, Z, H and P. A one-off track does not create a seed | A multi-genre label's releases still need routing (below) |
| **Manual only** | Toni assigns each | Exactly right | Hundreds of decisions before the first listen |

**Routing a release** to a genre queue, in order:

1. **The release's own tags** through a small, editable map: Bandcamp tags such as
   `dark psytrance`, `forest`, `night psy` → Z; `psytrance`, `full on` → P;
   `hard techno`, `industrial techno` → H; `psybient`, `psychill`, `dark ambient`,
   `downtempo` → C. Beatport and Discogs genres and styles map the same way.
2. **The seed's genre**, when only one seed found it.
3. **The seed genre with the most tracks**, when several seeds found it and the tags
   do not decide.
4. **Toni moves it** with one key, and the move teaches the tag map ("always route
   tag `psytech` to Z?").

A release never appears in two queues. Tracks already owned do not appear at all (§6).

## 4. State, watermarks and "nothing comes up twice"

### 4.1 Model

```
Seed      { id, kind: label|artist, name, genre, refs: {bandcamp, discogs, beatport, …},
            watermark: YYYY-MM | null, status: active|paused|dropped, origin, createdAt }
Release   { key, refs, title, artist, label, releaseDate, tags[], genre, seedIds[], firstSeenAt,
            state: new|opened|done|dismissed }
TrackItem { key, releaseKey, artist, title, mix, duration, refs,
            pass1: null|skip|maybe|like, rating: 1..5|null, decision: null|buy|skip,
            note, bought: bool, owned: bool, decidedAt }
```

Keys are normalised (§6.2), so the same release found by two sources is one row with
two refs.

### 4.2 Watermarks

- **Meaning**: "I have heard everything this seed released up to and including
  YYYY-MM". A month is the granularity of Toni's OneNote marks, and day precision
  is not available from most sources anyway.
- **Effect**: releases dated on or before the watermark are stored as `dismissed`
  and never queued. The queue shows the oldest unheard first, so a label is listened
  through in order and the watermark can advance.
- **Advancing**: when every release of a seed up to month M is done, the seed's row
  offers "Heard through M". One tap moves the watermark. It never moves on its own,
  because skipping a release is not the same as having heard the label.
- **Initial values**: from the OneNote import (§7) when it has them. Otherwise
  Toni sets them in the seed list. The collection cannot supply them (no dates for
  Padang or Another Psyde). The newest release *owned* is shown as a hint, not used.
- **Undated releases** (some Discogs rows) are queued once and then remembered by key.

### 4.3 Hard rule: never offer what Toni has already heard

Toni: *"When you ingest that you'll know which releases on which labels are already
listened. Don't offer to listen another time."* This is a rule, not a preference:

- **Every imported track URL** and **every imported release or catalogue header**
  (for example `ER029`–`ER031`) marks that track and its release as **heard**,
  **whatever the verdict**: `ei`, `ehkä`, `osta`, `ostettu`, or no token at all.
  The verdict is kept as the item's triage state (§7.1), but "heard" does not
  depend on it.
- **A period header** (`=== 2023-06`, `2023-4`) sets the "heard through" watermark of
  every label whose URLs follow it, until the next header, to **at least** that month.
  A watermark only ever moves forward: an import never lowers one.
- **Releases on a label dated on or before its watermark are never queued** (§4.2).
- **Matching works across URL shapes and sources:**
  - a Bandcamp **track** URL is mapped to its release (the release page lists its
    tracks; until that page is fetched, the track key is remembered and matched when
    it is), and an **album** URL marks every track on it;
  - a Discogs or Beatport release that is the same as an imported Bandcamp one
    (same normalised label + catalogue number, or same normalised artist + title,
    §6.2) is heard too;
  - a catalogue header (`ER031`, `ER30`) is matched by label + catalogue number,
    tolerating a missing leading zero.
- **The same rule covers the other two sources of "heard":**
  - every item on the **Bandcamp wishlist** import (§7.2) is heard (it lands in pass
    2 to decide, never back in pass 1);
  - every track **already in the collection** is heard and owned (§6.2), and a
    release whose tracks are all owned is never queued.
- "Heard" is stored as its own flag, separate from the verdict, so a later re-import
  or a cleared verdict never brings a track back.

## 5. Triage workflow

### 5.1 Where it lives

- **Sidebar**: a "Discover" section above `FolderTree` with one entry per genre
  (Z, H, P, C) and a count of new items. The sidebar is already a drawer on the
  tablet, so nothing new is needed for narrow screens.
- **`/discover/{genre}`** with three tabs: **Listen** (pass 1), **Decide** (pass 2)
  and **Shortlist** (§6). A fourth, **Seeds**, lists the watch list with
  watermarks, last refresh and counts.
- **`/discover/seeds/new?playlist={uuid}`** for the playlist seed flow.

### 5.2 Two passes

| Pass | Queue | Order | Actions |
|---|---|---|---|
| **1. Listen** | New tracks of releases newer than the watermark | Oldest release first, within a release in track order | **Skip**, **Maybe**, **Like**; skip the whole release. Continuous play on by default, and skim mode is a toggle |
| **2. Decide** | Liked and maybe tracks | Liked first, then by release date | **Rate 1–5**, **Buy** or **Skip**, a note. Full-length play by default |

A single-pass mode ("rate straight away") is a setting, since some sessions are short.

### 5.3 Keys and touch

| Action | Key | Touch (tablet) |
|---|---|---|
| Skip | `S` or `X` | Swipe left, or the big ✕ button |
| Maybe | `M` | Swipe up |
| Like | `L` or `+` | Swipe right, or ♥ |
| Skip the whole release | `Shift+S` | Long-press ✕ |
| Rate | `1`…`5` in Decide (in Listen, `1`…`9` seek to 10–90%) | Star row |
| Buy / don't buy | `B` / `N` | Buttons |
| Note | `Enter` opens a one-line field | A text field |
| Jump to the drop | `D` | A "drop" button on the progress bar |
| Seek | `←` `→` ±15 s, `Shift+→` +60 s | Drag |
| Undo the last decision | `U` or `Ctrl+Z` | A snackbar "Undo" for 5 s |
| Open the release in the store | `O` | A link |

Every decision **advances to the next item immediately** while playing. The undo
snackbar covers mis-taps, which matter more on touch than confirmation dialogs do.
The buttons sit at the bottom of the card, within thumb reach on a tablet held in
landscape. Increment 2 tests this on the Android tablet.

## 6. Curated output

### 6.1 The shortlist

- **Per genre**: tracks with decision **buy**, sorted by rating, then release date.
  Each row: artist, title, release, label, the source badges, **purchase links** (one
  per source that sells it), the note, and "Bought" and "Remove" actions.
- **Export**: "Copy links" (one URL per line, grouped by store, so a Bandcamp cart can
  be filled release by release), and Markdown or CSV for the old OneNote habit.
- **Bandcamp wishlist sync**: adding to the wishlist is `POST /collect_item_cb` with
  Toni's session cookie and a page crumb. That means storing a cookie that can do
  anything his account can, on an endpoint `robots.txt` disallows. **Rejected.** The
  row has "Open on Bandcamp", where he taps the heart himself. The shortlist is the
  app's own list, and the wishlist becomes optional.
- **Bought**: marking bought hides the row from the shortlist. On the next
  collection sync, a bought track that is now in the collection becomes **owned**
  automatically. A bought track that never shows up after 30 days is flagged
  ("bought but not imported?"), which catches downloads that never reached Traktor.

### 6.2 Owned detection and dedupe

- **Normalisation** for matching: lower case, Unicode-folded (`ö` → `o`), `&`/`and`,
  `feat.` parts removed from titles and moved to artists, `(Original Mix)` dropped,
  other mix names kept (a remix is a different track), punctuation and whitespace
  collapsed.
- **Owned**: a release track whose normalised `artist + title + mix` equals a
  collection track's is `owned` and never queued. Near matches (edit distance ≤ 2
  on the title, same artist) are shown with an "already owned?" marker instead of
  being hidden. The parse runs in the browser, where `CollectionService` already has
  every track.
- **Across sources**: the same normalised key merges Bandcamp, Beatport and Discogs
  rows into one release with several refs. When a source gives an ISRC (Beatport,
  MusicBrainz), it wins over the name key.

## 7. Imports

### 7.1 The old OneNote documents

| Option | Effort for Toni | What survives | Verdict |
|---|---|---|---|
| **Copy the page in OneNote, paste into the app** (recommended) | Select all, copy, paste into a textarea | The browser's paste event carries `text/html`, so **hyperlinks survive** as well as the text. Works with OneNote on any platform | **Recommended** |
| Export to `.docx`, `.mht` or `.pdf` | Windows OneNote exports Word and MHT. Mac OneNote exports PDF only | Links in `.docx`, often lost in PDF | A fallback: upload the `.docx` |
| Microsoft Graph OneNote API | A Microsoft OAuth app, consent, a second identity provider | Everything, as HTML | Too much machinery for a one-time import. **Rejected** |

**Toni's real format.** The coordinator read his OneNote "Musiikki" section in
OneNote for the web (read only, 2026-10-25). Music pages: *Zenon playlist*, *Zenon
playlist – history*, *Gig 2024-12-20 Menom…*, *Industrial heavy*, *Industrial*,
*Tekno playlist*, *Minimal playlist*, *Ozora 2018 Pumpui*, *Techgnosis & Zentrix
pl…*, *Chill / alt*, *DJ Setup*, *DJ bio*.

- **Period headers** `=== 2023-06`, `2023-4`, sometimes a bare `===`, with a
  `=====…` separator line before a new block. These are the "listened through"
  marks.
- **Release or group headers** as plain lines: `ER031 - Source Of Darkness (Part 1)`,
  `ER30`, `ER29` (catalogue numbers), `Hefty dark chill 3`.
- **A section header `OSTOON NYT`** ("to buy now"), followed by URLs marked
  `ostettu` ("bought").
- **Track lines**: one Bandcamp URL and free text after it. Almost always
  `https://<label>.bandcamp.com/track/<slug>`, sometimes `/album/<slug>`. Slugs often
  carry the BPM (`-138-bpm`, `-134bpm`). Subdomains seen: `erebosrecordsofficial`,
  `anotherpsyderecords`, `tyamat`, `chronozonerecords`, `groove`, `adngroove`.
  (So Erebos's real Bandcamp is `erebosrecordsofficial`; `erebosrecords.bandcamp.com`
  is an empty stub, see S1.)
- **Verdicts and ratings, in Finnish**, after the URL: a digit 1–4 is the rating;
  `ei` no; `ehkä` maybe; `osta` buy; `ostettu` bought; `wishlist`/`wislist` on the
  Bandcamp wishlist; `joo` yes; `kova` strong (positive); `huh`, `öö`, `?`,
  `hmm ehkä parempi` unsure. Combined: `3 ehkä`, `4. osta`, `3 Ei`, `grooovi. Ei`,
  `kova wishlist`, `wislist osta`. Free comments with a rating: `alku 3 loppu 2
  ostettu` (intro 3, ending 2, bought), `aloitus biisi. 3. osta` (opener, 3, buy),
  `Tiukka loppu. 3 Ei`, `mogwai alun jälkeen kova. ei`. Some lines have no token.
- **Highlights**: grey marks a line as bought. Yellow is an emphasis note
  (`<-- Mogwai tällä levyllä`, "Mogwai is on this release").
- ***Industrial heavy* (2019)** is a seed list: artist names as links, with a
  country code `(FI)`, `(DE)`, and short verdicts (`EGGVN ei hyvä` "not good",
  `tsekkaa` "check it out").
- ***Tekno playlist* (2017)** is a table with Artist and Track columns.

**The parser (increment 4), shaped by that format.** Every line is classified and
shown in a preview table before anything is saved:

1. **Input**: read `text/html` from the paste event first (OneNote for the web keeps
   hyperlinks and highlight colours in it), fall back to `text/plain`.
2. **Period header** (`^=+\s*\d{4}-\d{1,2}` or a bare `\d{4}-\d{1,2}` line) → the
   current period. It applies to the URLs below it until the next header, and raises
   each of their labels' watermarks to at least that month (§4.3).
3. **URL line**: the URL is the item (track or album, label from the subdomain). In
   the text after it: **rating** = the first standalone digit 1–5; **verdict** = the
   last of `osta`, `ostettu`, `ei`, `ehkä`, `joo`, `wishlist`/`wislist`; the rest is
   the **note**, with the original text always kept. Mapping: `ei` → skip;
   `ehkä`, `huh`, `öö`, `?` → maybe; `joo`, `kova`, `wishlist` → like; `osta` →
   decision buy; `ostettu` or a grey highlight → bought. BPM in the slug is kept as
   metadata.
4. **Under `OSTOON NYT`**: items default to decision buy.
5. **Release or catalogue header** (`ER031 - …`, `ER30`): a heard release of the
   label of the URLs around it.
6. **A line without a URL** in a list of links (the *Industrial heavy* style): an
   artist or label **seed candidate**, with the country code and verdict as a note;
   `ei hyvä` proposes "don't seed".
7. **A table**: Artist / Track rows → heard tracks matched like any other.
8. **Everything else** is shown as "not understood", never dropped silently.
9. **Dedupe by URL**, and owned detection (§6.2) still applies.

**Every imported item is heard**, whatever its verdict (§4.3).

### 7.2 The Bandcamp wishlist

- The wishlist page `bandcamp.com/<fan>/wishlist` embeds its first items in the
  page data, and its own JavaScript pages through
  `/api/fancollection/1/wishlist_items`, which is under `/api/` (robots-disallowed).
  It needs no cookie when the wishlist is public.
- **A one-time, Toni-initiated import**: the server reads the wishlist page only
  (decision 1 rules out `/api/`, so no paging endpoint; items beyond the first page
  are pasted as URLs instead). Every item is **heard** (§4.3) and becomes a liked
  track in pass 2, with its genre from its tags. The
  fan name may be `toini`: it appears among a release's supporters in a Bandcamp page
  that was pasted into one track's artist field (open question 2).
- The alternative is Toni exporting nothing and the wishlist staying where it is,
  with only new finds in the app.

## 8. Storage and refresh

The app has no writable storage, and the auth flow has `drive.readonly` only.

| Option | Concurrency, tablet and laptop | Backup | Hand-editing | Server-side refresh possible? | Cost and risk |
|---|---|---|---|---|---|
| **S-A. Drive `appDataFolder`, per-device append-only event logs** (recommended) | **No conflicts**: each device writes only its own `events-<deviceId>-<yyyy-mm>.jsonl`. State is the fold of all logs, last decision per item by timestamp. A periodic compacted `snapshot.json` keeps loading fast | It lives in Toni's Drive (hidden from the Drive UI, deleted if he removes the app's access). An "Export all" button writes JSON | Not in the Drive UI. Via export and import only | No: the server holds no token | The **`drive.appdata` scope** (not sensitive), one re-consent, **shared with the set-tracklist ADR**. No new infrastructure |
| S-B. A Google Sheet created by the app (`drive.file` + Sheets API) | Row-level. Two devices editing the same row is last-write-wins, which is rare for one user | Sheets version history | **Yes**, the best of all | No | `drive.file` scope (not sensitive). Sheets API read quotas are per minute. Schema migrations are awkward. A good **export target** for the shortlist later |
| S-C. Firestore in `traktor-toni-2025`, accessed only by the server | Real transactions | Scheduled exports to GCS | No | **Yes**, with the Cloud Run service account | New infrastructure (enable the API, IAM for the service account), and the server becomes stateful. Free tier is ample. Every call needs the allowlist check |
| S-D. SQLite on a Cloud Run volume (GCS FUSE) | Two instances exist briefly during each deploy, and SQLite on GCS FUSE has no safe locking | Bucket versioning | With a tool | Yes | **Corruption risk. Rejected** |
| S-E. A JSON file in visible Drive (`drive.file`) | Whole-file last-write-wins: **the tablet overwrites the laptop's decisions** | Drive versions | Yes, as raw JSON | No | Simple, but the concurrency failure is exactly the tablet + laptop case. **Rejected** |

**Recommendation: S-A.** It costs no infrastructure, the data stays in Toni's own
Drive, the per-device logs remove the two-device conflict by construction, and the
scope is the one the set-tracklist ADR already proposes. Data is small: thousands of
items at ~200 bytes each.

**Refresh: on demand.**

| Option | Gives | Costs |
|---|---|---|
| **On demand** (recommended) | Opening a genre's Listen tab refreshes its seeds in the background, stalest first, while the queue already plays cached items. "Refresh" on a seed forces it | Bandcamp at 1 request per 2 s means ~2 minutes for 60 seeds. That is fine while listening |
| Cloud Scheduler job | New items are waiting | Needs S-C (server-side storage and credentials), and for Bandcamp it is B1, which §1.2 rejects. Reasonable later for Discogs and Beatport only |

The server's caches (label pages, release data, resolved stream URLs) are in memory,
so a restart costs a few refetches. The client keeps fetched release metadata in
IndexedDB, so it survives a reload without asking the server.

## 9. Privacy and security

- **No credentials in the repo.** The Discogs personal token and any Beatport client
  secret go into Secret Manager, as `Discover__DiscogsToken` and
  `Discover__BeatportClientSecret`, like `RemoteAuth__TicketSecret` today.
- **No Bandcamp session cookie, ever.** Nothing in the design needs one: wishlist
  writes are links out (§6.1), and the wishlist import reads the public page.
- **Every `/api/discover/*` endpoint requires the allowlisted user**
  (`RemoteAuth.IdentifyAsync` with `Authorization: Bearer`, cached per token for its
  lifetime). Without it, Cloud Run's `--allow-unauthenticated` service would be an
  open proxy that fetches Bandcamp for anyone, under our IPs.
- **No SSRF**: an adapter fetches only its `AllowedHosts` (`*.bandcamp.com`,
  `bandcamp.com`, `api.discogs.com`, `api.beatport.com`, `musicbrainz.org`), over
  HTTPS, following redirects only within them. URLs Toni pastes are parsed into
  source refs, never fetched as given. The audio endpoint redirects only to the
  adapter's CDN hosts (`*.bcbits.com`, Beatport's sample host).
- **Outbound rate limits**, enforced in the server, per host: Bandcamp 1 request per
  2 s, burst 3, 500 per day; Discogs 50 per minute; MusicBrainz 1 per second, as its
  policy requires. Hitting a cap
  returns 429 to the app, which says so, rather than queueing silently.
- **User-Agent**: `traktor-google-drive/1.0 (+https://github.com/toini/traktor-google-drive)`,
  with no personal email in it.
- **Logs**: stream URLs carry signed tokens. The audio endpoint logs the track ref,
  never the redirect target. Our own URLs carry no secrets.
- **Personal data**: only Toni's own (his ratings, notes, wishlist). Artist and label
  names are public catalogue data. Nothing goes to a new processor: Drive is where his
  data already lives, and the sources receive only catalogue queries. If B3 is chosen,
  reading his email is the one sensitive step, and the Apps Script variant keeps it
  inside his own account rather than giving this app a Gmail scope.

## 10. Increments

| # | What | Why first or later |
|---|---|---|
| **0** | **Spikes S1–S5** (§11). No app code | Each one can change a recommendation above |
| **1** | **Seeds**: from a playlist (H16) and genre top lists, manual URL add, the stop list. **Bandcamp adapter** (B2): label/artist `/music`, release pages, tags. **Playback**: `PlayableItem`, the redirect endpoint, a discovery queue in the player bar, continuous play. **Listen tab** with skip / maybe / like / rate / buy, undo. **Watermarks.** **Owned detection.** **Shortlist** with "copy links". **Storage S-A** with the `drive.appdata` scope | The smallest thing that replaces the OneNote loop for psy, where Bandcamp is the source. The playlist seed keeps the first queue small and current |
| **2** | **Decide tab** (pass 2), notes, **skim mode** and drop offsets, learned offsets, tablet swipe layout, remote-control `triage`/`rate` commands | Polishes the loop once its basic shape is proven on real listening |
| **3** | **Techno**: the **Discogs adapter** for release lists and dates (and label URLs), plus the **Beatport adapter** if access is granted. Discogs finds play through a Bandcamp URL when Discogs has one, else a Beatport preview, else they are link-out only (Juno/Traxsource search links, §1.4) | Techno coverage. Depends on S2 and S3 |
| **4** | **Imports**: OneNote paste parser, Bandcamp wishlist import; the **Bandcamp release-email feed** (B3) if S4 works | Needs Toni's samples. B3 then takes the "what's new" load off B2 |
| **5** | **Suggestions**: label mates, remixers, compilation artists, Bandcamp's "you may also like", with "add as seed" and "never suggest" | Most valuable once there is triage history to rank them with |

## 11. Feasibility risks and spikes

| Risk | Uncertainty | Spike | Done when |
|---|---|---|---|
| **Bandcamp page data** | Is `data-tralbum` still on release pages with `mp3-128` URLs? How long does a stream URL live? Does `bcbits.com` check `Referer`, and does it send CORS headers? Does Chrome (desktop and Android) play a 302 from our origin to it? | **S1**: by hand from a laptop (not Cloud Run): fetch 3 label pages and 5 release pages for H16 and Z labels (Darker Sounds, Erebos, Padang), at most 20 requests. Play a stream URL in `<audio>` with no referrer, after 1 h, 6 h and 24 h. Serve a 302 from a local ASP.NET stub | A one-page note: page fields found, URL lifetime, Referer and CORS behaviour, playback result on both devices |
| **Bandcamp from Cloud Run** | Does Bandcamp answer requests from Google Cloud egress IPs, or challenge them? | **S1b**: one deploy of a tiny Cloud Run job in `traktor-toni-2025` fetching 3 pages, then deleted | Status codes and any challenge page |
| **Beatport access** | Will Beatport grant partner API access to an individual for personal non-commercial use? How long does it take? | **S2**: submit the application now, describing the use honestly. Also check whether sample URLs play cross-origin in `<audio>` | Granted, refused, or no answer in 4 weeks (then techno relies on Discogs, with Bandcamp audio where it exists and link-outs elsewhere) |
| **Discogs coverage of digital techno and psy** | Are H labels' digital releases on Discogs with dates? Do label and release profiles list a Bandcamp URL, which is their only playable fallback without Beatport? | **S3**: for the top 20 labels per genre and the 14 H16 labels, query Discogs (authenticated, ≤ 60/min) and count releases since 2024, date precision, and Bandcamp URLs | A coverage table per genre. If H coverage is poor and Beatport is refused, Bandcamp is the H source too |
| **Bandcamp release emails** | Do follows of labels and artists produce emails with release URLs, and can they be filtered reliably? | **S4**: Toni follows 3 labels on Bandcamp. Inspect the next emails' structure | A sample email and a parse rule. Decide Apps Script vs Gmail API |
| **`drive.appdata` from WASM** | Writing and listing `appDataFolder` from the browser with the GIS token flow, and how re-consent looks | **S5**: a branch-only page that writes and reads a log file. Shared with the set-tracklist ADR | Works on laptop and tablet after one consent |

### Spike findings (increment 0, 2026-10-25)

All Bandcamp requests were by hand, one at a time with ≥ 2 s between them, with the
User-Agent `traktor-google-drive/1.0 (+https://github.com/toini/traktor-google-drive)`:
**7 page requests from the laptop (plus one stream range request and two short
plays of one stream) and 3 from Cloud Run**, no `/search`, no `/api/`.

**S1, Bandcamp page data (laptop):**

| Question | Result |
|---|---|
| Label page `/music` | **Darker Sounds: 59 releases** (16 rendered in the grid, 12 albums and 4 singles, with `/album/…` or `/track/…` links and `data-item-id="album-<id>"`, the other 43 in a `data-client-items` JSON attribute with `id`, `title`, `artist`, `page_url`, `type`). **Erebos (`erebosrecordsofficial`): 53** (16 + 37). **Carnivor Records: 10**, all rendered, including Obwigszyh's *Internal Motion*. **The label page has no release dates.** `erebosrecords.bandcamp.com` is an empty stub page: a label's real subdomain must come from a known URL (the collection's comments, Discogs, OneNote), never from guessing |
| Release page | `data-tralbum` is there, with `album_release_date` (`09 Nov 2025 18:21:12 GMT`), `trackinfo[]` (title, duration, `track_id`, `title_link`, `file["mp3-128"]`), and `play_cap_data: {streaming_limits_enabled: true, streaming_limit: 3}`. Tags come from the `ld+json` block's `keywords` (`Hard Techno`, `Dark Hard Techno`, `Dark Techno Industrial`…), not from tag links. `data-band` gives the label name, `data-embed` the embed ids |
| Track page `/track/<slug>` | The same `data-tralbum` with `album_url` (`/album/shadow-alliance-ep`) and the album's release date, and the track's `isrc` in `current`. **So an imported track URL maps to its release** (§4.3) with one fetch |
| Stream URL lifetime | `https://t4.bcbits.com/stream/<hash>/mp3-128/<track_id>?p=0&ts=…&t=…&token=<unix expiry>_<sig>`. The expiry is **fetch time + 24 h** (86,400 s, measured on two fetches) |
| Referer and CORS on the stream | A `Range` request with **no Referer** and a foreign `Origin` gets **206**, `audio/mpeg`, `accept-ranges: bytes`. **No `Access-Control-Allow-Origin`** header: the bytes cannot be read by `fetch`, so no waveforms (as §2.4 expected) |
| A 302 from our origin in `<audio>` | **Plays** in desktop Google Chrome and in Playwright's Chromium: a local server answered `/audio/bandcamp/2927639579` with 302 to the stream URL. Playback started, `duration` was right (470 s), and **a seek to 300 s worked without hitting our endpoint again**: the element keeps using the redirected URL for its range requests. So after the 24 h expiry a seek fails, and the player re-requests our URL at the current position (the existing reload-at-position path) |
| Android Chrome | **Not verified**: no device or emulator was reachable from this machine. To check on the tablet in increment 1, against the branch deploy |
| Play cap | `streaming_limit: 3` is per listener: Bandcamp counts full plays of an unbought track. Triage plays a track once or twice, so the cap matters only for repeated pass-2 listening. Not a blocker |

Two consequences for the adapter:

- **Dates cost a release-page fetch each.** The grid is newest first, so the adapter
  walks it from the top, fetching release pages until it passes the seed's
  watermark (usually a handful), then queues those oldest first. With no watermark it
  stops after the 10 newest, and "Load older" continues the walk on request.
- **The stream URL is cached for 23 h** per track, and the redirect endpoint only
  refetches the release page after that.

**S1b, Bandcamp from Cloud Run:** a throwaway Cloud Run **job `bc-spike-s1b`** in
`traktor-toni-2025` / europe-north1 (`curlimages/curl`) fetched Carnivor's `/music`,
Darker Sounds' *Shadow Alliance EP* and Erebos's `/music`: **all three 200, the same
byte sizes as from the laptop, with no challenge page**. The job was deleted
afterwards (`gcloud run jobs list` is empty).

**S3, Discogs coverage of the H16 labels and the first seed** (unauthenticated API,
one request per 2.6 s, 56 requests; database search answered without a token):

| Label | Discogs releases | Since 2024 | Newest (date precision) | Bandcamp URL in profile | Bandcamp releases |
|---|---|---|---|---|---|
| **Carnivor Records** | **2** | 0 | 2020-04-15 (day) | yes | **10** |
| Darker Sounds | 9 | 2 | 2026-02-25 (day) | yes | 59 |
| Erebos Records | 13 | 0 | 2021-04-20 | no | 53 |
| RAW. | wrong match (a 1990s CD label of the same name) | | | | |
| Valhall Records | 1 | 1 | 2025-08-15 | no | |
| Attack MuZik | 9 | 4 | 2025-03-21 | no | |
| Legion IX | 1 | 0 | 2020-12-17 (vinyl) | no | |
| Antibody | not found | | | | |
| Falsive Records | 34 | 17 | 2026-01-22 | yes | |
| Fresh Cut | 8 | 0 | 2020-06-15 | no | |
| Definition Of Hard Techno | 27 | 0 | 2023-02-16 | no | |
| Eternal Damnation | 9 | 0 | 2020-02-10 (vinyl) | yes (and Beatport) | |
| DEMIAN RECORDS | 24 | 4 | 2024-03-14 (vinyl) | yes | |
| Vision Serpent | 1 | 1 | 2026-05-22 | no | |

**Obwigszyh** on Discogs: 34 releases (2018 onward, mostly self-released or small
labels), profile links SoundCloud and Facebook only.

What this says:

- **For these H labels Discogs is far behind Bandcamp** (Carnivor 2 vs 10, Darker
  Sounds 9 vs 59, Erebos 13 vs 53). Where a label has a Bandcamp page, Bandcamp is the
  better release list as well as the only audio. Discogs earns its place for labels
  with **no** Bandcamp page (Attack MuZik, Definition Of Hard Techno, Fresh Cut,
  Valhall, Vision Serpent), and as a source of Bandcamp URLs (6 of 13 found).
- When Discogs has a full date, it is day-accurate, but only on the release itself:
  the label's release list gives the year only, so a date costs one request each,
  as on Bandcamp.
- Name matching needs a human check: `RAW.` matched an unrelated label, and
  `Antibody` matched nothing. The seed's Discogs ref is confirmed by Toni once, then
  stored.
- **Carnivor and Obwigszyh, the first seed, live on Bandcamp** (Carnivor's own page,
  10 releases, *Internal Motion* among them). Increment 1, which is Bandcamp-only,
  therefore covers the first seed. Obwigszyh's other labels (LÄRM, EXILES and
  self-releases) need their own Bandcamp URLs: unknown until a release page or Toni
  supplies them.

**S2, Beatport:** not applied for; that is Toni's to do. Steps:

1. Sign in to (or create) a Beatport account, and open the partner portal's
   developer section:
   <https://partnerportal.beatport.com/hc/en-us/categories/13076449714580-Developers>.
2. Submit a request (<https://partnerportal.beatport.com/hc/en-us/requests/new>) for
   **API v4 access**: a personal, non-commercial tool for one user that lists label
   and artist releases and plays their samples, in order to buy tracks on Beatport;
   no data redistribution, a few hundred requests a day.
3. Give the OAuth callback URI they ask for: `https://<the Cloud Run URL>/api/discover/beatport/callback`
   (only needed for a user flow; the adapter uses client credentials).
4. When a `client_id` and `client_secret` arrive, put them into Secret Manager as
   `Discover__BeatportClientId` / `Discover__BeatportClientSecret`. Never in the repo.

**S4, Bandcamp release emails:** not run (needs Toni to follow labels). Deferred to
increment 4.

**S5, `drive.appdata` from the browser:** **not verified live.** Minting a token with
the new scope needs Toni's own consent click, which no automated session has (no
user-scope token was available from `gcloud`). The calls are the documented ones:
`GET /drive/v3/files?spaces=appDataFolder`, and a multipart `POST /upload/drive/v3/files`
with `parents: ["appDataFolder"]`. Increment 1 covers them with the Drive mock in e2e,
and the first real check is Toni's re-consent on the branch.

## 12. Testing

- **No live calls in tests.** Each adapter's parser is a pure function over a
  recorded response. Fixtures live in `tests/fixtures/discover/<source>/` (Bandcamp
  label and release HTML, Discogs JSON), recorded once by a manual,
  rate-limited script (`scripts/discover-record-fixtures`) that is never run in CI.
  Personal data is stripped (no fan pages beyond Toni's own wishlist, which is
  replaced by a synthetic one).
- **Parser unit tests**: a new xUnit project, `tests/server-unit`, since the repo has
  none. The parsers are where Bandcamp's markup changes will show up, and they are
  cheap to test there.
- **Server tests** (`tests/server/discover.sh`, against the real server): with
  `Discover__FixtureMode=1`, the adapters' `HttpClient`s get a handler that serves
  fixtures. Tested: the allowlist is required, a non-allowlisted host is refused, the
  audio endpoint 302s only to CDN hosts and answers `?probe=1` with a status, the
  rate limiter returns 429 at the cap.
- **e2e** (Playwright): `/api/discover/**` is routed to a mock, as `drive-mock.ts`
  does for Drive, and the audio endpoint redirects to a short local silent MP3.
  Tested: the H16 seed preview shows 14 labels and the stop list works (on a fixture
  collection with the same shape); the queue plays in the player bar with next,
  previous and continuous play; skip/maybe/like advance and undo works; a watermark
  hides older releases; an owned track never appears; the shortlist copies links;
  two simulated devices' logs merge without losing a decision; a remote controller
  can skip and rate.
- **Fixture drift**: the record script also diffs new recordings against the
  committed ones, run by hand when Bandcamp seems to have changed. That is the early
  warning for the markup risk.

**As built for increment 1** (the plan above, adjusted):

- `tests/fixtures/discover/bandcamp/`: five pages recorded in S1 (Carnivor's and
  Darker Sounds' `/music`, *Internal Motion*, *Shadow Alliance EP* and one of its
  track pages), trimmed to what the parser reads. Fan names, "supported by" and
  sponsor data are removed and stream signatures replaced with `fixture`. The
  recording script is not written yet; the trimming was done by hand from the spike.
- `tests/server-unit` (xUnit, the repo's first): the parser on those pages, the URL
  allowlist, the polite fetcher and caches with a fake clock and a fake store, the
  real endpoints in a `WebApplicationFactory` host with recorded pages behind them
  (allowlist, refused URL shapes, the redirect only to the CDN, `?probe=1`), and the
  client's pure logic (names, seed finding, the event fold, the queue, the
  shortlist text). The server shell test (`discover.sh`) was not needed: the
  endpoint tests cover it in-process.
- `tests/e2e/discover.spec.ts` with `discover-mock.ts` and
  `fixtures/discover-collection.nml` (an H16-shaped slice): `/api/discover/**` and
  the appDataFolder are mocked, and any request to a store host is aborted and
  recorded, and the tests assert there were none.
- `tests/e2e/discover-play.spec.ts`: the mocked audio endpoint answers 302 to a
  stand-in CDN serving a generated silent MP3 (with ranges, no CORS headers). Tested:
  playing through the redirect, no download, waveform or buffer bar, a verdict and
  the bar's Next moving card and player together, continuous play, the probe and
  reload on a failed stream, and that a reload does not remember a preview.
- The xUnit project is `tests/server-unit/TraktorGoogleDrive.Server.Tests.csproj`,
  beside `tests/unit` (the client's gig-preview rules), which landed on master
  meanwhile.

## Consequences

**Positive**

- One place that replaces the OneNote loop: find, fetch, listen, judge, keep.
- Triage in the player Toni already uses, with the queue, keyboard and remote
  behaviour he knows.
- Genres stay apart, by routing on the release's tags, while multi-genre labels
  stay seeds where they belong.
- Nothing comes up twice (watermarks and per-item state), and nothing he owns
  comes up at all.
- No new infrastructure in increment 1. Data stays in his Drive.

**Cost / risk**

- **The Bandcamp adapter operates outside Bandcamp's acceptable-use policy**, at low
  volume and at Toni's pace. The realistic failure is a block of Cloud Run's IPs,
  which would turn Bandcamp into link-out only until B3 or a change of plan. The
  adapter is isolated, so the rest keeps working.
- **Markup breakage**: Bandcamp can change its pages at any time. Fixtures and the
  drift check make that visible, not impossible.
- **Techno coverage depends on others' decisions** (Beatport's grant) and on
  Discogs' coverage. Without them, H discovery is weaker than psy.
- **A new OAuth scope** (`drive.appdata`): one re-consent.
- **`PlayerService` gets an item abstraction** (`PlayableItem`). That is the
  largest change to existing code, and it touches the code other branches
  (prefetch and buffering) are working on, so it should land after them.
- **No waveforms for discovery items.**

## Open questions for Toni

Answered (see §"Decisions Toni made"): the Bandcamp posture (B2), Hefty (not an
alias), storage (`drive.appdata`), the first genre (H via H16), and the OneNote
format (read from OneNote for the web, §7.1). Still open:

1. **What are X, F and DNB**, and the root playlists `ZX3`, `Psybreaks-uudet`,
   `Downtempo`, `Techno 02`? X shares 100 of its 166 tracks with H. Should any of
   them be a Discover genre, or count towards one?
2. **Your Bandcamp fan name**: is it `toini`? Is the wishlist public? Import it
   once into pass 2?
3. **Beatport**: will you apply for partner API access (steps in §11)? Without it,
   Discogs-found techno with no Bandcamp page is link-out only.
4. **Defaults**: auto seed at ≥ 2 tracks in a genre? Skim 60 s from 35% for Z and P,
   25% for H, 20% for C?
5. **Bought detection**: tracks bought on Bandcamp land in Traktor how, and how
   soon? The "bought but not imported after 30 days" flag assumes they do.
