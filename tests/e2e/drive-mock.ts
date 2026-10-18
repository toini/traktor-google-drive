import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import type { Page, Route } from '@playwright/test';

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
  // Gig Night. The separator's silent file is in Drive and must still never
  // be requested; Cafe Noir is stored NFC while the collection has it NFD.
  { id: 'drive-silent', name: 'silent.wav', mimeType: 'audio/wav', parents: ['folder-gig'] },
  { id: 'drive-cafe', name: 'Caf\u00e9 Noir.wav', mimeType: 'audio/wav', parents: ['folder-gig'] },
  { id: 'drive-usb', name: 'usb-anthem.mp3', mimeType: 'audio/mpeg', parents: ['folder-gig'] },
];

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
  kind: 'collection' | 'query' | 'media' | 'folder' | 'meta';
  url: string;
  range?: string;
  /** Media only: the token the request carried (proxy cookie or ?token=), and the status served. */
  token?: string | null;
  status?: number;
};

/** The cookie auth.js keeps for the audio proxy, so media URLs carry no token. */
export const PROXY_COOKIE = 'drive_token';

/** The Drive token a proxy request carries: the cookie first, else the legacy ?token=. */
export function proxyToken(url: string, cookieHeader: string | undefined): string | null {
  const cookie = (cookieHeader ?? '')
    .split(';')
    .map((c) => c.trim())
    .find((c) => c.startsWith(`${PROXY_COOKIE}=`));
  if (cookie) return decodeURIComponent(cookie.slice(PROXY_COOKIE.length + 1));
  return new URL(url).searchParams.get('token');
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
  /** Override the NML body. */
  nml?: string;
  /** RFC3339 modifiedTime reported for the collection file. */
  collectionModifiedTime?: string;
};

/**
 * Intercepts every outbound call the app makes: the two googleapis.com
 * endpoints and the same-origin audio proxy. Returns the call log so tests can
 * assert on request *shape* (e.g. that the token stopped being a query param,
 * or that a long playlist got chunked into several queries).
 */
export async function mockDrive(page: Page, opts: MockOptions = {}): Promise<DriveCall[]> {
  const calls: DriveCall[] = [];
  const nml = opts.nml ?? readFileSync(join(here, 'fixtures', 'collection.nml'), 'utf8');
  const wav = makeWav(opts.wavSeconds);

  const serveMedia = async (route: Route, url: string) => {
    const headers = await route.request().allHeaders();
    const call: DriveCall = { kind: 'media', url, range: headers['range'], token: proxyToken(url, headers['cookie']) };
    calls.push(call);
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
    (route) => {
      const url = route.request().url();
      const u = new URL(url);

      // The Cast sender SDK. Blocked rather than stubbed so no test silently
      // depends on the network; cast-mock.ts installs a working fake instead.
      if (u.hostname === 'www.gstatic.com') return route.abort();

      // Same-origin audio proxy.
      if (u.pathname.startsWith('/api/proxy/drive/')) return serveMedia(route, url);

      // Drive: list files matching a name query.
      if (u.pathname === '/drive/v3/files') {
        calls.push({ kind: 'query', url });
        const q = u.searchParams.get('q') ?? '';

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
                    modifiedTime: '2026-08-16T10:00:00.000Z',
                  },
                ];
          return route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ files }),
          });
        }

        const files = DRIVE_FILES.filter((f) => q.includes(`'${f.name}'`));
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
        const name = DRIVE_FOLDERS[id];
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
        return route.fulfill({ status: 200, contentType: 'text/xml', body: nml });
      }
      return serveMedia(route, url);
    },
  );

  return calls;
}

/**
 * Seeds the token the app expects, so tests never touch Google's consent
 * screen. Must run before the Blazor bootstrap reads sessionStorage.
 */
export async function seedToken(page: Page, token = FAKE_TOKEN): Promise<void> {
  await page.addInitScript((t) => {
    sessionStorage.setItem('access_token', t as string);
  }, token);
}
