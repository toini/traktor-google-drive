import { test, expect, type Page, type Route } from '@playwright/test';
import { mockDrive, seedToken, makeWav, isDirect, FAKE_TOKEN, type DriveCall } from './drive-mock';

/**
 * Whole-file download for local playback (prefetch.js), and robust streaming
 * for what is too big to download. "On a train the audio breaks up": the
 * current and next track are fetched whole, straight from Drive, and play from
 * memory once complete; a set over the cap streams through the proxy and
 * reconnects after a drop by itself.
 */

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';  // .wav tracks
const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62'; // Z11-3: a 2-hour .mp3, over the cap
const GIG = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f66';
const RENEWED = 'renewed-access-token';

const playButtons = (page: Page) => page.locator('#playlist-table tbody .play-button');
const title = (page: Page) => page.locator('.player-title');

const audio = (page: Page) =>
  page.evaluate(async () => {
    const a = (await import('/player.js')).element();
    return { current: a?.currentTime ?? -1, paused: a?.paused ?? true, src: a?.src ?? '' };
  });

const bufferState = (page: Page) =>
  page.evaluate(async () => (await import('/player.js')).bufferState());

const seek = (page: Page, seconds: number) =>
  page.evaluate(async (s) => (await import('/player.js')).seek(s), seconds);

const directFor = (calls: DriveCall[], id: string) => calls.filter((c) => isDirect(c) && c.url.includes(`/files/${id}?`));

/** Every route to Drive or the proxy fails, as in a tunnel. Returns the undo. */
async function dropNetwork(page: Page, match: (u: URL) => boolean = () => true) {
  const handler = (route: Route) => route.abort('internetdisconnected');
  const predicate = (u: URL) => (u.hostname === 'www.googleapis.com' || u.pathname.startsWith('/api/proxy/')) && match(u);
  await page.route(predicate, handler);
  return () => page.unroute(predicate, handler);
}

async function expectPlayingPast(page: Page, seconds: number, timeout = 20_000) {
  await expect.poll(async () => (await audio(page)).current, { timeout }).toBeGreaterThan(seconds);
  expect((await audio(page)).paused).toBe(false);
}

