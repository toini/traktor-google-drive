"""Tests for the set-tracklist spike. Run: tools/set-tracklist/.venv/bin/python -m unittest -v test_spike
(from tools/set-tracklist). The history tests need no audio and no Toni data; the pipeline
test synthesises its own tracks."""
from __future__ import annotations

import datetime as dt
import os
import sys
import unittest
import warnings

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import detect  # noqa: E402
import evaluate  # noqa: E402
import nml  # noqa: E402
import pair_history as ph  # noqa: E402
import synth  # noqa: E402

warnings.filterwarnings("ignore", category=FutureWarning)
warnings.filterwarnings("ignore", category=RuntimeWarning)


class HistoryHelpers(unittest.TestCase):
    def test_startdate_packs_year_month_day(self):
        self.assertEqual(nml.decode_startdate(132710935), dt.date(2025, 2, 23))

    def test_raw_recording_names(self):
        self.assertEqual(nml.raw_recording_start("Recording_2025-02-23_13h28m54s.wav"), dt.datetime(2025, 2, 23, 13, 28, 54))
        self.assertEqual(nml.raw_recording_start("2022-02-19_20h12m36.wav"), dt.datetime(2022, 2, 19, 20, 12, 36))
        self.assertIsNone(nml.raw_recording_start("Z19-2025 Sentimony 2025-02-23.wav"))

    def test_wav_seconds_from_filesize(self):
        # Z19: FILESIZE 1398964 KiB, PLAYTIME_FLOAT 8120.95
        self.assertAlmostEqual(nml.wav_seconds(1398964), 8120.9, delta=0.5)

    def test_union_seconds_merges_overlaps(self):
        self.assertEqual(ph.union_seconds([(0, 10), (5, 20), (30, 40)], 0, 35), 25)

    def test_end_check_finds_utc_offset(self):
        raw = nml.Track("k", "t", "", "/x", None, None, None, None, None,
                        modified=dt.datetime(2025, 2, 23, 23, 44, 14))
        msg = ph.end_check(raw, dt.datetime(2025, 2, 23, 13, 28, 54), 8120.95)
        self.assertIn("UTC-8", msg)

    def test_family_playlists_by_name_prefix(self):
        fam = ph.family_playlists("2026-02-14-Ystavien tanssit.prep-2025-12-07.wav",
                                  {"Gigs/2026-02-14 Ystavien tanssit-2.prep.rec": set(), "Z/Z19-2025 Sentimony ": set()})
        self.assertEqual(list(fam), ["Gigs/2026-02-14 Ystavien tanssit-2.prep.rec"])


class SyntheticPipeline(unittest.TestCase):
    """A small mix with ±3–8 % tempo, bass-swap blends and unplayed candidates, end to end."""

    @classmethod
    def setUpClass(cls):
        rng = np.random.default_rng(11)
        tracks = [synth.make_track(1100 + i, bpm=140 / (1 + rng.uniform(0.03, 0.08) * rng.choice([-1, 1])), bars=96)
                  for i in range(8)]
        cls.played = tracks[:5]
        cls.mix = synth.make_mix(cls.played, 140.0, rng)
        cls.rec = detect.beat_features(cls.mix.audio, synth.SR, 140.0)
        cls.feats = {t.name: detect.beat_features(detect.excerpts(t.audio, synth.SR), synth.SR, t.bpm) for t in tracks}
        cls.truth = [(s.start, s.end, s.name) for s in cls.mix.segments]
        cls.blends = [(a.name, b.name, b.deck_start, a.deck_end) for a, b in zip(cls.mix.segments, cls.mix.segments[1:])]

    def test_truth_is_contiguous(self):
        for a, b in zip(self.truth, self.truth[1:]):
            self.assertAlmostEqual(a[1], b[0])

    def test_base_list_viterbi_meets_adr_targets(self):
        cands = [self.played[0].name, "synth-1105", self.played[2].name, self.played[1].name, "synth-1106",
                 self.played[3].name, self.played[4].name, "synth-1107"]
        segs = detect.detect(self.rec, {c: self.feats[c] for c in cands}, prior="base")
        sc = evaluate.evaluate(self.truth, [(s.start, s.end, s.track) for s in segs], self.blends)
        self.assertGreaterEqual(sc.accuracy, 0.9)
        self.assertLessEqual(sc.boundary_median, 15)
        self.assertGreaterEqual(sc.played_f1, 0.9)

    def test_rec_list_viterbi_is_exact_in_order(self):
        cands = [t.name for t in self.played]
        segs = detect.detect(self.rec, {c: self.feats[c] for c in cands}, prior="rec")
        self.assertEqual([s.track for s in segs], cands)


if __name__ == "__main__":
    unittest.main()
