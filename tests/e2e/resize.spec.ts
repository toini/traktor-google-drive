import { test, expect, type Page, type Locator } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * Dragging a column's resize handle widens it, and never sorts by it.
 *
 * These drive real input (page.mouse, CDP touch), not dispatchEvent. An
 * earlier version dispatched mousedown/mousemove/mouseup/click synchronously
 * inside one page.evaluate; the click-swallowing listener was removed by a
 * setTimeout(0) that could not run in between, so the test passed while every
 * real drag (separate input events, timers running between them) still sorted.
 */
const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

async function open(page: Page) {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await expect(page.getByRole('row')).toHaveCount(5);
  const header = page.locator('#playlist-table th[data-col="title"]');
  await expect(header).toHaveAttribute('aria-sort', 'none');
  await expect(header.locator('.column-resizer')).toBeAttached();
  return header;
}

const width = (header: Locator) => header.evaluate((el) => (el as HTMLElement).offsetWidth);

async function handleCenter(header: Locator) {
  const box = (await header.locator('.column-resizer').boundingBox())!;
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

/** A user's drag: press on the handle, move in several steps, release. */
async function mouseDrag(page: Page, header: Locator, dx: number, steps = 8) {
  const { x, y } = await handleCenter(header);
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx, y, { steps });
  await page.mouse.up();
}

/** The sort runs through Blazor; give a wrongly delivered click time to land. */
async function settle(page: Page) {
  await page.waitForTimeout(400);
}

test('dragging the handle wider, released over the header, widens it and does not sort', async ({ page }) => {
  const header = await open(page);
  const before = await width(header);

  await mouseDrag(page, header, 80);
  await settle(page);

  await expect(header).toHaveAttribute('aria-sort', 'none');
  expect(await width(header)).toBeGreaterThan(before + 40);
});

test('dragging the handle narrower, released inside the same header, does not sort', async ({ page }) => {
  const header = await open(page);
  const before = await width(header);

  await mouseDrag(page, header, -40);
  await settle(page);

  await expect(header).toHaveAttribute('aria-sort', 'none');
  expect(await width(header)).toBeLessThan(before - 20);
});

test('a tiny drag (or a click) on the handle does not sort', async ({ page }) => {
  const header = await open(page);

  await mouseDrag(page, header, 2, 2);
  await settle(page);
  await expect(header).toHaveAttribute('aria-sort', 'none');

  const { x, y } = await handleCenter(header);
  await page.mouse.click(x, y);
  await settle(page);
  await expect(header).toHaveAttribute('aria-sort', 'none');
});

test('the width a drag sets is remembered for that column', async ({ page }) => {
  const header = await open(page);
  await mouseDrag(page, header, 80);
  const dragged = await width(header);

  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('table-widths:v2:playlist-table') || '{}'));
  expect(Object.keys(saved)).toEqual(['title']);
  expect(Math.abs(saved.title - dragged)).toBeLessThanOrEqual(2);

  await page.reload();
  await expect(page.getByRole('row')).toHaveCount(5);
  await expect.poll(() => width(header)).toBeGreaterThan(dragged - 3);
});

test('a header click right after a drag still sorts', async ({ page }) => {
  const header = await open(page);
  await mouseDrag(page, header, 60);
  await settle(page);
  await expect(header).toHaveAttribute('aria-sort', 'none');

  await header.click({ position: { x: 20, y: 8 } });
  await expect(header).toHaveAttribute('aria-sort', 'ascending');
});

test('a real header click (no resizer involved) still sorts', async ({ page }) => {
  const header = await open(page);
  await header.click({ position: { x: 20, y: 8 } }); // away from the resizer edge
  await expect(header).toHaveAttribute('aria-sort', 'ascending');
});

test.describe('on a touch screen', () => {
  test.use({ hasTouch: true });

  // One CDP session for the whole gesture: a touch sequence lives in it.
  async function fingerDrag(page: Page, x: number, y: number, dx: number, steps = 8) {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
    for (let i = 1; i <= steps; i++) {
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: x + (dx * i) / steps, y }] });
    }
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await cdp.detach();
  }

  test('a finger drag on the handle widens the column and does not sort', async ({ page }) => {
    const header = await open(page);
    const before = await width(header);
    const { x, y } = await handleCenter(header);

    await fingerDrag(page, x, y, 80);
    await settle(page);

    await expect(header).toHaveAttribute('aria-sort', 'none');
    expect(await width(header)).toBeGreaterThan(before + 40);
  });

  test('a tap on the handle does not sort, a tap on the label does', async ({ page }) => {
    const header = await open(page);
    const { x, y } = await handleCenter(header);

    await page.touchscreen.tap(x, y);
    await settle(page);
    await expect(header).toHaveAttribute('aria-sort', 'none');

    const box = (await header.boundingBox())!;
    await page.touchscreen.tap(box.x + 20, box.y + 8);
    await expect(header).toHaveAttribute('aria-sort', 'ascending');
  });
});
