import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken, FAKE_TOKEN } from './drive-mock';

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a'; // in folder "My Sets"
const FOLDERS_KEY = 'sidebar-folders:v1';
const PLAYBACK_KEY = 'playback:v1';

const folder = (page: Page, name: string) => page.locator(`details[data-folder="${name}"]`);
const isOpen = (page: Page, name: string) => folder(page, name).evaluate((el) => (el as HTMLDetailsElement).open);
const stored = (page: Page, key: string) => page.evaluate((k) => localStorage.getItem(k), key);
const storedPlayback = async (page: Page) => JSON.parse((await stored(page, PLAYBACK_KEY)) ?? 'null');

/** The app's one audio element, through the same module instance Blazor imported. */
const audioState = (page: Page) =>
  page.evaluate(async () => {
    const player = await import('/player.js');
    const el = player.element() as HTMLAudioElement | null;
    return el ? { current: el.currentTime, paused: el.paused, src: el.currentSrc } : null;
  });

/** Seeds a record for Beta Pulse at 1 s and opens its playlist, so it is cued. */
async function restoreBeta(page: Page) {
  await page.addInitScript(([k, v]) => {
    if (!sessionStorage.getItem('seeded-playback')) {
      localStorage.setItem(k, v);
      sessionStorage.setItem('seeded-playback', '1');
    }
  }, [PLAYBACK_KEY, JSON.stringify({ v: 1, fileId: 'drive-beta', title: 'Beta Pulse', isWav: true, duration: 2, position: 1, savedAt: 1 })]);
  await page.goto(`/playlist/${PSYTECH}`);
  await expect(page.locator('.player-title')).toContainText('Beta Pulse');
  await expect(page.getByRole('row')).toHaveCount(5);
  await expect.poll(async () => (await audioState(page))?.current ?? 0).toBeCloseTo(1, 1);
}

test.describe('remembers the sidebar', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('keeps collapsed and expanded folders across a reload', async ({ page }) => {
    await page.goto('/music');
    await expect(folder(page, 'Archive')).toBeVisible();
    // Two folders, so both start open by default.
    expect(await isOpen(page, 'My Sets')).toBe(true);
    expect(await isOpen(page, 'Archive')).toBe(true);

    await folder(page, 'Archive').locator('summary').click();
    await expect.poll(() => stored(page, FOLDERS_KEY)).toContain('"Archive":false');

    await page.reload();
    await expect(folder(page, 'Archive')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Old Sets' })).toBeHidden();
    expect(await isOpen(page, 'Archive')).toBe(false);
    expect(await isOpen(page, 'My Sets')).toBe(true);

    // And the other way round: reopen one, close the other.
    await folder(page, 'Archive').locator('summary').click();
    await folder(page, 'My Sets').locator('summary').click();
    await expect.poll(() => stored(page, FOLDERS_KEY)).toContain('"My Sets":false');
    await expect.poll(() => stored(page, FOLDERS_KEY)).toContain('"Archive":true');

    await page.reload();
    await expect(page.getByRole('link', { name: 'Old Sets' })).toBeVisible();
    expect(await isOpen(page, 'Archive')).toBe(true);
    expect(await isOpen(page, 'My Sets')).toBe(false);
  });

  test('expands the folder holding the active playlist, and keeps it highlighted', async ({ page }) => {
    await page.goto('/music');
    await expect(folder(page, 'My Sets')).toBeVisible();
    await folder(page, 'My Sets').locator('summary').click();
    await expect.poll(() => stored(page, FOLDERS_KEY)).toContain('"My Sets":false');

    await page.goto(`/playlist/${PSYTECH}`);
    const link = page.getByRole('link', { name: 'Psytech 2024' });
    await expect(link).toBeVisible();
    await expect(link).toHaveClass(/active/);
    expect(await isOpen(page, 'My Sets')).toBe(true);

    await page.reload();
    await expect(link).toBeVisible();
    await expect(link).toHaveClass(/active/);
  });

  test('ignores an unreadable stored value', async ({ page }) => {
    await page.addInitScript((k) => localStorage.setItem(k, '{not json'), FOLDERS_KEY);
    await page.goto('/music');
    await expect(page.getByRole('link', { name: 'Psytech 2024' })).toBeVisible();
  });
});

