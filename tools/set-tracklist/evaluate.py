"""The ADR §8 metrics for a predicted timeline against ground truth.

Truth and prediction are lists of (start, end, track) in seconds. Truth may also carry
blends: (track_a, track_b, from, to) spans where either track counts as correct.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.stats import kendalltau

UNKNOWN = "UNKNOWN"


@dataclass
class Scores:
    accuracy: float           # time-weighted ID accuracy
    boundary_median: float    # seconds
    within_15: float
    within_30: float
    played_precision: float
    played_recall: float
    played_f1: float
    kendall_tau: float
    boundaries: int


def label_at(segs, t: float) -> str:
    for s, e, name in segs:
        if s <= t < e:
            return name
    return UNKNOWN


def evaluate(truth: list[tuple[float, float, str]], pred: list[tuple[float, float, str]],
             blends: list[tuple[str, str, float, float]] = (), step: float = 0.5) -> Scores:
    end = truth[-1][1]
    ok = n = 0
    for t in np.arange(0, end, step):
        want = label_at(truth, t)
        got = label_at(pred, t)
        allowed = {want}
        for a, b, lo, hi in blends:
            if lo <= t < hi:
                allowed |= {a, b}
        ok += got in allowed
        n += 1
    acc = ok / n if n else 0.0

    # boundaries: a true boundary A->B is matched by a predicted boundary into B (or out of A)
    tb = [(truth[i][2], truth[i + 1][2], truth[i + 1][0]) for i in range(len(truth) - 1)]
    pb = [(pred[i][2], pred[i + 1][2], pred[i + 1][0]) for i in range(len(pred) - 1)]
    errs = []
    for a, b, t in tb:
        same = [p for x, y, p in pb if y == b] or [p for x, y, p in pb if x == a]
        errs.append(min(abs(p - t) for p in same) if same else end)
    errs = np.array(errs) if errs else np.array([0.0])

    tp = {x[2] for x in truth}
    pp = {x[2] for x in pred} - {UNKNOWN}
    hit = len(tp & pp)
    prec = hit / len(pp) if pp else 0.0
    rec = hit / len(tp) if tp else 0.0
    f1 = 2 * prec * rec / (prec + rec) if prec + rec else 0.0

    order_t = [x[2] for x in truth]
    order_p = list(dict.fromkeys(x[2] for x in pred if x[2] in tp))
    if len(order_p) >= 2:
        tau = kendalltau([order_t.index(x) for x in order_p], range(len(order_p))).statistic
    else:
        tau = float("nan")
    return Scores(round(acc, 3), round(float(np.median(errs)), 1), round(float((errs <= 15).mean()), 3),
                  round(float((errs <= 30).mean()), 3), round(prec, 3), round(rec, 3), round(f1, 3),
                  round(float(tau), 3), len(tb))
