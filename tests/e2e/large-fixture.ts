/**
 * A collection the size of the real one (about 1,600 tracks, 193 playlists,
 * the largest near 200 entries), generated rather than committed: a 1 MB NML
 * fixture would drown every diff that touches it.
 *
 * Deterministic, so a timing or a call count means the same thing on every run.
 */

export type DriveFileFixture = { id: string; name: string; mimeType: string; parents: string[] };

export const LARGE_TRACKS = 1600;
export const LARGE_PLAYLISTS = 193;

/** The two big playlists the switch timing is measured between. */
export const BIG_A = { uuid: 'b16a0000000000000000000000000001', name: 'Big Set A', entries: 196 };
export const BIG_B = { uuid: 'b16b0000000000000000000000000002', name: 'Big Set B', entries: 200 };

const GENRES = 12;

function folderOf(i: number) {
  return `Genre ${i % GENRES}`;
}

/**
 * Every 15th name has an accent, stored NFD in the collection (as macOS writes
 * it) and NFC in Drive, the PR 8 case. Every 100th shares its file name with a
 * copy in another Drive folder, so disambiguation by folder is exercised too.
 */
function fileName(i: number, form: 'NFC' | 'NFD' = 'NFC') {
  const base = i % 15 === 0 ? `Café Track ${i}` : `Track ${i}`;
  return `${base} - Artist ${i % 97}.mp3`.normalize(form);
}

const xml = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const key = (i: number) => `Macintosh HD/:Music/:Library/:${folderOf(i)}/:${xml(fileName(i, 'NFD'))}`;

/** A small fixed-seed PRNG, so the playlists are the same on every run. */
function rng(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0;
    return s / 2 ** 32;
  };
}

function uuid(n: number) {
  return n.toString(16).padStart(32, '0');
}

