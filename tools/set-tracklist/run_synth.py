#!/usr/bin/env python3
"""End-to-end run on synthetic mixes: synthesise tracks, mix them, detect, score (ADR §8).

Scenarios, all at one set tempo with every track stretched ±3–8 % and bass-swap blends:
  rec       the candidate list is exactly the played tracks, in order (a .rec playlist)
  base      the played tracks plus unplayed ones, order lightly shuffled (a base playlist)
  shuffled  the same candidates in random order, no order prior
  varispeed like rec, but mixed without keylock (pitch follows tempo), to see what keylock buys

Usage: run_synth.py [--seed N] [--tracks 14] [--played 8] [--json OUT]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import warnings
from dataclasses import asdict

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detect  # noqa: E402
import evaluate  # noqa: E402
import synth  # noqa: E402

warnings.filterwarnings("ignore", category=FutureWarning)
warnings.filterwarnings("ignore", category=RuntimeWarning)


def scenario_lists(played: list[str], unplayed: list[str], rng: np.random.Generator) -> dict[str, tuple[list[str], str]]:
    base = list(played)
    for _ in range(2):  # two adjacent swaps: "most often in order"
        i = int(rng.integers(0, len(base) - 1))
        base[i], base[i + 1] = base[i + 1], base[i]
    for u in unplayed:
        base.insert(int(rng.integers(0, len(base) + 1)), u)
    shuffled = list(rng.permutation(played + unplayed))
    return {"rec": (list(played), "rec"), "base": (base, "base"), "shuffled": (shuffled, "none")}


def timeline_json(mix: synth.Mix, segs: list[detect.Segment], cands: list[str]) -> dict:
    """The ADR §0 artefact, to check it carries what the UI needs."""
    return {
        "v": 1,
        "recording": {"name": "synthetic", "duration": round(len(mix.audio) / synth.SR, 1)},
        "generator": {"tool": "set-tracklist", "version": "0.1.0", "method": "audio"},
        "candidates": cands,
        "segments": [{
            "start": round(s.start, 1), "end": round(s.end, 1), "blendIn": None, "blendOut": None,
            "track": None if s.track == detect.UNKNOWN else {"key": s.track, "title": s.track, "artist": ""},
            "trackBeat": s.track_beat, "confidence": s.confidence, "source": "audio",
        } for s in segs],
    }


def run_one(seed: int, a: argparse.Namespace) -> tuple[list[dict], dict | None, float]:
    rng = np.random.default_rng(seed)
    sr = synth.SR
    tracks = []
    for i in range(a.tracks):
        dev = rng.uniform(0.03, 0.08) * rng.choice([-1, 1])
        tracks.append(synth.make_track(seed * 100 + i, bpm=a.set_bpm / (1 + dev), bars=a.bars))
    order = list(rng.permutation(len(tracks)))
    played = [tracks[i] for i in order[:a.played]]
    unplayed = [tracks[i] for i in order[a.played:]]
    kw = dict(play_bars=(16, 32), blend_bars=(32, 64)) if a.hard else {}
    mixes = {"keylock": synth.make_mix(played, a.set_bpm, rng, **kw),
             "varispeed": synth.make_mix(played, a.set_bpm, np.random.default_rng(seed), keylock=False, **kw)}
    if a.hard:  # room noise at about -30 dB under the music
        for m in mixes.values():
            m.audio = (m.audio + 0.03 * rng.standard_normal(len(m.audio))).astype(np.float32)
    mix_min = len(mixes["keylock"].audio) / sr / 60
    feats = {
        "whole": {t.name: detect.beat_features(t.audio, sr, t.bpm) for t in tracks},
        "excerpts": {t.name: detect.beat_features(detect.excerpts(t.audio, sr), sr, t.bpm) for t in tracks},
    }
    recf = {k: detect.beat_features(m.audio, sr, a.set_bpm) for k, m in mixes.items()}
    lists = scenario_lists([t.name for t in played], [t.name for t in unplayed], rng)
    lists["varispeed"] = lists["rec"]
    rows, example = [], None
    for scen, (cands, prior) in lists.items():
        mixkey = "varispeed" if scen == "varispeed" else "keylock"
        mix = mixes[mixkey]
        truth = [(s.start, s.end, s.name) for s in mix.segments]
        blends = [(mix.segments[i].name, mix.segments[i + 1].name, mix.segments[i + 1].deck_start,
                   mix.segments[i].deck_end) for i in range(len(mix.segments) - 1)]
        for rep in ("whole", "excerpts"):
            for method in ("viterbi", "greedy"):
                t1 = time.time()
                segs = detect.detect(recf[mixkey], {c: feats[rep][c] for c in cands}, prior=prior, method=method)
                dt = time.time() - t1
                sc = evaluate.evaluate(truth, [(s.start, s.end, s.track) for s in segs], blends)
                rows.append({"seed": seed, "scenario": scen, "candidates": rep, "method": method,
                             "seconds": round(dt, 2), "segments": len(segs), **asdict(sc)})
                if scen == "base" and rep == "excerpts" and method == "viterbi":
                    example = timeline_json(mix, segs, cands)
    return rows, example, mix_min


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--seeds", type=int, nargs="+", default=[7])
    ap.add_argument("--tracks", type=int, default=14)
    ap.add_argument("--played", type=int, default=8)
    ap.add_argument("--bars", type=int, default=128)
    ap.add_argument("--set-bpm", type=float, default=140.0)
    ap.add_argument("--hard", action="store_true", help="shorter plays, 32-64 bar blends, added noise")
    ap.add_argument("--json", help="write all scores and one §0 timeline here")
    a = ap.parse_args()
    t0 = time.time()
    rows, example, minutes = [], None, []
    for seed in a.seeds:
        r, ex, mm = run_one(seed, a)
        rows += r
        example = example or ex
        minutes.append(mm)
    print(f"{len(a.seeds)} seed(s), mixes of {min(minutes):.1f}-{max(minutes):.1f} min, {time.time() - t0:.0f}s total"
          + (" [hard]" if a.hard else ""))
    keys = ["accuracy", "boundary_median", "within_15", "within_30", "played_f1", "kendall_tau", "segments", "seconds"]
    groups: dict[tuple, list[dict]] = {}
    for r in rows:
        groups.setdefault((r["scenario"], r["candidates"], r["method"]), []).append(r)
    print("| scenario | candidates | method | " + " | ".join(keys) + " | worst accuracy |")
    print("|" + "---|" * (len(keys) + 4))
    for (sc, rep, me), rs in groups.items():
        mean = [np.nanmean([x[k] for x in rs]) for k in keys]
        print(f"| {sc} | {rep} | {me} | " + " | ".join(f"{v:.3g}" for v in mean)
              + f" | {min(x['accuracy'] for x in rs):.3g} |")
    if a.json:
        os.makedirs(os.path.dirname(os.path.abspath(a.json)), exist_ok=True)
        with open(a.json, "w") as f:
            json.dump({"seeds": a.seeds, "hard": a.hard, "rows": rows, "example_timeline": example}, f, indent=2)


if __name__ == "__main__":
    main()
