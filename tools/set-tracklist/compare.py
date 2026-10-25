#!/usr/bin/env python3
"""Waveform comparison report: does each detected segment really contain the matched track?

For every segment it renders out/<recording>/segment-NN.png:
  - the recording's waveform over the segment, with context before and after;
  - the matched track's waveform, mapped onto the recording's time axis through the
    beat alignment (offset into the track, tempo ratio), so breakdowns, drops and intros
    line up if the match is right;
  - the runner-up candidate, aligned the same way, so a wrong match stands out;
  - the beat-by-beat feature similarity along that alignment, for both;
  - a header with the track, confidence, offset into the track and tempo ratio.
Plus overview.png (the whole set with coloured segment bands) and report.html with A/B
audio excerpts (recording, matched track stretched to the set tempo, and a stereo file
with the recording on the left and the track on the right). Everything is written
locally; nothing is uploaded.

Demo on a synthetic mix (no real audio needed):
  .venv/bin/python compare.py --synthetic out/synthetic-demo
"""
from __future__ import annotations

import argparse
import html
import os
import sys
import warnings
from dataclasses import dataclass
from typing import Callable

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
import soundfile as sf  # noqa: E402
import librosa  # noqa: E402

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detect  # noqa: E402

warnings.filterwarnings("ignore", category=FutureWarning)
warnings.filterwarnings("ignore", category=RuntimeWarning)

ENV_HZ = 50  # envelope resolution for the waveform plots


@dataclass
class Candidate:
    label: str                           # "Artist – Title"
    load: Callable[[], np.ndarray]       # mono audio at the report's sample rate
    bpm: float | None = None


@dataclass
class Alignment:
    beat_offset: int        # candidate beat aligned with the segment's first recording beat
    tempo_ratio: float      # played tempo / native tempo
    track_offset: float     # seconds into the original track at the segment start
    rec_beats: np.ndarray   # recording beat indices covered (with context)
    cand_beats: np.ndarray  # aligned candidate beat indices (may fall outside the track)
    similarity: np.ndarray  # per recording beat, feature similarity along the alignment (nan outside)
    agreement: float        # margin of the best diagonal over the best non-neighbouring one (0 = ambiguous)
    alternatives: list[int] # other offsets (beats, relative) that fit as well: a repeating pattern


def envelope(y: np.ndarray, sr: int, hz: int = ENV_HZ) -> np.ndarray:
    """Peak envelope: max |y| per 1/hz s frame."""
    hop = sr // hz
    n = len(y) // hop
    return np.abs(y[: n * hop]).reshape(n, hop).max(axis=1) if n else np.zeros(1)


