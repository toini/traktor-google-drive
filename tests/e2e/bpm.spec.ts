import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

// Psytech 2024, in playlist order, with TEMPO BPM as the fixture writes it:
//   1 Alpha Drift                128.000061  -> "128"
//   2 Beta Pulse                 127.530014  -> "127.5"
//   3 Gamma Echo                 141.960007  -> "142"
//   4 Alpha Drift (Archive Cut)  no TEMPO    -> blank
const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

const headerIds = (page: Page) =>
  page.locator('#playlist-table thead th').evaluateAll((ths) => ths.map((th) => (th as HTMLElement).dataset.col));
const header = (page: Page, col: string) => page.locator(`#playlist-table thead th[data-col="${col}"]`);
const rows = (page: Page) => page.locator('#playlist-table tbody tr');
const titles = (page: Page) => page.locator('#playlist-table tbody tr .cell-title').allTextContents();
const bpms = (page: Page) => page.locator('#playlist-table tbody tr td.col-bpm').allTextContents();
const toggle = (page: Page) => page.getByRole('button', { name: 'Columns' });
const option = (page: Page, label: string) =>
  page.getByRole('group', { name: 'Visible columns' }).getByRole('checkbox', { name: label, exact: true });

async function openPsytech(page: Page) {
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await expect(rows(page)).toHaveCount(4);
}

test.describe('BPM column', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('is shown by default, between Rating and Length', async ({ page }) => {
    await openPsytech(page);

    const ids = await headerIds(page);
    expect(ids.indexOf('bpm')).toBe(ids.indexOf('rating') + 1);
    expect(ids.indexOf('length')).toBe(ids.indexOf('bpm') + 1);
    await expect(header(page, 'bpm')).toHaveText('BPM');

    await toggle(page).click();
    await expect(option(page, 'BPM')).toBeChecked();
  });

  test('a playlist saved before BPM existed still shows it', async ({ page }) => {
    // The map the previous version wrote: every toggleable column it knew, no "bpm".
    await page.addInitScript((uuid) => {
      localStorage.setItem(`playlist-columns:v1:${uuid}`, JSON.stringify({
        artist: true, release: false, folder: false, label: true, comment2: true, comment: true,
        rating: true, length: true, added: true,
      }));
    }, PSYTECH_UUID);
    await openPsytech(page);

    await expect(header(page, 'bpm')).toBeVisible();
    await expect(bpms(page)).resolves.toEqual(['128', '127.5', '142', '']);
  });

  test('rounds to one decimal, drops a trailing .0, right-aligned in tabular numerals', async ({ page }) => {
    await openPsytech(page);

    await expect(bpms(page)).resolves.toEqual(['128', '127.5', '142', '']);

    const cell = rows(page).nth(1).locator('td.col-bpm');
    const style = await cell.evaluate((el) => {
      const s = getComputedStyle(el);
      return { align: s.textAlign, numeric: s.fontVariantNumeric, width: el.getBoundingClientRect().width };
    });
    expect(style.align).toBe('right');
    expect(style.numeric).toBe('tabular-nums');
    expect(style.width).toBeLessThan(100);
    // "127.5" fits without an ellipsis.
    expect(await cell.evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
  });

  test('sorts numerically, tracks without BPM last both ways', async ({ page }) => {
    await openPsytech(page);
    const bpm = header(page, 'bpm');

    await bpm.click();
    await expect(bpm).toHaveAttribute('aria-sort', 'ascending');
    await expect(bpms(page)).resolves.toEqual(['127.5', '128', '142', '']);
    await expect(titles(page)).resolves.toEqual(['Beta Pulse', 'Alpha Drift', 'Gamma Echo', 'Alpha Drift (Archive Cut)']);

    await bpm.click();
    await expect(bpm).toHaveAttribute('aria-sort', 'descending');
    await expect(bpms(page)).resolves.toEqual(['142', '128', '127.5', '']);

    const sort = await page.evaluate((uuid) => localStorage.getItem(`playlist-sort:${uuid}`), PSYTECH_UUID);
    expect(sort).toBe('Bpm:desc');
  });

  test('can be hidden with the column picker, and stays hidden', async ({ page }) => {
    await openPsytech(page);

    await toggle(page).click();
    await option(page, 'BPM').uncheck();
    await page.keyboard.press('Escape');

    await expect(header(page, 'bpm')).toHaveCount(0);
    await expect(page.locator('#playlist-table tbody td.col-bpm')).toHaveCount(0);
    const stored = await page.evaluate((uuid) => localStorage.getItem(`playlist-columns:v1:${uuid}`), PSYTECH_UUID);
    expect(JSON.parse(stored!)).toMatchObject({ bpm: false });

    await page.reload();
    await expect(rows(page)).toHaveCount(4);
    await expect(header(page, 'bpm')).toHaveCount(0);
  });
});
