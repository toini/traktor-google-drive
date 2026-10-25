import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * The sidebar folds away so the playlist gets the width. On a wide screen « hides
 * it and ☰ brings it back, remembered across reloads. Below 1000px (a tablet in
 * portrait) it starts hidden and opens as a drawer over the content.
 *
 * Before: on an 800px Android tablet the sidebar took a third of the width, and
 * "H14 Best of" wrapped onto three lines beside a two-line Preview set button.
 */
const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';
const COLLAPSED_KEY = 'sidebar-collapsed:v1';

const sidebar = (page: Page) => page.locator('#app-sidebar');
const toggle = (page: Page) => page.locator('[data-sidebar-toggle]');
const close = (page: Page) => page.locator('[data-sidebar-close]');
const backdrop = (page: Page) => page.locator('[data-sidebar-backdrop]');

async function open(page: Page, path = `/playlist/${PSYTECH}`) {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(path);
  await expect(page.locator('#playlist-table tbody tr').first()).toBeVisible();
}

const pageOverflow = (page: Page) => page.evaluate(
  () => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test.describe('wide screen', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test('« collapses the sidebar, ☰ expands it, and the choice survives a reload', async ({ page }) => {
    await open(page);
    await expect(sidebar(page)).toBeVisible();
    await expect(toggle(page)).toBeHidden();
    const before = await page.locator('main').boundingBox();

    await close(page).click();
    await expect(sidebar(page)).toBeHidden();
    await expect(toggle(page)).toBeVisible();
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle(page)).toBeFocused();
    // The content takes the whole width, and the wordmark moves to the top bar.
    const after = await page.locator('main').boundingBox();
    expect(after!.width).toBeGreaterThan(before!.width + 200);
    expect(after!.x).toBe(0);
    await expect(page.locator('.top-brand-logo')).toBeVisible();
    expect(await page.evaluate((k) => localStorage.getItem(k), COLLAPSED_KEY)).toBe('1');

    await page.reload();
    await expect(page.locator('#playlist-table tbody tr').first()).toBeVisible();
    await expect(sidebar(page)).toBeHidden();
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false');

    await toggle(page).click();
    await expect(sidebar(page)).toBeVisible();
    await expect(toggle(page)).toBeHidden();
    await expect(page.locator('.top-brand-logo')).toBeHidden();
    expect(await page.evaluate((k) => localStorage.getItem(k), COLLAPSED_KEY)).toBe('0');

    await page.reload();
    await expect(sidebar(page).getByText('My Sets')).toBeVisible();
  });

  test('picking a playlist keeps the docked sidebar open', async ({ page }) => {
    await open(page);
    await sidebar(page).getByRole('link', { name: 'Gig Night' }).click();
    await expect(page).toHaveURL(/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67/);
    await expect(sidebar(page)).toBeVisible();
  });
});

