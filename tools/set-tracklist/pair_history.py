#!/usr/bin/env python3
"""Pair each history-era set recording with the stretch of Traktor history it captured.

Evidence used, strongest first:
  1. A raw Traktor recording (Recording_YYYY-MM-DD_HHhMMmSSs.wav) with the same FILESIZE as the
     named recording. Toni renames the raw file into Setit/ or Gig preparation/, and Traktor keeps
     both entries, so the raw name gives the start time to the second.
  2. Otherwise a fitted start: slide a window of the recording's length over the public plays and
     keep the start that the window covers best, with the dates in the name and IMPORT_DATE as priors.

Usage: pair_history.py [--ni-root DIR] [--json OUT]
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import sys
from dataclasses import asdict, dataclass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nml  # noqa: E402

SET_DIRS = ("/Setit/", "/Gig preparation/", "/Recordings/")
MIN_SET_SECONDS = 20 * 60
MAX_PLAY_SECONDS = 15 * 60   # a deck left running is not 3.5 hours of music
EDGE_SECONDS = 10 * 60       # how far outside the window to look for activity that should be inside it
LOOKBACK_DAYS = 30           # a recording may be renamed / moved (new IMPORT_DATE) days after it was made


@dataclass
class Pairing:
    recording: str
    duration_s: float
    import_date: str
    anchor: str            # "raw-twin" | "fitted"
    raw_name: str | None
    start: str             # local wall clock, same clock as history STARTTIME
    end: str
    public_tracks: int
    coverage: float        # share of the recording covered by public deck activity
    before: float          # share of the 10 min before the start with public activity
    after: float           # share of the 10 min after the end with public activity
    runner_up: str | None
    runner_up_score: float | None
    score: float
    family_playlist: str | None  # the recording's own playlist that best matches the window
    family_f1: float             # F1 of the window's public tracks against family_playlist
    confidence: str
    notes: str


def union_seconds(intervals: list[tuple[float, float]], lo: float, hi: float) -> float:
    total, cur_lo, cur_hi = 0.0, None, None
    for a, b in sorted((max(a, lo), min(b, hi)) for a, b in intervals if b > lo and a < hi):
        if cur_hi is None or a > cur_hi:
            if cur_hi is not None:
                total += cur_hi - cur_lo
            cur_lo, cur_hi = a, b
        else:
            cur_hi = max(cur_hi, b)
    if cur_hi is not None:
        total += cur_hi - cur_lo
    return total


def is_set_recording(t: nml.Track) -> bool:
    return any(d in t.path for d in SET_DIRS) and (t.playtime or 0) >= MIN_SET_SECONDS


def simple_name(s: str) -> str:
    return re.sub(r"[-_. ]+", " ", os.path.splitext(os.path.basename(s))[0].lower()).strip()


def family_playlists(recording: str, playlists: dict[str, set[str]]) -> dict[str, set[str]]:
    """Playlists whose name shares a long prefix with the recording's name (same gig or set code)."""
    r = simple_name(recording)
    out = {}
    for name, keys in playlists.items():
        n = simple_name(name.split("/")[-1])
        common = os.path.commonprefix([r, n])
        if len(common) >= 12:
            out[name] = keys
    return out


def end_check(raw: nml.Track, start: dt.datetime, dur: float) -> str | None:
    """Traktor stamps a new recording's entry when the file is closed, in UTC. If MODIFIED minus
    (name start + length) is a whole number of hours, the name is the local start time to the second."""
    if not raw.modified:
        return None
    diff = (raw.modified - (start + dt.timedelta(seconds=dur))).total_seconds()
    hours = round(diff / 3600)
    resid = diff - hours * 3600
    if abs(hours) <= 14 and abs(resid) <= 90:
        return f"end check: entry stamped {resid:+.0f} s from name+length at UTC{-hours:+d}"
    return None


def names_dates(name: str) -> set[dt.date]:
    out = set()
    for y, m, d in re.findall(r"(20\d\d)-(\d\d)-(\d\d)", name):
        try:
            out.add(dt.date(int(y), int(m), int(d)))
        except ValueError:
            pass
    return out


