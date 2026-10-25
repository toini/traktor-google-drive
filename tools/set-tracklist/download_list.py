#!/usr/bin/env python3
"""List the audio files a spike run needs: one recording plus every track in its playlists.

Paths are printed relative to the Drive root ("My Drive/..."), taken from the NML LOCATION
(/Users/toni/Google Drive/<rest> -> My Drive/<rest>). Sizes come from the NML FILESIZE (KiB).
The output names Toni's files, so write it outside the repo (it is not committed).

Usage: download_list.py --recording NAME --playlist PATH [--playlist PATH ...] [--tsv OUT] [--ni-root DIR]
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import nml  # noqa: E402
from ground_truth import is_separator  # noqa: E402

DRIVE_ROOTS = ("/Users/toni/Google Drive/", "/Users/toni/My Drive/")


def drive_rel(path: str) -> str:
    for root in DRIVE_ROOTS:
        if path.startswith(root):
            return "My Drive/" + path[len(root):]
    return "NOT ON DRIVE: " + path


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ni-root", default=nml.DEFAULT_NI_ROOT)
    ap.add_argument("--recording", required=True, help="file name of the recording")
    ap.add_argument("--playlist", action="append", required=True, help="playlist path as Folder/Name")
    ap.add_argument("--tsv", help="write role, size, Drive path as TSV here")
    a = ap.parse_args()
    coll = nml.read_collection(os.path.join(a.ni_root, "Traktor 4.5.1", "collection.nml"))

    rows: list[tuple[str, int | None, str]] = []
    recs = [t for t in coll.tracks.values() if os.path.basename(t.path) == a.recording]
    if not recs:
        sys.exit(f"recording {a.recording!r} not in the collection")
    rows.append(("recording", recs[0].filesize_kb, drive_rel(recs[0].path)))

    roles: dict[str, list[str]] = {}
    for pl in a.playlist:
        if pl not in coll.playlists:
            sys.exit(f"playlist {pl!r} not found")
        for k in coll.playlists[pl]:
            t = coll.tracks.get(k)
            if t is None or is_separator(t):
                continue
            roles.setdefault(k, [])
            if pl not in roles[k]:
                roles[k].append(pl)
    for k, pls in roles.items():
        t = coll.tracks[k]
        role = "+".join("rec" if p.endswith(".rec") else "base" for p in pls)
        rows.append((role, t.filesize_kb, drive_rel(t.path)))

    total = sum(r[1] or 0 for r in rows)
    missing = [r for r in rows if r[1] is None]
    print(f"{len(rows)} files, {total / 1024 / 1024:.2f} GiB total"
          + (f" ({len(missing)} without FILESIZE, not counted)" if missing else ""))
    for role in ("recording", "base+rec", "rec", "base"):
        sel = [r for r in rows if r[0] == role]
        if sel:
            print(f"  {role:9s}: {len(sel):3d} files, {sum(r[1] or 0 for r in sel) / 1024:8.1f} MiB")
    exts: dict[str, list[int]] = {}
    for r in rows[1:]:
        exts.setdefault(os.path.splitext(r[2])[1].lower(), []).append(r[1] or 0)
    print("  formats  :", ", ".join(f"{e} {len(v)} ({sum(v) / 1024:.0f} MiB)" for e, v in sorted(exts.items())))
    folders: dict[str, int] = {}
    for r in rows:
        folders[os.path.dirname(r[2])] = folders.get(os.path.dirname(r[2]), 0) + 1
    print("  folders  :")
    for d, n in sorted(folders.items()):
        print(f"    {n:3d}  {d}")
    if a.tsv:
        with open(a.tsv, "w") as f:
            f.write("role\tsize_kib\tdrive_path\n")
            for r in rows:
                f.write(f"{r[0]}\t{r[1] if r[1] is not None else ''}\t{r[2]}\n")


if __name__ == "__main__":
    main()
