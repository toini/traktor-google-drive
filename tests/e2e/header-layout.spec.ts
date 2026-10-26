import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

/**
 * The playlist table's header row stays one line high at laptop widths, and
 * the table never makes the page scroll sideways.
 *
 * Before: at ~1400px "Comment 2" and "Length" wrapped their sort arrow onto a
 * second line, the row grew to 54px with the labels on the upper line, and
 * main grew past the window (the table's min-width), so the page scrolled.
 */
const GIG_NIGHT = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f67';

async function open(page: Page, width: number, extraColumns: boolean) {
  await page.setViewportSize({ width, height: 800 });
  await mockDrive(page);
  await seedToken(page);
  if (extraColumns) {
    await page.addInitScript((uuid) => localStorage.setItem(
      `playlist-columns:v1:${uuid}`, JSON.stringify({ release: true, folder: true })), GIG_NIGHT);
  }
  await page.goto(`/playlist/${GIG_NIGHT}`);
  await expect(page.locator('#playlist-table tbody tr')).toHaveCount(10);
}

function measure(page: Page) {
  return page.evaluate(() => {
    const doc = document.documentElement;
    const row = document.querySelector('#playlist-table thead tr') as HTMLElement;
    const headers = [...document.querySelectorAll('#playlist-table th[data-col]')]
      .filter((th) => th.querySelector('.th-inner'))
      .map((th) => {
        const inner = th.querySelector('.th-inner') as HTMLElement;
        const label = th.querySelector('.th-label') as HTMLElement;
        const arrow = th.querySelector('.sort-indicator') as HTMLElement;
        const box = th.getBoundingClientRect();
        const a = arrow.getBoundingClientRect();
        return {
          col: (th as HTMLElement).dataset.col,
          innerHeight: inner.getBoundingClientRect().height,
          labelLines: label.getClientRects().length,
          bottomGap: box.bottom - inner.getBoundingClientRect().bottom,
          arrowInside: a.right <= box.right + 0.5 && a.top >= box.top,
          // Centres, as the arrow glyph's box is not the label's height. A
          // wrapped arrow is a whole line (~16px) below.
          arrowLineOffset: arrow.textContent
            ? Math.abs((a.top + a.bottom) / 2 - (label.getBoundingClientRect().top + label.getBoundingClientRect().bottom) / 2)
            : 0,
          truncated: label.scrollWidth > label.clientWidth,
          title: label.getAttribute('title'),
          text: label.textContent,
          resizers: th.querySelectorAll('.column-resizer').length,
        };
      });
    return {
      pageOverflow: doc.scrollWidth - doc.clientWidth,
      rowHeight: row.getBoundingClientRect().height,
      headers,
    };
  });
}

for (const width of [1280, 1400]) {
  for (const extraColumns of [false, true]) {
    const what = extraColumns ? 'with Release and Folder on' : 'with the default columns';
    test(`headers stay on one line at ${width}px ${what}, and the page does not scroll sideways`, async ({ page }) => {
      await open(page, width, extraColumns);

      // Sort by Comment 2 so its arrow is drawn: the arrow is what used to wrap.
      await page.locator('#playlist-table th[data-col="comment2"]').click({ position: { x: 12, y: 8 } });
      await expect(page.locator('#playlist-table th[data-col="comment2"]')).toHaveAttribute('aria-sort', 'ascending');

      const m = await measure(page);
      expect(m.pageOverflow).toBeLessThanOrEqual(0);
      expect(m.rowHeight).toBeLessThan(44);
      expect(m.headers.length).toBe(extraColumns ? 12 : 10);

      const bottomGaps = m.headers.map((h) => h.bottomGap);
      for (const h of m.headers) {
        expect(h.labelLines, `${h.col} label is one line`).toBe(1);
        expect(h.innerHeight, `${h.col} header is one line`).toBeLessThan(22);
        expect(h.arrowInside, `${h.col} arrow stays inside its header`).toBe(true);
        expect(h.arrowLineOffset, `${h.col} arrow sits on the label's line`).toBeLessThan(4);
        // A cut label still says what it is.
        if (h.truncated) expect(h.title, `${h.col} truncated label has a tooltip`).toBe(h.text);
      }
      // Bottom-aligned, all of them alike.
      expect(Math.max(...bottomGaps) - Math.min(...bottomGaps)).toBeLessThan(1);
    });
  }
}

test('every data header keeps its resize handle, and dragging one still widens it', async ({ page }) => {
  await open(page, 1280, true);
  const m = await measure(page);
  // The last visible column hides its handle (nothing to its right), but has one.
  for (const h of m.headers) expect(h.resizers, `${h.col} has a resizer`).toBe(1);

  const header = page.locator('#playlist-table th[data-col="comment2"]');
  const before = await header.evaluate((el) => (el as HTMLElement).offsetWidth);
  // Real input: synthetic events in one evaluate hid a drag that sorted.
  const box = (await header.locator('.column-resizer').boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 60, box.y + box.height / 2, { steps: 6 });
  await page.mouse.up();
  await page.waitForTimeout(400); // let a wrongly delivered sort click land
  await expect(header).toHaveAttribute('aria-sort', 'none');
  const after = await header.evaluate((el) => (el as HTMLElement).offsetWidth);
  expect(after).toBeGreaterThan(before + 20);

  // Widened past the window, the table scrolls inside its box, not the page.
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