export function largeNml(): string {
  const out: string[] = [];
  out.push('<?xml version="1.0" encoding="UTF-8" standalone="no"?>');
  out.push('<NML VERSION="19"><HEAD COMPANY="www.native-instruments.com" PROGRAM="Traktor"></HEAD><MUSICFOLDERS></MUSICFOLDERS>');
  out.push(`<COLLECTION ENTRIES="${LARGE_TRACKS}">`);
  for (let i = 1; i <= LARGE_TRACKS; i++) {
    const month = (i % 12) + 1;
    const day = (i % 28) + 1;
    out.push(
      `<ENTRY MODIFIED_DATE="2024/3/17" TITLE="Track ${i}" ARTIST="Artist ${i % 97}">`
      + `<LOCATION DIR="/:Music/:Library/:${folderOf(i)}/:" FILE="${xml(fileName(i, 'NFD'))}" VOLUME="Macintosh HD"></LOCATION>`
      + `<ALBUM TITLE="Release ${i % 300}"></ALBUM>`
      + `<INFO BITRATE="320000" GENRE="${folderOf(i)}" LABEL="Label ${i % 40}" COMMENT="comment ${i}"`
      + ` PLAYTIME="${200 + (i % 300)}" PLAYTIME_FLOAT="${200 + (i % 300)}.5" RATING="c2 ${i % 7}"`
      + ` RANKING="${(i % 6) * 51}" KEY="${(i % 12) + 1}A" IMPORT_DATE="${2018 + (i % 8)}/${month}/${day}"`
      + (i % 8 === 0 ? '' : ` COLOR="${(i % 7) + 1}"`)
      + `></INFO><TEMPO BPM="${(120 + (i % 30)).toFixed(6)}" BPM_QUALITY="100.000000"></TEMPO>`
      // What else a real Traktor 4 ENTRY carries, so the parse costs what it really does.
      + '<MODIFICATION_INFO AUTHOR_TYPE="user"></MODIFICATION_INFO>'
      + '<LOUDNESS PEAK_DB="-0.205" PERCEIVED_DB="-1.234" ANALYZED_DB="-1.234"></LOUDNESS>'
      + `<MUSICAL_KEY VALUE="${i % 24}"></MUSICAL_KEY>`
      + `<CUE_V2 NAME="AutoGrid" DISPL_ORDER="0" TYPE="4" START="${(i % 900) + 12.345}" LEN="0.000000" REPEATS="-1" HOTCUE="0"><GRID BPM="${(120 + (i % 30)).toFixed(6)}"></GRID></CUE_V2>`
      + `<CUE_V2 NAME="Load" DISPL_ORDER="0" TYPE="3" START="${(i % 900) + 12.345}" LEN="0.000000" REPEATS="-1" HOTCUE="1"></CUE_V2>`
      + `<CUE_V2 NAME="Drop" DISPL_ORDER="0" TYPE="0" START="${(i % 900) * 3 + 64000.5}" LEN="0.000000" REPEATS="-1" HOTCUE="2"></CUE_V2>`
      + `<CUE_V2 NAME="Out" DISPL_ORDER="0" TYPE="5" START="${(i % 900) * 5 + 180000.25}" LEN="15000.000000" REPEATS="-1" HOTCUE="3"></CUE_V2>`
      + '</ENTRY>',
    );
  }
  out.push('</COLLECTION><SETS ENTRIES="0"></SETS><PLAYLISTS><NODE TYPE="FOLDER" NAME="$ROOT">');

  const random = rng(42);
  const playlist = (name: string, id: string, tracks: number[]) =>
    `<NODE TYPE="PLAYLIST" NAME="${xml(name)}"><PLAYLIST ENTRIES="${tracks.length}" TYPE="LIST" UUID="${id}">`
    + tracks.map((t) => `<ENTRY><PRIMARYKEY TYPE="TRACK" KEY="${key(t)}"></PRIMARYKEY></ENTRY>`).join('')
    + '</PLAYLIST></NODE>';
  const pick = (count: number) =>
    Array.from({ length: count }, () => 1 + Math.floor(random() * LARGE_TRACKS));

  // 10 folders; the two big sets go in the first one.
  const folders = 10;
  let made = 0;
  out.push(`<SUBNODES COUNT="${folders}">`);
  for (let f = 0; f < folders; f++) {
    const inFolder = f === folders - 1 ? LARGE_PLAYLISTS - made : Math.floor(LARGE_PLAYLISTS / folders);
    out.push(`<NODE TYPE="FOLDER" NAME="Crate ${f}"><SUBNODES COUNT="${inFolder}">`);
    for (let p = 0; p < inFolder; p++) {
      if (f === 0 && p === 0) out.push(playlist(BIG_A.name, BIG_A.uuid, pick(BIG_A.entries)));
      else if (f === 0 && p === 1) out.push(playlist(BIG_B.name, BIG_B.uuid, pick(BIG_B.entries)));
      else out.push(playlist(`Playlist ${made}`, uuid(0x1000 + made), pick(5 + Math.floor(random() * 60))));
      made++;
    }
    out.push('</SUBNODES></NODE>');
  }
  out.push('</SUBNODES></NODE></PLAYLISTS></NML>');
  return out.join('\n');
}

export const LARGE_FOLDERS: Record<string, string> = Object.fromEntries([
  ...Array.from({ length: GENRES }, (_, g) => [`lf-genre-${g}`, `Genre ${g}`]),
  ['lf-dupes', 'Dupes'],
]);

/** Drive's side: every track but each 50th, NFC names, plus same-named copies elsewhere. */
export function largeDriveFiles(): DriveFileFixture[] {
  const files: DriveFileFixture[] = [];
  for (let i = 1; i <= LARGE_TRACKS; i++) {
    if (i % 50 === 0) continue; // in the collection, not in Drive
    files.push({ id: `lf-${i}`, name: fileName(i), mimeType: 'audio/mpeg', parents: [`lf-genre-${i % GENRES}`] });
    if (i % 100 === 1) {
      files.push({ id: `lf-dupe-${i}`, name: fileName(i), mimeType: 'audio/mpeg', parents: ['lf-dupes'] });
    }
  }
  return files;
}