test.describe('whole-file download', () => {
  test('downloads the playing track from Drive and plays on through a network drop', async ({ page, context }) => {
    const calls = await mockDrive(page, { wavSeconds: 60 });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const first = playButtons(page).first();
    const id = (await first.getAttribute('data-drive-file-id'))!;
    await first.click();

    // Downloaded whole, straight from googleapis.com with the token as a header.
    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'cached');
    const direct = directFor(calls, id);
    expect(direct.length).toBeGreaterThan(0);
    expect(direct.every((c) => c.token === FAKE_TOKEN && c.url.includes('alt=media'))).toBe(true);
    await expect.poll(async () => (await audio(page)).src).toMatch(/^blob:/);
    // Offline also stops the app's own scripts loading, WaveSurfer among them.
    await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');

    // The tunnel. Nothing reaches Drive or the proxy any more.
    await dropNetwork(page);
    await context.setOffline(true);
    const before = calls.length;
    const at = (await audio(page)).current;
    await expectPlayingPast(page, at + 2);

    // Even a seek far ahead plays: the whole file is here.
    await seek(page, 50);
    await expectPlayingPast(page, 51);
    expect(calls.length).toBe(before);
    await expect(page.locator('[data-network-status]')).toHaveCount(0);
    await expect(page.locator('.error-banner')).toHaveCount(0);
    await context.setOffline(false);
  });

  test('prefetches the next track, and Next plays it with no network at all', async ({ page }) => {
    const calls = await mockDrive(page, { wavSeconds: 30 });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const nextId = (await playButtons(page).nth(1).getAttribute('data-drive-file-id'))!;
    await playButtons(page).first().click();
    await expect(title(page)).not.toHaveText('');
    const firstTitle = (await title(page).textContent())!;

    await expect(page.locator('[data-next-ready]')).toBeVisible();
    expect(directFor(calls, nextId).length).toBeGreaterThan(0);

    await dropNetwork(page);
    const before = calls.length;
    await page.getByRole('button', { name: 'Next track' }).click();

    await expect(title(page)).not.toHaveText(firstTitle);
    await expect.poll(async () => (await bufferState(page)).source).toBe('cache');
    await expect.poll(async () => (await audio(page)).src).toMatch(/^blob:/);
    await expectPlayingPast(page, 1);
    // The waveform reads the downloaded copy too.
    await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');
    expect(calls.slice(before)).toEqual([]);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('a seek while the download runs streams from there, then moves to memory at the same place', async ({ page }) => {
    await mockDrive(page, { wavSeconds: 60 });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const id = (await playButtons(page).first().getAttribute('data-drive-file-id'))!;
    let release!: () => void;
    const held = new Promise<void>((r) => { release = r; });
    await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === `/drive/v3/files/${id}`,
      async (route) => { await held; return route.fallback(); });
    await playButtons(page).first().click();

    await expectPlayingPast(page, 0.3);
    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'downloading');
    expect((await bufferState(page)).source).toBe('stream');
    await seek(page, 40);
    await expectPlayingPast(page, 40.3);

    release();
    await expect.poll(async () => (await bufferState(page)).source).toBe('cache');
    const { current } = await audio(page);
    expect(current).toBeGreaterThan(40);
    expect(current).toBeLessThan(50);
    await expectPlayingPast(page, current + 0.5);
  });

  test('a dropped download resumes from the byte it reached, by Range', async ({ page }) => {
    const seconds = 60;
    const wav = makeWav(seconds);
    const firstPart = 256 * 1024;
    const calls = await mockDrive(page, { wavSeconds: seconds });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const id = (await playButtons(page).first().getAttribute('data-drive-file-id'))!;

    // First request: the connection dies after 256 KB. The next one: reset
    // outright. After that, Drive as usual.
    let n = 0;
    await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === `/drive/v3/files/${id}`, (route) => {
      n++;
      if (n === 1) {
        calls.push({ kind: 'media', url: route.request().url(), status: 200 });
        return route.fulfill({
          status: 200,
          headers: {
            'content-type': 'audio/wav',
            'content-length': String(wav.length),
          },
          body: wav.subarray(0, firstPart),
        });
      }
      if (n === 2) return route.abort('connectionreset');
      return route.fallback();
    });
    await playButtons(page).first().click();

    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'cached', { timeout: 30_000 });
    const resumed = directFor(calls, id).filter((c) => c.range);
    expect(resumed.length).toBeGreaterThan(0);
    // Picked up where it stopped, never from the top again.
    expect(resumed.every((c) => c.range === `bytes=${firstPart}-`)).toBe(true);
    const entry = (await bufferState(page)).cache.find((e: { id: string }) => e.id === id);
    expect(entry.size).toBe(wav.length);
    await expectPlayingPast(page, 1);
  });

  test('a token renewed mid-download carries the download on', async ({ page }) => {
    const seconds = 60;
    const wav = makeWav(seconds);
    const firstPart = 256 * 1024;
    const calls = await mockDrive(page, { wavSeconds: seconds });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const id = (await playButtons(page).first().getAttribute('data-drive-file-id'))!;

    // The first part arrives with the old token; then Drive stops taking it.
    // (Only for the download: the proxy stream has the whole short file by then.)
    let first = true;
    await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === `/drive/v3/files/${id}`, async (route) => {
      const token = (await route.request().allHeaders())['authorization']?.replace(/^Bearer /, '') ?? null;
      if (!first && token === FAKE_TOKEN) {
        calls.push({ kind: 'media', url: route.request().url(), token, status: 401 });
        return route.fulfill({ status: 401, body: '' });
      }
      if (!first) return route.fallback();
      first = false;
      return route.fulfill({
        status: 200,
        headers: {
          'content-type': 'audio/wav',
          'content-length': String(wav.length),
        },
        body: wav.subarray(0, firstPart),
      });
    });
    await playButtons(page).first().click();

    await expect.poll(() => directFor(calls, id).some((c) => c.status === 401 && c.token === FAKE_TOKEN)).toBe(true);
    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'downloading');

    // What auth.js stores when its renewal lands.
    await page.evaluate((t) => sessionStorage.setItem('access_token', t), RENEWED);

    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'cached');
    const renewed = directFor(calls, id).filter((c) => c.token === RENEWED);
    expect(renewed.some((c) => c.status === 206 && c.range === `bytes=${firstPart}-`)).toBe(true);
    await expectPlayingPast(page, 1);
    await expect(page.locator('[data-player-signin]')).toHaveCount(0);
  });
});

