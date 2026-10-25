import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a'; // 4 tracks
const OLD_SETS_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5c'; // 1 track

const headerIds = (page: Page) =>
  page.locator('#playlist-table thead th').evaluateAll((ths) => ths.map((th) => (th as HTMLElement).dataset.col));
const header = (page: Page, col: string) => page.locator(`#playlist-table thead th[data-col="${col}"]`);
const toggle = (page: Page) => page.getByRole('button', { name: 'Columns' });
const option = (page: Page, label: string) =>
  page.getByRole('group', { name: 'Visible columns' }).getByRole('checkbox', { name: label, exact: true });

async function openPlaylist(page: Page, uuid: string, rows: number) {
  await page.goto(`/playlist/${uuid}`);
  await expect(page.locator('#playlist-table tbody tr')).toHaveCount(rows);
}

async function setColumn(page: Page, label: string, visible: boolean) {
  if ((await toggle(page).getAttribute('aria-expanded')) !== 'true') await toggle(page).click();
  const box = option(page, label);
  if (visible) await box.check();
  else await box.uncheck();
}

const widthOf = (page: Page, col: string) => header(page, col).evaluate((el) => (el as HTMLElement).offsetWidth);

test.describe('playlist column picker', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('hides Folder and Release by default and shows every other column', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);

    await expect(headerIds(page)).resolves.toEqual([
      'expand', 'play', 'index', 'title', 'artist', 'label', 'comment2', 'comment', 'rating', 'bpm', 'length', 'added',
    ]);
    await expect(page.locator('#playlist-table .cell-folder')).toHaveCount(0);

    await toggle(page).click();
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'true');
    await expect(option(page, 'Folder')).not.toBeChecked();
    await expect(option(page, 'Artist')).toBeChecked();
    // Title is always shown, and says so rather than silently ignoring a click.
    await expect(option(page, 'Title')).toBeChecked();
    await expect(option(page, 'Title')).toBeDisabled();
  });

  test('toggles a column on and off', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);

    await setColumn(page, 'Folder', true);
    await expect(header(page, 'folder')).toBeVisible();
    await expect(page.locator('#playlist-table tbody .cell-folder')).toHaveCount(4);
    // Back in its usual place, between Artist and Label.
    const ids = await headerIds(page);
    expect(ids.indexOf('folder')).toBe(ids.indexOf('artist') + 1);

    await setColumn(page, 'Label', false);
    await expect(header(page, 'label')).toHaveCount(0);
    await expect(page.locator('#playlist-table tbody .cell-label')).toHaveCount(0);

    await setColumn(page, 'Label', true);
    await expect(header(page, 'label')).toBeVisible();
    await expect(page.locator('#playlist-table tbody .cell-label')).toHaveCount(4);
  });

  test('is keyboard operable and closes on Escape', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);

    await toggle(page).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('group', { name: 'Visible columns' })).toBeVisible();

    // Focus moves into the menu: Tab reaches the first toggleable box
    // (Title is disabled and skipped), Space flips it.
    await page.keyboard.press('Tab');
    await expect(option(page, 'Artist')).toBeFocused();
    await page.keyboard.press('Space');
    await expect(header(page, 'artist')).toHaveCount(0);

    await page.keyboard.press('Escape');
    await expect(page.getByRole('group', { name: 'Visible columns' })).toHaveCount(0);
    await expect(toggle(page)).toBeFocused();
  });

  test('remembers the choice across a reload', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);
    await setColumn(page, 'Folder', true);
    await setColumn(page, 'Comment', false);

    await page.reload();
    await expect(page.locator('#playlist-table tbody tr')).toHaveCount(4);
    await expect(header(page, 'folder')).toBeVisible();
    await expect(header(page, 'comment')).toHaveCount(0);

    const stored = await page.evaluate((uuid) => localStorage.getItem(`playlist-columns:v1:${uuid}`), PSYTECH_UUID);
    expect(JSON.parse(stored!)).toMatchObject({ folder: true, comment: false, artist: true });
  });

  test('keeps a different setting per playlist', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);
    await setColumn(page, 'Folder', true);
    await page.keyboard.press('Escape');

    await openPlaylist(page, OLD_SETS_UUID, 1);
    // A playlist that was never customised gets the defaults, not the
    // other playlist's choice.
    await expect(header(page, 'folder')).toHaveCount(0);
    await setColumn(page, 'Artist', false);
    await page.keyboard.press('Escape');

    await openPlaylist(page, PSYTECH_UUID, 4);
    await expect(header(page, 'folder')).toBeVisible();
    await expect(header(page, 'artist')).toBeVisible();

    await openPlaylist(page, OLD_SETS_UUID, 1);
    await expect(header(page, 'folder')).toHaveCount(0);
    await expect(header(page, 'artist')).toHaveCount(0);
  });

  test('ignores unknown ids and defaults columns missing from stored data', async ({ page }) => {
    // Written by a future (or past) version: "key" is not a column here, and
    // "comment2" is absent, as a column added after the save would be.
    await page.addInitScript((uuid) => {
      localStorage.setItem(`playlist-columns:v1:${uuid}`, JSON.stringify({ key: true, folder: true, label: false }));
    }, PSYTECH_UUID);
    await openPlaylist(page, PSYTECH_UUID, 4);

    await expect(headerIds(page)).resolves.toEqual([
      'expand', 'play', 'index', 'title', 'artist', 'folder', 'comment2', 'comment', 'rating', 'bpm', 'length', 'added',
    ]);
  });

  test('survives corrupt stored data', async ({ page }) => {
    await page.addInitScript((uuid) => localStorage.setItem(`playlist-columns:v1:${uuid}`, '{not json'), PSYTECH_UUID);
    await openPlaylist(page, PSYTECH_UUID, 4);
    await expect(header(page, 'folder')).toHaveCount(0);
    await expect(header(page, 'artist')).toBeVisible();
  });

  test('reset restores the defaults and forgets the override', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);
    await setColumn(page, 'Folder', true);
    await setColumn(page, 'Artist', false);

    await page.getByRole('button', { name: 'Reset to default' }).click();
    await expect(header(page, 'folder')).toHaveCount(0);
    await expect(header(page, 'artist')).toBeVisible();
    const stored = await page.evaluate((uuid) => localStorage.getItem(`playlist-columns:v1:${uuid}`), PSYTECH_UUID);
    expect(stored).toBeNull();
  });

  test('hiding the sorted column falls back to # order', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);
    await header(page, 'artist').click();
    await expect(header(page, 'artist')).toHaveAttribute('aria-sort', 'ascending');

    await setColumn(page, 'Artist', false);
    await expect(header(page, 'index')).toHaveAttribute('aria-sort', 'ascending');
    await expect(page.locator('#playlist-table tbody td.col-index')).toHaveText(['1', '2', '3', '4']);
    const sort = await page.evaluate((uuid) => localStorage.getItem(`playlist-sort:${uuid}`), PSYTECH_UUID);
    expect(sort).toBe('Index:asc');
  });

  test('a stored sort on a hidden column falls back to # order', async ({ page }) => {
    // Sorted by Folder before Folder became hidden by default.
    await page.addInitScript((uuid) => localStorage.setItem(`playlist-sort:${uuid}`, 'Folder:desc'), PSYTECH_UUID);
    await openPlaylist(page, PSYTECH_UUID, 4);
    await expect(header(page, 'index')).toHaveAttribute('aria-sort', 'ascending');
    await expect(page.locator('#playlist-table tbody td.col-index')).toHaveText(['1', '2', '3', '4']);
  });
});

