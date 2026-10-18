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

  test('on a Cast device, starts from the track playing on the TV, at its load cue', async ({ page }) => {
    await mockCastSdk(page);
    await open(page);
    await page.locator('.cast-button').click();
    await expect(page.locator('[data-cast-bar]')).toBeVisible();

    await play(page, 'Gig Three');
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);
    await expect(page.locator('[data-preview-set]')).toHaveAttribute('data-preview-from', '4');

    await page.locator('[data-preview-set]').click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(2);
    const load = (await castLoads(page))[1];
    expect(load.title).toBe('Gig Three');
    expect(load.currentTime).toBeCloseTo(0.4);
    await expect(page.locator('[data-preview-chip]')).toContainText('3/4');
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

/*
 * "Going to preview mode starts from the first track. I want to start at song 6
 * where I am, but if I click play on it then preview gets disabled."
 *
 * The 2-second fixture files leave no time to click around, so these tests play
 * at a tenth of normal speed and switch back to 1x (`speed`) to let a bound or an
 * end go by.
 */
test.describe('gig preview from where you are', () => {
  const PLAYBACK_KEY = 'playback:v1';

  async function slowMotion(page: Page) {
    await page.addInitScript(() => {
      const w = window as unknown as { __rate: number };
      w.__rate = 0.1;
      const play = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function (this: HTMLMediaElement) {
        this.playbackRate = w.__rate;
        return play.call(this);
      };
    });
  }

  const speed = (page: Page, rate: number) =>
    page.evaluate(async (r) => {
      (window as unknown as { __rate: number }).__rate = r;
      const m = await import('/player.js');
      (m.element() as HTMLAudioElement).playbackRate = r;
    }, rate);

  const paused = (page: Page) =>
    page.evaluate(async () => {
      const m = await import('/player.js');
      return (m.element() as HTMLAudioElement).paused;
    });

  /** Seeds PlaybackMemory with Gig Three at `position`, so it is cued on load. */
  async function restoreGigThree(page: Page, position = 0) {
    await page.addInitScript(([k, v]) => {
      if (!sessionStorage.getItem('seeded-playback')) {
        localStorage.setItem(k, v);
        sessionStorage.setItem('seeded-playback', '1');
      }
    }, [PLAYBACK_KEY, JSON.stringify({ v: 1, fileId: 'drive-gig-three', title: 'Gig Three', isWav: false, duration: 2, position, savedAt: 1 })]);
  }

  const previewButton = (page: Page) => page.locator('[data-preview-set]');
  const chip = (page: Page) => page.locator('[data-preview-chip]');

  /**
   * The preview STARTED on `name`. Short timeout on purpose: at a tenth of normal
   * speed a preview that started at Gig One takes ~18 s to advance to Gig Three
   * by itself, which the default 20 s expect timeout would accept.
   */
  async function previewStartedAt(page: Page, queuePosition: string, name: string) {
    await expect(chip(page)).toContainText(queuePosition, { timeout: 3_000 });
    await expect(title(page)).toContainText(name, { timeout: 3_000 });
  }

  test('with nothing loaded, Preview set says it starts at the first row', async ({ page }) => {
    await open(page);
    await expect(previewButton(page)).toHaveAttribute('data-preview-from', '1');
    await expect(previewButton(page)).toHaveAttribute('title', /^Preview from #1:/);
  });

  test('starts from the playing track, at its load cue, and carries on from there', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await play(page, 'Gig Three');
    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing');

    // Gig Three is row #4 in the table (Local Only is #3).
    await expect(previewButton(page)).toHaveAttribute('data-preview-from', '4');
    await expect(previewButton(page)).toHaveAttribute('title', /^Preview from #4 \(the current track\)/);

    await previewButton(page).click();
    await previewStartedAt(page, '3/4', 'Gig Three');
    await expect(page.locator('[data-preview-range]')).toHaveAttribute('data-start', '0.4');
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.4);

    // Still previewing: Gig Three ends and the preview moves on to Gig Four.
    await speed(page, 1);
    await expect(title(page)).toContainText('Gig Four', { timeout: 10_000 });
  });

  test('starts from a paused track at its load cue', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await play(page, 'Gig Four');
    await expect(row(page, 'Gig Four').locator('.play-button')).toHaveAttribute('data-state', 'playing');
    await play(page, 'Gig Four'); // pause
    await expect.poll(() => paused(page)).toBe(true);

    await previewButton(page).click();
    await previewStartedAt(page, '4/4', 'Gig Four');
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(1.5);
    expect(await paused(page)).toBe(false);
  });

  test('starts from a track restored after a reload, at its load cue', async ({ page }) => {
    await slowMotion(page);
    await restoreGigThree(page);
    await open(page);
    await expect(title(page)).toContainText('Gig Three');
    await expect(previewButton(page)).toHaveAttribute('data-preview-from', '4');

    await previewButton(page).click();
    await previewStartedAt(page, '3/4', 'Gig Three');
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.4);
  });

  test('a restored track played from its row first, then Preview set, previews that track', async ({ page }) => {
    await slowMotion(page);
    await restoreGigThree(page);
    await open(page);
    await expect(title(page)).toContainText('Gig Three');
    await play(page, 'Gig Three');
    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing');

    await previewButton(page).click();
    await previewStartedAt(page, '3/4', 'Gig Three');
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.4);
  });

  // The reported bug. A row clicked while the previous track is still buffering
  // interrupts that track's play() (an AbortError), which was reported as a
  // failed track: the preview skipped ahead, and three in a row stopped it.
  test('clicking rows while a track is still loading stays in preview', async ({ page }) => {
    await slowMotion(page);
    await mockDrive(page);
    // Registered after the mock, so it runs first: every audio request is slow.
    await page.route((u) => u.pathname.startsWith('/api/proxy/drive/'), async (route) => {
      await new Promise((r) => setTimeout(r, 1500));
      await route.fallback();
    });
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await expect(row(page, 'Gig One')).toBeVisible();

    await previewButton(page).click();
    await expect(title(page)).toContainText('Gig One');
    await play(page, 'Gig Two');
    await expect(title(page)).toContainText('Gig Two');
    await play(page, 'Gig Three');

    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    await expect(chip(page)).toContainText('3/4');
    await expect(title(page)).toContainText('Gig Three');
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('pausing and resuming the current row keeps the preview and its end bound', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await previewButton(page).click();
    await expect(row(page, 'Gig One').locator('.play-button')).toHaveAttribute('data-state', 'playing');

    await play(page, 'Gig One'); // pause
    await expect.poll(() => paused(page)).toBe(true);
    await expect(chip(page)).toContainText('1/4');
    await play(page, 'Gig One'); // resume
    await expect.poll(() => paused(page)).toBe(false);
    await expect(chip(page)).toContainText('1/4');

    // The 1.5 s bound is still armed: the preview moves on rather than playing
    // Gig One to its end.
    await speed(page, 1);
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
    await expect(chip(page)).toContainText('2/4');
  });

  test('the player bar pause and play keep the end bound too', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await previewButton(page).click();
    await expect(row(page, 'Gig One').locator('.play-button')).toHaveAttribute('data-state', 'playing');

    await page.locator('.player-toggle').click(); // pause
    await expect.poll(() => paused(page)).toBe(true);
    await page.locator('.player-toggle').click(); // play
    await expect.poll(() => paused(page)).toBe(false);

    await speed(page, 1);
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
  });

  test('clicking the playlist again in the sidebar does not end the preview', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await previewButton(page).click();
    await previewStartedAt(page, '1/4', 'Gig One');

    await page.getByRole('link', { name: 'Gig Prep' }).click();
    await page.waitForTimeout(500);
    await expect(chip(page)).toContainText('1/4');
    expect(await paused(page)).toBe(false);
  });

  /** Opens Gig Prep with every audio request held back, like a large file over the proxy. */
  async function openBuffering(page: Page) {
    await slowMotion(page);
    await mockDrive(page);
    // Registered after the mock, so it runs first.
    await page.route((u) => u.pathname.startsWith('/api/proxy/drive/'), async (route) => {
      await new Promise((r) => setTimeout(r, 1500));
      await route.fallback();
    });
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await expect(row(page, 'Gig One')).toBeVisible();
  }

  const next = (page: Page) => page.getByRole('button', { name: 'Next track' }).click();

  // "I started preview from the beginning and clicked Next a few times; preview
  // stayed on. After a few more clicks preview mode went away." Each Next while a
  // track was still buffering aborted its play(), counted as a failed track,
  // skipped one more row, and the third stopped the preview.
  test('rapid Next while tracks are buffering moves one row per click and stays in preview', async ({ page }) => {
    await openBuffering(page);
    await previewButton(page).click();
    await expect(title(page)).toContainText('Gig One');
    await next(page);
    await next(page);

    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    await expect(chip(page)).toContainText('3/4');
    await page.waitForTimeout(1000); // nothing skips on further
    await expect(title(page)).toContainText('Gig Three');
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  // Toni got ten "Playback failed" banners in six seconds skipping quickly, while
  // the proxy answered every request 206: superseded loads reporting as failures.
  const previous = (page: Page) => page.getByRole('button', { name: 'Previous track' }).click();

  test('eight quick Next clicks in normal playback: no failure banners, the last track plays', async ({ page }) => {
    await openBuffering(page);
    await play(page, 'Gig One');
    for (let i = 0; i < 8; i++) await next(page); // the last four are past the end: no-ops

    await expect(row(page, 'Gig Four').locator('.play-button')).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    await expect(title(page)).toContainText('Gig Four');
    await page.waitForTimeout(500);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('eight quick Next/Previous clicks in preview: no failures, one row per click, still previewing', async ({ page }) => {
    await openBuffering(page);
    await previewButton(page).click();
    await expect(title(page)).toContainText('Gig One');
    // One → Two → Three → Four → Three → Two → Three → Four → Three.
    for (const step of [next, next, next, previous, previous, next, next, previous]) await step(page);

    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    await expect(chip(page)).toContainText('3/4');
    await page.waitForTimeout(500);
    await expect(title(page)).toContainText('Gig Three');
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('Next with pauses between clicks while buffering stays in preview', async ({ page }) => {
    await openBuffering(page);
    await previewButton(page).click();
    await expect(title(page)).toContainText('Gig One');
    for (const expected of ['Gig Two', 'Gig Three']) {
      await page.waitForTimeout(400);
      await next(page);
      await expect(title(page)).toContainText(expected);
    }
    await expect(row(page, 'Gig Three').locator('.play-button')).toHaveAttribute('data-state', 'playing', { timeout: 10_000 });
    await expect(chip(page)).toContainText('3/4');
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('Next past the last row ends the preview and the bar says why', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await previewButton(page).click();
    for (const expected of ['2/4', '3/4', '4/4']) {
      await next(page);
      await expect(chip(page)).toContainText(expected);
    }
    await next(page);
    await expect(chip(page)).toHaveCount(0);
    await expect(page.locator('[data-preview-stopped]')).toContainText('Preview stopped: end of the playlist');
  });

  test('a file the browser cannot decode is skipped, not taken for an expired token', async ({ page }) => {
    await slowMotion(page);
    await mockDrive(page);
    // Gig Two arrives as bytes no decoder accepts: MEDIA_ERR_SRC_NOT_SUPPORTED,
    // the same code a 401 body produces, but the proxy said 200.
    await page.route((u) => u.pathname.startsWith('/api/proxy/drive/drive-gig-two'), (route) =>
      route.fulfill({ status: 200, contentType: 'audio/flac', body: 'this is not audio at all' }));
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await expect(row(page, 'Gig One')).toBeVisible();

    await previewButton(page).click();
    await previewStartedAt(page, '1/4', 'Gig One');
    await next(page);

    await expect(title(page)).toContainText('Gig Three', { timeout: 10_000 });
    await expect(chip(page)).toContainText('3/4');
    await expect(page.locator('.error-banner')).toContainText('Skipped "Gig Two"');
    await expect(page.locator('.error-banner')).not.toContainText('rejected the token');
  });

  test('a seek before the load cue while paused is kept on resume', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await previewButton(page).click();
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.3);

    await play(page, 'Gig One'); // pause
    await expect.poll(() => paused(page)).toBe(true);
    await page.evaluate(async () => (await import('/player.js')).seek(0.1));
    await expect.poll(() => position(page)).toBeLessThan(0.2);

    await play(page, 'Gig One'); // resume
    await expect.poll(() => paused(page)).toBe(false);
    expect(await position(page)).toBeLessThan(0.3);
    await expect(chip(page)).toContainText('1/4');
  });
});

/**
 * The queue lives in the player, not the page (ADR 20261018-63 §5.1): browsing
 * never stops a preview, and only playing a row of another playlist ends it.
 */
test.describe('gig preview while browsing', () => {
  const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

  /** In-app navigation, as a sidebar click does; page.goto would reload the app. */
  const navigate = (page: Page, path: string) =>
    page.evaluate((p) => (window as unknown as { Blazor: { navigateTo(u: string): void } }).Blazor.navigateTo(p), path);

  async function slowMotion(page: Page) {
    await page.addInitScript(() => {
      const play = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function (this: HTMLMediaElement) {
        this.playbackRate = 0.1;
        return play.call(this);
      };
    });
  }

  test('keeps previewing on another playlist, and Next still walks its own', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(page.locator('[data-preview-chip]')).toContainText('1/4');

    await navigate(page, `/playlist/${PSYTECH}`);
    await expect(page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' })).toBeVisible();
    await expect(page.locator('[data-preview-chip]')).toContainText('1/4');
    await expect(title(page)).toContainText('Gig One');

    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(title(page)).toContainText('Gig Two');
    await expect(page.locator('[data-preview-chip]')).toContainText('2/4');
  });

  test('playing a row of another playlist ends the preview and says why', async ({ page }) => {
    await slowMotion(page);
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(page.locator('[data-preview-chip]')).toContainText('1/4');

    await navigate(page, `/playlist/${PSYTECH}`);
    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(title(page)).toContainText('Beta Pulse');
    await expect(page.locator('[data-preview-chip]')).toHaveCount(0);
    await expect(page.locator('[data-preview-stopped]')).toContainText('Preview stopped: another playlist started');
  });
});