/**
 * Fake Google Identity Services, as in token-expiry.spec.ts: a token request is
 * a popup, refused unless a real pointer press happened in the last 5 s.
 * Counts every request, so a test can see who asked for a renewal.
 */
async function fakeGis(page: Page, next: string) {
  await page.route('https://accounts.google.com/**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/javascript', body: '' }));
  await page.addInitScript((next) => {
    let lastPress = -Infinity;
    addEventListener('pointerdown', (e) => { if (e.isTrusted) lastPress = performance.now(); }, true);
    const gis = { calls: 0, active: () => performance.now() - lastPress < 5000 };
    (window as any).__gis = gis;
    (window as any).google = {
      accounts: {
        oauth2: {
          initTokenClient: (cfg: any) => ({
            requestAccessToken: () => {
              gis.calls++;
              if (!gis.active()) {
                setTimeout(() => cfg.error_callback?.({ type: 'popup_failed_to_open' }), 10);
                return;
              }
              setTimeout(() => cfg.callback({ access_token: next, expires_in: 3600 }), 50);
            },
          }),
        },
      },
    };
  }, next);
}

const gisCalls = (page: Page) => page.evaluate(() => (window as any).__gis.calls as number);

test.describe('sign-in prompts with the cache on', () => {
  test('a download Drive refuses never prompts or asks Google itself', async ({ page }) => {
    const seconds = 60;
    const calls = await mockDrive(page, { wavSeconds: seconds });
    await fakeGis(page, RENEWED);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const id = (await playButtons(page).first().getAttribute('data-drive-file-id'))!;

    // Drive takes the old token for the stream but refuses it for the download.
    await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === `/drive/v3/files/${id}`, async (route) => {
      const token = (await route.request().allHeaders())['authorization']?.replace(/^Bearer /, '') ?? null;
      if (token !== FAKE_TOKEN) return route.fallback();
      calls.push({ kind: 'media', url: route.request().url(), token, status: 401 });
      return route.fulfill({ status: 401, body: '' });
    });
    await playButtons(page).first().click();
    await expect.poll(() => directFor(calls, id).some((c) => c.status === 401)).toBe(true);
    // The download waits for a different token rather than retrying this one.
    await expectPlayingPast(page, 3);
    expect(directFor(calls, id).filter((c) => c.status === 401)).toHaveLength(1);

    // Refused, and still: no prompt, no popup, playback goes on.
    expect(await gisCalls(page)).toBe(0);
    await expect(page.locator('[data-player-signin]')).toHaveCount(0);
    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'downloading');
  });

  // A set too big for the cache streams either way, so the cache must change
  // nothing about when the sign-in appears or how often Google is asked.
  for (const cache of ['on', 'off'] as const) {
    test(`a streamed set that hits an expired token prompts exactly as before (cache ${cache})`, async ({ page }) => {
      let accepted = true;
      const calls = await mockDrive(page, { wavSeconds: 1200, maxSliceBytes: 64 * 1024, acceptToken: () => accepted });
      await fakeGis(page, RENEWED);
      if (cache === 'off') await page.addInitScript(() => localStorage.setItem('player:prefetch', 'off'));
      await seedToken(page);
      await page.goto(`/playlist/${RECORDED}`);
      await page.locator('#playlist-table tbody tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.play-button').click();
      await expectPlayingPast(page, 0.5);
      // The click's activation runs out, as it would an hour into a set.
      await expect.poll(() => page.evaluate(() => (window as any).__gis.active()), { timeout: 10_000 }).toBe(false);

      accepted = false;
      await seek(page, 1000);

      await expect(page.locator('[data-player-signin-button]')).toBeVisible();
      // One silent renewal try, the player's own.
      expect(await gisCalls(page)).toBe(1);
      expect(directFor(calls, 'drive-z11-3')).toHaveLength(0);

      // The sign-in continues from the same place.
      accepted = true;
      await page.locator('[data-player-signin-button]').click();
      await expectPlayingPast(page, 1000.5);
      expect((await audio(page)).current).toBeLessThan(1010);
      await expect(page.locator('[data-player-signin]')).toHaveCount(0);
    });
  }

  test('a downloaded track plays on past an expired token, with no prompt and no popup', async ({ page }) => {
    let accepted = true;
    await mockDrive(page, { wavSeconds: 60, acceptToken: () => accepted });
    await fakeGis(page, RENEWED);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await playButtons(page).first().click();
    await expect.poll(async () => (await bufferState(page)).source).toBe('cache');
    await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');

    accepted = false;
    await seek(page, 50);
    await expectPlayingPast(page, 51);
    await expect(page.locator('[data-player-signin]')).toHaveCount(0);
    expect(await gisCalls(page)).toBe(0);
  });
});

