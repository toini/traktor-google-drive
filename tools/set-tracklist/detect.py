"""Track identification and boundaries in a DJ set (ADR §2e, §3a, §4a, §4d).

Pipeline:
  1. beat_features: beat-track the audio and take one feature vector per beat
     (chroma + cepstral-mean-normalised MFCC). Working per beat makes tempo vanish.
  2. beat_scores: for every candidate, the best diagonal match of L consecutive
     candidate beats against the recording around each recording beat (subsequence
     matching in the beat domain), plus the matched beat inside the candidate.
  3. window_scores: average those per window of W beats.
  4. viterbi: candidates + UNKNOWN as states; emissions from the window scores;
     transitions from the playlist order, with the prior's strength set by the list type
     (strong for a .rec list, weak for a base list); then a re-decode pass that keeps
     each track to one run (the "played at most once" rule).
  5. refine: move each boundary to the beat where the incoming track's beat score
     overtakes the outgoing one's.
  greedy: the §4d baseline (best window, median-filtered).
"""
from __future__ import annotations

from dataclasses import dataclass

import librosa
import numpy as np
from scipy.ndimage import median_filter

HOP = 512
UNKNOWN = "UNKNOWN"


@dataclass
class BeatFeatures:
    times: np.ndarray   # beat times, seconds
    feats: np.ndarray   # (beats, dims), rows L2-normalised
    tempo: float


@dataclass
class Segment:
    start: float
    end: float
    track: str           # candidate name or UNKNOWN
    confidence: float
    track_beat: int | None = None  # matched beat inside the candidate at the segment start
    beat_a: int = 0                # first recording beat of the segment
    beat_b: int = 0                # one past its last beat
    runner_up: str | None = None   # best other candidate over the segment


def beat_features(y: np.ndarray, sr: int, bpm_hint: float | None = None,
                  chroma_weight: float = 1.0, mfcc_weight: float = 0.5) -> BeatFeatures:
    onset = librosa.onset.onset_strength(y=y, sr=sr, hop_length=HOP)
    tempo, beats = librosa.beat.beat_track(onset_envelope=onset, sr=sr, hop_length=HOP,
                                           start_bpm=bpm_hint or 140.0, tightness=400, trim=False)
    tempo = float(np.atleast_1d(tempo)[0])
    if len(beats) < 8:  # no usable pulse: fall back to a fixed grid
        period = int(round(60.0 / (bpm_hint or 140.0) * sr / HOP))
        beats = np.arange(0, len(onset), period)
    chroma = librosa.feature.chroma_stft(y=y, sr=sr, hop_length=HOP, n_fft=4096)
    mfcc = librosa.feature.mfcc(y=y, sr=sr, hop_length=HOP, n_mfcc=20)[1:]
    mfcc = mfcc - mfcc.mean(axis=1, keepdims=True)
    c = librosa.util.sync(chroma, beats, aggregate=np.mean)
    m = librosa.util.sync(mfcc, beats, aggregate=np.mean)
    c = c / (np.linalg.norm(c, axis=0, keepdims=True) + 1e-9)
    m = m / (np.linalg.norm(m, axis=0, keepdims=True) + 1e-9)
    f = np.vstack([chroma_weight * c, mfcc_weight * m]).T
    f = f / (np.linalg.norm(f, axis=1, keepdims=True) + 1e-9)
    edges = np.concatenate([[0], beats, [chroma.shape[1]]])
    times = librosa.frames_to_time(edges[:-1], sr=sr, hop_length=HOP)
    n = min(len(times), len(f))
    return BeatFeatures(times=times[:n], feats=f[:n].astype(np.float32), tempo=tempo)


def excerpts(y: np.ndarray, sr: int, n: int = 3, seconds: float = 30.0) -> np.ndarray:
    """n slices of the track, evenly spread over its middle (skips intro and outro), concatenated."""
    ln = int(seconds * sr)
    if len(y) <= n * ln:
        return y
    starts = np.linspace(0.15 * len(y), 0.85 * len(y) - ln, n).astype(int)
    return np.concatenate([y[s:s + ln] for s in starts])


