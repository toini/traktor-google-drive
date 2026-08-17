import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import { mockDrive, seedToken, FAKE_TOKEN } from './drive-mock';
import { CAST_DEVICE_NAME, castLoads, castState, mockCastSdk } from './cast-mock';

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

const castButton = (page: Page) => page.locator('.cast-button');
const castBar = (page: Page) => page.locator('[data-cast-bar]');

/**
 * By Drive id, not by accessible name: the name flips between "Play X" and
 * "Pause X", so a name-based locator silently retargets a different row once
 * playback starts. Two fixture tracks are also both called "Alpha Drift".
 */
const playButton = (page: Page, driveFileId: string) =>
  page.locator(`.play-button[data-drive-file-id="${driveFileId}"]`);

const ALPHA_IN_SETS = 'drive-alpha-sets';

/** Connects to the fake device and waits for the app to notice. */
async function connect(page: Page): Promise<void> {
  await expect(castButton(page)).toHaveAttribute('data-cast-state', 'ready');
  await castButton(page).click();
  await expect(castButton(page)).toHaveAttribute('data-cast-state', 'connected');
}

/** Marks the stored token as nearly expired, and installs a refresh outcome. */
async function expireTokenSoon(page: Page, refreshed: string | null): Promise<void> {
  await page.evaluate((next) => {
    sessionStorage.setItem('access_token_expires_at', String(Date.now() + 120_000));
    window['authRefreshToken'] = () => {
      if (next === null) return Promise.resolve(null);
      sessionStorage.setItem('access_token', next);
      sessionStorage.setItem('access_token_expires_at', String(Date.now() + 3_600_000));
      return Promise.resolve(next);
    };
  }, refreshed);
}

test.describe('cast sender', () => {
  test.beforeEach(async ({ page }) => {
    await mockCastSdk(page);
    await seedToken(page);
  });

  test('offers casting once the sender SDK is present', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await expect(castButton(page)).toHaveAttribute('data-cast-state', 'ready');
    await expect(castBar(page)).toHaveCount(0);
  });

  test('asks for the default media receiver, scoped to this origin', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await expect(castButton(page)).toHaveAttribute('data-cast-state', 'ready');

    const options = (await castState(page)).optionsSeen;
    expect(options?.receiverApplicationId).toBe('CC1AD845');
    expect(options?.autoJoinPolicy).toBe('origin_scoped');
  });

  test('shows the device in a casting bar once connected', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await connect(page);
    await expect(castBar(page)).toContainText(CAST_DEVICE_NAME);
  });

  test('sends an absolute proxy URL, because the device fetches the audio itself', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    const [load] = await castLoads(page);
    // A relative path means nothing on the far side of the network.
    expect(load.url).toMatch(/^http:\/\/localhost:\d+\/api\/proxy\/drive\//);
    expect(load.url).toContain(FAKE_TOKEN);
    expect(load.contentType).toBe('audio/wav');
    expect(load.streamType).toBe('BUFFERED');
    expect(load.currentTime).toBe(0);
  });

  test('labels the track on the TV with its Traktor metadata', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    const [load] = await castLoads(page);
    expect(load.title).toBe('Alpha Drift');
    expect(load.artist).toBe('Test Artist One');
    await expect(castBar(page)).toContainText('Alpha Drift');
  });

  test('this browser never fetches the audio while casting', async ({ page }) => {
    const calls = await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    // The point of casting is that the download happens on the device. A media
    // request from the page would mean both are streaming the same 1-2 GB file.
    expect(calls.filter((c) => c.kind === 'media')).toHaveLength(0);
  });

  test('a second press pauses on the device instead of reloading the file', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    const play = playButton(page, ALPHA_IN_SETS);
    await play.click();
    await expect.poll(async () => (await castState(page)).playerState).toBe('PLAYING');

    await play.click();
    await expect.poll(async () => (await castState(page)).playerState).toBe('PAUSED');
    // Reloading a 1-2 GB file just to pause it would restart the download.
    expect((await castLoads(page)).length).toBe(1);
  });

  test('keeps casting when the user browses to another page', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castState(page)).playerState).toBe('PLAYING');

    // Router navigation, not a reload: leaving a playlist stops the local
    // element, and must not stop the TV.
    await page.locator('.traktor-logo').click();
    await expect(page).toHaveURL(/\/music$/);

    await expect(castBar(page)).toContainText(CAST_DEVICE_NAME);
    expect((await castState(page)).playerState).toBe('PLAYING');
  });

  test('stops casting when the button is pressed again', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await connect(page);

    await castButton(page).click();
    await expect(castButton(page)).toHaveAttribute('data-cast-state', 'ready');
    await expect(castBar(page)).toHaveCount(0);
  });

  test('surfaces a rejected load rather than sitting on a spinner', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    await page.evaluate(() => window.__castTest.failNextLoad('LOAD_FAILED'));
    await playButton(page, ALPHA_IN_SETS).click();

    await expect(page.locator('.error-banner')).toBeVisible();
    await expect(page.locator('.error-summary').first()).toContainText(/casting failed/i);
  });

  test('replaces an expiring token mid-set and resumes at the same position', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    // The device holds the token inside its media URL, so a set longer than the
    // ~1h token has to be re-issued while it is still playing.
    await expireTokenSoon(page, 'refreshed-token');
    await page.evaluate(() => window.__castTest.tick(1800));

    await expect.poll(async () => (await castLoads(page)).length).toBe(2);
    const loads = await castLoads(page);
    expect(loads[1].url).toContain('refreshed-token');
    expect(loads[1].currentTime).toBe(1800);
  });

  test('reports a refused refresh once, not every second', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    await expireTokenSoon(page, null);
    for (const seconds of [10, 20, 30]) {
      await page.evaluate((s) => window.__castTest.tick(s), seconds);
    }

    await expect(page.locator('.error-summary').first()).toContainText(/token expires/i);
    // Once a second for two hours is not a useful error log.
    await expect(page.locator('.error-banner-title')).toHaveText('1 error');
  });
});

test.describe('cast sender without the SDK', () => {
  test('says casting is unavailable instead of failing', async ({ page }) => {
    // mockDrive aborts the sender SDK, which is what a browser with no Cast
    // support amounts to.
    await mockDrive(page);
    await seedToken(page);
    await page.goto('/music');

    await expect(castButton(page)).toHaveAttribute('data-cast-state', 'unavailable');
    await expect(castButton(page)).toHaveAttribute('title', /Chrome or Edge/i);
    await expect(page.locator('#blazor-error-ui')).toBeHidden();
  });
});
