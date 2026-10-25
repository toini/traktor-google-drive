import { test, expect } from '@playwright/test';
import type { Page } from '@playwright/test';
import { mockDrive, seedToken, FAKE_TOKEN } from './drive-mock';
import { CAST_DEVICE_NAME, castLoads, castState, mockCastSdk } from './cast-mock';
import type { TicketServer } from './cast-mock';

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
  /** The stand-in for /api/cast/tickets. Tests change its answers through `options`. */
  let server: TicketServer;

  test.beforeEach(async ({ page }) => {
    server = await mockCastSdk(page);
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
    // A relative path means nothing on the far side of the network. The device
    // gets a ticket for this one file, never the Google token.
    expect(load.url).toMatch(/^http:\/\/localhost:\d+\/api\/proxy\/cast\/drive-alpha-sets\?ticket=/);
    expect(load.url).not.toContain(FAKE_TOKEN);
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

  test('keeps playing across a token renewal: the server gets the new token, the TV keeps its URL', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);
    const [mint] = server.calls.filter((c) => c.kind === 'mint');

    // A set outlives the ~1h token. The token lives on the server behind the
    // ticket, so renewing it is a call to the server, not a new LOAD on the TV.
    await expireTokenSoon(page, 'refreshed-token');
    await page.evaluate(() => window.__castTest.tick(1800));

    await expect.poll(() => server.calls.filter((c) => c.kind === 'renew').length).toBeGreaterThan(0);
    const renew = server.calls.find((c) => c.kind === 'renew')!;
    expect(renew.token).toBe('refreshed-token');
    expect(renew.ticket).toBe(mint.ticket);
    expect(renew.renewKey).toBe(mint.renewKey);

    // No rebuffer: still the one load, still playing, from where it was.
    await page.evaluate(() => window.__castTest.tick(1801));
    expect(await castLoads(page)).toHaveLength(1);
    const state = await castState(page);
    expect(state.playerState).toBe('PLAYING');
    expect(state.currentTime).toBe(1801);
  });

  test('a token renewed elsewhere reaches the server without reloading the TV', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    // auth.js renewed on its own schedule (or inside a click).
    await page.evaluate(() => sessionStorage.setItem('access_token', 'renewed-elsewhere'));
    await page.evaluate(() => window.__castTest.tick(60));

    await expect.poll(() => server.calls.find((c) => c.kind === 'renew')?.token).toBe('renewed-elsewhere');
    expect(await castLoads(page)).toHaveLength(1);
  });

  test('a ticket that reaches its cap mid-track is replaced, at the same position', async ({ page }) => {
    // The first ticket starts just outside the five-minute lead, and is inside
    // it a couple of seconds later, by when the set has moved on.
    server.options.ticketLifetimeMs = (n) => (n === 1 ? 5 * 60_000 + 2_000 : 4 * 3600_000);
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    await page.evaluate(() => window.__castTest.tick(899));
    expect(await castLoads(page)).toHaveLength(1);
    await page.waitForTimeout(2_500);
    await page.evaluate(() => window.__castTest.tick(900));

    await expect.poll(async () => (await castLoads(page)).length).toBe(2);
    const loads = await castLoads(page);
    expect(loads[1].currentTime).toBe(900);
    expect(loads[1].url).toContain('ticket=ticket-2-drive-alpha-sets');
    // The old ticket is revoked, and nothing loops: one more tick, no third load.
    await expect.poll(() => server.calls.some((c) => c.kind === 'revoke' && c.ticket === 'ticket-1-drive-alpha-sets')).toBe(true);
    await page.evaluate(() => window.__castTest.tick(901));
    expect(await castLoads(page)).toHaveLength(2);
  });

  test('a renewal the server refuses as expired re-issues the load with a fresh ticket', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    // The server says this ticket is past its cap (410) once; the new one is fine.
    let refused = false;
    server.options.renewStatus = () => (refused ? 204 : ((refused = true), 410));
    await page.evaluate(() => sessionStorage.setItem('access_token', 'renewed-elsewhere'));
    await page.evaluate(() => window.__castTest.tick(1200));

    await expect.poll(async () => (await castLoads(page)).length).toBe(2);
    const loads = await castLoads(page);
    expect(loads[1].currentTime).toBe(1200);
    const mints = server.calls.filter((c) => c.kind === 'mint');
    expect(mints[1].token).toBe('renewed-elsewhere');
  });

  test('no Cast load URL ever contains the Google token', async ({ page }) => {
    // The second track's ticket is already inside the renewal lead.
    server.options.ticketLifetimeMs = (n) => (n === 2 ? 120_000 : 4 * 3600_000);
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);

    // First load, a renewal, a second track, and that track's ticket running out.
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);
    await expireTokenSoon(page, 'refreshed-token');
    await page.evaluate(() => window.__castTest.tick(30));
    await expect.poll(() => server.calls.some((c) => c.kind === 'renew')).toBe(true);

    // Beta's ticket is inside the lead from the start, so its first PLAYING tick
    // already replaces it: two loads for Beta, three in all.
    await playButton(page, 'drive-beta').first().click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(3);
    expect((await castLoads(page)).slice(1).every((l) => l.url.includes('/drive-beta?'))).toBe(true);

    for (const load of await castLoads(page)) {
      expect(load.url).not.toContain(FAKE_TOKEN);
      expect(load.url).not.toContain('refreshed-token');
      expect(load.url).not.toMatch(/[?&](access_)?token=/);
      expect(load.url).toContain('ticket=');
    }
    // The token itself went to the server in a header, on calls whose URLs carry nothing.
    const sent = server.calls.filter((c) => c.kind !== 'revoke');
    expect(sent.map((c) => c.token)).toEqual(expect.arrayContaining([FAKE_TOKEN, 'refreshed-token']));
    for (const call of server.calls) {
      expect(call.url).not.toContain(FAKE_TOKEN);
      expect(call.url).not.toContain('refreshed-token');
      expect(new URL(call.url).search).toBe('');
    }
  });

  test('stopping the cast revokes its ticket', async ({ page }) => {
    await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await connect(page);
    await playButton(page, ALPHA_IN_SETS).click();
    await expect.poll(async () => (await castLoads(page)).length).toBe(1);

    await castButton(page).click();
    await expect.poll(() => server.calls.filter((c) => c.kind === 'revoke').map((c) => c.ticket))
      .toContain('ticket-1-drive-alpha-sets');
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