def beat_scores(rec: np.ndarray, cand: np.ndarray, length: int = 16) -> tuple[np.ndarray, np.ndarray]:
    """For each recording beat i, the mean similarity of the best diagonal run of `length`
    beats starting there, and the candidate beat where that run starts."""
    n, m = len(rec), len(cand)
    L = min(length, n, m)
    s = rec @ cand.T
    acc = np.zeros((n - L + 1, m - L + 1), dtype=np.float32)
    for k in range(L):
        acc += s[k:n - L + 1 + k, k:m - L + 1 + k]
    acc /= L
    best = acc.max(axis=1)
    arg = acc.argmax(axis=1)
    pad = n - len(best)
    # a run that starts at i covers i..i+L; spread its score over the beats it covers
    cover = np.full(n, -1.0, dtype=np.float32)
    for k in range(L):
        cover[k:k + len(best)] = np.maximum(cover[k:k + len(best)], best)
    return cover, np.concatenate([arg, np.full(pad, arg[-1] if len(arg) else 0)])


def contrast(scores: np.ndarray) -> np.ndarray:
    """Subtract, per beat or window, the median over candidates. Every psytrance track shares the
    kick and hats, so raw similarity sits near 0.8 for all of them; only the excess identifies.
    Divided by the spread across candidates (a robust z-score), so the UNKNOWN level means
    the same thing in a clean mix and a noisy one."""
    if scores.shape[0] < 3:
        return (scores - 0.75) * 20  # too few candidates for a median: a fixed floor and scale
    med = np.median(scores, axis=0, keepdims=True)
    mad = np.median(np.abs(scores - med), axis=0, keepdims=True) * 1.4826
    return (scores - med) / (mad + 0.01)


def window_scores(scores: np.ndarray, window: int) -> np.ndarray:
    """(cands, beats) -> (cands, windows) by averaging non-overlapping windows."""
    nwin = int(np.ceil(scores.shape[1] / window))
    out = np.zeros((scores.shape[0], nwin), dtype=np.float32)
    for w in range(nwin):
        out[:, w] = scores[:, w * window:(w + 1) * window].mean(axis=1)
    return out


def transition_matrix(k: int, prior: str) -> np.ndarray:
    """States 0..k-1 are candidates in list order, k is UNKNOWN. prior: rec | partial | base | none."""
    p = {
        "rec": dict(stay=0.90, nxt=0.08, skip=0.010, decay=0.3, back=0.0005, unk=0.005),
        # a .rec-like list where the DJ played shorter parts, skipped some and left the tail
        # unplayed (Toni on H15): order still strong, skips a few times likelier and slower to decay.
        # The tail needs no rule: the path may end on any state.
        "partial": dict(stay=0.88, nxt=0.08, skip=0.030, decay=0.5, back=0.0005, unk=0.005),
        "base": dict(stay=0.90, nxt=0.03, skip=0.020, decay=0.9, back=0.008, unk=0.01),
        "none": dict(stay=0.90, nxt=0.01, skip=0.010, decay=1.0, back=0.010, unk=0.01),
    }[prior]
    t = np.zeros((k + 1, k + 1))
    for a in range(k):
        for b in range(k):
            if a == b:
                t[a, b] = p["stay"]
            elif b == a + 1:
                t[a, b] = p["nxt"]
            elif b > a + 1:
                t[a, b] = p["skip"] * p["decay"] ** (b - a - 2)
            else:
                t[a, b] = p["back"]
        t[a, k] = p["unk"]
    t[k, :k] = (1 - p["stay"]) / k
    t[k, k] = p["stay"]
    t /= t.sum(axis=1, keepdims=True)
    return np.log(t + 1e-12)


def viterbi(em: np.ndarray, logt: np.ndarray, log_start: np.ndarray) -> np.ndarray:
    """em: (states, windows) log emissions."""
    s, n = em.shape
    dp = log_start + em[:, 0]
    back = np.zeros((s, n), dtype=np.int32)
    for w in range(1, n):
        cand = dp[:, None] + logt
        back[:, w] = cand.argmax(axis=0)
        dp = cand.max(axis=0) + em[:, w]
    path = np.zeros(n, dtype=np.int32)
    path[-1] = int(dp.argmax())
    for w in range(n - 1, 0, -1):
        path[w - 1] = back[path[w], w]
    return path


def runs(path: np.ndarray) -> list[tuple[int, int, int]]:
    """[(state, first window, last window + 1)]"""
    out, start = [], 0
    for w in range(1, len(path) + 1):
        if w == len(path) or path[w] != path[start]:
            out.append((int(path[start]), start, w))
            start = w
    return out


