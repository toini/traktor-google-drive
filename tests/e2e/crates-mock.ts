import type { Page } from '@playwright/test';

// Crates (ADR 20261025-54, "Crates"): the server's Discogs and SoundCloud
// endpoints, the Bandcamp pages the P crate's mocked finds live on, and
// SoundCloud's widget. Must run AFTER mockDiscover: Playwright tries the most
// recently added route first, and anything not handled here falls back to it.
// Real store and widget hosts are aborted and recorded, so a test can assert none was called.

export const CRATE_ID = 'looney-boon-hug-halloween-2026';
export const PARVATI = 'https://beatspace-parvati.bandcamp.com';
export const MUIMINA_ALBUM = 'https://muiminasounds.bandcamp.com/album/abyss';
export const NOBOT_ALBUM = 'https://nobotmusic.bandcamp.com/album/the-future';
export const CODEX_SC = 'https://soundcloud.com/codex-spiralis/something-different';
/** Bioport's page, from the shipped research batch (2026-11-01.2). */
export const BIOPORT_TRACK = 'https://muscaria1.bandcamp.com/track/bioport';
export const RESEARCH_BATCH = '2026-11-01.2';

/** The key the app gives a crate item: "crate:<id>:<artist slug>--<title slug>". */
export const itemKey = (artist: string, title: string) => {
  const slug = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/&/g, ' and ').toLowerCase()
    .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return `crate:${CRATE_ID}:${slug(artist)}--${slug(title)}`;
};

type Track = { trackId: number; title: string; artist: string };

function release(url: string, title: string, label: string, tracks: Track[]) {
  const base = url.split('/album/')[0];
  return {
    source: 'bandcamp', url, type: 'album', id: tracks[0].trackId * 3, title, artist: tracks[0].artist,
    label, labelUrl: base, releaseDate: '2024-02-02T00:00:00+00:00', tags: ['psytrance', 'forest'],
    tracks: tracks.map((t, i) => ({
      ...t, number: i + 1, duration: 420, isrc: null, playable: true,
      trackUrl: `${base}/track/${t.title.toLowerCase().replace(/\W+/g, '-')}`,
    })),
    albumUrl: null, streamingLimit: 3,
  };
}

export const CRATE_RELEASES = [
  release(`${PARVATI}/album/testimony-20`, 'Testimony 20', 'Parvati Records', [
    { trackId: 3000, title: 'Tropical Fog', artist: 'Tengri' },
    { trackId: 3001, title: 'Patch Hell', artist: 'Tengri & Jahbo' },
  ]),
  release(`${PARVATI}/album/20-years-journey-of-discovery`, '20 Years Journey Of Discovery', 'Parvati Records', [
    { trackId: 3002, title: 'Shesharka', artist: 'Atriohm' },
  ]),
  release(NOBOT_ALBUM, 'The Future', 'Nobot Music', [{ trackId: 3003, title: 'The Future', artist: 'Nobot' }]),
  release(MUIMINA_ALBUM, 'Abyss', 'Muimina Sounds', [
    { trackId: 3010, title: 'Wail', artist: 'Into The Screaming Abyss' },
    { trackId: 3004, title: 'Ululation', artist: 'Into The Screaming Abyss' },
  ]),
  { ...release('https://muscaria1.bandcamp.com/album/bioport', 'Blue Sangoma Sounds', 'Blue Hour Sounds', [
    { trackId: 3020, title: 'Bioport', artist: 'Ianuaria & Muscaria' },
  ]), url: BIOPORT_TRACK },
];

const LISTS: Record<string, string[]> = {
  'beatspace-parvati.bandcamp.com': [`${PARVATI}/album/testimony-20`, `${PARVATI}/album/20-years-journey-of-discovery`],
};

const parvati = (title: string, release: string, id: number) => ({
  artist: '', title, ambiguous: false,
  candidates: [{ id, artist: 'Tengri', title: release, year: '2024', labels: ['Parvati Records'], catno: null, formats: ['File'], webUrl: `https://www.discogs.com/release/${id}`, score: 5 }],
  best: { id, title: release, artists: 'Tengri', labels: ['Parvati Records'], released: '2024-02-02', webUrl: `https://www.discogs.com/release/${id}`, trackFound: true, trackTitle: title, bandcampUrls: [`${PARVATI}/music`] },
});

/** Discogs lookups by title (and "#<release>" for Toni's pick). Anything else: nothing on Discogs. */
const DISCOGS: Record<string, unknown> = {
  'Patch Hell': parvati('Patch Hell', 'Testimony 20', 29681248),
  Shesharka: parvati('Shesharka', '20 Years Journey Of Discovery', 29240617),
  'The Future': {
    artist: 'Nobot', title: 'The Future', ambiguous: true, best: null,
    candidates: [
      { id: 11, artist: 'Nobot', title: 'The Future', year: '2024', labels: ['Nobot Music'], catno: null, formats: ['File'], webUrl: 'https://www.discogs.com/release/11', score: 6 },
      { id: 12, artist: 'Nobot (2)', title: 'Futures', year: '2019', labels: ['Other Label'], catno: null, formats: ['File'], webUrl: 'https://www.discogs.com/release/12', score: 6 },
    ],
  },
  'The Future#11': {
    artist: 'Nobot', title: 'The Future', ambiguous: false, candidates: [],
    best: { id: 11, title: 'The Future', artists: 'Nobot', labels: ['Nobot Music'], released: '2024', webUrl: 'https://www.discogs.com/release/11', trackFound: true, trackTitle: 'The Future', bandcampUrls: [NOBOT_ALBUM] },
  },
};

