import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * Every Traktor playlist ENTRY is a row, numbered as Traktor numbers it, even
 * when it cannot be played. TraktorNmlParser's Playlist.Tracks used to drop
 * those rows silently, so a 73-entry playlist showed 55 rows.
 *
 * The "Gig Night" fixture playlist, in Traktor order:
 *    1 Gig One          playable
 *    2 ==== Part 2      separator (silent.wav; in Drive, never played)
 *    3 Gig Two          playable
 *    4 Stream Only      Beatport streaming, no file
 *    5 Cafe Accent      collection NFD, key NFC: recovered, playable
 *    6 vanished-dub     key with no collection entry
 *    7 USB Anthem       volume TONIPIONEER: recovered, playable
 *    8 Gig Local Only   in the collection, not in Drive
 *    9 Gig One          the same track again, its own row
 *   10 Gig Three        playable
 */
const GIG_NIGHT = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67';

const rows = (page: Page) => page.locator('#playlist-table tbody tr');
const rowAt = (page: Page, position: number) =>
  page.locator('#playlist-table tbody tr', {
    has: page.locator('td.col-index', { hasText: new RegExp(`^${position}$`) }),
  });
const title = (page: Page) => page.locator('.player-title');
const next = (page: Page) => page.getByRole('button', { name: 'Next track' }).click();
const previous = (page: Page) => page.getByRole('button', { name: 'Previous track' }).click();

async function open(page: Page) {
  const calls = await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${GIG_NIGHT}`);
  await expect(rows(page)).toHaveCount(10);
  return calls;
}

test.describe('unresolved playlist entries', () => {
  test('every entry is a row, and # is the Traktor position', async ({ page }) => {
    await open(page);
    await expect(page.locator('#playlist-table tbody td.col-index'))
      .toHaveText(['1', '2', '3', '4', '5', '6', '7', '8', '9', '10']);
  });

  test('the header counts all of them, and says how many cannot play and why', async ({ page }) => {
    await open(page);
    const count = page.locator('.track-count');
    await expect(count).toHaveAttribute('data-track-count', '10');
    await expect(count).toHaveText(/10 tracks · 1 separator · 3 not playable/);

    const notPlayable = page.locator('[data-not-playable]');
    await expect(notPlayable).toHaveAttribute('data-not-playable', '3');
    const why = await notPlayable.getAttribute('title');
    expect(why).toContain('1 no matching file in Drive');
    expect(why).toContain('1 streaming');
    expect(why).toContain('1 not in the collection');
  });

  test('a playlist with nothing unresolved shows just the count', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a');
    await expect(page.locator('.track-count')).toHaveText(/^\s*4 tracks\s*$/);
  });

  test('a separator renders as a rule with its words, and no play button', async ({ page }) => {
    await open(page);
    const separator = rowAt(page, 2);
    await expect(separator).toHaveAttribute('data-entry-kind', 'separator');
    await expect(separator.locator('.separator-label')).toHaveText('Part 2');
    await expect(separator.locator('.play-button')).toHaveCount(0);
    await expect(separator.locator('.missing-marker')).toHaveCount(0);
    await expect(page.locator('tr.separator-row')).toHaveCount(1);
  });

  test('streaming and not-in-collection rows show a marker, their #, and no play button', async ({ page }) => {
    await open(page);

    const stream = rowAt(page, 4);
    await expect(stream).toHaveAttribute('data-entry-kind', 'streaming');
    await expect(stream.locator('.cell-title')).toContainText('Stream Only');
    await expect(stream.locator('[data-entry-marker="streaming"]')).toHaveText('streaming');
    await expect(stream.locator('.play-button')).toHaveCount(0);
    await expect(stream.locator('.missing-marker')).toBeVisible();

    // No collection entry, so the title comes from the key's file name.
    const gone = rowAt(page, 6);
    await expect(gone).toHaveAttribute('data-entry-kind', 'not-in-collection');
    await expect(gone.locator('.cell-title')).toContainText('vanished-dub');
    await expect(gone.locator('[data-entry-marker="not-in-collection"]')).toHaveText('not in collection');
    await expect(gone.locator('.play-button')).toHaveCount(0);

    // In the collection but not in Drive: the existing no-file state.
    const local = rowAt(page, 8);
    await expect(local).toHaveAttribute('data-entry-kind', 'no-drive-file');
    await expect(local.locator('.missing-marker')).toHaveAttribute('title', 'No matching file in Drive');
  });

  test('NFD/NFC file names and tracks on another volume resolve and play', async ({ page }) => {
    await open(page);
    for (const [position, name] of [[5, 'Cafe Accent'], [7, 'USB Anthem']] as const) {
      const row = rowAt(page, position);
      await expect(row).toHaveAttribute('data-entry-kind', 'track');
      await expect(row.locator('.cell-title')).toHaveText(name);
      await expect(row.locator('.play-button')).toBeVisible();
    }
    await rowAt(page, 5).locator('.play-button').click();
    await expect(title(page)).toContainText('Cafe Accent');
  });

  test('a track listed twice keeps both rows', async ({ page }) => {
    await open(page);
    await expect(rowAt(page, 1).locator('.cell-title')).toHaveText('Gig One');
    await expect(rowAt(page, 9).locator('.cell-title')).toHaveText('Gig One');
    await expect(rowAt(page, 9).locator('.play-button')).toBeVisible();
  });

  test('next and previous skip every row that cannot play', async ({ page }) => {
    const calls = await open(page);
    await rowAt(page, 1).locator('.play-button').click();
    await expect(title(page)).toContainText('Gig One');

    await next(page);
    await expect(title(page)).toContainText('Gig Two'); // separator skipped
    await next(page);
    await expect(title(page)).toContainText('Cafe Accent'); // streaming skipped
    await next(page);
    await expect(title(page)).toContainText('USB Anthem'); // not-in-collection skipped
    await next(page);
    await expect(title(page)).toContainText('Gig One'); // no Drive file skipped: the second Gig One
    await next(page);
    await expect(title(page)).toContainText('Gig Three');
    await previous(page);
    await expect(title(page)).toContainText('Gig One');

    // The separator's silent file is in Drive, and was still never fetched.
    expect(calls.filter((c) => c.kind === 'media' && c.url.includes('drive-silent'))).toHaveLength(0);
  });

  test('gig preview walks only the playable rows', async ({ page }) => {
    const calls = await open(page);
    await page.locator('[data-preview-set]').click();

    const chip = page.locator('[data-preview-chip]');
    await expect(title(page)).toContainText('Gig One');
    await expect(chip).toContainText('1/6'); // six playable of ten rows
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
    await expect(chip).toContainText('2/6');
    await expect(title(page)).toContainText('Cafe Accent', { timeout: 10_000 });
    await expect(chip).toContainText('3/6');

    expect(calls.filter((c) => c.kind === 'media' && c.url.includes('drive-silent'))).toHaveLength(0);
  });
});
