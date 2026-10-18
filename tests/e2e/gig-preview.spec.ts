import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockCastSdk, castLoads } from './cast-mock';

/**
 * Waveforms for compressed files, NML cue markers, continuous play and gig
 * preview (docs/decisions/20261018-47-waveform-cue-markers-gig-preview.md).
 *
 * The "Gig Prep" fixture playlist, in Traktor order:
 *   1 Gig One   .mp3  load 0.3 s, loops 0.5–0.9 and 1.0–1.5  -> 0.3 → 1.5
 *   2 Gig Two   .flac no load, loop 0.2–0.6                  -> 0   → 0.6
 *   3 Gig Local Only  not in Drive                           -> skipped
 *   4 Gig Three .mp3  load 0.4, no loop                      -> 0.4 → end
 *   5 Gig Four  .mp3  load 1.5, loop ends at 0.5             -> 1.5 → end
 * Every file is the mock's 2-second WAV, so a whole preview takes seconds.
 */
const GIG = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f66';

const row = (page: Page, title: string) =>
  page.locator('#playlist-table tbody tr', { hasText: title }).first();

const play = (page: Page, title: string) => row(page, title).locator('.play-button').click();

const title = (page: Page) => page.locator('.player-title');

/** WaveSurfer renders into an open shadow root; Playwright's CSS pierces it. */
const markers = (page: Page, kind: string) => page.locator(`.waveform-canvas [data-kind="${kind}"]`);

const waveformMounted = (page: Page) =>
  page.locator('.waveform-canvas').evaluate(
    (el) => el.querySelector('div')?.shadowRoot?.querySelector('canvas') != null,
  );

/** The one audio element, read through the same module instance the app imported. */
const position = (page: Page) =>
  page.evaluate(async () => {
    const m = await import('/player.js');
    return m.position()?.current ?? -1;
  });