const SOUNDCLOUD: Record<string, { url: string; title: string; author: string; trackId: number; bandcampUrls: string[] }> = {
  'Something Different': { url: CODEX_SC, title: 'Something Different', author: 'Codex Spiralis', trackId: 1853600883, bandcampUrls: [] },
};

export type CrateCalls = { discogs: string[]; soundcloud: string[]; bandcamp: string[]; storeRequests: string[] };

/**
 * A stand-in for SoundCloud's Widget API (w.soundcloud.com/player/api.js): records
 * calls on window.__sc and lets a test fire the widget's events with window.__scFire.
 */
const FAKE_WIDGET_API = `
window.__sc = { calls: [], handlers: {} };
window.__scFire = (name) => (window.__sc.handlers[name] || []).forEach((cb) => cb());
window.SC = {
  Widget: Object.assign(function (iframe) {
    window.__sc.calls.push('attach ' + iframe.src);
    return {
      bind(name, cb) { (window.__sc.handlers[name] = window.__sc.handlers[name] || []).push(cb); if (name === 'ready') setTimeout(cb, 10); },
      play() { window.__sc.calls.push('play'); window.__scFire('play'); },
      pause() { window.__sc.calls.push('pause'); window.__scFire('pause'); },
    };
  }, { Events: { READY: 'ready', PLAY: 'play', PAUSE: 'pause', FINISH: 'finish', ERROR: 'error' } }),
};`;

export type CrateMockOptions = {
  /** Keep the shipped research batch (buy links for the unresolved tracks). Off: the list starts unresolved. */
  research?: boolean;
  /** Switch SoundCloud on in this browser (DiscoverFeatures.SoundCloud). */
  soundcloud?: boolean;
  /** localStorage entries to set before the app starts. */
  storage?: Record<string, string>;
};

export async function mockCrates(page: Page, opts: CrateMockOptions = {}): Promise<CrateCalls> {
  const calls: CrateCalls = { discogs: [], soundcloud: [], bandcamp: [], storeRequests: [] };

  const storage = { ...(opts.storage ?? {}), ...(opts.soundcloud ? { 'discover.soundcloud': 'on' } : {}) };
  if (Object.keys(storage).length > 0) {
    await page.addInitScript((entries) => {
      for (const [k, v] of Object.entries(entries)) localStorage.setItem(k, v);
    }, storage);
  }

  // The real starter file (mockDiscover's realStarter), with or without the research batch.
  await page.route('**/discover/starter.json', async (route) => {
    const response = await route.fetch();
    const starter = await response.json();
    if (!opts.research) starter.batches = starter.batches.filter((b: { version: string }) => b.version !== RESEARCH_BATCH);
    return route.fulfill({ response, body: JSON.stringify(starter) });
  });

  // SoundCloud's own pages and API: never from the app (the widget below is the exception).
  await page.route(/soundcloud\.com/, (route) => {
    calls.storeRequests.push(route.request().url());
    return route.abort();
  });
  await page.route('https://w.soundcloud.com/player/api.js', (route) =>
    route.fulfill({ status: 200, contentType: 'application/javascript', body: FAKE_WIDGET_API }));
  await page.route(/^https:\/\/w\.soundcloud\.com\/player\/\?/, (route) =>
    route.fulfill({ status: 200, contentType: 'text/html', body: '<!doctype html><title>widget</title><p>SoundCloud widget</p>' }));

  await page.route('**/api/discover/**', async (route) => {
    const u = new URL(route.request().url());
    if (!(route.request().headers()['authorization'] ?? '').startsWith('Bearer ')) return route.fulfill({ status: 401, body: '' });

    if (u.pathname === '/api/discover/discogs/lookup') {
      const title = u.searchParams.get('title') ?? '';
      const release = u.searchParams.get('release');
      calls.discogs.push(title + (release ? `#${release}` : ''));
      const hit = DISCOGS[title + (release ? `#${release}` : '')];
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify(hit ?? { artist: u.searchParams.get('artist'), title, candidates: [], ambiguous: false, best: null }),
      });
    }
    if (u.pathname === '/api/discover/soundcloud/find') {
      const url = u.searchParams.get('url');
      const title = u.searchParams.get('title') ?? '';
      calls.soundcloud.push(url ?? title);
      const hit = url ? Object.values(SOUNDCLOUD).find((s) => s.url === url) : SOUNDCLOUD[title];
      return hit
        ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(hit) })
        : route.fulfill({ status: 404, contentType: 'application/json', body: '{"error":"Not at the uploader\'s or the artist\'s SoundCloud under that title"}' });
    }

    const target = u.searchParams.get('url') ?? '';
    if (u.pathname === '/api/discover/bandcamp/releases') {
      const host = new URL(target.startsWith('http') ? target : `https://${target}`).host;
      if (host.includes('carnivorrecords') || host.includes('darkersounds')) return route.fallback();
      calls.bandcamp.push('list ' + host);
      const list = LISTS[host];
      if (!list) return route.fulfill({ status: 404, contentType: 'application/json', body: '{"error":"Bandcamp has no such page"}' });
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify({
          source: 'bandcamp', page: `https://${host}/music`,
          releases: list.map((url) => CRATE_RELEASES.find((r) => r.url === url)!)
            .map((r) => ({ url: r.url, type: 'album', id: r.id, title: r.title, artist: r.artist })),
        }),
      });
    }
    if (u.pathname === '/api/discover/bandcamp/release') {
      const r = CRATE_RELEASES.find((x) => x.url === target);
      if (!r) return route.fallback();
      calls.bandcamp.push('release ' + target);
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(r) });
    }
    return route.fallback();
  });
  return calls;
}
