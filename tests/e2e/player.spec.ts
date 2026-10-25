import { test, expect } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';


/**
 * Sampling is finished once WaveSurfer has mounted, which strictly follows
 * computePeaks. `.waveform-status` is not a usable signal: it is absent both
 * before sampling starts and after it ends.
 */
const waveformMounted = (page) =>
  page.locator('.waveform-canvas').evaluate(
    (el) => el.querySelector('div')?.shadowRoot?.querySelector('canvas') != null,
  );

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';   // ordinary tracks (.wav fixtures)
const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62'; // recordings: one .mp3, one .wav

test.describe('player bar', () => {
  test('is hidden until something plays, then names the track', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page.locator('.player-bar')).toHaveCount(0);

    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(page.locator('.player-bar')).toBeVisible();
    await expect(page.locator('.player-title')).toHaveText('Beta Pulse');
  });

  test('survives navigating to another playlist', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(page.locator('.player-bar')).toBeVisible();

    // Browsing never stops the music (ADR 20261018-63 §5.1): the bar and the
    // track stay. In-app navigation, not page.goto, which is a full reload.
    await page.locator('a.traktor-logo').click();
    await expect(page).toHaveURL(/\/music/);
    await expect(page.locator('.player-bar')).toBeVisible();
    await expect(page.locator('.player-title')).toHaveText('Beta Pulse');
    await expect.poll(() => page.evaluate(() => localStorage.getItem('playback:v1'))).not.toBeNull();
  });

  test('keeps its queue while browsing away and back', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const rows = page.locator('#playlist-table tbody tr');
    await rows.first().locator('.play-button').click();
    await expect(page.locator('.player-bar')).toBeVisible();
    const firstTitle = await page.locator('.player-title').textContent();

    // Over to another playlist: the music and its queue stay.
    await page.locator('a.traktor-logo').click();
    await expect(page.locator('.player-title')).toHaveText(firstTitle!);
    await page.goBack();
    await expect(rows.first()).toBeVisible();

    // Next still steps through the playlist that is playing.
    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(page.locator('.player-title')).not.toHaveText(firstTitle!);
  });

  test('shows elapsed and total time', async ({ page }) => {
    // Hold the audio so the element has no metadata yet. Without the hold,
    // the 5:12 phase lasts only until metadata loads and the check races it.
    let releaseMedia!: () => void;
    const mediaGate = new Promise<void>((resolve) => { releaseMedia = resolve; });
    // Longer than the default 2s, so the track does not end and advance
    // the queue before the total is checked.
    await mockDrive(page, { mediaGate, wavSeconds: 30 });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();

    const elapsed = page.locator('.player-time > span').first();
    const total = page.locator('.player-time > span').last();
    // 312s from the fixture's PLAYTIME_FLOAT, used until the media element
    // reports its own duration.
    await expect(elapsed).toHaveText('0:00');
    await expect(total).toHaveText('5:12');

    // Once the element has metadata, its real duration (the 30s mock WAV) wins.
    releaseMedia();
    await expect(total).toHaveText('0:30');
  });
});

test.describe('waveform', () => {
  test('samples a real waveform for an uncompressed recording', async ({ page }) => {
    const calls = await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    await page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();
    await expect(page.locator('.waveform-canvas')).toBeVisible();

    // Peaks come from many small range reads, not one huge download.
    await expect
      .poll(() => calls.filter((c) => c.url.includes('drive-c4-2026')).length, { timeout: 30_000 })
      .toBeGreaterThan(5);

    await expect.poll(() => waveformMounted(page), { timeout: 60_000 }).toBe(true);
  });

  test('draws markers only for a compressed recording too large to decode', async ({ page }) => {
    const calls = await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    // Z11-3 is a 2-hour .mp3 (~300 MB by bitrate x length), over the 150 MB cap.
    await page.locator('#playlist-table tbody tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.play-button').click();
    await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-source', 'none');
    await expect(page.locator('.waveform-status')).toContainText('markers only');
    await expect.poll(() => waveformMounted(page), { timeout: 30_000 }).toBe(true);

    // Never downloaded for a picture: only playback reaches Drive, via the proxy.
    expect(calls.filter((c) => c.url.startsWith('https://www.googleapis.com/drive/v3/files/drive-z11-3'))).toHaveLength(0);
  });

  test('reuses cached peaks instead of re-sampling', async ({ page }) => {
    const calls = await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);

    const play = () =>
      page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button');

    // Sampling reads are exactly WINDOW_BYTES wide; playback reads are not, so
    // counting only those separates cache hits from ordinary streaming.
    const samplingReads = () =>
      calls.filter((c) => c.url.includes('drive-c4-2026') && /bytes=\d+-\d+$/.test(c.range ?? '')
        && Number(c.range!.split('-')[1]) - Number(c.range!.split('=')[1].split('-')[0]) === 2047).length;

    await play().click();
    // Sampling must FINISH before reloading, or the peaks were never cached.
    await expect.poll(() => waveformMounted(page), { timeout: 60_000 }).toBe(true);
    expect(samplingReads()).toBeGreaterThan(5);

    const afterFirst = samplingReads();
    await page.reload();
    await play().click();
    await expect.poll(() => waveformMounted(page), { timeout: 60_000 }).toBe(true);

    // Cached: no further window reads at all.
    expect(samplingReads() - afterFirst).toBe(0);
  });
});
