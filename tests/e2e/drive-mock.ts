import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import type { Page, Route } from '@playwright/test';
import type { DriveFileFixture } from './large-fixture';

const here = dirname(fileURLToPath(import.meta.url));

/** Hardcoded in CollectionService.GetCollectionFileId. */
export const COLLECTION_FILE_ID = '1yqP8GXUb9qLV8gXRLpvKpyy7DDY7CqAC';

export const FAKE_TOKEN = 'fixture-access-token';

/** Drive folder ids -> names, used to disambiguate same-named files. */
export const DRIVE_FOLDERS: Record<string, string> = {
  'folder-sets': 'Sets',
  'folder-archive': 'Archive',
  'folder-gig': 'Gig',
  // Mirrors a real Traktor install: one collection.nml per version, plus backups.
  'folder-tk-3': 'Traktor 3.11.1',
  'folder-tk-40': 'Traktor 4.0.0',
  'folder-tk-441': 'Traktor 4.4.1',
  'folder-tk-441-backup': 'Backup',
};

/**
 * Several collection.nml files across Traktor versions. Deliberately gives the
 * BACKUP the newest modifiedTime, because that is the case that defeats a
 * naive "most recently modified" pick.
 */
export const COLLECTION_CANDIDATES = [
  { id: 'nml-backup', parent: 'folder-tk-441-backup', modifiedTime: '2026-08-15T10:00:00.000Z' },
  { id: 'nml-v3', parent: 'folder-tk-3', modifiedTime: '2024-01-01T10:00:00.000Z' },
  { id: 'nml-v441', parent: 'folder-tk-441', modifiedTime: '2026-07-27T10:00:00.000Z' },
  { id: 'nml-v40', parent: 'folder-tk-40', modifiedTime: '2025-06-01T10:00:00.000Z' },
];

/** The one the app must choose: highest Traktor version, not a backup. */
export const EXPECTED_COLLECTION_ID = 'nml-v441';

/**
 * Drive file ids the mock hands out, keyed by the bare filename the app
 * queries for. Two entries share the name `alpha.wav` on purpose — that is
 * the B8 collision case, distinguished only by `parents`.
 */