class Fitter:
    def __init__(self, plays: list[nml.Play], playlists: dict[str, list[str]], tracks: dict[str, nml.Track]):
        self.public = [p for p in plays if p.public]
        self.t0 = self.public[0].start
        self.iv = [(self.sec(p.start), self.sec(p.start) + self.audible(p, tracks)) for p in self.public]
        self.playlists = {n: set(k) for n, k in playlists.items() if not n.split("/")[-1].lower().startswith("history")}

    @staticmethod
    def audible(p: nml.Play, tracks: dict[str, nml.Track]) -> float:
        """A deck left running past the end of its track is silent: cap at the track's length."""
        t = tracks.get(p.key)
        cap = t.playtime + 1 if t and t.playtime else MAX_PLAY_SECONDS
        return min(p.duration, cap, MAX_PLAY_SECONDS)

    def sec(self, t: dt.datetime) -> float:
        return (t - self.t0).total_seconds()

    def at(self, s: float) -> dt.datetime:
        return self.t0 + dt.timedelta(seconds=s)

    def measure(self, s: float, dur: float) -> tuple[float, float, float]:
        cov = union_seconds(self.iv, s, s + dur) / dur
        before = union_seconds(self.iv, s - EDGE_SECONDS, s) / EDGE_SECONDS
        after = union_seconds(self.iv, s + dur, s + dur + EDGE_SECONDS) / EDGE_SECONDS
        return cov, before, after

    def score(self, s: float, dur: float, dates: set[dt.date], family: dict[str, set[str]]) -> float:
        cov, before, after = self.measure(s, dur)
        bonus = 0.05 if self.at(s).date() in dates or self.at(s + dur).date() in dates else 0.0
        return cov - 0.25 * before - 0.25 * after + bonus + 0.3 * self.family_f1(s, dur, family)[1]

    def family_f1(self, s: float, dur: float, family: dict[str, set[str]]) -> tuple[str | None, float]:
        """Best F1 between the window's public tracks and a playlist of the recording's own family."""
        w = {p.key for p in self.window_plays(s, dur)}
        best = (None, 0.0)
        for name, keys in family.items():
            hit = len(w & keys)
            if hit and keys:
                f1 = 2 * hit / (len(w) + len(keys))
                if f1 > best[1]:
                    best = (name, f1)
        return best

    def window_plays(self, s: float, dur: float) -> list[nml.Play]:
        """Public plays audible inside the window for at least 30 s (or for most of their length)."""
        return [p for p, (a, b) in zip(self.public, self.iv)
                if min(b, s + dur) - max(a, s) >= min(30.0, (b - a) / 2)]