test.describe('tablet in portrait', () => {
  test.use({ viewport: { width: 800, height: 1280 }, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });

  test('starts collapsed, with the content at full width', async ({ page }) => {
    await open(page);
    await expect(sidebar(page)).toBeHidden();
    await expect(toggle(page)).toBeVisible();
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false');
    const main = await page.locator('main').boundingBox();
    expect(main!.x).toBe(0);
    expect(main!.width).toBe(800);
  });

  test('☰ opens a drawer over the content; the backdrop closes it', async ({ page }) => {
    await open(page);
    const mainBefore = await page.locator('main').boundingBox();

    await toggle(page).click();
    await expect(sidebar(page)).toBeVisible();
    await expect(backdrop(page)).toBeVisible();
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'true');
    // An overlay: the content does not move.
    expect(await page.locator('main').boundingBox()).toEqual(mainBefore);
    // The folder holding the open playlist is expanded, and focus is on it.
    const active = sidebar(page).locator('a.tree-row.active');
    await expect(active).toBeVisible();
    await expect(active).toBeFocused();

    // Tap to the right of the drawer.
    await backdrop(page).click({ position: { x: 700, y: 600 } });
    await expect(sidebar(page)).toBeHidden();
    await expect(backdrop(page)).toHaveCount(0);
    await expect(toggle(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle(page)).toBeFocused();
  });

  test('Esc and « close the drawer', async ({ page }) => {
    await open(page);
    await toggle(page).click();
    await expect(sidebar(page)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(sidebar(page)).toBeHidden();
    await expect(toggle(page)).toBeFocused();

    await toggle(page).click();
    await expect(sidebar(page)).toBeVisible();
    await close(page).click();
    await expect(sidebar(page)).toBeHidden();
    // Closing the drawer is not a remembered choice.
    expect(await page.evaluate((k) => localStorage.getItem(k), COLLAPSED_KEY)).toBeNull();
  });

  test('picking a playlist in the drawer opens it and closes the drawer', async ({ page }) => {
    await open(page);
    await toggle(page).click();
    await sidebar(page).getByRole('link', { name: 'Gig Night' }).click();
    await expect(page).toHaveURL(/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67/);
    await expect(page.locator('.playlist-title')).toHaveText('Gig Night');
    await expect(sidebar(page)).toBeHidden();
    await expect(backdrop(page)).toHaveCount(0);
  });

  test('the playlist title stays on one line, with the counts below it', async ({ page }) => {
    await open(page);
    const m = await page.evaluate(() => {
      const title = document.querySelector('.playlist-title') as HTMLElement;
      const count = document.querySelector('.track-count') as HTMLElement;
      const lineHeight = parseFloat(getComputedStyle(title).lineHeight);
      const preview = document.querySelector('[data-preview-set]') as HTMLElement;
      const columns = document.querySelector('.column-picker-toggle') as HTMLElement;
      return {
        titleHeight: title.getBoundingClientRect().height,
        lineHeight,
        truncated: title.scrollWidth > title.clientWidth,
        countBelow: count.getBoundingClientRect().top >= title.getBoundingClientRect().bottom - 1,
        previewHeight: preview.getBoundingClientRect().height,
        columnsHeight: columns.getBoundingClientRect().height,
      };
    });
    expect(m.titleHeight).toBeLessThan(m.lineHeight * 1.5);
    expect(m.truncated).toBe(false);
    expect(m.countBelow).toBe(true);
    // Single-line buttons.
    expect(m.previewHeight).toBeLessThan(34);
    expect(m.columnsHeight).toBeLessThan(34);
  });

  test('the notice is one line, with the full text behind Show details', async ({ page }) => {
    await mockDrive(page, { legacyCollectionMissing: true, manyCollections: true });
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    const banner = page.locator('.error-banner');
    await expect(banner).toBeVisible();
    expect((await banner.boundingBox())!.height).toBeLessThan(48);
    await expect(banner.locator('.error-banner-peek')).toHaveAttribute('title', /Traktor 4\.4\.1/);

    await banner.getByRole('button', { name: /show details/i }).click();
    await expect(banner.locator('.error-list')).toContainText('Pick a different one');
    await banner.getByRole('button', { name: 'Hide' }).click();
    await expect(banner.locator('.error-list')).toHaveCount(0);
  });
});

for (const [width, height, isMobile] of [[800, 1280, true], [1280, 800, false], [1440, 900, false]] as const) {
  test.describe(`${width}px`, () => {
    test.use({ viewport: { width, height }, isMobile, hasTouch: isMobile });

    test(`the page never scrolls sideways at ${width}px, sidebar open or not`, async ({ page }) => {
      await open(page);
      expect(await pageOverflow(page)).toBeLessThanOrEqual(0);
      await (isMobile ? toggle(page) : close(page)).click();
      await expect(sidebar(page)).toBeVisible({ visible: isMobile });
      expect(await pageOverflow(page)).toBeLessThanOrEqual(0);
    });
  });
}
