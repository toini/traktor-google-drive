#!/usr/bin/env python3
"""Write a history-derived ground-truth timeline for one recording, in the ADR §0 shape.

Times are seconds into the recording. The recording's start on the history clock comes from
pair_history.py (the raw Traktor recording's name, cross-checked against its UTC end stamp).

What history can and cannot say:
  - deckStart is when the deck started, not when the fader came up. The audible entry of a track
    is somewhere in [deckStart, previous track's deckEnd]; only audio alignment can pin it.
  - deckEnd is deckStart + DURATION (capped at the track's own length): when the deck stopped.
  - segment start/end is the midpoint of the overlap between consecutive decks (the ADR's
    "midpoint of the blend"), so it is an estimate, good to about half the blend length.

Output holds titles, artists and times only (no file paths), so it is safe to commit.

Usage: ground_truth.py [--recording SUBSTRING] [--out FILE] [--ni-root DIR]
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nml  # noqa: E402
import pair_history as ph  # noqa: E402

SHORT_PLAY_SECONDS = 30  # a public play this short is a cue check or a scratch, not a track in the set


def build(ni_root: str, recording: str) -> dict:
    pairs, _ = ph.pair_all(ni_root)
    match = [p for p in pairs if recording.lower() in p.recording.lower()]
    if not match:
        sys.exit(f"no paired recording matches {recording!r}")
    pr = sorted(match, key=lambda p: (p.anchor != "raw-twin", len(p.recording)))[0]
    coll = nml.read_collection(os.path.join(ni_root, "Traktor 4.5.1", "collection.nml"))
    plays = [p for p in nml.read_history(ni_root) if p.public]
    t0 = dt.datetime.fromisoformat(pr.start)
    dur = pr.duration_s

    rows = []
    for p in plays:
        a = (p.start - t0).total_seconds()
        b = a + ph.Fitter.audible(p, coll.tracks)
        if b <= 0 or a >= dur:
            continue
        t = coll.tracks.get(p.key)
        rows.append({
            "title": t.title if t else os.path.splitext(p.key.rsplit("/:", 1)[-1])[0],
            "artist": t.artist if t else "",
            "deck": p.deck,
            "historyStart": f"{p.start:%Y-%m-%d %H:%M:%S}",
            "deckStart": round(a, 1),
            "deckEnd": round(b, 1),
            "deckSeconds": round(p.duration, 1),
            "short": p.duration < SHORT_PLAY_SECONDS,
            "_key": p.key,
        })
    main_rows = [r for r in rows if not r["short"]]

    segments = []
    for i, r in enumerate(main_rows):
        prev = main_rows[i - 1] if i else None
        nxt = main_rows[i + 1] if i + 1 < len(main_rows) else None
        blend_in = [r["deckStart"], prev["deckEnd"]] if prev and prev["deckEnd"] > r["deckStart"] else None
        blend_out = [nxt["deckStart"], r["deckEnd"]] if nxt and r["deckEnd"] > nxt["deckStart"] else None
        start = (sum(blend_in) / 2 if blend_in else r["deckStart"]) if prev else 0.0
        end = (sum(blend_out) / 2 if blend_out else nxt["deckStart"]) if nxt else dur
        segments.append({
            "index": i + 1,
            "start": round(max(0.0, start), 1),
            "end": round(min(dur, end), 1),
            "blendIn": blend_in,
            "blendOut": blend_out,
            "deckStart": r["deckStart"],
            "deckEnd": r["deckEnd"],
            "playedSeconds": round(min(r["deckEnd"], dur) - max(r["deckStart"], 0.0), 1),
            "track": {"title": r["title"], "artist": r["artist"]},
            "deck": r["deck"],
            "confidence": 1.0,
            "source": "history",
        })

    # Compare with the recording's own playlist: same tracks? same order?
    fam = [k for k in coll.playlists.get(pr.family_playlist or "", []) if not is_separator(coll.tracks.get(k))]
    played = [r["_key"] for r in main_rows]
    in_list = [k for k in played if k in set(fam)]
    pos = {k: i for i, k in enumerate(dict.fromkeys(fam))}
    ordered = [pos[k] for k in dict.fromkeys(in_list)]
    lis = _lis_len(ordered)
    title = lambda k: coll.tracks[k].title if k in coll.tracks else k  # noqa: E731
    comparison = {
        "playlist": pr.family_playlist,
        "playlistEntries": len(fam),
        "playlistUniqueTracks": len(set(fam)),
        "playedTracks": len(set(played)),
        "playedNotInPlaylist": sorted({title(k) for k in played if k not in set(fam)}),
        "inPlaylistNotPlayed": sorted({title(k) for k in fam if k not in set(played)}),
        "playlistDuplicates": sorted({title(k) for k in fam if fam.count(k) > 1}),
        "longestInOrderRun": lis,
    }
    return {
        "v": 1,
        "recording": {"name": pr.recording, "duration": dur},
        "anchor": {
            "recordingStartLocal": pr.start,
            "method": pr.anchor,
            "evidence": pr.notes,
            "uncertaintySeconds": 2 if pr.anchor == "raw-twin" else None,
            "needsAudioConfirmation": True,
        },
        "generator": {"tool": "set-tracklist/ground_truth.py", "version": "0.1.0", "method": "history"},
        "segments": segments,
        "shortPublicPlays": [{k: v for k, v in r.items() if k != "_key"} for r in rows if r["short"]],
        "playlistComparison": comparison,
    }


def is_separator(t: nml.Track | None) -> bool:
    """Toni uses '====' entries as dividers inside playlists."""
    return t is not None and set(t.title.strip()) <= set("=-_ ")


def _lis_len(xs: list[int]) -> int:
    import bisect
    tails: list[int] = []
    for x in xs:
        i = bisect.bisect_left(tails, x)
        tails[i:i + 1] = [x]
    return len(tails)


def fmt(s: float) -> str:
    s = max(0, s)
    return f"{int(s // 3600)}:{int(s % 3600 // 60):02d}:{int(s % 60):02d}"


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ni-root", default=nml.DEFAULT_NI_ROOT)
    ap.add_argument("--recording", default="Z19-2025 Sentimony 2025-02-23.wav")
    ap.add_argument("--out")
    a = ap.parse_args()
    gt = build(a.ni_root, a.recording)
    if a.out:
        with open(a.out, "w") as f:
            json.dump(gt, f, indent=2, ensure_ascii=False)
            f.write("\n")
    print(f"{gt['recording']['name']}: start {gt['anchor']['recordingStartLocal']} ({gt['anchor']['evidence']})")
    for s in gt["segments"]:
        print(f"{s['index']:3d} {fmt(s['start'])}-{fmt(s['end'])}  deck {fmt(s['deckStart'])}+{s['playedSeconds']:5.0f}s  "
              f"{s['track']['artist'][:22]:22s} {s['track']['title'][:55]}")
    print(json.dumps(gt["playlistComparison"], indent=1, ensure_ascii=False))
    if gt["shortPublicPlays"]:
        print("short public plays:", [(r["title"], r["deckStart"], r["deckSeconds"]) for r in gt["shortPublicPlays"]])


if __name__ == "__main__":
    main()
