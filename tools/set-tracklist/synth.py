"""Synthetic psytrance-ish tracks and DJ mixes with exact ground truth (ADR §8.3).

No real audio is needed: each track is synthesised from a seed (its own key, bassline,
lead motif, chord progression and timbre), with a drums-only intro and outro and a
breakdown with no kick, which is what makes real psytrance hard to identify.

The mixer plays an ordered subset of the tracks at one set tempo. Each track is
time-stretched with keylock (librosa phase vocoder, pitch unchanged) by ±3–8 %,
entered on the bar grid, and blended over 16–32 bars with a bass swap: the outgoing
track loses its low end at the blend midpoint, the incoming gets it.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import librosa
import numpy as np
from scipy.signal import butter, sosfilt

SR = 22050
SCALE = [0, 2, 3, 5, 7, 8, 10]  # natural minor


@dataclass
class SynthTrack:
    name: str
    bpm: float
    key: int  # 0 = A
    audio: np.ndarray
    bars: int


@dataclass
class MixSegment:
    name: str
    start: float          # boundary: midpoint of the blend in, seconds in the mix
    end: float
    deck_start: float     # when the track entered (blend start)
    deck_end: float       # when it left (blend end)
    track_offset: float   # seconds into the original track at deck_start
    tempo_ratio: float    # played / native tempo


@dataclass
class Mix:
    audio: np.ndarray
    bpm: float
    segments: list[MixSegment] = field(default_factory=list)


def _env(n: int, attack: int, decay: float) -> np.ndarray:
    t = np.arange(n)
    e = np.exp(-t / max(1.0, decay))
    if attack > 0:
        e[:attack] *= np.linspace(0, 1, attack)
    return e


def _tone(freq: float, n: int, harmonics: np.ndarray, sr: int = SR) -> np.ndarray:
    t = np.arange(n) / sr
    out = np.zeros(n)
    for h, w in enumerate(harmonics, start=1):
        if w and freq * h < sr / 2:
            out += w * np.sin(2 * np.pi * freq * h * t)
    return out


def make_track(seed: int, bpm: float, bars: int = 64, sr: int = SR) -> SynthTrack:
    """One track: 8-bar drum intro, main, 8-bar breakdown, main, 8-bar drum outro."""
    rng = np.random.default_rng(seed)
    key = int(rng.integers(0, 12))
    beat = 60.0 / bpm
    spb = int(round(beat * sr))           # samples per beat
    n = spb * 4 * bars
    y = np.zeros(n)
    root = 55.0 * 2 ** (key / 12)          # A1 + key

    def note(deg: int, octave: int) -> float:
        d = deg % len(SCALE)
        o = deg // len(SCALE)
        return root * 2 ** (octave + o + SCALE[d] / 12)

    bass_pattern = rng.integers(0, 5, 16)            # scale degrees per 16th
    bass_h = np.array([1.0, 0.5, 0.33, 0.25, 0.2]) * rng.uniform(0.6, 1.0, 5)
    lead_motif = rng.integers(0, 10, 16)             # 8th notes over 2 bars
    lead_rest = rng.random(16) < 0.3
    lead_h = rng.uniform(0, 1, 8) ** 2
    lead_h[0] = 1.0
    chords = rng.integers(0, 7, 4)                   # one chord per 2 bars
    pad_h = np.array([1.0, 0.3, 0.1])

    t = np.arange(spb // 2) / sr
    kick = np.sin(2 * np.pi * (50 * t + 100 * (1 - np.exp(-t * 30)) / 30)) * _env(len(t), 20, sr * 0.08)
    hat = rng.standard_normal(spb // 8) * _env(spb // 8, 5, spb / 40)
    hat = sosfilt(butter(4, 7000, "hp", fs=sr, output="sos"), hat) * 0.15

    intro, outro = 8, 8
    brk0 = bars // 2 - 4
    for b in range(bars):
        drums = not (brk0 <= b < brk0 + 8)
        music = intro <= b < bars - outro
        for q in range(4):
            i0 = (b * 4 + q) * spb
            if drums:
                y[i0:i0 + len(kick)] += 0.9 * kick
                h0 = i0 + spb // 2
                y[h0:h0 + len(hat)] += hat
            if music and drums:  # rolling offbeat bass: 3 of 4 sixteenths, not on the beat
                for s in (1, 2, 3):
                    st = (q * 4 + s)
                    f = note(int(bass_pattern[st]), 0)
                    ln = spb // 4
                    seg = _tone(f, ln, bass_h) * _env(ln, 30, ln / 3)
                    a = i0 + s * ln
                    y[a:a + ln] += 0.35 * seg
        if music:
            # lead: 8th notes, 2-bar motif
            for e in range(8):
                idx = (b % 2) * 8 + e
                if lead_rest[idx]:
                    continue
                f = note(int(lead_motif[idx]), 3)
                ln = spb // 2
                a = (b * 4) * spb + e * ln
                y[a:a + ln] += 0.12 * _tone(f, ln, lead_h) * _env(ln, 60, ln / 2)
            # pad: triad on the chord degree, held 1 bar
            c = int(chords[(b // 2) % 4])
            ln = spb * 4
            a = b * 4 * spb
            for d in (0, 2, 4):
                y[a:a + ln] += 0.05 * _tone(note(c + d, 2), ln, pad_h) * _env(ln, 2000, ln * 2)
    y /= np.max(np.abs(y)) + 1e-9
    return SynthTrack(name=f"synth-{seed:03d}", bpm=bpm, key=key, audio=(0.8 * y).astype(np.float32), bars=bars)


def _bands(y: np.ndarray, sr: int, fc: float = 150.0) -> tuple[np.ndarray, np.ndarray]:
    low = sosfilt(butter(4, fc, "lp", fs=sr, output="sos"), y)
    return low, y - low


def make_mix(tracks: list[SynthTrack], set_bpm: float, rng: np.random.Generator,
             play_bars: tuple[int, int] = (24, 40), blend_bars: tuple[int, int] = (16, 32),
             keylock: bool = True, sr: int = SR) -> Mix:
    """Mix the tracks in the given order at set_bpm, with bass-swap blends on the bar grid."""
    bar = 4 * 60.0 / set_bpm
    pieces, segs = [], []
    prev_end_bar = 0
    for i, tr in enumerate(tracks):
        ratio = set_bpm / tr.bpm
        if keylock:
            y = librosa.effects.time_stretch(tr.audio, rate=ratio)
        else:  # varispeed: resample, pitch moves with tempo
            y = librosa.resample(tr.audio, orig_sr=sr, target_sr=int(round(sr / ratio)))
        pieces.append((tr, ratio, y))
        skip = int(rng.integers(0, 9)) if i else 0                  # bars of intro skipped
        blend = 0 if i == 0 else int(rng.integers(blend_bars[0] // 4, blend_bars[1] // 4 + 1)) * 4
        play = int(rng.integers(play_bars[0] // 4, play_bars[1] // 4 + 1)) * 4
        avail = int(len(y) / sr / bar) - skip
        if segs:  # never blend longer than half of what the previous deck played
            blend = min(blend, int(round((segs[-1].deck_end - segs[-1].deck_start) / bar)) // 2)
        start_bar = prev_end_bar - blend
        end_bar = start_bar + min(blend + play, avail)
        segs.append(MixSegment(tr.name, start=0, end=0, deck_start=start_bar * bar, deck_end=end_bar * bar,
                               track_offset=skip * bar * ratio, tempo_ratio=ratio))
        prev_end_bar = end_bar
    total = int(segs[-1].deck_end * sr) + sr
    mix = _render(pieces, segs, bar, sr, total)
    mix.bpm = set_bpm
    return mix


def _render(pieces, segs: list[MixSegment], bar: float, sr: int, total: int) -> Mix:
    out = np.zeros(total)
    for i, ((tr, ratio, y), s) in enumerate(zip(pieces, segs)):
        enter = int(round(s.track_offset / ratio * sr))
        a = int(round(s.deck_start * sr))
        ln = int(round((s.deck_end - s.deck_start) * sr))
        seg = y[enter:enter + ln].astype(np.float64)
        if len(seg) < ln:
            seg = np.pad(seg, (0, ln - len(seg)))
        low, high = _bands(seg, sr)
        g_hi, g_lo = np.ones(ln), np.ones(ln)
        if i > 0:  # blend in
            bl = int(round((segs[i - 1].deck_end - s.deck_start) * sr))
            g_hi[:bl] = np.linspace(0, 1, bl)
            g_lo[:bl // 2] = 0
        if i + 1 < len(segs):  # blend out
            bl = int(round((s.deck_end - segs[i + 1].deck_start) * sr))
            if bl > 0:
                g_hi[ln - bl:] *= np.linspace(1, 0, bl)
                g_lo[ln - bl + bl // 2:] = 0
        out[a:a + ln] += low * g_lo + high * g_hi
    end = int(round(segs[-1].deck_end * sr))
    out = out[:end]
    out /= np.max(np.abs(out)) + 1e-9
    for i, s in enumerate(segs):
        s.start = 0.0 if i == 0 else (s.deck_start + segs[i - 1].deck_end) / 2
        s.end = segs[-1].deck_end if i + 1 == len(segs) else (segs[i + 1].deck_start + s.deck_end) / 2
    return Mix(audio=(0.8 * out).astype(np.float32), bpm=240.0 / bar, segments=segs)
