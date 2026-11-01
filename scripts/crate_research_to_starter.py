#!/usr/bin/env python3
"""Turn a web search's buy links for a crate's unresolved tracks into a starter batch.

Usage:
    python3 scripts/crate_research_to_starter.py <crate-research.json> [--version 2026-11-01.2] [--dry-run]

The research file is a JSON list of {artist, title, status, bandcamp_url,
beatport_url, release, label, date, confidence, note}. Each entry with a Bandcamp
or Beatport URL becomes a "resolution" in a new batch of
TraktorGoogleDrive/wwwroot/discover/starter.json, keyed by the crate's own artist
and title (a research entry may spell the artist differently, or complete a title
the screenshot cut off). The app applies the batch once per log and never over
something Toni resolved, pasted, picked or judged (StarterBatch.Resolutions,
ADR 20261025-54 §13.8). Running the script again replaces the batch of the same
version, so a corrected research file can be re-shipped before it is deployed.
"""
import argparse
import json
import re
import sys
import unicodedata
from pathlib import Path

# Titles the screenshots cut off or misspelled, and one misread artist: the research's
# spelling replaces the item's (its key stays), and the screenshot text goes into the note.
FIXES = {
    ("Seraburayka & Fear Disorder", "Self R… (truncated)"),
    ("The Hierophants (Via Axis, Purist & Yachay)", "(truncated)"),
    ("NoizeBug", "The Cosmos Welcomes You (Psilocybin Intellig… truncated)"),
    ("Into The Screaming Abyss", "Ululation"),
    ("Koobluda", "NOT UR KIND!"),
}

# What a row says when there is nothing to buy, in a few words, with the link that goes
# with it ("… for sale ↗"). The research's full note stays in the batch.
SHORT = {
    ("KETA.KALI", "NO LANE BACK"): ("Not found by a web search either", None),
    ("Arimaspo & Dark Crystal", "Unknown Creatures"): (
        "Maybe on Visionary Shamanics' Across The Unknown EP (2022), unchecked. The label on Beatport",
        "https://www.beatport.com/label/visionary-shamanics/40130"),
    ("Goch", "Plants and Animals"): ("Not on goch.bandcamp.com, and not found by a web search either", None),
    ("Gidra", "Spider Skunk (Edit 4)"): (
        "Gidra's own edit, not for sale. The original for sale", "https://beatspace-parvati.bandcamp.com/track/gidra-spider-skunk"),
    ("Access Nxum", "Computer Manfunction (Gidra Remix)"): (
        "Gidra's edit, not for sale. The original, Computer Malfunction, for sale",
        "https://beatspace-digitalshivapower.bandcamp.com/track/computer-malfunction"),
    ("NoizeBug", "The Cosmos Welcomes You (Psilocybin Intellig… truncated)"): (
        "Free on Ektoplazm", "https://ektoplazm.com/free-music/psilocybin-intelligences"),
}

ROOT = Path(__file__).resolve().parent.parent
STARTER = ROOT / "TraktorGoogleDrive" / "wwwroot" / "discover" / "starter.json"
CRATE = "looney-boon-hug-halloween-2026"


def slug(text: str) -> str:
    """Close to Names.Normalize + CrateMatch.Slug: accents off, & as and, non-alphanumerics to spaces."""
    t = unicodedata.normalize("NFD", text or "")
    t = "".join(c for c in t if unicodedata.category(c) != "Mn").lower().replace("&", " and ")
    return re.sub(r"[^a-z0-9]+", " ", t).strip()


def search_title(title: str) -> str:
    """CrateMatch.SearchTitle: the part before an ellipsis, without "(truncated)" or an unclosed bracket."""
    t = title.split("…")[0]
    t = re.sub(r"\(\s*truncated\s*\)|\btruncated\)?$", " ", t, flags=re.I).strip()
    if "(" in t and ")" not in t[t.rfind("("):]:
        t = t[: t.rfind("(")].strip()
    return t


def artist_words(artist: str) -> set[str]:
    return {w for w in slug(artist).split() if len(w) > 2 and w not in {"and", "the", "feat", "vs"}}