export const DRIVE_FILES = [
  { id: 'drive-alpha-sets', name: 'alpha.wav', mimeType: 'audio/wav', parents: ['folder-sets'] },
  { id: 'drive-beta', name: 'beta.wav', mimeType: 'audio/wav', parents: ['folder-sets'] },
  { id: 'drive-gamma', name: 'gamma.wav', mimeType: 'audio/wav', parents: ['folder-sets'] },
  { id: 'drive-alpha-archive', name: 'alpha.wav', mimeType: 'audio/wav', parents: ['folder-archive'] },
  { id: 'drive-delta', name: 'delta.wav', mimeType: 'audio/wav', parents: ['folder-archive'] },
  { id: 'drive-z11-3', name: 'Z11-3 2021-09-17.mp3', mimeType: 'audio/mpeg', parents: ['folder-sets'] },
  { id: 'drive-c4-2026', name: 'C4 2026-08-16.wav', mimeType: 'audio/wav', parents: ['folder-sets'] },
  // Gig preview. Named as compressed formats; the mock still serves its WAV,
  // which the browser decodes just the same.
  { id: 'drive-gig-one', name: 'gig-one.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
  { id: 'drive-gig-two', name: 'gig-two.flac', mimeType: 'audio/flac', parents: ['folder-gig'] },
  { id: 'drive-gig-three', name: 'gig-three.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
  { id: 'drive-gig-four', name: 'gig-four.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
  { id: 'drive-gig-exit', name: 'gig-exit.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
  { id: 'drive-gig-gas-chamber', name: 'gig-gas-chamber.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
  // Gig Night. The separator's silent file is in Drive and must still never
  // be requested; Cafe Noir is stored NFC while the collection has it NFD.
  { id: 'drive-silent', name: 'silent.wav', mimeType: 'audio/wav', parents: ['folder-gig'] },
  { id: 'drive-cafe', name: 'Caf\u00e9 Noir.wav', mimeType: 'audio/wav', parents: ['folder-gig'] },
  { id: 'drive-usb', name: 'usb-anthem.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
];

/**
 * Files served longer than the default WAV, matching their PLAYTIME in the
 * fixture: an entrance loop (less than 30 s after the load cue) can only be told
 * from an exit loop on a track that long.
 */
export const WAV_SECONDS: Record<string, number> = {
  'drive-gig-exit': 40,
  'drive-gig-gas-chamber': 240,
};

/**
 * Minimal 16-bit mono PCM WAV. Generated rather than committed so there is no
 * binary fixture; a real decodable body matters because the player's state
 * machine is driven by canplay/ended, which never fire for a stub body.
 */
export function makeWav(seconds = 2, sampleRate = 8000): Buffer {
  const samples = seconds * sampleRate;
  const data = Buffer.alloc(samples * 2);
  for (let i = 0; i < samples; i++) {
    // 440 Hz at low amplitude — audible if a human ever runs this headed.
    data.writeInt16LE(Math.round(Math.sin((2 * Math.PI * 440 * i) / sampleRate) * 8000), i * 2);
  }
  const header = Buffer.alloc(44);
  header.write('RIFF', 0);
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVE', 8);
  header.write('fmt ', 12);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20); // PCM
  header.writeUInt16LE(1, 22); // mono
  header.writeUInt32LE(sampleRate, 24);
  header.writeUInt32LE(sampleRate * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36);
  header.writeUInt32LE(data.length, 40);
  return Buffer.concat([header, data]);
}

export type DriveCall = {
  /**
   * query: a files.list by name. list: a files.list with no name in it, i.e.
   * walking the whole library. changes / about: the incremental index refresh.
   */
  kind: 'collection' | 'query' | 'list' | 'media' | 'folder' | 'meta' | 'changes' | 'about';
  url: string;
  range?: string;
  /** Media only: the token the request carried (the proxy cookie), and the status served. */
  token?: string | null;
  status?: number;
};

/** The cookie auth.js keeps for the audio proxy, so media URLs carry no token. */
export const PROXY_COOKIE = 'drive_token';

/**
 * The Drive token a proxy request carries. Only the cookie, like the real proxy:
 * its ?token= fallback is gone (Cast has its own ticket route).
 */
export function proxyToken(cookieHeader: string | undefined): string | null {
  const cookie = (cookieHeader ?? '')
    .split(';')
    .map((c) => c.trim())
    .find((c) => c.startsWith(`${PROXY_COOKIE}=`));
  return cookie ? decodeURIComponent(cookie.slice(PROXY_COOKIE.length + 1)) : null;
}

/** What Drive hands back when the app searches for collection.nml by name. */
export const DISCOVERED_COLLECTION_ID = 'drive-collection-discovered';

export type MockOptions = {
  /** Fail the collection fetch with this status instead of serving it. */
  collectionStatus?: number;
  /**
   * Make the hardcoded/legacy collection id 404, as it does in the real Drive
   * account. Forces the app down its discovery path.
   */
  legacyCollectionMissing?: boolean;
  /** Return no results when searching for collection.nml by name. */
  noCollectionFound?: boolean;
  /** Return the realistic multi-version set instead of a single discovered file. */
  manyCollections?: boolean;
  /** Fail every media fetch with this status (401 exercises token expiry). */
  mediaStatus?: number;
  /**
   * Which tokens Drive still accepts for media; anything else gets a 401. A
   * function so a test can expire a token mid-playback. Unset = all accepted.
   */
  acceptToken?: (token: string | null) => boolean;
  /** Length of the served WAV. Long enough, and the browser has to come back for more slices. */
  wavSeconds?: number;
  /** Cap each 206 at this many bytes, like the real proxy's 8 MiB slices. */
  maxSliceBytes?: number;
  /**
   * Hold every media response until this settles. Lets a test observe the
   * state before the media element has any metadata, which otherwise races.
   */
  mediaGate?: Promise<unknown>;
  /** Override the NML body. */
  nml?: string;
  /**
   * RFC3339 modifiedTime reported for the collection file, by both discovery
   * and the metadata call. '' reports none. Read on every request, so a test
   * can change it between page loads to model Traktor syncing a new file.
   */
  collectionModifiedTime?: string;
  /** Replace the audio files Drive holds (the large fixture). */
  driveFiles?: DriveFileFixture[];
  /** Replace the folder id -> name map. */
  driveFolders?: Record<string, string>;
  /**
   * Delay every googleapis.com answer by this much, so a timing reflects how
   * many round trips a step makes. Media is not delayed.
   */
  latencyMs?: number;
  /**
   * Drive's change log, for changes.list. A test pushes to it (after the
   * first load) to model a file added or removed in Drive since.
   */
  changes?: DriveChange[];
};

export type DriveChange = { fileId: string; removed?: boolean; file?: DriveFileFixture & { trashed?: boolean } };

/** The account the mock's `about` reports, so the app can key its caches by user. */
export const FIXTURE_PERMISSION_ID = 'perm-fixture-user';

/**
 * Intercepts every outbound call the app makes: the two googleapis.com
 * endpoints and the same-origin audio proxy. Returns the call log so tests can
 * assert on request *shape* (e.g. that the token stopped being a query param,
 * or that a long playlist got chunked into several queries).
 */
export async function mockDrive(page: Page, opts: MockOptions = {}): Promise<DriveCall[]> {
  const calls: DriveCall[] = [];
  // opts.nml is read per request (a test may swap it between loads); this is the default.
  const fixtureNml = readFileSync(join(here, 'fixtures', 'collection.nml'), 'utf8');
  const defaultWav = makeWav(opts.wavSeconds);
  const longWavs = new Map<string, Buffer>();
  const wavFor = (url: string) => {
    const id = Object.keys(WAV_SECONDS).find((k) => new URL(url).pathname.endsWith(`/${k}`));
    if (!id) return defaultWav;
    if (!longWavs.has(id)) longWavs.set(id, makeWav(WAV_SECONDS[id]));
    return longWavs.get(id)!;
  };

  const serveMedia = async (route: Route, url: string) => {
    const wav = wavFor(url);
    const headers = await route.request().allHeaders();
    // A direct googleapis.com download (the player's whole-file cache, the
    // waveform) carries the token as a header; the proxy, as a cookie.
    const bearer = headers['authorization']?.match(/^Bearer (.+)$/)?.[1] ?? null;
    const call: DriveCall = {
      kind: 'media', url, range: headers['range'], token: bearer ?? proxyToken(headers['cookie']),
    };
    calls.push(call);
    if (opts.mediaGate) await opts.mediaGate;
    if (opts.mediaStatus) {
      call.status = opts.mediaStatus;
      return route.fulfill({ status: opts.mediaStatus, body: '' });
    }
    if (opts.acceptToken && !opts.acceptToken(call.token ?? null)) {
      call.status = 401;
      return route.fulfill({ status: 401, body: '' });
    }

    const range = route.request().headers()['range'];
    const m = range?.match(/bytes=(\d+)-(\d*)/);
    if (!m) {
      call.status = 200;
      return route.fulfill({
        status: 200,
        headers: {
          'content-type': 'audio/wav',
          'accept-ranges': 'bytes',
          'content-length': String(wav.length),
        },
        body: wav,
      });
    }
    const start = Number(m[1]);
    let end = m[2] ? Number(m[2]) : wav.length - 1;
    if (opts.maxSliceBytes) end = Math.min(end, start + opts.maxSliceBytes - 1);
    call.status = 206;
    const slice = wav.subarray(start, end + 1);
    return route.fulfill({
      status: 206,
      headers: {
        'content-type': 'audio/wav',
        'accept-ranges': 'bytes',
        'content-length': String(slice.length),
        'content-range': `bytes ${start}-${end}/${wav.length}`,
      },
      body: slice,
    });
  };

  await page.route(
    (url) =>
      url.hostname === 'www.googleapis.com' ||
      url.hostname === 'www.gstatic.com' ||
      url.pathname.startsWith('/api/proxy/drive/'),
    async (route) => {
      const url = route.request().url();
      const u = new URL(url);

      // The Cast sender SDK. Blocked rather than stubbed so no test silently
      // depends on the network; cast-mock.ts installs a working fake instead.
      if (u.hostname === 'www.gstatic.com') return route.abort();

      // Same-origin audio proxy.
      if (u.pathname.startsWith('/api/proxy/drive/')) return serveMedia(route, url);

      // Drive API calls only: a direct audio download (alt=media on a track) is
      // media, which latencyMs leaves alone like the proxy.
      const fileId = u.pathname.replace('/drive/v3/files/', '');
      const isCollectionId = fileId === COLLECTION_FILE_ID || fileId === DISCOVERED_COLLECTION_ID
        || COLLECTION_CANDIDATES.some((c) => c.id === fileId);
      const isTrackMedia = u.searchParams.get('alt') === 'media' && !isCollectionId;
      if (opts.latencyMs && !isTrackMedia) await new Promise((r) => setTimeout(r, opts.latencyMs));

      const driveFiles = opts.driveFiles ?? DRIVE_FILES;
      const json = (body: unknown) =>
        route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });

      // Drive: who is signed in. The app keys its file index by it.
      if (u.pathname === '/drive/v3/about') {
        calls.push({ kind: 'about', url });
        return json({ user: { permissionId: FIXTURE_PERMISSION_ID } });
      }

      // Drive: changes since a page token. The token is an index into the log.
      if (u.pathname === '/drive/v3/changes/startPageToken') {
        calls.push({ kind: 'changes', url });
        return json({ startPageToken: String((opts.changes ?? []).length) });
      }
      if (u.pathname === '/drive/v3/changes') {
        calls.push({ kind: 'changes', url });
        const log = opts.changes ?? [];
        const from = Number(u.searchParams.get('pageToken') ?? '0') || 0;
        return json({ changes: log.slice(from), newStartPageToken: String(log.length) });
      }

      // Drive: list files matching a name query.
      if (u.pathname === '/drive/v3/files') {
        const q = u.searchParams.get('q') ?? '';

        // No name in it: the app walking every audio file once, page by page.
        if (!q.includes('name =') && !q.includes("'collection.nml'")) {
          calls.push({ kind: 'list', url });
          const all: { id: string; name: string }[] = q.includes('application/vnd.google-apps.folder')
            ? Object.entries(opts.driveFolders ?? DRIVE_FOLDERS).map(([id, name]) => ({ id, name }))
            : driveFiles;
          const pageSize = Number(u.searchParams.get('pageSize') ?? '100');
          const from = Number(u.searchParams.get('pageToken') ?? '0') || 0;
          const page = all.slice(from, from + pageSize);
          const next = from + pageSize < all.length ? String(from + pageSize) : undefined;
          return json({ files: page, ...(next ? { nextPageToken: next } : {}) });
        }

        calls.push({ kind: 'query', url });

        // Discovery: the app searching for collection.nml by name.
        if (q.includes("'collection.nml'")) {
          const files = opts.noCollectionFound
            ? []
            : opts.manyCollections
              ? COLLECTION_CANDIDATES.map((c) => ({
                  id: c.id,
                  name: 'collection.nml',
                  modifiedTime: c.modifiedTime,
                  parents: [c.parent],
                }))
              : [
                  {
                    id: DISCOVERED_COLLECTION_ID,
                    name: 'collection.nml',
                    ...(opts.collectionModifiedTime === ''
                      ? {}
                      : { modifiedTime: opts.collectionModifiedTime ?? '2026-08-16T10:00:00.000Z' }),
                  },
                ];
          return route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ files }),
          });
        }

        const files = driveFiles.filter((f) => q.includes(`'${f.name}'`));
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ files }),
        });
      }

      const id = u.pathname.replace('/drive/v3/files/', '');

      // Drive: file metadata (id/name/modifiedTime) — the collection's sync time.
      if (u.searchParams.get('fields')?.includes('modifiedTime')) {
        calls.push({ kind: 'meta', url });
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            id,
            name: 'collection.nml',
            modifiedTime: opts.collectionModifiedTime ?? '2026-08-17T09:32:00.000Z',
          }),
        });
      }

      // Drive: folder metadata lookup (used to disambiguate same-named files).
      if (u.searchParams.get('fields') === 'name') {
        calls.push({ kind: 'folder', url });
        const name = (opts.driveFolders ?? DRIVE_FOLDERS)[id];
        return name
          ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ name }) })
          : route.fulfill({ status: 404, body: '' });
      }

      // Drive: download a single file by id.
      if (id === COLLECTION_FILE_ID || id === DISCOVERED_COLLECTION_ID
          || COLLECTION_CANDIDATES.some((c) => c.id === id)) {
        calls.push({ kind: 'collection', url });
        if (opts.collectionStatus) return route.fulfill({ status: opts.collectionStatus, body: '' });

        if (id === COLLECTION_FILE_ID && opts.legacyCollectionMissing) {
          // Google's real 404 body — the old code fed exactly this to the XML
          // parser, which is what blanked the page.
          return route.fulfill({
            status: 404,
            contentType: 'application/json',
            body: JSON.stringify({
              error: { code: 404, message: 'File not found: ' + id, status: 'NOT_FOUND' },
            }),
          });
        }
        return route.fulfill({ status: 200, contentType: 'text/xml', body: opts.nml ?? fixtureNml });
      }
      return serveMedia(route, url);
    },
  );

  return calls;
}

/**
 * Turns off the player's whole-file cache (prefetch.js), so playback streams
 * through the proxy only, as a set too big to cache does. For tests about the
 * streaming path itself: its slices, its 401 recovery, its waveform reads.
 */
export async function noPrefetch(page: Page): Promise<void> {
  await page.addInitScript(() => localStorage.setItem('player:prefetch', 'off'));
}

/** True for a direct googleapis.com media download, as opposed to the proxy. */
export const isDirect = (c: DriveCall) => c.kind === 'media' && c.url.startsWith('https://www.googleapis.com/');

/**
 * Seeds the token the app expects, so tests never touch Google's consent
 * screen. Must run before the Blazor bootstrap reads sessionStorage.
 */
export async function seedToken(page: Page, token = FAKE_TOKEN): Promise<void> {
  await page.addInitScript((t) => {
    sessionStorage.setItem('access_token', t as string);
  }, token);
}
