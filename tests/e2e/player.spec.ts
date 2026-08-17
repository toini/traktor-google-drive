import { test, expect } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';   // ordinary tracks (.wav fixtures)
const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62'; // recordings: one .mp3, one .wav

test.describe('player bar', () => {
  test('is hidden until something plays, then names the track', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page.locator('.player-bar')).toHaveCount(0);

    await page.locator('tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(page.locator('.player-bar')).toBeVisible();
    await expect(page.locator('.player-title')).toHaveText('Beta Pulse');
  });

  test('survives navigating to another playlist', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await page.locator('tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(page.locator('.player-bar')).toBeVisible();

    // Switching playlists stops playback, so the bar goes with it rather than
    // claiming something is playing when nothing is.
    await page.goto('/music');
    await expect(page.locator('.player-bar')).toHaveCount(0);
  });

  test('shows elapsed and total time', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await page.locator('tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    // 312s from the fixture's PLAYTIME_FLOAT, used until the media element
    // reports its own duration.
    await expect(page.locator('.player-time')).toContainText('5:12');
  });
});

test.describe('waveform', () => {
  test('samples a real waveform for an uncompressed recording', async ({ page }) => {
    const calls = await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    await page.locator('tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();
    await expect(page.locator('.waveform-canvas')).toBeVisible();

    // Peaks come from many small range reads, not one huge download.
    await expect
      .poll(() => calls.filter((c) => c.url.includes('drive-c4-2026')).length, { timeout: 30_000 })
      .toBeGreaterThan(50);

    await expect(page.locator('.waveform-status')).toHaveCount(0);
    const painted = await page.locator('.waveform-canvas').evaluate(
      (c: HTMLCanvasElement) => c.width > 0 && c.height > 0,
    );
    expect(painted).toBe(true);
  });

  test('falls back to a plain seek bar for a compressed recording', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    // Z11-3 is an .mp3, which would have to be fully decoded to get peaks.
    await page.locator('tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.play-button').click();
    await expect(page.locator('.player-seek')).toBeVisible();
    await expect(page.locator('.waveform-canvas')).toHaveCount(0);
  });

  test('reuses cached peaks instead of re-sampling', async ({ page }) => {
    const calls = await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    const play = () =>
      page.locator('tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button');

    // Sampling reads are exactly WINDOW_BYTES wide; playback reads are not, so
    // counting only those separates cache hits from ordinary streaming.
    const samplingReads = () =>
      calls.filter((c) => c.url.includes('drive-c4-2026') && /bytes=\d+-\d+$/.test(c.range ?? '')
        && Number(c.range!.split('-')[1]) - Number(c.range!.split('=')[1].split('-')[0]) === 2047).length;

    await play().click();
    await expect(page.locator('.waveform-canvas')).toBeVisible();
    // Sampling must FINISH before reloading, or peaks were never cached — the
    // status element disappears only once computePeaks has stored them.
    await expect(page.locator('.waveform-status')).toHaveCount(0, { timeout: 60_000 });
    expect(samplingReads()).toBeGreaterThan(50);

    const afterFirst = samplingReads();
    await page.reload();
    await play().click();
    await expect(page.locator('.waveform-canvas')).toBeVisible();
    await page.waitForTimeout(2500);

    // Cached: no further window reads at all.
    expect(samplingReads() - afterFirst).toBe(0);
  });
});
