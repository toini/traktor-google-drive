import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import type { Page, Route } from '@playwright/test';

const here = dirname(fileURLToPath(import.meta.url));

/** The H16-like collection for Discover (fixtures/discover-collection.nml). */
export const discoverNml = () => readFileSync(join(here, 'fixtures', 'discover-collection.nml'), 'utf8');

export const H16_UUID = '16161616161616161616161616161616';
export const CARNIVOR = 'https://carnivorrecords.bandcamp.com';

type Track = { trackId: number; number: number; title: string; artist: string; duration: number; slug?: string };

function release(slug: string, title: string, artist: string, date: string, tracks: Track[]) {
  const url = `${CARNIVOR}/album/${slug}`;
  return {
    source: 'bandcamp', url, type: 'album', id: tracks[0].trackId * 7, title, artist,
    label: 'Carnivor Records', labelUrl: CARNIVOR, releaseDate: `${date}T00:00:00+00:00`,
    tags: ['techno', 'peak time techno'],
    tracks: tracks.map(({ slug, ...t }) => ({ ...t, trackUrl: `${CARNIVOR}/track/${slug ?? t.title.toLowerCase().replace(/\W+/g, '-')}`, isrc: null, playable: true })),
    albumUrl: null, streamingLimit: 3,
  };
}

/**
 * Carnivor Records as the server would describe it, from the recorded pages of
 * spike S1 (ADR 20261025-54). Grid order is newest first, as on Bandcamp.
 * "1616 (Original Mix)" is in the fixture collection, so it must never queue.
 */
export const RELEASES = {
  newer: release('feeding-generation-z-ii', 'Feeding Generation Z II', 'Various', '2024-03-01', [
    { trackId: 501, number: 1, title: 'Hunger', artist: 'Kristonov', duration: 380 },
    { trackId: 502, number: 2, title: 'Machine Choir', artist: 'Strasse Killer', duration: 402 },
    // The two tracks of Toni's starter list (wwwroot/discover/starter.json).
    { trackId: 503, number: 3, title: 'Satanas', artist: 'Fixture Artist A', duration: 395, slug: 'satanas' },
    { trackId: 504, number: 4, title: "I'm No Immigrant", artist: 'Fixture Artist B', duration: 371, slug: 'im-no-immigrant' },
  ]),
  internalMotion: release('internal-motion', 'Internal Motion', 'obwigszyh', '2020-08-19', [
    { trackId: 1402866115, number: 1, title: '1616 (Original Mix)', artist: 'obwigszyh', duration: 358 },
    { trackId: 776289323, number: 2, title: 'Internal Motion (Original Mix)', artist: 'obwigszyh', duration: 390 },
    { trackId: 1945791144, number: 3, title: 'Burnt Side (Original Mix)', artist: 'obwigszyh', duration: 390 },
  ]),
};

/**
 * A silent MPEG-1 Layer III stream: mono, 44.1 kHz, 128 kbps, every frame's side
 * information zeroed, so a decoder outputs silence. Generated rather than committed,
 * like drive-mock's WAV. 1152 samples per 417-byte frame.
 */
export function makeSilentMp3(seconds = 4): Buffer {
  const frames = Math.ceil((seconds * 44100) / 1152);
  const frame = Buffer.alloc(417);
  frame.set([0xff, 0xfb, 0x90, 0xc0]);
  return Buffer.concat(Array.from({ length: frames }, () => frame));
}

export type DiscoverCalls = {
  /** Requests to our audio endpoint (redirects and probes). */
  audio: string[];
  /** Audio bodies served (not probes), by track id. */
  cdn: string[];
  lists: string[];
  releases: string[];
  appDataCreates: { name: string; body: string }[];
  appDataUpdates: { id: string; body: string }[];
  storeRequests: string[];
};

export type DiscoverMockOptions = {
  /** jsonl files already in appDataFolder, by name. */
  appData?: Record<string, string>;
  /** Answer appData calls with 403 insufficient scope, as a pre-Discover token gets. */
  appDataScopeMissing?: boolean;
  /** Serve the real wwwroot/discover/starter.json. Otherwise an empty one, so other tests start blank. */
  realStarter?: boolean;
  /** Length of each preview's silent MP3. */
  audioSeconds?: number;
};

/**
 * Mocks the server's /api/discover endpoints and the Drive appDataFolder. Must
 * run AFTER mockDrive: Playwright tries the most recently added route first.
 * Any request to a real store fails the test (no live calls in e2e, ADR §12).
 */
