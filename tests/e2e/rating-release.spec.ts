import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

// Psytech 2024, in playlist order:
//   1 Alpha Drift                RANKING 255 (5 stars)  ALBUM "Fixture Album"
//   2 Beta Pulse                 RANKING 204 (4 stars)  ALBUM "Another Release"
//   3 Gamma Echo                 RANKING 0   (unrated)  no ALBUM
//   4 Alpha Drift (Archive Cut)  RANKING 60  (1 star)   no ALBUM
const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

const headerIds = (page: Page) =>
  page.locator('#playlist-table thead th').evaluateAll((ths) => ths.map((th) => (th as HTMLElement).dataset.col));
const header = (page: Page, col: string) => page.locator(`#playlist-table thead th[data-col="${col}"]`);
const rows = (page: Page) => page.locator('#playlist-table tbody tr');
const titles = (page: Page) => page.locator('#playlist-table tbody tr .cell-title').allTextContents();
const releases = (page: Page) => page.locator('#playlist-table tbody tr .cell-release').allTextContents();
const toggle = (page: Page) => page.getByRole('button', { name: 'Columns' });
const option = (page: Page, label: string) =>
  page.getByRole('group', { name: 'Visible columns' }).getByRole('checkbox', { name: label, exact: true });

async function openPsytech(page: Page) {
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await expect(rows(page)).toHaveCount(4);
}

test.describe('Rating and Release columns', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('shows Rating and hides Release by default', async ({ page }) => {
    await openPsytech(page);

    const ids = await headerIds(page);
    expect(ids).toContain('rating');
    expect(ids).not.toContain('release');
    // Rating sits right after Comment.
    expect(ids.indexOf('rating')).toBe(ids.indexOf('comment') + 1);
    await expect(page.locator('#playlist-table .cell-release')).toHaveCount(0);

    await toggle(page).click();
    await expect(option(page, 'Rating')).toBeChecked();
    await expect(option(page, 'Release')).not.toBeChecked();
  });

  test('a playlist saved before these columns existed shows Rating but not Release', async ({ page }) => {
    // A map written by the previous version: every toggleable column it knew
    // about, and neither "rating" nor "release".
    await page.addInitScript((uuid) => {
      localStorage.setItem(`playlist-columns:v1:${uuid}`, JSON.stringify({
        artist: true, folder: true, label: true, comment2: true, comment: false, length: true, added: true,
      }));
    }, PSYTECH_UUID);
    await openPsytech(page);

    await expect(headerIds(page)).resolves.toEqual([
      'expand', 'play', 'index', 'title', 'artist', 'folder', 'label', 'comment2', 'rating', 'bpm', 'length', 'added',
    ]);
  });

  test('renders INFO RANKING as stars, blank when unrated', async ({ page }) => {
    await openPsytech(page);
    const stars = (i: number) => rows(page).nth(i).locator('td.col-rating .rating-stars');

    await expect(stars(0)).toHaveAttribute('aria-label', '5 of 5 stars');
    await expect(stars(0)).toHaveAttribute('title', '5 of 5 stars');
    await expect(stars(0).locator('.rating-filled')).toHaveText('★★★★★');
    await expect(stars(0).locator('.rating-empty')).toHaveText('');

    await expect(stars(1)).toHaveAttribute('aria-label', '4 of 5 stars');
    await expect(stars(1).locator('.rating-filled')).toHaveText('★★★★');
    await expect(stars(1).locator('.rating-empty')).toHaveText('★');

    // RANKING="0": unrated, so nothing rather than five empty stars.
    await expect(stars(2)).toHaveCount(0);
    await expect(rows(page).nth(2).locator('td.col-rating')).toHaveText('');

    // RANKING="60" is not a multiple of 51: it rounds to the nearest star.
    await expect(stars(3)).toHaveAttribute('aria-label', '1 of 5 stars');
    await expect(stars(3).locator('.rating-filled')).toHaveText('★');

    // The filled stars are Traktor orange.
    const color = await stars(0).locator('.rating-filled').evaluate((el) => getComputedStyle(el).color);
    expect(color).toBe('rgb(255, 102, 0)');

    // Five stars fit the default width rather than ending in an ellipsis.
    const cell = rows(page).nth(0).locator('td.col-rating');
    expect(await cell.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
  });

  test('sorts Rating numerically, unrated last both ways', async ({ page }) => {
    await openPsytech(page);
    const rating = header(page, 'rating');

    await rating.click();
    await expect(rating).toHaveAttribute('aria-sort', 'ascending');
    // 60 (1 star) < 204 < 255: a text sort would put "60" last.
    await expect(titles(page)).resolves.toEqual(['Alpha Drift (Archive Cut)', 'Beta Pulse', 'Alpha Drift', 'Gamma Echo']);

    await rating.click();
    await expect(rating).toHaveAttribute('aria-sort', 'descending');
    await expect(titles(page)).resolves.toEqual(['Alpha Drift', 'Beta Pulse', 'Alpha Drift (Archive Cut)', 'Gamma Echo']);
  });

  test('Release can be shown, shows ALBUM TITLE, and sorts as text with blanks last', async ({ page }) => {
    await openPsytech(page);

    await toggle(page).click();
    await option(page, 'Release').check();
    await page.keyboard.press('Escape');

    const ids = await headerIds(page);
    // Release sits right after Artist, where Traktor puts it.
    expect(ids.indexOf('release')).toBe(ids.indexOf('artist') + 1);
    await expect(releases(page)).resolves.toEqual(['Fixture Album', 'Another Release', '', '']);

    const release = header(page, 'release');
    await release.click();
    await expect(release).toHaveAttribute('aria-sort', 'ascending');
    await expect(releases(page)).resolves.toEqual(['Another Release', 'Fixture Album', '', '']);

    await release.click();
    await expect(release).toHaveAttribute('aria-sort', 'descending');
    await expect(releases(page)).resolves.toEqual(['Fixture Album', 'Another Release', '', '']);

    // Remembered like any other column.
    const stored = await page.evaluate((uuid) => localStorage.getItem(`playlist-columns:v1:${uuid}`), PSYTECH_UUID);
    expect(JSON.parse(stored!)).toMatchObject({ release: true, rating: true });
  });
});
