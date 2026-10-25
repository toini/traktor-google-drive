#!/usr/bin/env python3
"""Run the detector on a real recording once its audio is local, and grade it against the
history ground truth.

Files are found by base name anywhere under --audio-dir (a Drive for desktop folder, or an
unzipped download). Candidate lists come from the collection's playlists, as in the app.

The signed boundary error (detected minus history, median over boundaries) also measures the
anchor: history deck starts lead the audible handover, so expect it positive by roughly half
a blend. A large offset common to every boundary means the recording's start time is wrong.

Usage:
  run_real.py --audio-dir DIR [--recording NAME] [--rec-playlist P] [--base-playlist P]
              [--ground-truth JSON] [--out DIR] [--ni-root DIR]
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
import detect  # noqa: E402
import evaluate  # noqa: E402
import nml  # noqa: E402
from ground_truth import is_separator  # noqa: E402

warnings.filterwarnings("ignore", category=FutureWarning)
SR = 22050
HERE = os.path.dirname(os.path.abspath(__file__))


def find_files(root: str) -> dict[str, str]:
    out = {}
    for d, _, files in os.walk(root):
        for f in files:
            out.setdefault(f.lower(), os.path.join(d, f))
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--audio-dir", required=True)
    ap.add_argument("--recording", default="Z19-2025 Sentimony 2025-02-23.wav")
    ap.add_argument("--rec-playlist", default="Z/Z19-2025 Sentimony 2025-02-23.rec")
    ap.add_argument("--base-playlist", default="Z/Z19-2025 Sentimony ")
    ap.add_argument("--ground-truth", default=os.path.join(HERE, "ground-truth", "z19-2025-sentimony-2025-02-23.json"))
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--ni-root", default=nml.DEFAULT_NI_ROOT)
    a = ap.parse_args()
    coll = nml.read_collection(os.path.join(a.ni_root, "Traktor 4.5.1", "collection.nml"))
    files = find_files(a.audio_dir)

    def keys(pl: str) -> list[str]:
        return [k for k in coll.playlists[pl] if k in coll.tracks and not is_separator(coll.tracks[k])]

    rec_keys, base_keys = keys(a.rec_playlist), keys(a.base_playlist)
    missing = [coll.tracks[k].path for k in dict.fromkeys(base_keys + rec_keys)
               if os.path.basename(coll.tracks[k].path).lower() not in files]
    if a.recording.lower() not in files or missing:
        sys.exit(f"missing audio: recording={a.recording.lower() not in files}, tracks={len(missing)}\n"
                 + "\n".join(missing[:10]))

    t0 = time.time()
    y, _ = librosa.load(files[a.recording.lower()], sr=SR, mono=True)
    rec = detect.beat_features(y, SR, None)
    t_rec = time.time() - t0
    print(f"recording: {len(y) / SR / 60:.1f} min, tempo {rec.tempo:.1f}, features in {t_rec:.0f}s")

    feats: dict[str, dict[str, detect.BeatFeatures]] = {"whole": {}, "excerpts": {}}
    t0 = time.time()
    for k in dict.fromkeys(base_keys + rec_keys):
        t = coll.tracks[k]
        ty, _ = librosa.load(files[os.path.basename(t.path).lower()], sr=SR, mono=True)
        feats["whole"][k] = detect.beat_features(ty, SR, t.bpm)
        feats["excerpts"][k] = detect.beat_features(detect.excerpts(ty, SR), SR, t.bpm)
    print(f"{len(feats['whole'])} candidates, features in {time.time() - t0:.0f}s")

    gt = json.load(open(a.ground_truth))
    title_of = {k: coll.tracks[k].title for k in feats["whole"]}
    truth = [(s["start"], s["end"], s["track"]["title"]) for s in gt["segments"]]
    blends = [(p["track"]["title"], n["track"]["title"], n["deckStart"], p["deckEnd"])
              for p, n in zip(gt["segments"], gt["segments"][1:])]

    os.makedirs(a.out, exist_ok=True)
    rows = []
    for scen, cands, prior in (("rec", rec_keys, "rec"), ("base", base_keys, "base"), ("base-no-order", base_keys, "none")):
        for rep in ("whole", "excerpts"):
            for method in ("viterbi", "greedy"):
                t1 = time.time()
                segs = detect.detect(rec, {c: feats[rep][c] for c in cands}, prior=prior, method=method)
                pred = [(s.start, s.end, title_of.get(s.track, s.track)) for s in segs]
                sc = evaluate.evaluate(truth, pred, blends)
                signed = [p[0] - t[0] for t, p in zip(truth[1:], pred[1:])] if len(pred) == len(truth) else []
                rows.append({"scenario": scen, "candidates": rep, "method": method, "seconds": round(time.time() - t1, 2),
                             "signed_boundary_median": round(float(np.median(signed)), 1) if signed else None,
                             **asdict(sc)})
                with open(os.path.join(a.out, f"z19-{scen}-{rep}-{method}.json"), "w") as f:
                    json.dump([{"start": s.start, "end": s.end, "title": title_of.get(s.track, s.track),
                                "confidence": s.confidence} for s in segs], f, indent=1, ensure_ascii=False)
    hdr = ["scenario", "candidates", "method", "accuracy", "boundary_median", "signed_boundary_median",
           "within_15", "within_30", "played_f1", "kendall_tau", "seconds"]
    print("| " + " | ".join(hdr) + " |")
    print("|" + "---|" * len(hdr))
    for r in rows:
        print("| " + " | ".join(str(r[h]) for h in hdr) + " |")
    with open(os.path.join(a.out, "z19-scores.json"), "w") as f:
        json.dump(rows, f, indent=1)


if __name__ == "__main__":
    main()
