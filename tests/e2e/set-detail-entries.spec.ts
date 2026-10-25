import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * The set view under a recording lists the same entries as the playlist
 * table: every ENTRY, numbered as Traktor numbers it, separators included.
 * It used to list the parser's Playlist.Tracks, which drops separators and
 * unresolved entries, so its numbers drifted from the table's.
 *
 * "Gig Night" (see unresolved-entries.spec.ts): 10 entries, #2 a separator
 * ("==== Part 2"), #4 streaming, #6 not in the collection.
 */
const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62';
const GIG_NIGHT = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67';

async function tableEntries(page: Page) {
  await page.goto(`/playlist/${GIG_NIGHT}`);
  await expect(page.locator('#playlist-table tbody tr')).toHaveCount(10);
  return page.locator('#playlist-table tbody tr').evaluateAll((rows) => rows.map((r) => ({
    position: Number(r.querySelector('td.col-index')?.textContent?.trim()),
    separator: r.getAttribute('data-entry-kind') === 'separator',
  })));
}

async function openSetDetailOnGigNight(page: Page) {
  await page.goto(`/playlist/${RECORDED}`);
  await page.locator('#playlist-table tbody tr', { hasText: 'Z11-3 2021-09-17' }).first()
    .locator('.expander').click();
  const select = page.locator('.set-detail select');
  await expect(select).toBeVisible();
  // The option counts every entry, as the table header does.
  await expect(select.locator(`option[value="${GIG_NIGHT}"]`)).toHaveText('Gig Night (10)');
  await select.selectOption(GIG_NIGHT);
  await expect(page.locator('.set-tracklist li')).toHaveCount(10);
}

test('the set view shows separators and unresolved entries, numbered like the table', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  const table = await tableEntries(page);

  await openSetDetailOnGigNight(page);
  const items = page.locator('.set-tracklist li');
  const detail = await items.evaluateAll((lis) => lis.map((li) => ({
    position: (li as HTMLLIElement).value,
    separator: li.getAttribute('data-entry-kind') === 'separator',
  })));

  expect(detail).toEqual(table);
  expect(detail.map((d) => d.position)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

  const separator = page.locator('.set-tracklist li[data-entry-kind="separator"]');
  await expect(separator).toHaveCount(1);
  await expect(separator).toHaveAttribute('data-position', '2');
  await expect(separator.locator('.separator-label')).toHaveText('Part 2');
  // Dim, and nothing to play.
  const color = await separator.evaluate((el) => getComputedStyle(el).color);
  expect(color).toBe('rgb(110, 118, 129)');
  await expect(separator.locator('button, .play-button')).toHaveCount(0);

  await expect(page.locator('.set-tracklist li[data-position="4"]')).toHaveAttribute('data-entry-kind', 'streaming');
  await expect(page.locator('.set-tracklist li[data-position="6"]')).toHaveAttribute('data-entry-kind', 'not-in-collection');
  await expect(page.locator('.set-tracklist li[data-position="6"] .set-track-title')).toHaveText('vanished-dub');
  await expect(page.locator('.set-tracklist li[data-position="9"] .set-track-title')).toHaveText('Gig One');

  await expect(page.locator('.set-detail-count')).toHaveText(/10 tracks · 1 separator/);
});
