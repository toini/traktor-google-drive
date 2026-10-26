import { test, expect, type Page, type Route } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * Near-gapless advance (player.js): the next track waits in a second, hidden
 * element, loaded and seeked to where it will start, and advancing swaps the
 * elements instead of loading a new source.
 *
 * Measured from the old track's `ended` (or its gig-preview end) to the new
 * one's `playing`. Locally that is ~12 ms primed, against 25–35 ms from the
 * cache and ~320 ms with 300 ms of network latency unprimed. The bound here is
 * loose for slow CI machines; the point is that it is not a load.
 */
const MAX_GAP_MS = 150;

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';
const GIG = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f66';

const playButtons = (page: Page) => page.locator('#playlist-table tbody .play-button');
const title = (page: Page) => page.locator('.player-title');

type Gap = { from: string; to: string; gapMs: number; primed: boolean; at: number };
const gaps = (page: Page) => page.evaluate(async () => (await import('/player.js')).gaps() as Gap[]);
const primedState = (page: Page) =>
  page.evaluate(async () => (await import('/player.js')).primedState() as { id: string; start: number; ready: boolean } | null);
const audio = (page: Page) =>
  page.evaluate(async () => {
    const a = (await import('/player.js')).element();
    return { current: a?.currentTime ?? -1, paused: a?.paused ?? true };
  });

async function dropNetwork(page: Page) {
  await page.route((u) => u.hostname === 'www.googleapis.com' || u.pathname.startsWith('/api/proxy/'),
    (route: Route) => route.abort('internetdisconnected'));
}

test('the next track starts within milliseconds of the last ending, with no network', async ({ page }) => {
  const calls = await mockDrive(page, { wavSeconds: 4 });
  await seedToken(page);
  await page.addInitScript(() => localStorage.setItem('player:continuous-play', '1'));
  await page.goto(`/playlist/${PSYTECH}`);
  const firstId = (await playButtons(page).nth(0).getAttribute('data-drive-file-id'))!;
  const nextId = (await playButtons(page).nth(1).getAttribute('data-drive-file-id'))!;
  await playButtons(page).first().click();

  await expect.poll(() => primedState(page)).toMatchObject({ id: nextId, ready: true });
  await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');
  await dropNetwork(page);
  const before = calls.length;

  await expect.poll(async () => (await gaps(page)).length, { timeout: 15_000 }).toBeGreaterThan(0);
  const [gap] = await gaps(page);
  expect(gap).toMatchObject({ from: firstId, to: nextId, primed: true });
  expect(gap.gapMs).toBeLessThan(MAX_GAP_MS);
  await expect.poll(async () => (await audio(page)).current).toBeGreaterThan(0.5);
  expect((await audio(page)).paused).toBe(false);
  expect(calls.slice(before)).toEqual([]);
  // The waveform follows the element that now plays.
  await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');
  await expect(page.locator('.error-banner')).toHaveCount(0);
});

test('gig preview advances on the primed element, already at the load cue', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${GIG}`);
  // Gig Exit is the one fixture track whose preview end comes from an exit loop
  // (31.5 s); the others' loops are entrance loops, so they play to their end.
  await page.locator('#playlist-table tbody tr', { hasText: 'Gig Exit' }).first().locator('.play-button').click();
  await expect(title(page)).toContainText('Gig Exit');
  await page.locator('[data-preview-set]').click();
  await expect(page.locator('[data-preview-range]')).toHaveAttribute('data-end', '31.5');

  // Gas Chamber's load cue is at 120 s: primed there while Gig Exit plays.
  await expect.poll(() => primedState(page), { timeout: 20_000 })
    .toMatchObject({ id: 'drive-gig-gas-chamber', start: 120, ready: true });

  // Up to just before the exit loop's end, so the advance is the preview bound,
  // not the 40-second file ending.
  await page.evaluate(async () => (await import('/player.js')).seek(31));
  await expect.poll(async () => (await gaps(page)).find((g) => g.to === 'drive-gig-gas-chamber'), { timeout: 20_000 })
    .toBeTruthy();
  const gap = (await gaps(page)).find((g) => g.to === 'drive-gig-gas-chamber')!;
  expect(gap.from).toBe('drive-gig-exit');
  expect(gap.primed).toBe(true);
  expect(gap.gapMs).toBeLessThan(MAX_GAP_MS);
  // It started at the cue, not at the top.
  expect(gap.at).toBeGreaterThanOrEqual(119.99);
  expect(gap.at).toBeLessThan(120.2);
  await expect(page.locator('[data-preview-chip]')).toContainText('6/6');
});

test('picking another row discards the primed track and plays the pick', async ({ page }) => {
  await mockDrive(page, { wavSeconds: 30 });
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH}`);
  const nextId = (await playButtons(page).nth(1).getAttribute('data-drive-file-id'))!;
  const pickId = (await playButtons(page).nth(3).getAttribute('data-drive-file-id'))!;
  expect(pickId).not.toBe(nextId);
  await playButtons(page).first().click();
  await expect.poll(() => primedState(page)).toMatchObject({ id: nextId, ready: true });

  await playButtons(page).nth(3).click();

  await expect.poll(async () => (await primedState(page))?.id ?? null).not.toBe(nextId);
  await expect.poll(async () => (await audio(page)).current).toBeGreaterThan(0.5);
  expect((await audio(page)).paused).toBe(false);
  await expect(page.locator(`.play-button[data-drive-file-id="${pickId}"]`).first()).toHaveClass(/playing/);
  await expect(page.locator(`.play-button[data-drive-file-id="${nextId}"]`).first()).not.toHaveClass(/playing/);
});
