/**
 * Captures the app's main screens into tests/e2e/screenshots/ so UX work can be
 * reviewed without a Google account. Not an assertion suite — run it with
 *   npx playwright test screenshots
 * and look at the output.
 */
import { test } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { mockDrive, seedToken } from './drive-mock';

const OUT = 'screenshots';
const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

test.beforeAll(() => mkdirSync(OUT, { recursive: true }));

test('capture login', async ({ page }) => {
  await mockDrive(page);
  await page.goto('/login');
  await page.getByRole('button', { name: /sign in/i }).waitFor();
  await page.screenshot({ path: `${OUT}/01-login.png`, fullPage: true });
});

test('capture login after expiry', async ({ page }) => {
  await mockDrive(page);
  await page.goto('/login?expired=1');
  await page.getByRole('button', { name: /sign in/i }).waitFor();
  await page.screenshot({ path: `${OUT}/02-login-expired.png`, fullPage: true });
});

test('capture empty collection view', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto('/music');
  await page.getByText('My Sets').waitFor();
  await page.screenshot({ path: `${OUT}/03-collection.png`, fullPage: true });
});

test('capture playlist', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await page.getByText('Beta Pulse').waitFor();
  await page.screenshot({ path: `${OUT}/04-playlist.png`, fullPage: true });
});

test('capture playlist with a track playing', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await page.getByText('Beta Pulse').waitFor();
  await page.locator('.play-button').nth(1).click();
  await page.locator('tr.currently-playing').waitFor();
  await page.screenshot({ path: `${OUT}/05-playing.png`, fullPage: true });
});

test('capture the error surface', async ({ page }) => {
  await mockDrive(page, { legacyCollectionMissing: true, noCollectionFound: true });
  await seedToken(page);
  await page.goto('/music');
  await page.locator('.error-banner').waitFor();
  await page.getByRole('button', { name: /show details/i }).click();
  await page.locator('.error-detail').waitFor();
  await page.screenshot({ path: `${OUT}/06-error.png`, fullPage: true });
});

test('capture an expanded set', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62');
  await page.getByText('Z11-3 2021-09-17').waitFor();
  await page.locator('.expander').first().click();
  await page.locator('.set-tracklist').waitFor();
  await page.screenshot({ path: `${OUT}/07-set-expanded.png`, fullPage: true });
});