def decode(win: np.ndarray, prior: str, beta: float = 3.0, unknown_level: float = 0.5,
           max_passes: int = 4) -> np.ndarray:
    """Viterbi with the one-run-per-track rule enforced by masking and re-decoding."""
    k, n = win.shape
    em = np.vstack([win, np.full((1, n), unknown_level)]) * beta
    logt = transition_matrix(k, prior)
    start = np.full(k + 1, np.log(1.0 / (k + 1)))
    if prior in ("rec", "partial"):
        start[:k] = np.log(0.5 ** np.arange(k) + 1e-6)
    path = viterbi(em, logt, start)
    for _ in range(max_passes):
        by_state: dict[int, list[tuple[int, int]]] = {}
        for st, a, b in runs(path):
            if st < k:
                by_state.setdefault(st, []).append((a, b))
        dup = {st: r for st, r in by_state.items() if len(r) > 1}
        if not dup:
            break
        for st, rs in dup.items():
            keep = max(rs, key=lambda r: em[st, r[0]:r[1]].sum())
            for a, b in rs:
                if (a, b) != keep:
                    em[st, a:b] = -1e9
        path = viterbi(em, logt, start)
    return path


def greedy(win: np.ndarray, unknown_level: float = 0.5, smooth: int = 3) -> np.ndarray:
    k = win.shape[0]
    best = win.argmax(axis=0)
    best = np.where(win.max(axis=0) < unknown_level, k, best)
    return median_filter(best, size=smooth, mode="nearest").astype(np.int32)


def refine(path: np.ndarray, beat: np.ndarray, window: int) -> list[tuple[int, int, int]]:
    """Runs in beat units, each boundary moved to the change point of score(B) - score(A)."""
    rs = [(st, a * window, min(b * window, beat.shape[1])) for st, a, b in runs(path)]
    k = beat.shape[0]
    for i in range(1, len(rs)):
        (sa, a0, a1), (sb, b0, b1) = rs[i - 1], rs[i]
        if sa >= k or sb >= k:
            continue
        lo, hi = max(a0 + 1, a1 - window), min(b1 - 1, b0 + window)
        if hi <= lo:
            continue
        d = beat[sb, lo:hi] - beat[sa, lo:hi]
        # change point: maximise sum(before: A wins) + sum(after: B wins)
        gain = -np.concatenate([[0], np.cumsum(d)])[:-1] + (d.sum() - np.concatenate([[0], np.cumsum(d)])[:-1])
        cut = lo + int(np.argmax(gain))
        rs[i - 1] = (sa, a0, cut)
        rs[i] = (sb, cut, b1)
    return rs


def score_matrix(rec: BeatFeatures, cands: dict[str, BeatFeatures], run_length: int = 16
                 ) -> tuple[list[str], np.ndarray, np.ndarray]:
    """Raw per-beat similarity (cands, beats) and the matched candidate beat for each."""
    names = list(cands)
    beat = np.zeros((len(names), len(rec.feats)), dtype=np.float32)
    offs = np.zeros((len(names), len(rec.feats)), dtype=np.int32)
    for i, n in enumerate(names):
        beat[i], offs[i] = beat_scores(rec.feats, cands[n].feats, run_length)
    return names, beat, offs


def detect(rec: BeatFeatures, cands: dict[str, BeatFeatures], prior: str = "base", method: str = "viterbi",
           window: int = 32, run_length: int = 16, unknown_level: float = 0.5) -> list[Segment]:
    """cands must be in list order (it is the order prior)."""
    names, raw, offs = score_matrix(rec, cands, run_length)
    beat = contrast(raw)
    win = window_scores(beat, window)
    path = decode(win, prior, unknown_level=unknown_level) if method == "viterbi" else greedy(win, unknown_level)
    out = []
    times = np.append(rec.times, rec.times[-1] + (rec.times[-1] - rec.times[-2]))
    for st, a, b in refine(path, beat, window):
        name = names[st] if st < len(names) else UNKNOWN
        means = beat[:, a:b].mean(axis=1)
        if st < len(names):
            other = np.delete(beat[:, a:b], st, axis=0).max(axis=0) if len(names) > 1 else np.zeros(b - a)
            conf = float(np.clip((beat[st, a:b] - other).mean() / 6, 0, 1))
            tb = int(offs[st, a])
            means[st] = -np.inf
        else:
            conf, tb = 0.0, None
        ru = names[int(np.argmax(means))] if len(names) > 1 or st >= len(names) else None
        out.append(Segment(float(times[a]), float(times[min(b, len(times) - 1)]), name, round(conf, 3), tb,
                           beat_a=a, beat_b=b, runner_up=ru))
    return out
