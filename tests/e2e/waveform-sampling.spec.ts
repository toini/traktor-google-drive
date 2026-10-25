import { test, expect, type Page, type Request } from '@playwright/test';
import { mockDrive, seedToken, type DriveCall } from './drive-mock';

/**
 * The WAV peak sampler (waveform.js). It used to fire 400 reads of 2 KB through
 * the proxy, 12 at a time, while the track was still loading, and a previous
 * track's sampler kept going after a track change. Now:
 *  - a file the player downloads whole is sampled from that copy, with no
 *    network reads of its own;
 *  - a file too big for that gets at most 80 reads of 32 KB, straight from
 *    Drive, and only once the audio can play;
 *  - a track change aborts the old file's reads.
 */

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';  // alpha.wav: ~43 MB by the NML, cached
const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62'; // C4: a 90-minute WAV (~950 MB), over the cap
const NET_READS_MAX = 80 + 1; // windows, plus the header

/** A sampler read: a closed byte range. The player's stream and download ask for open ones (bytes=N-). */
const isSamplerRead = (c: DriveCall) => c.kind === 'media' && /bytes=\d+-\d+$/.test(c.range ?? '');
const samplerReads = (calls: DriveCall[], id: string) => calls.filter((c) => isSamplerRead(c) && c.url.includes(id));

const waveformState = (page: Page) => page.locator('.waveform');

test('a WAV the player downloads is sampled from that copy, with no reads of its own', async ({ page }) => {
  const calls = await mockDrive(page, { wavSeconds: 60 });
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH}`);
  const row = page.locator('#playlist-table tbody .play-button').first();
  const id = (await row.getAttribute('data-drive-file-id'))!;

  // Hold the whole-file download, to see what the sampler does meanwhile.
  let release!: () => void;
  const held = new Promise<void>((r) => { release = r; });
  await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === `/drive/v3/files/${id}`,
    async (route) => { await held; return route.fallback(); });
  await row.click();

  await expect.poll(async () => page.evaluate(async () => (await import('/player.js')).position()?.current ?? 0))
    .toBeGreaterThan(0.5);
  await page.waitForTimeout(1_500);
  await expect(waveformState(page)).toHaveAttribute('data-waveform-state', 'loading');
  expect(samplerReads(calls, id)).toEqual([]);

  release();
  await expect(page.locator('.player-buffer')).toHaveAttribute('data-buffer-state', 'cached');
  await expect(waveformState(page)).toHaveAttribute('data-waveform-state', 'ready');
  // Not one range read, through the proxy or to Drive.
  expect(samplerReads(calls, id)).toEqual([]);
});

test('an over-cap WAV is sampled in a few large reads from Drive, and only once the audio can play', async ({ page }) => {
  const calls = await mockDrive(page, { wavSeconds: 300 });
  await seedToken(page);
  await page.goto(`/playlist/${RECORDED}`);

  // Hold the audio stream: until it answers, the element cannot play.
  let release!: () => void;
  const held = new Promise<void>((r) => { release = r; });
  await page.route((u) => u.pathname.startsWith('/api/proxy/drive/drive-c4-2026'),
    async (route) => { await held; return route.fallback(); });
  await page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();

  await page.waitForTimeout(2_000);
  expect(samplerReads(calls, 'drive-c4-2026')).toEqual([]);

  release();
  await expect(waveformState(page)).toHaveAttribute('data-waveform-state', 'ready', { timeout: 30_000 });
  const reads = samplerReads(calls, 'drive-c4-2026');
  expect(reads.length).toBeGreaterThan(10);
  expect(reads.length).toBeLessThanOrEqual(NET_READS_MAX);
  // Straight from Drive, with the token as a header: none through the proxy.
  expect(reads.every((c) => c.url.startsWith('https://www.googleapis.com/') && c.token)).toBe(true);
});

test('an over-cap WAV draws as reads land, and a restarted sampler reads only what is missing', async ({ page }) => {
  const calls = await mockDrive(page, { wavSeconds: 300 });
  await seedToken(page);
  // About a second per read, like Drive in prod: the picture has to fill in.
  await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === '/drive/v3/files/drive-c4-2026',
    async (route) => { await new Promise((r) => setTimeout(r, 500)); return route.fallback().catch(() => {}); });
  await page.goto(`/playlist/${RECORDED}`);
  const play = () => page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();
  const drawn = () => page.evaluate(async () => (await import('/waveform.js')).drawnPeaks());
  await play();

  // Partly drawn while still sampling, markers already up.
  await expect.poll(async () => (await drawn())?.have ?? 0, { timeout: 30_000 }).toBeGreaterThanOrEqual(20);
  await expect(waveformState(page)).toHaveAttribute('data-waveform-state', 'loading');
  const firstRun = samplerReads(calls, 'drive-c4-2026').length;
  expect(firstRun).toBeLessThan(NET_READS_MAX);

  // Gone mid-way (a reload), and back.
  await page.reload();
  const before = calls.length;
  await play();
  await expect(waveformState(page)).toHaveAttribute('data-waveform-state', 'ready', { timeout: 60_000 });
  expect((await drawn())).toMatchObject({ have: 80, total: 80 });

  // The second run read only the gap: at most the missing windows, plus the header.
  const secondRun = samplerReads(calls.slice(before), 'drive-c4-2026').length;
  expect(secondRun).toBeLessThan(NET_READS_MAX - 15);
});

test('changing track aborts the previous file\'s sampler reads', async ({ page }) => {
  await mockDrive(page, { wavSeconds: 300 });
  await seedToken(page);
  await page.goto(`/playlist/${RECORDED}`);

  // Slow sampler reads, so some are in flight when the track changes.
  await page.route((u) => u.hostname === 'www.googleapis.com' && u.pathname === '/drive/v3/files/drive-c4-2026',
    async (route) => { await new Promise((r) => setTimeout(r, 400)); return route.fallback().catch(() => {}); });
  const started: { at: number; req: Request }[] = [];
  const aborted: Request[] = [];
  const c4 = (r: Request) => r.url().includes('drive-c4-2026') && /bytes=\d+-\d+$/.test(r.headers()['range'] ?? '');
  page.on('request', (r) => { if (c4(r)) started.push({ at: Date.now(), req: r }); });
  page.on('requestfailed', (r) => { if (c4(r)) aborted.push(r); });

  await page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();
  await expect.poll(() => started.length, { timeout: 20_000 }).toBeGreaterThan(2);

  await page.locator('#playlist-table tbody tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.play-button').click();
  await expect(page.locator('.player-title')).toContainText('Z11-3');
  const switchedAt = Date.now();
  await page.waitForTimeout(3_000);

  // In-flight reads were cancelled, and no new ones started for the old file.
  expect(aborted.length).toBeGreaterThan(0);
  expect(started.filter((s) => s.at > switchedAt + 200)).toEqual([]);
  // A stale waveform is not reported as a failure.
  await expect(page.locator('.error-banner')).toHaveCount(0);
});
