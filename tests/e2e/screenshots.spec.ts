/**
 * Captures the app's main screens into tests/e2e/screenshots/ so UX work can be
 * reviewed without a Google account. Not an assertion suite — run it with
 *   npx playwright test screenshots
 * and look at the output.
 */
import { test, type Page } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mockDrive, seedToken } from './drive-mock';

const here = dirname(fileURLToPath(import.meta.url));

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

test('capture gig preview with cue markers', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f66');
  await page.locator('#playlist-table').getByText('Gig One').waitFor();
  await page.locator('[data-preview-set]').click();
  await page.locator('.waveform-canvas [data-kind="load"]').waitFor();
  await page.screenshot({ path: `${OUT}/09-gig-preview.png`, fullPage: true });
});

test('capture the player bar with a waveform', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62');
  await page.locator('#playlist-table').getByText('C4 2026-08-16').waitFor();
  await page.locator('#playlist-table tbody tr', { hasText: 'C4 2026-08-16' }).first().locator('.play-button').click();
  await page.locator('.waveform-canvas').waitFor();
  await page.locator('.expander').first().click();
  // Wait for sampling to finish so the waveform is drawn, not mid-progress.
  await page.locator('.waveform-status').waitFor({ state: 'detached', timeout: 60000 });
  await page.screenshot({ path: `${OUT}/08-player-waveform.png`, fullPage: true });
});

test('capture the column picker open', async ({ page }) => {
  await mockDrive(page);
  await seedToken(page);
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await page.getByText('Beta Pulse').waitFor();
  await page.getByRole('button', { name: 'Columns' }).click();
  await page.getByRole('group', { name: 'Visible columns' }).waitFor();
  await page.screenshot({ path: `${OUT}/10-column-picker.png`, fullPage: true });
});

/**
 * A sidebar shaped like a real collection: more than three folders (so they
 * render collapsed), loose playlists after them, and Traktor's long history
 * playlist names, which have to ellipsise rather than run off the edge.
 */
function manyFoldersNml(): string {
  const base = readFileSync(join(here, 'fixtures', 'collection.nml'), 'utf8');
  let n = 0;
  const uuid = () => `ffff${String(++n).padStart(28, '0')}`;
  const playlist = (name: string) =>
    `<NODE TYPE="PLAYLIST" NAME="${name}"><PLAYLIST ENTRIES="1" TYPE="LIST" UUID="${uuid()}">`
    + '<ENTRY><PRIMARYKEY TYPE="TRACK" KEY="Macintosh HD/:Music/:Sets/:alpha.wav"></PRIMARYKEY></ENTRY>'
    + '</PLAYLIST></NODE>';
  const folder = (name: string, children: string[]) =>
    `<NODE TYPE="FOLDER" NAME="${name}"><SUBNODES COUNT="${children.length}">`
    + children.map(playlist).join('') + '</SUBNODES></NODE>';
  const extra = [
    folder('C', ['C1 Warmup', 'C2 Peak']),
    folder('DNB', ['Liquid', 'Neuro']),
    folder('Gigs', ['Flow 2025', 'Kaiku 2026-05-02']),
    folder('History', ['history_2020y01m27d_23h14m05s_a_very_long_name', 'history_2020y02m03d']),
    folder('X', ['X1']),
    playlist('Preparation'),
    playlist('history_2020y01m27d_19h02m44s_another_long_one'),
  ].join('\n');
  return base.replace(/(\s*<\/SUBNODES>\s*<\/NODE>\s*<\/PLAYLISTS>)/, `\n${extra}$1`);
}

// 'History' is the 4th folder; its first child is the 7th generated playlist.
const NESTED_HISTORY_UUID = `ffff${'7'.padStart(28, '0')}`;

const sidebar = (page: Page) => page.locator('aside.sidebar');

test('capture the sidebar', async ({ page }) => {
  await mockDrive(page, { nml: manyFoldersNml() });
  await seedToken(page);
  await page.goto('/music');
  await page.locator('.folder-tree summary', { hasText: 'History' }).waitFor();
  await sidebar(page).screenshot({ path: `${OUT}/11-sidebar.png`, animations: 'disabled' });
});

test('capture the sidebar with a nested playlist active', async ({ page }) => {
  await mockDrive(page, { nml: manyFoldersNml() });
  await seedToken(page);
  await page.goto(`/playlist/${NESTED_HISTORY_UUID}`);
  // No click on History: the folder holding the active playlist opens by
  // itself (remember.spec.ts), and a click would collapse it again.
  await page.locator('.folder-tree a.active').waitFor();
  await page.locator('.folder-tree summary', { hasText: 'Gigs' }).click();
  await page.locator('.folder-tree a', { hasText: 'Kaiku' }).hover();
  await sidebar(page).screenshot({ path: `${OUT}/12-sidebar-nested-active.png`, animations: 'disabled' });
});