def match(entry: dict, tracks: list[dict]) -> dict | None:
    want = slug(entry["title"])
    best, best_score = None, 0
    for t in tracks:
        have = slug(search_title(t["title"]))
        if not have:
            continue
        if have == want:
            score = 3
        elif want.startswith(have) or want.endswith(" " + have) or have in want:
            score = 2
        else:
            continue
        if artist_words(t["artist"]) & artist_words(entry["artist"]):
            score += 2
        if score > best_score:
            best, best_score = t, score
    if best is None:
        # A title the screenshot cut off entirely, or spelled another way ("NOT UR KIND!"):
        # the artist, when only one track of the crate has it.
        same = [t for t in tracks if slug(t["artist"]) == slug(entry["artist"])]
        if len(same) == 1:
            best = same[0]
    return best


def batch_text(version: str, resolutions: list[dict]) -> str:
    lines = [json.dumps(r, ensure_ascii=False) for r in resolutions]
    body = ",\n        ".join(lines)
    return (
        "    {\n"
        f'      "version": {json.dumps(version)},\n'
        '      "_comment": "Buy links a web search found for the crate tracks Find left unresolved (2026-11-01). '
        'Applied once, never over a track Toni resolved, pasted, picked or judged.",\n'
        '      "resolutions": [\n'
        f"        {body}\n"
        "      ]\n"
        "    }"
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("research")
    ap.add_argument("--version", default="2026-11-01.2")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    research = json.loads(Path(args.research).read_text())
    text = STARTER.read_text()
    starter = json.loads(text)
    crate = next(c for b in starter["batches"] for c in (b.get("crates") or []) if c["id"] == CRATE)
    tracks = crate["tracks"]

    resolutions, skipped, unmatched = [], [], []
    for e in research:
        bandcamp, beatport = e.get("bandcamp_url"), e.get("beatport_url")
        t = match(e, tracks)
        if t is None:
            unmatched.append(f'{e["artist"]} - {e["title"]}')
            continue
        key = (t["artist"], t["title"])
        short, link = SHORT.get(key, (None, None))
        if not bandcamp and not beatport and short is None:
            skipped.append(f'{e["artist"]} - {e["title"]} ({e.get("status")})')
            continue
        r = {"crate": CRATE, "artist": t["artist"], "title": t["title"]}
        notes = [n for n in (t.get("note"), f'{t["bpm"]} BPM' if t.get("bpm") else None,
                             f'track {t["trackNumber"]}' if t.get("trackNumber") else None) if n]
        if key in FIXES:
            if slug(e["artist"]) != slug(t["artist"]):
                r["fixArtist"] = e["artist"]
            if e["title"] != t["title"]:
                r["fixTitle"] = e["title"]
            notes.append(f'screenshot: “{t["artist"]} – {t["title"]}”')
        if short:
            notes.append(short)
        if short or key in FIXES:
            r["itemNote"] = " · ".join(notes)
        if link:
            r["link"] = link
        for k, value in (("bandcamp", bandcamp), ("beatport", beatport), ("release", e.get("release")),
                         ("label", e.get("label")), ("date", e.get("date")), ("confidence", e.get("confidence")),
                         ("note", e.get("note"))):
            if value:
                r[k] = value
        if e.get("status") == "free-download":
            r["free"] = True
        resolutions.append(r)

    keys = [(r["artist"], r["title"]) for r in resolutions]
    dupes = {k for k in keys if keys.count(k) > 1}
    print(f"{len(resolutions)} resolutions, {len(skipped)} without a buy link, {len(unmatched)} unmatched", file=sys.stderr)
    for s in skipped:
        print("  no link:   " + s, file=sys.stderr)
    for u in unmatched:
        print("  unmatched: " + u, file=sys.stderr)
    if dupes:
        print(f"  two research entries for one track: {sorted(dupes)}", file=sys.stderr)
        return 1
    if args.dry_run:
        return 0

    # Replace a batch of the same version, or add one at the end; the rest of the file keeps its layout.
    block = batch_text(args.version, resolutions)
    existing = re.search(r',\n    \{\n      "version": ' + re.escape(json.dumps(args.version)) + r'.*?\n    \}(?=\n  \]\n\}\n?$)', text, re.S)
    if existing:
        text = text[: existing.start()] + ",\n" + block + text[existing.end():]
    else:
        end = text.rstrip().rfind("\n  ]")
        text = text[:end] + ",\n" + block + text[end:]
    json.loads(text)  # still valid
    STARTER.write_text(text if text.endswith("\n") else text + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
