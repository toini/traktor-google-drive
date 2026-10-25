import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * A playlist can list the same track twice (the real collection has hundreds of
 * repeats). Playback follows the row, not the Drive file: the other copy is a
 * different pick, only the row playing is highlighted, and next/previous step
 * from that row's place.
 *
 * "Gig Night", in Traktor order (see unresolved-entries.spec.ts): rows 1 and 9
 * are both Gig One; the playable rows are 1, 3, 5, 7, 9, 10, so row 9 is 5/6 in
 * the queue. Gig One's load cue is at 0.3 s, its last loop ends at 1.5 s.
 */
const GIG_NIGHT = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67';

const rowAt = (page: Page, position: number) =>
  page.locator('#playlist-table tbody tr', {
    has: page.locator('td.col-index', { hasText: new RegExp(`^${position}$`) }),
  });
const button = (page: Page, position: number) => rowAt(page, position).locator('.play-button');
const title = (page: Page) => page.locator('.player-title');
const chip = (page: Page) => page.locator('[data-preview-chip]');
const next = (page: Page) => page.getByRole('button', { name: 'Next track' }).click();
const previous = (page: Page) => page.getByRole('button', { name: 'Previous track' }).click();

/** Every media element plays at a tenth of normal speed, so positions are easy to read. */
async function slowMotion(page: Page) {
  await page.addInitScript(() => {
    const play = HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play = function (this: HTMLMediaElement) {
      this.playbackRate = 0.1;
      return play.call(this);
    };
  });
}

const position = (page: Page) =>
  page.evaluate(async () => {
    const m = await import('/player.js');
    return m.position()?.current ?? -1;
  });

const seek = (page: Page, seconds: number) =>
  page.evaluate(async (s) => {
    const m = await import('/player.js');
    m.seek(s);
  }, seconds);

async function open(page: Page) {
  await slowMotion(page);
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${GIG_NIGHT}`);
  await expect(page.locator('#playlist-table tbody tr')).toHaveCount(10);
}

/** Row 1 playing, moved well past the load cue so a restart is visible. */
async function playFirstCopy(page: Page) {
  await button(page, 1).click();
  await expect(button(page, 1)).toHaveAttribute('data-state', 'playing');
  await seek(page, 1.6);
  await expect.poll(() => position(page)).toBeGreaterThanOrEqual(1.6);
}

test.describe('a track listed twice', () => {
  test('clicking the other copy switches to that row and restarts it', async ({ page }) => {
    await open(page);
    await playFirstCopy(page);
    await expect(page.locator('.currently-playing')).toHaveCount(1);
    await expect(rowAt(page, 1)).toHaveClass(/currently-playing/);

    await button(page, 9).click();

    // Not a pause of the first copy: the second one plays, from the top.
    await expect(button(page, 9)).toHaveAttribute('data-state', 'playing');
    await expect(button(page, 1)).toHaveAttribute('data-state', 'idle');
    await expect(page.locator('.currently-playing')).toHaveCount(1);
    await expect(rowAt(page, 9)).toHaveClass(/currently-playing/);
    await expect.poll(() => position(page)).toBeLessThan(1);
    await expect(title(page)).toContainText('Gig One');

    // And back: the first copy is again a different row, not a pause.
    await button(page, 1).click();
    await expect(button(page, 1)).toHaveAttribute('data-state', 'playing');
    await expect(button(page, 9)).toHaveAttribute('data-state', 'idle');
    await expect(rowAt(page, 1)).toHaveClass(/currently-playing/);
    await expect(page.locator('.currently-playing')).toHaveCount(1);
  });

  test('clicking the playing copy still pauses it', async ({ page }) => {
    await open(page);
    await button(page, 9).click();
    await expect(button(page, 9)).toHaveAttribute('data-state', 'playing');
    await button(page, 9).click();
    await expect(button(page, 9)).toHaveAttribute('data-state', 'idle');
    await expect(rowAt(page, 9)).toHaveClass(/currently-playing/);
    await expect(rowAt(page, 1)).not.toHaveClass(/currently-playing/);
  });

  test('next and previous continue from the second copy, not the first', async ({ page }) => {
    await open(page);
    await playFirstCopy(page);
    await button(page, 9).click();
    await expect(button(page, 9)).toHaveAttribute('data-state', 'playing');

    await next(page);
    await expect(title(page)).toContainText('Gig Three'); // row 10, not Gig Two (row 3)
    await previous(page);
    await expect(title(page)).toContainText('Gig One');
    await expect(rowAt(page, 9)).toHaveClass(/currently-playing/);
    await expect(rowAt(page, 1)).not.toHaveClass(/currently-playing/);
    await previous(page);
    await expect(title(page)).toContainText('USB Anthem'); // row 7, before row 9
  });

  test('in gig preview, the other copy starts at its load cue and the preview carries on from it', async ({ page }) => {
    await open(page);
    await page.locator('[data-preview-set]').click();
    await expect(chip(page)).toContainText('1/6');
    await expect(title(page)).toContainText('Gig One');
    await expect(button(page, 1)).toHaveAttribute('data-state', 'playing');
    await seek(page, 1.2);
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(1.2);

    await button(page, 9).click();

    await expect(chip(page)).toContainText('5/6', { timeout: 3_000 });
    await expect(page.locator('[data-preview-range]')).toHaveAttribute('data-start', '0.3');
    await expect(button(page, 9)).toHaveAttribute('data-state', 'playing');
    await expect(button(page, 1)).toHaveAttribute('data-state', 'idle');
    await expect(page.locator('.currently-playing')).toHaveCount(1);
    await expect(rowAt(page, 9)).toHaveClass(/currently-playing/);
    // Restarted from the load cue, not left at 1.2 s.
    await expect.poll(() => position(page)).toBeGreaterThanOrEqual(0.3);
    expect(await position(page)).toBeLessThan(1.0);

    await next(page);
    await expect(chip(page)).toContainText('6/6');
    await expect(title(page)).toContainText('Gig Three');
  });
});