test.describe('column widths', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('stay on their own column when another column is hidden or shown', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('table-widths:v2:playlist-table', JSON.stringify({ label: 222, comment: 177 }));
    });
    await openPlaylist(page, PSYTECH_UUID, 4);

    await expect.poll(() => widthOf(page, 'label')).toBe(222);
    expect(await widthOf(page, 'comment')).toBe(177);

    // Artist sits before both; a position-keyed width would now land on
    // the column that slid into its slot.
    await setColumn(page, 'Artist', false);
    expect(await widthOf(page, 'label')).toBe(222);
    expect(await widthOf(page, 'comment')).toBe(177);

    await setColumn(page, 'Folder', true);
    expect(await widthOf(page, 'label')).toBe(222);
    expect(await widthOf(page, 'comment')).toBe(177);
  });

  test('a column shown again gets its remembered width and a resizer', async ({ page }) => {
    await page.addInitScript(() => {
      localStorage.setItem('table-widths:v2:playlist-table', JSON.stringify({ folder: 199 }));
    });
    await openPlaylist(page, PSYTECH_UUID, 4);

    await setColumn(page, 'Folder', true);
    await expect.poll(() => widthOf(page, 'folder')).toBe(199);
    await expect(header(page, 'folder').locator('.column-resizer')).toHaveCount(1);
  });

  test('a drag saves the width under the column id', async ({ page }) => {
    await openPlaylist(page, PSYTECH_UUID, 4);
    await expect(header(page, 'artist').locator('.column-resizer')).toHaveCount(1);

    // Real input: synthetic events in one evaluate hid a drag that sorted.
    const box = (await header(page, 'artist').locator('.column-resizer').boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + 60, box.y + box.height / 2, { steps: 6 });
    await page.mouse.up();

    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('table-widths:v2:playlist-table') ?? '{}'));
    expect(Object.keys(saved)).toEqual(['artist']);
    expect(saved.artist).toBe(await widthOf(page, 'artist'));
  });

  test('migrates old position-keyed widths to column ids', async ({ page }) => {
    // The v1 format: one width per header, in the 11-column order the table
    // had before columns could be hidden.
    await page.addInitScript(() => {
      if (sessionStorage.getItem('seeded')) return;
      sessionStorage.setItem('seeded', '1');
      localStorage.setItem('table-widths-playlist-table',
        JSON.stringify([30, 56, 52, 200, 150, 120, 140, 110, 160, 84, 108]));
    });
    await openPlaylist(page, PSYTECH_UUID, 4);

    await expect.poll(() => page.evaluate(() => localStorage.getItem('table-widths-playlist-table'))).toBeNull();
    const saved = await page.evaluate(() => JSON.parse(localStorage.getItem('table-widths:v2:playlist-table') ?? '{}'));
    expect(saved).toMatchObject({ title: 200, artist: 150, folder: 120, label: 140, comment2: 110, comment: 160 });
  });

  test('drops old position-keyed widths it cannot place', async ({ page }) => {
    await page.addInitScript(() => {
      if (sessionStorage.getItem('seeded')) return;
      sessionStorage.setItem('seeded', '1');
      localStorage.setItem('table-widths-playlist-table', JSON.stringify([30, 56, 200, 150]));
    });
    await openPlaylist(page, PSYTECH_UUID, 4);

    await expect.poll(() => page.evaluate(() => localStorage.getItem('table-widths-playlist-table'))).toBeNull();
    expect(await page.evaluate(() => localStorage.getItem('table-widths:v2:playlist-table'))).toBeNull();
  });
});
