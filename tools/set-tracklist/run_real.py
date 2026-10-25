#!/usr/bin/env python3
"""Run the detector on a real recording against one of its playlists and print a tracklist
Toni can check by ear: start time, artist - title, confidence, and the candidates judged
"not played".

Track files are found by base name under --audio-dir (a Drive for desktop folder or an
unzipped download). The same file in two folders (same base name) counts as one candidate.

With --ground-truth (a history-derived JSON from ground_truth.py) it also scores the result
with the ADR §8 metrics. Its signed boundary median also checks the anchor: history deck starts
lead the audible handover, so expect it positive by about half a blend. A large offset shared
by every boundary means the recording's start time is wrong.

Usage:
  run_real.py --recording FILE --playlist "Folder/Name" --audio-dir DIR
              [--prior partial|rec|base|none] [--method viterbi|greedy] [--candidates excerpts|whole]
              [--no-compare]
              [--grid] [--ground-truth JSON] [--out DIR] [--ni-root DIR]

Examples:
  run_real.py --recording ~/Downloads/h15/H15\\ 2023-05-22.wav --audio-dir ~/Downloads/h15 \\
      --playlist "H/H15 2023 Darker Sounds - nauhoitettu 2023-05-22?"
  run_real.py --recording ".../Z19-2025 Sentimony 2025-02-23.wav" --audio-dir ... \\
      --playlist "Z/Z19-2025 Sentimony " --prior base --grid \\
      --ground-truth ground-truth/z19-2025-sentimony-2025-02-23.json
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import warnings
from dataclasses import asdict

import librosa
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import compare  # noqa: E402
import detect  # noqa: E402
import evaluate  # noqa: E402
import nml  # noqa: E402
from ground_truth import fmt, is_separator  # noqa: E402

warnings.filterwarnings("ignore", category=FutureWarning)
warnings.filterwarnings("ignore", category=RuntimeWarning)
SR = 22050
HERE = os.path.dirname(os.path.abspath(__file__))


def find_files(root: str) -> dict[str, str]:
    out = {}
    for d, _, files in os.walk(root):
        for f in files:
            out.setdefault(f.lower(), os.path.join(d, f))
    return out


def label(t: nml.Track) -> str:
    return f"{t.artist} – {t.title}" if t.artist and t.artist not in t.title else t.title


def conf_word(c: float) -> str:
    return "high" if c >= 0.5 else "likely" if c >= 0.2 else "unsure"


def tracklist(segs: list[detect.Segment], cands: dict[str, nml.Track], playlist_order: list[str]) -> str:
    lines = []
    played = set()
    for i, s in enumerate(segs, 1):
        if s.track == detect.UNKNOWN:
            lines.append(f"{i:3d}. {fmt(s.start)}  {'?':6s}  (unknown)  [{fmt(s.start)}-{fmt(s.end)}]")
            continue
        played.add(s.track)
        t = cands[s.track]
        lines.append(f"{i:3d}. {fmt(s.start)}  {conf_word(s.confidence):6s}  {label(t)}"
                     f"  [{(s.end - s.start) / 60:.1f} min, list #{playlist_order.index(s.track) + 1}]")
    not_played = [k for k in playlist_order if k not in played]
    if not_played:
        lines.append("")
        lines.append("Not played (judged absent from the recording):")
        for k in not_played:
            lines.append(f"     list #{playlist_order.index(k) + 1:2d}  {label(cands[k])}")
    return "\n".join(lines)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--recording", required=True, help="path to the recording's audio file")
    ap.add_argument("--playlist", required=True, help='collection playlist, "Folder/Name" (as nml.py prints it)')
    ap.add_argument("--audio-dir", required=True, help="folder holding the playlist's track files (searched recursively)")
    ap.add_argument("--prior", default="partial", choices=["partial", "rec", "base", "none"],
                    help="playlist order trust: partial = strong order, partial plays, skips and an "
                         "unplayed tail (H15); rec = strong, nearly no skips; base = weak; none")
    ap.add_argument("--no-compare", action="store_true",
                    help="skip the waveform comparison report (out/<recording>/report.html)")
    ap.add_argument("--method", default="viterbi", choices=["viterbi", "greedy"])
    ap.add_argument("--candidates", default="excerpts", choices=["excerpts", "whole"])
    ap.add_argument("--grid", action="store_true", help="also run every prior x method x candidates combination")
    ap.add_argument("--ground-truth", help="history ground-truth JSON to score against")
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--ni-root", default=nml.DEFAULT_NI_ROOT)
    a = ap.parse_args()

    coll = nml.read_collection(os.path.join(a.ni_root, "Traktor 4.5.1", "collection.nml"))
    if a.playlist not in coll.playlists:
        near = [p for p in coll.playlists if a.playlist.lower().strip() in p.lower()]
        sys.exit(f"playlist {a.playlist!r} not found" + (f"; did you mean {near[:5]}?" if near else ""))
    files = find_files(a.audio_dir)

    # Candidates in list order, separators dropped, one per file base name.
    order: list[str] = []
    cands: dict[str, nml.Track] = {}
    for k in coll.playlists[a.playlist]:
        t = coll.tracks.get(k)
        if t is None or is_separator(t):
            continue
        base = os.path.basename(t.path).lower()
        if base not in cands:
            cands[base] = t
            order.append(base)
    missing = [cands[b].path for b in order if b not in files]
    if not os.path.exists(a.recording) or missing:
        sys.exit(f"missing audio: recording={'ok' if os.path.exists(a.recording) else a.recording}, "
                 f"tracks missing={len(missing)}\n" + "\n".join(missing))

    t0 = time.time()
    y, _ = librosa.load(a.recording, sr=SR, mono=True)
    rec = detect.beat_features(y, SR, None)
    print(f"recording: {os.path.basename(a.recording)}, {len(y) / SR / 60:.1f} min, "
          f"tempo {rec.tempo:.1f} BPM, features in {time.time() - t0:.0f}s")

    reps = ["excerpts", "whole"] if a.grid else [a.candidates]
    feats: dict[str, dict[str, detect.BeatFeatures]] = {r: {} for r in reps}
    t0 = time.time()
    for b in order:
        t = cands[b]
        ty, _ = librosa.load(files[b], sr=SR, mono=True)
        if "whole" in feats:
            feats["whole"][b] = detect.beat_features(ty, SR, t.bpm)
        if "excerpts" in feats:
            feats["excerpts"][b] = detect.beat_features(detect.excerpts(ty, SR), SR, t.bpm)
    print(f"{len(order)} candidates from {a.playlist!r}, features in {time.time() - t0:.0f}s")

    stem = os.path.splitext(os.path.basename(a.recording))[0]
    os.makedirs(a.out, exist_ok=True)

    segs = detect.detect(rec, feats[a.candidates if not a.grid else "excerpts"], prior=a.prior, method=a.method)
    text = tracklist(segs, cands, order)
    print(f"\nTracklist ({a.method}, prior={a.prior}, {a.candidates if not a.grid else 'excerpts'}):\n{text}")
    with open(os.path.join(a.out, f"{stem}.tracklist.txt"), "w") as f:
        f.write(text + "\n")
    with open(os.path.join(a.out, f"{stem}.tracklist.json"), "w") as f:
        json.dump({
            "v": 1,
            "recording": {"name": os.path.basename(a.recording), "duration": round(len(y) / SR, 1)},
            "playlist": a.playlist,
            "generator": {"tool": "set-tracklist", "version": "0.1.0", "method": "audio",
                          "prior": a.prior, "decoder": a.method},
            "segments": [{"start": round(s.start, 1), "end": round(s.end, 1),
                          "track": None if s.track == detect.UNKNOWN else
                          {"title": cands[s.track].title, "artist": cands[s.track].artist},
                          "confidence": s.confidence, "source": "audio"} for s in segs],
            "notPlayed": [{"title": cands[b].title, "artist": cands[b].artist}
                          for b in order if b not in {s.track for s in segs}],
        }, f, indent=1, ensure_ascii=False)
    print(f"\nwrote {os.path.join(a.out, stem)}.tracklist.txt and .json")

    if not a.no_compare:
        t0 = time.time()
        cmp_cands = {b: compare.Candidate(label(cands[b]),
                                          (lambda b=b: librosa.load(files[b], sr=SR, mono=True)[0]), cands[b].bpm)
                     for b in order}
        report = compare.Report(os.path.join(a.out, stem), os.path.basename(a.recording), y, SR, rec, cmp_cands)
        path = report.build(segs, [b for b in order if b not in {s.track for s in segs}])
        print(f"comparison report: {path} ({time.time() - t0:.0f}s)")

    if not (a.grid or a.ground_truth):
        return
    truth = blends = None
    if a.ground_truth:
        gt = json.load(open(a.ground_truth))
        truth = [(s["start"], s["end"], s["track"]["title"]) for s in gt["segments"]]
        blends = [(p["track"]["title"], n["track"]["title"], n["deckStart"], p["deckEnd"])
                  for p, n in zip(gt["segments"], gt["segments"][1:])]
    rows = []
    for prior in (["partial", "rec", "base", "none"] if a.grid else [a.prior]):
        for rep in reps:
            for method in (["viterbi", "greedy"] if a.grid else [a.method]):
                t1 = time.time()
                s2 = detect.detect(rec, feats[rep], prior=prior, method=method)
                row = {"prior": prior, "candidates": rep, "method": method, "seconds": round(time.time() - t1, 2),
                       "segments": len(s2), "unknown": sum(s.track == detect.UNKNOWN for s in s2)}
                if truth:
                    pred = [(s.start, s.end, cands[s.track].title if s.track in cands else s.track) for s in s2]
                    row.update(asdict(evaluate.evaluate(truth, pred, blends)))
                    if len(pred) == len(truth):
                        row["signed_boundary_median"] = round(float(np.median(
                            [p[0] - t[0] for t, p in zip(truth[1:], pred[1:])])), 1)
                rows.append(row)
    hdr = list(dict.fromkeys(k for r in rows for k in r))
    print("\n| " + " | ".join(hdr) + " |\n|" + "---|" * len(hdr))
    for r in rows:
        print("| " + " | ".join(str(r.get(h, "")) for h in hdr) + " |")
    with open(os.path.join(a.out, f"{stem}.scores.json"), "w") as f:
        json.dump(rows, f, indent=1)


if __name__ == "__main__":
    main()
