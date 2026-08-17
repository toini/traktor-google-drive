import { test, expect } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

test('dragging the column resizer does not trigger a sort', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await expect(page.getByRole('row')).toHaveCount(5);

  const titleHeader = page.getByRole('columnheader', { name: 'Title' });
  await expect(titleHeader).toHaveAttribute('aria-sort', 'ascending');
  await expect(page.locator('#playlist-table th .column-resizer').first()).toBeAttached();

  const widthBefore = await titleHeader.evaluate((el) => (el as HTMLElement).offsetWidth);

  // Dispatch the exact native-event sequence a real drag produces, entirely
  // inside the page, so we're testing the JS logic rather than fighting
  // Playwright's pointer-visibility heuristics on a 6px-wide strip.
  await page.evaluate(() => {
    // By text, not position: column indexes shift whenever a column is added.
    const header = [...document.querySelectorAll('#playlist-table th')]
      .find((th) => th.textContent?.trim().startsWith('Title')) as HTMLElement;
    const resizer = header.querySelector('.column-resizer') as HTMLElement;
    const rect = resizer.getBoundingClientRect();
    const opts = { bubbles: true, cancelable: true, clientX: rect.x + 2, clientY: rect.y + 2 };
    resizer.dispatchEvent(new MouseEvent('mousedown', opts));
    document.dispatchEvent(new MouseEvent('mousemove', { ...opts, clientX: rect.x + 80 }));
    document.dispatchEvent(new MouseEvent('mouseup', { ...opts, clientX: rect.x + 80 }));
    // The browser fires 'click' after 'mouseup' for the same interaction.
    resizer.dispatchEvent(new MouseEvent('click', { ...opts, clientX: rect.x + 80 }));
  });

  // Still ascending — the drag+click sequence must not have been read as a sort click.
  await expect(titleHeader).toHaveAttribute('aria-sort', 'ascending');

  const widthAfter = await titleHeader.evaluate((el) => (el as HTMLElement).offsetWidth);
  // Relative, not absolute: the starting width depends on how many
  // columns the table has, which changes as features are added.
  expect(widthAfter).toBeGreaterThan(widthBefore + 20);
});

test('a real header click (no resizer involved) still sorts', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await expect(page.getByRole('row')).toHaveCount(5);

  const titleHeader = page.getByRole('columnheader', { name: 'Title' });
  await titleHeader.click({ position: { x: 20, y: 8 } }); // away from the resizer edge
  await expect(titleHeader).toHaveAttribute('aria-sort', 'descending');
});