def pair_all(ni_root: str) -> tuple[list[Pairing], list[dict]]:
    coll = nml.read_collection(os.path.join(ni_root, "Traktor 4.5.1", "collection.nml"))
    plays = nml.read_history(ni_root)
    fit = Fitter(plays, coll.playlists, coll.tracks)
    first_day = plays[0].start.date()

    raws = []
    for t in coll.tracks.values():
        start = nml.raw_recording_start(os.path.basename(t.path))
        if start:
            raws.append((t, start, t.playtime or nml.wav_seconds(t.filesize_kb) or 0.0))

    recs = [t for t in coll.tracks.values()
            if is_set_recording(t) and not nml.raw_recording_start(os.path.basename(t.path))
            and t.import_date and t.import_date >= first_day]
    recs.sort(key=lambda t: t.import_date)

    out: list[Pairing] = []
    used_raw = set()
    for r in recs:
        dur = r.playtime
        twin = None
        for t, start, rdur in raws:
            same_size = r.filesize_kb and t.filesize_kb == r.filesize_kb
            same_len = abs(rdur - dur) <= 2 and t.import_date and abs((t.import_date - r.import_date).days) <= 1
            if same_size or same_len:
                twin = (t, start, "same FILESIZE" if same_size else "same length")
                break
        dates = names_dates(os.path.basename(r.path)) | {r.import_date}
        family = family_playlists(r.path, fit.playlists)
        notes = []
        if twin:
            used_raw.add(twin[0].key)
            s = fit.sec(twin[1])
            best, anchor = s, "raw-twin"
            notes.append(f"start from {os.path.basename(twin[0].path)} ({twin[2]})")
            check = end_check(twin[0], twin[1], dur)
            if check:
                notes.append(check)
            runner = None
        else:
            anchor = "fitted"
            lo = fit.sec(dt.datetime.combine(r.import_date - dt.timedelta(days=LOOKBACK_DAYS), dt.time()))
            hi = fit.sec(dt.datetime.combine(r.import_date + dt.timedelta(days=1), dt.time()))
            cands = sorted({a - 2 for a, _ in fit.iv if lo <= a <= hi})
            if not cands:
                continue
            scored = sorted(((fit.score(c, dur, dates, family), c) for c in cands), reverse=True)
            best = scored[0][1]
            others = [(sc, c) for sc, c in scored if abs(c - best) > dur / 2]
            runner = others[0] if others else None
        cov, before, after = fit.measure(best, dur)
        score = fit.score(best, dur, dates, family)
        fam_name, fam_f1 = fit.family_f1(best, dur, family)
        wp = fit.window_plays(best, dur)
        if anchor == "raw-twin" and cov >= 0.8:
            conf = "exact"
        elif anchor == "raw-twin":
            conf = "exact start, partial history"
        else:
            margin = score - (runner[0] if runner else 0)
            conf = "high" if score >= 0.85 and margin >= 0.15 and fam_f1 >= 0.8 else (
                "medium" if score >= 0.7 and margin >= 0.05 else "low")
            notes.append(f"margin over runner-up {margin:+.2f}")
        if before > 0.3:
            notes.append(f"public plays in the {EDGE_SECONDS // 60} min before the start ({before:.0%}): recording began mid-set")
        if after > 0.3:
            notes.append(f"public plays in the {EDGE_SECONDS // 60} min after the end ({after:.0%}): set continued past the recording")
        out.append(Pairing(
            recording=os.path.basename(r.path), duration_s=round(dur, 1), import_date=str(r.import_date),
            anchor=anchor, raw_name=os.path.basename(twin[0].path) if twin else None,
            start=f"{fit.at(best):%Y-%m-%d %H:%M:%S}", end=f"{fit.at(best + dur):%Y-%m-%d %H:%M:%S}",
            public_tracks=len({p.key for p in wp}), coverage=round(cov, 3), before=round(before, 3),
            after=round(after, 3),
            runner_up=f"{fit.at(runner[1]):%Y-%m-%d %H:%M}" if runner else None,
            runner_up_score=round(runner[0], 3) if runner else None, score=round(score, 3),
            family_playlist=fam_name, family_f1=round(fam_f1, 3), confidence=conf, notes="; ".join(notes)))

    # Raw Traktor recordings of set length that were never renamed into a set: possible extra sets.
    orphans = []
    for t, start, rdur in raws:
        if t.key in used_raw or rdur < MIN_SET_SECONDS or start.date() < first_day:
            continue
        s = fit.sec(start)
        cov, before, after = fit.measure(s, rdur)
        orphans.append({"raw": os.path.basename(t.path), "start": f"{start:%Y-%m-%d %H:%M:%S}",
                        "minutes": round(rdur / 60, 1), "coverage": round(cov, 3),
                        "public_tracks": len({p.key for p in fit.window_plays(s, rdur)})})
    return out, orphans


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ni-root", default=nml.DEFAULT_NI_ROOT)
    ap.add_argument("--json", help="write the pairings as JSON here")
    a = ap.parse_args()
    pairs, orphans = pair_all(a.ni_root)
    print("| Recording | Min | Anchor | Start (local) | End | Public tracks | Coverage | Own playlist (F1) | Runner-up | Confidence | Notes |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for p in pairs:
        ru = f"{p.runner_up} ({p.runner_up_score:.2f})" if p.runner_up else "-"
        print(f"| {p.recording} | {p.duration_s / 60:.0f} | {p.anchor} | {p.start} | {p.end[11:]} | {p.public_tracks} | "
              f"{p.coverage:.0%} | {p.family_playlist} ({p.family_f1:.2f}) | {ru} | {p.confidence} | {p.notes} |")
    if orphans:
        print("\nSet-length raw Traktor recordings with no renamed twin in the collection:")
        for o in orphans:
            print(f"- {o['raw']}: {o['minutes']} min from {o['start']}, history covers {o['coverage']:.0%}, "
                  f"{o['public_tracks']} public tracks")
    if a.json:
        with open(a.json, "w") as f:
            json.dump({"pairings": [asdict(p) for p in pairs], "unrenamed_raw": orphans}, f, indent=2, ensure_ascii=False)


if __name__ == "__main__":
    main()