def align(rec: detect.BeatFeatures, a: int, b: int, cand: detect.BeatFeatures, context_beats: int = 32,
          run_length: int = 16) -> Alignment:
    """Find the candidate beat offset whose whole diagonal best matches recording beats a..b.

    A whole-segment diagonal, rather than a vote of short runs, because psytrance repeats
    1-2 bar patterns for minutes: only intros, breakdowns and fills pin the offset down."""
    seg = rec.feats[a:b]
    n, m = len(seg), len(cand.feats)
    sim_m = seg @ cand.feats.T
    min_overlap = max(8, n // 2)
    best_d, best_v, scores = 0, -np.inf, {}
    for d in range(-(n - min_overlap), m - min_overlap + 1):
        diag = np.diagonal(sim_m, offset=d)  # entries (j, j + d)
        if len(diag) < min_overlap:
            continue
        v = float(diag.mean())
        scores[d] = v
        if v > best_v:
            best_d, best_v = d, v
    d = best_d
    # agreement: how far the best diagonal stands above the next best that is not a near neighbour
    others = [v for k, v in scores.items() if abs(k - d) > 4]
    agreement = float(best_v - max(others)) if others else 0.0
    alts = sorted((k - d for k, v in scores.items() if abs(k - d) > 4 and best_v - v < 0.003), key=abs)
    # keep one representative per cluster of neighbouring offsets
    alternatives: list[int] = []
    for k in alts:
        if all(abs(k - x) > 4 for x in alternatives):
            alternatives.append(k)
    # tempo ratio: slope of track beat time against recording beat time along the alignment
    j = np.arange(n)
    cb = d + j
    ok = (cb >= 0) & (cb < len(cand.times))
    if ok.sum() > 8:
        ratio = float(np.polyfit(rec.times[a + j[ok]], cand.times[cb[ok]], 1)[0])
    else:
        ratio = 1.0
    while ratio > 1.5:   # the two beat trackers locked to different metrical levels
        ratio /= 2
    while ratio < 0.67:
        ratio *= 2
    if 0 <= d < len(cand.times):
        track_offset = float(cand.times[d])
    else:
        track_offset = float(cand.times[0] + d * np.median(np.diff(cand.times)))
    lo, hi = max(0, a - context_beats), min(len(rec.feats), b + context_beats)
    rb = np.arange(lo, hi)
    cbx = d + (rb - a)
    sim = np.full(len(rb), np.nan, dtype=np.float32)
    inside = (cbx >= 0) & (cbx < len(cand.feats))
    sim[inside] = np.sum(rec.feats[rb[inside]] * cand.feats[cbx[inside]], axis=1)
    return Alignment(d, ratio, track_offset, rb, cbx, sim, agreement, alternatives[:3])


def map_times(t_rec: np.ndarray, rec: detect.BeatFeatures, al: Alignment, cand: detect.BeatFeatures) -> np.ndarray:
    """Recording times -> track times through the beat alignment (linear between beats,
    extrapolated with the tempo ratio outside the track's beat grid). NaN before 0."""
    rb, cb = al.rec_beats, al.cand_beats
    ok = (cb >= 0) & (cb < len(cand.times))
    if ok.sum() >= 2:
        xr, xc = rec.times[rb[ok]], cand.times[cb[ok]]
        out = np.interp(t_rec, xr, xc)
        before, after = t_rec < xr[0], t_rec > xr[-1]
        out[before] = xc[0] - (xr[0] - t_rec[before]) * al.tempo_ratio
        out[after] = xc[-1] + (t_rec[after] - xr[-1]) * al.tempo_ratio
    else:
        t0 = rec.times[min(rb[0] + (0 - cb[0]), len(rec.times) - 1)] if len(rb) else 0.0
        out = (t_rec - t0) * al.tempo_ratio
    out[out < 0] = np.nan
    return out


def smooth(x: np.ndarray, n: int = 8) -> np.ndarray:
    if len(x) < n:
        return x
    k = np.ones(n) / n
    m = np.isnan(x)
    v = np.convolve(np.where(m, 0, x), k, mode="same")
    w = np.convolve((~m).astype(float), k, mode="same")
    out = v / np.maximum(w, 1e-9)
    out[w < 0.5] = np.nan
    return out


def conf_word(c: float) -> str:
    return "high" if c >= 0.5 else "likely" if c >= 0.2 else "unsure"


def fmt(s: float) -> str:
    s = max(0.0, float(s))
    return f"{int(s // 3600)}:{int(s % 3600 // 60):02d}:{int(s % 60):02d}" if s >= 3600 else f"{int(s // 60)}:{int(s % 60):02d}"


class Report:
    def __init__(self, out_dir: str, rec_name: str, y: np.ndarray, sr: int, rec: detect.BeatFeatures,
                 cands: dict[str, Candidate], context: float = 20.0, excerpt: float = 20.0):
        self.out, self.name, self.y, self.sr, self.rec = out_dir, rec_name, y, sr, rec
        self.cands, self.context, self.excerpt = cands, context, excerpt
        self._audio: dict[str, np.ndarray] = {}
        self._feats: dict[str, detect.BeatFeatures] = {}
        self._env: dict[str, np.ndarray] = {}
        self.rec_env = envelope(y, sr)
        os.makedirs(out_dir, exist_ok=True)

    def audio(self, k: str) -> np.ndarray:
        if k not in self._audio:
            self._audio[k] = self.cands[k].load()
        return self._audio[k]

    def feats(self, k: str) -> detect.BeatFeatures:
        if k not in self._feats:  # whole-track features: the alignment needs the full timeline
            self._feats[k] = detect.beat_features(self.audio(k), self.sr, self.cands[k].bpm)
        return self._feats[k]

    def env(self, k: str) -> np.ndarray:
        if k not in self._env:
            self._env[k] = envelope(self.audio(k), self.sr)
        return self._env[k]

    def _env_at(self, env: np.ndarray, t: np.ndarray) -> np.ndarray:
        idx = np.round(t * ENV_HZ)
        out = np.full(len(t), np.nan)
        ok = ~np.isnan(idx) & (idx >= 0) & (idx < len(env))
        out[ok] = env[idx[ok].astype(int)]
        return out

    def segment_png(self, i: int, s: detect.Segment) -> tuple[str, dict]:
        t0, t1 = max(0.0, s.start - self.context), min(len(self.y) / self.sr, s.end + self.context)
        t = np.arange(t0, t1, 1.0 / ENV_HZ)
        rows = [("Recording", self._env_at(self.rec_env, t), None, None)]
        info: dict = {}
        for role, k in (("Match", s.track), ("Runner-up", s.runner_up)):
            if not k or k == detect.UNKNOWN or k not in self.cands:
                continue
            al = align(self.rec, s.beat_a, s.beat_b, self.feats(k))
            tt = map_times(t, self.rec, al, self.feats(k))
            rows.append((f"{role}: {self.cands[k].label}", self._env_at(self.env(k), tt), al, k))
            info[role] = (k, al)

        fig, axes = plt.subplots(len(rows) + 1, 1, figsize=(12, 1.6 * (len(rows) + 1) + 0.6), sharex=True,
                                 gridspec_kw={"height_ratios": [1] * len(rows) + [0.9]})
        colors = ["#4a4a4a", "#1f77b4", "#d62728"]
        for ax, (title, e, al, k), c in zip(axes, rows, colors):
            ax.fill_between(t, -e, e, color=c, linewidth=0)
            ax.axvspan(s.start, s.end, color="#ffd54f", alpha=0.18, linewidth=0)
            ax.set_ylim(-1.05, 1.05)
            ax.set_yticks([])
            ax.text(0.005, 0.92, title, transform=ax.transAxes, fontsize=8, va="top",
                    bbox=dict(facecolor="white", alpha=0.7, linewidth=0))
            if al is not None:
                ax.text(0.995, 0.92, f"offset {fmt(al.track_offset)} · ratio {al.tempo_ratio:.3f} "
                        f"· alignment margin {al.agreement:.3f}", transform=ax.transAxes, fontsize=7,
                        va="top", ha="right", bbox=dict(facecolor="white", alpha=0.7, linewidth=0))
        ax = axes[-1]
        for (role, (k, al)), c in zip(info.items(), colors[1:]):
            st = self.rec.times[np.clip(al.rec_beats, 0, len(self.rec.times) - 1)]
            ax.plot(st, smooth(al.similarity), color=c, linewidth=1.2, label=role)
        ax.axvspan(s.start, s.end, color="#ffd54f", alpha=0.18, linewidth=0)
        ax.set_ylim(0, 1)
        ax.set_ylabel("similarity", fontsize=7)
        ax.legend(fontsize=7, loc="lower right")
        ax.set_xlim(t0, t1)
        ticks = np.linspace(t0, t1, 7)
        ax.set_xticks(ticks, [fmt(x) for x in ticks], fontsize=7)
        name = self.cands[s.track].label if s.track in self.cands else "Unknown"
        head = f"{i:02d}. {fmt(s.start)}–{fmt(s.end)}  {name}  ·  {conf_word(s.confidence)} ({s.confidence:.2f})"
        if "Match" in info:
            al = info["Match"][1]
            head += f"  ·  enters the track at {fmt(al.track_offset)}  ·  tempo {al.tempo_ratio:.3f} ({(al.tempo_ratio - 1) * 100:+.1f} %)"
            if al.alternatives:
                head += f"\n    offset ambiguous: fits equally at {', '.join(f'{x:+d}' for x in al.alternatives)} beats (repeating pattern; no breakdown or intro in the segment)"
        fig.suptitle(head, fontsize=9, x=0.01, ha="left")
        fig.tight_layout(rect=(0, 0, 1, 0.93 if "\n" in head else 0.96))
        path = os.path.join(self.out, f"segment-{i:02d}.png")
        fig.savefig(path, dpi=110)
        plt.close(fig)
        return path, info

    def excerpts(self, i: int, s: detect.Segment, info: dict) -> dict[str, str]:
        """rec / track / A-B stereo excerpts from the middle of the segment."""
        mid = (s.start + s.end) / 2
        half = min(self.excerpt, max(4.0, s.end - s.start)) / 2
        a, b = int((mid - half) * self.sr), int((mid + half) * self.sr)
        rec = self.y[max(0, a):b]
        files = {}
        p = os.path.join(self.out, f"segment-{i:02d}-recording.wav")
        sf.write(p, rec, self.sr, subtype="PCM_16")
        files["recording"] = p
        if "Match" in info:
            k, al = info["Match"]
            tt = map_times(np.array([mid - half, mid + half]), self.rec, al, self.feats(k))
            if not np.isnan(tt).any():
                ty = self.audio(k)[int(tt[0] * self.sr):int(tt[1] * self.sr)]
                if len(ty) > self.sr:
                    ty = librosa.effects.time_stretch(ty, rate=al.tempo_ratio)  # play it at the set's tempo
                    n = min(len(ty), len(rec))
                    p = os.path.join(self.out, f"segment-{i:02d}-track.wav")
                    sf.write(p, ty, self.sr, subtype="PCM_16")
                    files["track"] = p
                    p = os.path.join(self.out, f"segment-{i:02d}-ab.wav")
                    sf.write(p, np.stack([rec[:n], ty[:n]], axis=1), self.sr, subtype="PCM_16")
                    files["ab"] = p
        return files

    def overview_png(self, segs: list[detect.Segment]) -> str:
        dur = len(self.y) / self.sr
        hz = 5
        hop = max(1, ENV_HZ // hz)
        e = self.rec_env[: len(self.rec_env) // hop * hop].reshape(-1, hop).max(axis=1)
        t = np.arange(len(e)) / hz
        fig, ax = plt.subplots(figsize=(14, 2.8))
        cmap = plt.get_cmap("tab10")
        for i, s in enumerate(segs, 1):
            unknown = s.track not in self.cands
            ax.axvspan(s.start, s.end, color="#bbbbbb" if unknown else cmap((i - 1) % 10),
                       alpha=0.2 + 0.4 * (0 if unknown else s.confidence), linewidth=0,
                       hatch="///" if unknown or s.confidence < 0.2 else None)
            ax.axvline(s.start, color="#555555", linewidth=0.6)
            name = self.cands[s.track].label if not unknown else "?"
            room = int((s.end - s.start) / dur * 190)  # characters that fit in the band at fontsize 7
            text = f"{i}. {name}" if room >= 8 else str(i)
            ax.text(s.start + dur * 0.002, 1.12, text[:max(room, 2)], ha="left", va="top", fontsize=7)
        ax.fill_between(t, -e, e, color="#333333", linewidth=0)
        ax.set_xlim(0, dur)
        ax.set_ylim(-1.1, 1.25)
        ax.set_yticks([])
        ticks = np.arange(0, dur, 600 if dur > 1800 else 60)
        ax.set_xticks(ticks, [fmt(x) for x in ticks], fontsize=7)
        ax.set_title(f"{self.name}: {len(segs)} segments (band opacity = confidence, hatched = unsure/unknown)",
                     fontsize=9, loc="left")
        fig.tight_layout()
        path = os.path.join(self.out, "overview.png")
        fig.savefig(path, dpi=110)
        plt.close(fig)
        return path

    def build(self, segs: list[detect.Segment], not_played: list[str] = ()) -> str:
        ov = self.overview_png(segs)
        parts = [f"<!doctype html><meta charset=utf-8><title>{html.escape(self.name)} – tracklist check</title>",
                 "<style>body{font:14px system-ui,sans-serif;max-width:1400px;margin:16px auto;padding:0 16px;"
                 "background:#fafafa;color:#222}img{max-width:100%;border:1px solid #ddd;background:#fff}"
                 "section{margin:28px 0;padding-top:8px;border-top:1px solid #ddd}audio{height:32px;vertical-align:middle}"
                 ".ab span{display:inline-block;margin-right:16px}td,th{padding:2px 10px;text-align:left}"
                 "@media (prefers-color-scheme: dark){body{background:#1b1b1b;color:#ddd}}</style>",
                 f"<h1>{html.escape(self.name)}</h1>",
                 "<p>Each segment shows the recording (grey) over the matched track (blue) and the runner-up (red), "
                 "mapped onto the recording's time through the beat alignment. If the match is right, intros, "
                 "breakdowns and drops line up, and the blue similarity curve stays above the red one. "
                 "The audio players are 20 s from the middle of the segment: the recording, the matched track "
                 "played at the set's tempo, and both together (recording left, track right).</p>",
                 f'<img src="{os.path.basename(ov)}" alt="overview">', "<table><tr><th>#</th><th>start</th>"
                 "<th>track</th><th>confidence</th><th>runner-up</th></tr>"]
        for i, s in enumerate(segs, 1):
            lab = self.cands[s.track].label if s.track in self.cands else "Unknown"
            ru = self.cands[s.runner_up].label if s.runner_up in self.cands else ""
            parts.append(f'<tr><td><a href="#s{i}">{i}</a></td><td>{fmt(s.start)}</td><td>{html.escape(lab)}</td>'
                         f"<td>{conf_word(s.confidence)} ({s.confidence:.2f})</td><td>{html.escape(ru)}</td></tr>")
        parts.append("</table>")
        if not_played:
            parts.append("<p><b>Not played</b> (judged absent): " + ", ".join(
                html.escape(self.cands[k].label) for k in not_played if k in self.cands) + "</p>")
        for i, s in enumerate(segs, 1):
            png, info = self.segment_png(i, s)
            files = self.excerpts(i, s, info)
            players = "".join(f'<span>{lbl} <audio controls preload=none src="{os.path.basename(files[k])}"></audio></span>'
                              for k, lbl in (("recording", "Recording"), ("track", "Track"), ("ab", "A/B stereo"))
                              if k in files)
            parts.append(f'<section id="s{i}"><img src="{os.path.basename(png)}" alt="segment {i}">'
                         f'<div class="ab">{players}</div></section>')
        path = os.path.join(self.out, "report.html")
        with open(path, "w") as f:
            f.write("\n".join(parts))
        return path


def synthetic_demo(out: str) -> None:
    import synth
    import evaluate
    sr = synth.SR
    rng = np.random.default_rng(21)
    tracks = [synth.make_track(2100 + i, bpm=140 / (1 + rng.uniform(0.03, 0.08) * rng.choice([-1, 1])), bars=96)
              for i in range(9)]
    played = tracks[:6]
    mix = synth.make_mix(played, 140.0, rng)
    rec = detect.beat_features(mix.audio, sr, 140.0)
    feats = {t.name: detect.beat_features(detect.excerpts(t.audio, sr), sr, t.bpm) for t in tracks}
    segs = detect.detect(rec, feats, prior="partial")
    by = {t.name: t for t in tracks}
    cands = {t.name: Candidate(t.name, (lambda t=t: t.audio), t.bpm) for t in tracks}
    rep = Report(out, "synthetic demo mix", mix.audio, sr, rec, cands)
    path = rep.build(segs, [t.name for t in tracks if t.name not in {s.track for s in segs}])
    truth = {m.name: m for m in mix.segments}
    print(f"report: {path}")
    print("seg  track      true ratio  est ratio   true offset  est offset  margin  equally good shifts (beats)")
    for i, s in enumerate(segs, 1):
        if s.track not in truth:
            continue
        m = truth[s.track]
        al = align(rec, s.beat_a, s.beat_b, rep.feats(s.track))
        true_off = m.track_offset + (s.start - m.deck_start) * m.tempo_ratio
        print(f"{i:3d}  {s.track}  {m.tempo_ratio:10.3f}  {al.tempo_ratio:9.3f}  {true_off:11.1f}  "
              f"{al.track_offset:10.1f}  {al.agreement:6.3f}  {al.alternatives}")
    sc = evaluate.evaluate([(m.start, m.end, m.name) for m in mix.segments], [(s.start, s.end, s.track) for s in segs])
    print(f"accuracy {sc.accuracy}, boundary median {sc.boundary_median} s; by name: {[by[s.track].name for s in segs if s.track in by]}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--synthetic", metavar="OUT_DIR", help="build a report for a synthetic mix")
    a = ap.parse_args()
    if a.synthetic:
        synthetic_demo(a.synthetic)
    else:
        ap.error("use --synthetic here; real recordings go through run_real.py --compare")


if __name__ == "__main__":
    main()