async function open(page: Page) {
  const calls = await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${GIG}`);
  await expect(row(page, 'Gig One')).toBeVisible();
  return calls;
}

test.describe('waveform for compressed files', () => {
  test('decodes a compressed track from a direct Drive download, not the proxy', async ({ page }) => {
    const calls = await open(page);
    await play(page, 'Gig One');

    await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-source', 'decode');
    await expect.poll(() => waveformMounted(page), { timeout: 30_000 }).toBe(true);

    // The picture comes from googleapis.com directly (no Cloud Run egress)...
    const direct = calls.filter((c) => c.url.startsWith('https://www.googleapis.com/drive/v3/files/drive-gig-one'));
    expect(direct).toHaveLength(1);
    expect(direct[0].url).toContain('alt=media');
    // ...and the proxy is only ever asked for playback, never range-sampled.
    const sampling = calls.filter((c) => c.url.includes('/api/proxy/drive/drive-gig-one')
      && /bytes=\d+-\d+$/.test(c.range ?? ''));
    expect(sampling).toHaveLength(0);
  });

  test('draws Traktor cues as markers, loops as regions, and hides the grid', async ({ page }) => {
    await open(page);
    await play(page, 'Gig One');
    await expect.poll(() => waveformMounted(page), { timeout: 30_000 }).toBe(true);

    await expect(markers(page, 'cue')).toHaveCount(1);
    await expect(markers(page, 'load')).toHaveCount(1);
    await expect(markers(page, 'loop')).toHaveCount(2);

    // Hotcue numbers are 1-based on screen (HOTCUE is 0-based in the NML), a
    // real name is kept, and loop length reads in beats at the track's 120 BPM.
    await expect(markers(page, 'cue')).toHaveAttribute('data-label', '1 · Intro');
    await expect(markers(page, 'load')).toHaveAttribute('data-label', '2');
    await expect(markers(page, 'loop').nth(0)).toHaveAttribute('data-label', '3 · 0.8 beats');
    await expect(markers(page, 'loop').nth(1)).toHaveAttribute('data-label', '4 · 1 beat');
    await expect(page.locator('.waveform-canvas')).toHaveAttribute('data-markers', '4');
  });
});

test.describe('continuous play', () => {
  test('is off by default: a finished track stays loaded and paused', async ({ page }) => {
    await open(page);
    await expect(page.locator('[data-continuous-play]')).toHaveCount(0); // bar not shown yet
    await play(page, 'Gig Three');
    await expect(page.locator('[data-continuous-play]')).not.toBeChecked();

    // The 2-second file ends; nothing else starts.
    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'idle', { timeout: 10_000 });
    await page.waitForTimeout(1000);
    await expect(title(page)).toContainText('Gig Three');
  });

  test('when on, advances to the next row in table order, and is remembered', async ({ page }) => {
    await open(page);
    await play(page, 'Gig Three');
    await page.locator('[data-continuous-play]').check();

    await expect(title(page)).toContainText('Gig Four', { timeout: 10_000 });

    await page.reload();
    await play(page, 'Gig One');
    await expect(page.locator('[data-continuous-play]')).toBeChecked();
  });

  test('follows the table as sorted, and skips rows with no Drive file', async ({ page }) => {
    await open(page);
    // Index is already the ascending default; one click makes it descending:
    // Four, Three, Local Only (no file), Two, One.
    await page.locator('th.col-index').click();
    await expect(page.locator('#playlist-table tbody tr').first()).toContainText('Gig Four');

    await play(page, 'Gig Three');
    await page.locator('[data-continuous-play]').check();
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
  });

  test('next and previous buttons step through the table', async ({ page }) => {
    await open(page);
    await play(page, 'Gig One');
    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(title(page)).toContainText('Gig Two');
    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(title(page)).toContainText('Gig Three'); // Local Only skipped
    await page.getByRole('button', { name: 'Previous track' }).click();
    await expect(title(page)).toContainText('Gig Two');
  });
});

test.describe('gig preview', () => {
  test('plays each track from its load cue to its last loop, then advances', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();

    const chip = page.locator('[data-preview-chip]');
    const range = page.locator('[data-preview-range]');

    // 1. Gig One: load cue to the end of the loop that ends latest.
    await expect(title(page)).toContainText('Gig One');
    await expect(chip).toContainText('1/4'); // four playable rows
    await expect(range).toHaveAttribute('data-start', '0.3');
    await expect(range).toHaveAttribute('data-end', '1.5');
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.3);

    // 2. Gig Two: no load cue, so from 0.
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
    await expect(chip).toContainText('2/4');
    await expect(range).toHaveAttribute('data-start', '0');
    await expect(range).toHaveAttribute('data-end', '0.6');
    await expect(range).toContainText('no load cue');

    // 3. Local Only is skipped. Gig Three has no loop, so it plays to the end.
    await expect(title(page)).toContainText('Gig Three', { timeout: 10_000 });
    await expect(chip).toContainText('3/4');
    await expect(range).toHaveAttribute('data-start', '0.4');
    await expect(range).toHaveAttribute('data-end', '');
    await expect(range).toContainText('no loop');

    // 4. Gig Four's only loop ends before its load cue: ignored, to the end.
    await expect(title(page)).toContainText('Gig Four', { timeout: 10_000 });
    await expect(range).toHaveAttribute('data-start', '1.5');
    await expect(range).toContainText('loop ends before load');

    // End of the list: preview stops, the last track stays loaded.
    await expect(chip).toHaveCount(0, { timeout: 10_000 });
    await expect(title(page)).toContainText('Gig Four');
  });

  test('advances even with continuous play off', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(page.locator('[data-continuous-play]')).not.toBeChecked();
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
  });

  test('dims what the preview skips on the waveform', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect.poll(() => waveformMounted(page), { timeout: 30_000 }).toBe(true);
    await expect(page.locator('.waveform-canvas [part~="preview-before"]')).toHaveCount(1);
    await expect(page.locator('.waveform-canvas [part~="preview-after"]')).toHaveCount(1);
  });

  test('clicking another row jumps the preview there', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(title(page)).toContainText('Gig One');

    await play(page, 'Gig Three');
    await expect(title(page)).toContainText('Gig Three');
    await expect(page.locator('[data-preview-chip]')).toContainText('3/4');
    await expect(page.locator('[data-preview-range]')).toHaveAttribute('data-start', '0.4');
  });

  test('stop leaves preview mode and the track plays on unbounded', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(title(page)).toContainText('Gig One');

    await page.locator('[data-stop-preview]').click();
    await expect(page.locator('[data-preview-chip]')).toHaveCount(0);
    // Past Gig One's 1.5 s bound it is still Gig One: nothing advanced.
    await page.waitForTimeout(2500);
    await expect(title(page)).toContainText('Gig One');
  });

  test('on a Cast device, each LOAD starts at the load cue and the end is watched from the ticks', async ({ page }) => {
    await mockCastSdk(page);
    await open(page);
    await page.locator('.cast-button').click();
    await expect(page.locator('[data-cast-bar]')).toBeVisible();

    await page.locator('[data-preview-set]').click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);
    expect((await castLoads(page))[0].currentTime).toBeCloseTo(0.3);
    expect((await castLoads(page))[0].title).toBe('Gig One');

    // The device reports it has passed the end of the last loop.
    await page.evaluate(() => window.__castTest.tick(1.6));
    await expect.poll(async () => (await castLoads(page)).length).toBe(2);
    const second = (await castLoads(page))[1];
    expect(second.title).toBe('Gig Two');
    expect(second.currentTime).toBe(0);
  });
});
