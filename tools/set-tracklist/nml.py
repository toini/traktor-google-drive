"""Read Traktor NML files: the collection, its playlists, and history sessions.

Pure standard library, so the pairing scripts run without the venv.
"""
from __future__ import annotations

import datetime as dt
import glob
import os
import re
import unicodedata
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

DEFAULT_NI_ROOT = os.path.expanduser("~/Downloads/NativeInstruments-2026-10-25/Native Instruments")

# 16-bit stereo PCM at 44.1 kHz, which is what Traktor's recorder writes (BITRATE="1411200").
WAV_BYTES_PER_SECOND = 44100 * 2 * 2


def norm_key(key: str) -> str:
    """Volume-less, NFC-normalised, lower-cased track key (the app's loose key)."""
    key = key.split("/:", 1)[1] if "/:" in key else key
    return unicodedata.normalize("NFC", key).lower()


def location_key(loc: ET.Element) -> str:
    return norm_key(loc.get("VOLUME", "") + loc.get("DIR", "") + loc.get("FILE", ""))


def drive_path(loc: ET.Element) -> str:
    """The file's path as a plain POSIX path (Traktor's DIR uses '/:' separators)."""
    return loc.get("DIR", "").replace("/:", "/") + loc.get("FILE", "")


@dataclass
class Track:
    key: str
    title: str
    artist: str
    path: str
    filesize_kb: int | None
    playtime: float | None
    bpm: float | None
    import_date: dt.date | None
    musical_key: str | None
    modified: dt.datetime | None = None  # MODIFIED_DATE/TIME; for a fresh raw recording this is its end, in UTC


@dataclass
class Collection:
    tracks: dict[str, Track]
    playlists: dict[str, list[str]]  # "Folder/Sub/Name" -> ordered track keys


def _date(s: str | None) -> dt.date | None:
    if not s:
        return None
    y, m, d = (int(x) for x in s.split("/"))
    return dt.date(y, m, d)


def read_collection(path: str) -> Collection:
    root = ET.parse(path).getroot()
    tracks: dict[str, Track] = {}
    for e in root.findall("COLLECTION/ENTRY"):
        loc = e.find("LOCATION")
        if loc is None or not loc.get("FILE"):
            continue
        info = e.find("INFO")
        tempo = e.find("TEMPO")
        mk = e.find("MUSICAL_KEY")
        info_get = info.get if info is not None else (lambda *_: None)
        pt = info_get("PLAYTIME_FLOAT") or info_get("PLAYTIME")
        fs = info_get("FILESIZE")
        k = location_key(loc)
        tracks[k] = Track(
            key=k,
            title=e.get("TITLE") or os.path.splitext(loc.get("FILE"))[0],
            artist=e.get("ARTIST") or "",
            path=drive_path(loc),
            filesize_kb=int(fs) if fs else None,
            playtime=float(pt) if pt else None,
            bpm=float(tempo.get("BPM")) if tempo is not None and tempo.get("BPM") else None,
            import_date=_date(info_get("IMPORT_DATE")),
            musical_key=mk.get("VALUE") if mk is not None else None,
            modified=(dt.datetime.combine(_date(e.get("MODIFIED_DATE")), dt.time())
                      + dt.timedelta(seconds=int(e.get("MODIFIED_TIME"))))
            if e.get("MODIFIED_DATE") and e.get("MODIFIED_TIME") else None,
        )
    playlists: dict[str, list[str]] = {}

    def walk(node: ET.Element, prefix: list[str]) -> None:
        for sub in node.findall("SUBNODES/NODE"):
            name = sub.get("NAME") or ""
            if sub.get("TYPE") == "FOLDER":
                walk(sub, prefix + [name])
                continue
            pl = sub.find("PLAYLIST")
            if pl is None:
                continue
            keys = [norm_key(pk.get("KEY", "")) for pk in pl.findall("ENTRY/PRIMARYKEY") if pk.get("TYPE") == "TRACK"]
            playlists["/".join(prefix + [name])] = keys

    top = root.find("PLAYLISTS/NODE")
    if top is not None:
        walk(top, [])
    return Collection(tracks, playlists)


@dataclass(order=True)
class Play:
    start: dt.datetime
    duration: float
    public: bool
    deck: int
    key: str = field(compare=False)
    session: str = field(compare=False)

    @property
    def end(self) -> dt.datetime:
        return self.start + dt.timedelta(seconds=self.duration)


def decode_startdate(v: int) -> dt.date:
    return dt.date(v >> 16, (v >> 8) & 0xFF, v & 0xFF)


def find_history_files(ni_root: str) -> dict[str, str]:
    """Unique history sessions by file name; Traktor copies them per version, keep the largest copy."""
    best: dict[str, str] = {}
    for p in glob.glob(os.path.join(ni_root, "**", "history_*.nml"), recursive=True):
        n = os.path.basename(p)
        if n not in best or os.path.getsize(p) > os.path.getsize(best[n]):
            best[n] = p
    return dict(sorted(best.items()))


def read_history(ni_root: str) -> list[Play]:
    """Every deck play across all sessions, sorted by start.

    STARTTIME is seconds since local midnight on the laptop's clock (the same clock that names
    Traktor's own recordings). The history file name is when the session was closed, not opened.
    """
    plays: list[Play] = []
    for name, path in find_history_files(ni_root).items():
        for e in ET.parse(path).getroot().findall(".//PLAYLIST/ENTRY"):
            x, pk = e.find("EXTENDEDDATA"), e.find("PRIMARYKEY")
            if x is None or pk is None or x.get("STARTDATE") is None:
                continue
            start = dt.datetime.combine(decode_startdate(int(x.get("STARTDATE"))), dt.time()) + dt.timedelta(
                seconds=int(x.get("STARTTIME"))
            )
            plays.append(
                Play(start, float(x.get("DURATION", 0)), x.get("PLAYEDPUBLIC") == "1", int(x.get("DECK", -1)),
                     norm_key(pk.get("KEY", "")), name)
            )
    plays.sort()
    return plays


RAW_RECORDING = re.compile(r"^Recording_(\d{4})-(\d{2})-(\d{2})_(\d{1,2})h(\d{2})m(\d{2})s?(?:_.*)?\.wav$", re.I)
OLD_RAW_RECORDING = re.compile(r"^(?:.* )?(\d{4})-(\d{2})-(\d{2})_(\d{1,2})h(\d{2})m(\d{2})\.wav$", re.I)


def raw_recording_start(filename: str) -> dt.datetime | None:
    """Start time from Traktor's default recording name (Recording_2025-02-23_13h28m54s.wav)."""
    m = RAW_RECORDING.match(filename) or OLD_RAW_RECORDING.match(filename)
    return dt.datetime(*(int(g) for g in m.groups())) if m else None


def wav_seconds(filesize_kb: int | None) -> float | None:
    return filesize_kb * 1024 / WAV_BYTES_PER_SECOND if filesize_kb else None