test.describe('streaming a file too big to download', () => {
  test('streams through the proxy and reconnects by itself after a drop', async ({ page }) => {
    const calls = await mockDrive(page, { wavSeconds: 300, maxSliceBytes: 256 * 1024 });
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);
    await page.locator('#playlist-table tbody tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.play-button').click();

    await expectPlayingPast(page, 0.3);
    await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'stream');
    expect(directFor(calls, 'drive-z11-3')).toHaveLength(0);

    // Into the tunnel, and far ahead, where nothing is buffered yet.
    const restore = await dropNetwork(page);
    await seek(page, 250);
    await expect(page.locator('[data-network-status="reconnecting"]')).toBeVisible();
    await expect(page.locator('[data-network-status]')).toContainText('reconnecting');

    // Out of it. No click: it picks up at the same place.
    await restore();
    await expectPlayingPast(page, 250.5, 30_000);
    expect((await audio(page)).current).toBeLessThan(262);
    await expect(page.locator('[data-network-status]')).toHaveCount(0);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });
});

test.describe('gig preview from the cache', () => {
  test('the prefetched next track still plays only its preview range', async ({ page }) => {
    // At a tenth of the speed, so the next track is downloaded well before it starts.
    await page.addInitScript(() => {
      const play = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function (this: HTMLMediaElement) {
        this.playbackRate = 0.1;
        return play.call(this);
      };
    });
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await page.locator('[data-preview-set]').click();
    await expect(title(page)).toContainText('Gig One');
    await expect(page.locator('[data-next-ready]')).toBeVisible();

    // Gig Two, from memory: load cue 0 to the end of its loop at 0.6 s.
    await expect(title(page)).toContainText('Gig Two', { timeout: 30_000 });
    expect((await bufferState(page)).source).toBe('cache');
    const range = page.locator('[data-preview-range]');
    await expect(range).toHaveAttribute('data-end', '0.6');

    let last = 0;
    await expect.poll(async () => {
      const t = await title(page).textContent();
      if (t?.includes('Gig Two')) last = Math.max(last, (await audio(page)).current);
      return t;
    }, { timeout: 30_000, intervals: [100] }).toContain('Gig Three');
    // Advanced at the bound, not at the end of the 2-second file.
    expect(last).toBeLessThan(1.0);
    // Gig Three starts at its load cue (0.4 s).
    await expect.poll(async () => (await audio(page)).current).toBeGreaterThanOrEqual(0.4);
  });
});