export async function mockDiscover(page: Page, opts: DiscoverMockOptions = {}): Promise<DiscoverCalls> {
  const calls: DiscoverCalls = { audio: [], cdn: [], lists: [], releases: [], appDataCreates: [], appDataUpdates: [], storeRequests: [] };
  const mp3 = makeSilentMp3(opts.audioSeconds ?? 4);
  const serveMp3 = (route: Route) => {
    const range = route.request().headers()['range'];
    const m = range?.match(/bytes=(\d+)-(\d*)/);
    if (!m) {
      return route.fulfill({ status: 200, headers: { 'content-type': 'audio/mpeg', 'accept-ranges': 'bytes', 'content-length': String(mp3.length) }, body: mp3 });
    }
    const start = Number(m[1]);
    const end = m[2] ? Math.min(Number(m[2]), mp3.length - 1) : mp3.length - 1;
    return route.fulfill({
      status: 206,
      headers: { 'content-type': 'audio/mpeg', 'accept-ranges': 'bytes', 'content-length': String(end - start + 1), 'content-range': `bytes ${start}-${end}/${mp3.length}` },
      body: mp3.subarray(start, end + 1),
    });
  };
  const files = new Map<string, { id: string; body: string }>();
  for (const [name, body] of Object.entries(opts.appData ?? {})) files.set(name, { id: `app-${name}`, body });

  await page.route(/bandcamp\.com|bcbits\.com|beatport\.com|discogs\.com/, (route) => {
    calls.storeRequests.push(route.request().url());
    return route.abort();
  });

  // The audio endpoint. The real server answers 302 to the store CDN (covered by
  // the xUnit endpoint tests); Playwright cannot hand a media element a fulfilled
  // redirect (Chrome reports a format error), so here the endpoint serves what
  // the CDN would: a silent MP3, with ranges and no CORS headers.
  await page.route('**/api/proxy/discover/bandcamp/**', (route) => {
    const u = new URL(route.request().url());
    calls.audio.push(u.pathname + u.search);
    if (!(route.request().headers()['cookie'] ?? '').includes('drive_token=')) return route.fulfill({ status: 401, body: '' });
    if (u.searchParams.get('probe') === '1') return route.fulfill({ status: 200, body: '' });
    calls.cdn.push(u.pathname.split('/').pop()!);
    return serveMp3(route);
  });

  if (!opts.realStarter) {
    await page.route('**/discover/starter.json', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ batches: [] }) }));
  }

  await page.route('**/api/discover/**', async (route) => {
    const u = new URL(route.request().url());
    const target = u.searchParams.get('url') ?? '';
    const auth = route.request().headers()['authorization'] ?? '';
    if (!auth.startsWith('Bearer ')) return route.fulfill({ status: 401, body: '' });

    if (u.pathname === '/api/discover/bandcamp/releases') {
      calls.lists.push(target);
      if (!target.includes('carnivorrecords')) return route.fulfill({ status: 404, body: '{}' });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          source: 'bandcamp', page: `${CARNIVOR}/music`,
          releases: [RELEASES.newer, RELEASES.internalMotion].map((r) => ({ url: r.url, type: 'album', id: r.id, title: r.title, artist: r.artist })),
        }),
      });
    }
    if (u.pathname === '/api/discover/bandcamp/release') {
      calls.releases.push(target);
      const r = Object.values(RELEASES).find((x) => x.url === target);
      return r
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(r) })
        : route.fulfill({ status: 404, body: '{}' });
    }
    return route.fulfill({ status: 404, body: '' });
  });

  const scopeError = (route: Route) =>
    route.fulfill({
      status: 403, contentType: 'application/json',
      body: JSON.stringify({ error: { code: 403, status: 'PERMISSION_DENIED', message: 'Request had insufficient authentication scopes.', details: [{ reason: 'ACCESS_TOKEN_SCOPE_INSUFFICIENT' }] } }),
    });

  await page.route(
    (url) => url.hostname === 'www.googleapis.com'
      && (url.searchParams.get('spaces') === 'appDataFolder' || url.pathname.startsWith('/upload/drive/v3/files')
        || [...files.values()].some((f) => url.pathname.endsWith(`/files/${f.id}`))),
    async (route) => {
      if (opts.appDataScopeMissing) return scopeError(route);
      const req = route.request();
      const u = new URL(req.url());

      if (req.method() === 'GET' && u.searchParams.get('spaces') === 'appDataFolder') {
        return route.fulfill({
          status: 200, contentType: 'application/json',
          body: JSON.stringify({ files: [...files.entries()].map(([name, f]) => ({ id: f.id, name, modifiedTime: '2026-10-25T10:00:00Z' })) }),
        });
      }
      if (req.method() === 'GET') {
        const f = [...files.values()].find((x) => u.pathname.endsWith(`/files/${x.id}`));
        return f ? route.fulfill({ status: 200, body: f.body }) : route.fulfill({ status: 404, body: '' });
      }
      if (req.method() === 'POST') {
        // multipart/related: the metadata part names the file, the second part is the jsonl.
        const raw = req.postData() ?? '';
        const name = /"name":"([^"]+)"/.exec(raw)?.[1] ?? 'unknown';
        const body = raw.split(/\r?\n\r?\n/).slice(2).join('\n\n').replace(/\r?\n--[^\n]*--\s*$/, '');
        const id = `app-${name}`;
        files.set(name, { id, body });
        calls.appDataCreates.push({ name, body });
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id }) });
      }
      if (req.method() === 'PATCH') {
        const id = u.pathname.split('/').pop() ?? '';
        const body = req.postData() ?? '';
        for (const [name, f] of files) if (f.id === id) files.set(name, { id, body });
        calls.appDataUpdates.push({ id, body });
        return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id }) });
      }
      return route.fulfill({ status: 405, body: '' });
    },
  );

  return calls;
}