test.describe('remembers the player', () => {
  test('brings the last track back paused at its position, and play resumes from there', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);

    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await expect(page.locator('.player-toggle.playing')).toBeVisible();
    // The fixture WAV is 2 s long; pause about halfway through.
    await expect.poll(async () => (await audioState(page))?.current ?? 0).toBeGreaterThan(0.6);
    await page.locator('.player-toggle').click();
    await expect(page.locator('.player-toggle')).not.toHaveClass(/playing/);

    // Pause saves at once rather than waiting for the 5 s throttle.
    await expect.poll(async () => (await storedPlayback(page))?.position ?? 0).toBeGreaterThan(0.5);
    const saved = await storedPlayback(page);
    expect(saved).toMatchObject({ v: 1, fileId: 'drive-beta', title: 'Beta Pulse' });
    expect(JSON.stringify(saved)).not.toContain(FAKE_TOKEN);

    await page.reload();

    await expect(page.locator('.player-bar')).toBeVisible();
    await expect(page.locator('.player-title')).toContainText('Beta Pulse');
    await expect
      .poll(async () => (await audioState(page))?.current ?? 0)
      .toBeCloseTo(saved.position, 1);

    // Not autoplaying: still paused a moment later, button offers Play.
    await page.waitForTimeout(500);
    const cued = await audioState(page);
    expect(cued?.paused).toBe(true);
    expect(cued?.src).toContain('drive-beta');
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Play');
    await expect(page.locator('.player-toggle .spinner')).toHaveCount(0);

    await page.locator('.player-toggle').click();
    await expect(page.locator('.player-toggle.playing')).toBeVisible();
    expect((await audioState(page))?.current ?? 0).toBeGreaterThanOrEqual(saved.position - 0.1);
  });

  test('next and previous continue from the restored track, not the top of the table', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await restoreBeta(page);

    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(page.locator('.player-title')).toContainText('Gamma Echo');
    await page.getByRole('button', { name: 'Previous track' }).click();
    await expect(page.locator('.player-title')).toContainText('Beta Pulse');
  });

  test('continuous play survives the reload and advances from the restored track', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSYTECH}`);
    await page.locator('#playlist-table tbody tr', { hasText: 'Beta Pulse' }).first().locator('.play-button').click();
    await page.locator('[data-continuous-play]').check();
    await page.locator('.player-toggle').click();
    await expect.poll(async () => (await storedPlayback(page))?.fileId).toBe('drive-beta');

    await page.reload();
    await expect(page.locator('.player-title')).toContainText('Beta Pulse');
    await expect(page.locator('[data-continuous-play]')).toBeChecked();

    // The fixture WAV is 2 s, so the restored track ends quickly and hands over.
    await page.locator('.player-toggle').click();
    await expect(page.locator('.player-title')).toContainText('Gamma Echo', { timeout: 15_000 });
    await expect.poll(async () => (await storedPlayback(page))?.fileId).toBe('drive-gamma');
  });

  test('forgets a track whose Drive file is gone, without breaking the page', async ({ page }) => {
    await mockDrive(page);
    // Registered after mockDrive, so it wins for this one file.
    await page.route('https://www.googleapis.com/drive/v3/files/drive-gone**', (route) =>
      route.fulfill({ status: 404, contentType: 'application/json', body: '{"error":{"code":404}}' }));
    await seedToken(page);
    await page.addInitScript(([k, v]) => {
      if (!sessionStorage.getItem('seeded-playback')) {
        localStorage.setItem(k, v);
        sessionStorage.setItem('seeded-playback', '1');
      }
    }, [PLAYBACK_KEY, JSON.stringify({ v: 1, fileId: 'drive-gone', title: 'Gone', isWav: true, duration: 300, position: 42, savedAt: 1 })]);

    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page.getByRole('row')).toHaveCount(5);
    await expect.poll(() => stored(page, PLAYBACK_KEY)).toBeNull();
    await expect(page.locator('.player-bar')).toHaveCount(0);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('keeps the track through a sign-in and restores it afterwards', async ({ page }) => {
    await mockDrive(page);
    await page.addInitScript(([k, v]) => {
      if (!sessionStorage.getItem('seeded-playback')) {
        localStorage.setItem(k, v);
        sessionStorage.setItem('seeded-playback', '1');
      }
    }, [PLAYBACK_KEY, JSON.stringify({ v: 1, fileId: 'drive-beta', title: 'Beta Pulse', isWav: true, duration: 2, position: 1, savedAt: 1 })]);

    // No token: the tab was closed long enough for it to lapse.
    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page).toHaveURL(/\/login/);
    expect(await storedPlayback(page)).toMatchObject({ fileId: 'drive-beta' });

    // Signed in again (the token Google would hand back).
    await page.evaluate((t) => sessionStorage.setItem('access_token', t), FAKE_TOKEN);
    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page.locator('.player-title')).toContainText('Beta Pulse');
    await expect.poll(async () => (await audioState(page))?.current ?? 0).toBeCloseTo(1, 1);
    expect((await audioState(page))?.paused).toBe(true);
  });

  test('a stored record from another format version is ignored', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.addInitScript((k) => localStorage.setItem(k, '{"v":99,"whatever":true}'), PLAYBACK_KEY);
    await page.goto(`/playlist/${PSYTECH}`);
    await expect(page.getByRole('row')).toHaveCount(5);
    await expect(page.locator('.player-bar')).toHaveCount(0);
  });
});
