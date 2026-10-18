import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken, FAKE_TOKEN, type DriveCall, type MockOptions } from './drive-mock';

/**
 * Local playback across the ~1h Google token lifetime.
 *
 * The bug: the proxy URL used to carry ?token=, fixed when the track loaded,
 * and the <audio> element fetched every later range slice with it. An hour in,
 * Drive 401'd the next slice and the set stopped (MEDIA_ERR_NETWORK, reported
 * as a generic "Playback failed"). Now auth.js keeps the token in a cookie the
 * proxy reads, renews it before expiry, and the player recovers from a 401.
 *
 * Google Identity Services is replaced by a fake that behaves like the real
 * token client in a browser: requestAccessToken opens a popup, and a popup
 * with no user activation behind it is blocked (popup_failed_to_open).
 * Activation is modelled as Chrome's: 5 s after a real (trusted) pointer
 * press. navigator.userActivation cannot be used here, because Playwright's
 * page.evaluate itself runs as a user gesture.
 */

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';
const GIG = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f66';
const ALPHA = 'drive-alpha-sets';
const RENEWED = 'renewed-access-token';

/** Long enough that the browser comes back for more slices after a far seek. */
const LONG: MockOptions = { wavSeconds: 300, maxSliceBytes: 256 * 1024 };

type Gis = { next: string | null; popupsAllowed: boolean; calls: { prompt?: string; active: boolean }[] };

/** Fake GIS token client. `popupsAllowed` = the user allowed popups for the site. */
async function fakeGis(page: Page, next: string | null, popupsAllowed: boolean) {
  await page.route('https://accounts.google.com/**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/javascript', body: '' }));
  await page.addInitScript(({ next, popupsAllowed }) => {
    let lastPress = -Infinity;
    addEventListener('pointerdown', (e) => { if (e.isTrusted) lastPress = performance.now(); }, true);
    const gis: Gis & { active: () => boolean } = {
      next, popupsAllowed, calls: [], active: () => performance.now() - lastPress < 5000,
    };
    (window as any).__gis = gis;
    (window as any).google = {
      accounts: {
        oauth2: {
          initTokenClient: (cfg: any) => ({
            requestAccessToken: (opts: any) => {
              const active = gis.active();
              gis.calls.push({ prompt: opts?.prompt, active });
              if (!active && !gis.popupsAllowed) {
                setTimeout(() => cfg.error_callback?.({ type: 'popup_failed_to_open' }), 10);
                return;
              }
              setTimeout(() => gis.next
                ? cfg.callback({ access_token: gis.next, expires_in: 3600 })
                : cfg.error_callback?.({ type: 'popup_closed' }), 50);
            },
          }),
        },
      },
    };
  }, { next, popupsAllowed });
}

const playButton = (page: Page, id: string) => page.locator(`.play-button[data-drive-file-id="${id}"]`);
const signIn = (page: Page) => page.locator('[data-player-signin-button]');
const title = (page: Page) => page.locator('.player-title');

const audio = (page: Page) =>
  page.evaluate(async () => {
    const a = (await import('/player.js')).element();
    return { current: a?.currentTime ?? -1, paused: a?.paused ?? true };
  });

const seek = (page: Page, seconds: number) =>
  page.evaluate(async (s) => (await import('/player.js')).seek(s), seconds);

const media = (calls: DriveCall[]) => calls.filter((c) => c.kind === 'media');

/** Plays the long Alpha track until it is audibly running. */
async function startAlpha(page: Page) {
  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await playButton(page, ALPHA).click();
  await expect.poll(async () => (await audio(page)).current).toBeGreaterThan(0.3);
}

/**
 * Lets the play click's transient user activation (5 s in Chrome) run out, so
 * what follows happens the way it does an hour into a set: with no click behind it.
 */
async function waitForActivationToLapse(page: Page) {
  await expect.poll(() => page.evaluate(() => (window as any).__gis.active()), { timeout: 10_000 }).toBe(false);
}

const setGisNext = (page: Page, next: string | null) =>
  page.evaluate((n) => { (window as any).__gis.next = n; }, next);

/** Playing again, past `seconds`, with nothing paused in between. */
async function expectPlayingPast(page: Page, seconds: number) {
  await expect.poll(async () => (await audio(page)).current, { timeout: 15_000 }).toBeGreaterThan(seconds + 0.5);
  expect((await audio(page)).paused).toBe(false);
}

test.describe('token expiry during local playback', () => {
  test('media URLs carry no token: the proxy gets it from a cookie', async ({ page }) => {
    const calls = await mockDrive(page, LONG);
    await seedToken(page);
    await startAlpha(page);

    const requests = media(calls);
    expect(requests.length).toBeGreaterThan(0);
    for (const c of requests) {
      expect(c.url).not.toContain('token=');
      expect(c.token).toBe(FAKE_TOKEN);
    }
  });

  test('renews before expiry, so playback carries on past the old token', async ({ page }) => {
    let valid = new Set([FAKE_TOKEN, RENEWED]);
    const calls = await mockDrive(page, { ...LONG, acceptToken: (t) => t !== null && valid.has(t) });
    await fakeGis(page, RENEWED, true);
    await seedToken(page);
    // Five minutes left: inside the renewal window.
    await page.addInitScript(() =>
      sessionStorage.setItem('access_token_expires_at', String(Date.now() + 5 * 60_000)));
    await startAlpha(page);

    await page.evaluate(() => (window as any).authCheckRenewal());
    await expect.poll(() => page.evaluate(() => sessionStorage.getItem('access_token'))).toBe(RENEWED);

    // The old token's hour is up. The next slice must already use the new one.
    valid = new Set([RENEWED]);
    const before = calls.length;
    await seek(page, 250);
    await expectPlayingPast(page, 250);

    const after = media(calls.slice(before));
    expect(after.length).toBeGreaterThan(0);
    expect(after.every((c) => c.token === RENEWED && c.status !== 401)).toBe(true);
    await expect(signIn(page)).toHaveCount(0);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('a blocked background renewal is retried inside the next click', async ({ page }) => {
    await mockDrive(page, LONG);
    await fakeGis(page, RENEWED, false);
    await seedToken(page);
    await startAlpha(page);
    await waitForActivationToLapse(page);
    await page.evaluate(() =>
      sessionStorage.setItem('access_token_expires_at', String(Date.now() + 5 * 60_000)));

    // From a timer the popup is blocked, exactly as a browser would.
    await page.evaluate(() => (window as any).authCheckRenewal());
    await expect.poll(() => page.evaluate(() => (window as any).__gis.calls.length)).toBeGreaterThan(0);
    expect(await page.evaluate(() => sessionStorage.getItem('access_token'))).toBe(FAKE_TOKEN);

    // Any click carries user activation, so the popup may open there.
    await page.locator('.player-title').click();
    await expect.poll(() => page.evaluate(() => sessionStorage.getItem('access_token'))).toBe(RENEWED);
    const calls = await page.evaluate(() => (window as any).__gis.calls as Gis['calls']);
    expect(calls.at(-1)).toEqual({ prompt: '', active: true });
    expect((await audio(page)).paused).toBe(false);
  });

  test('a 401 mid-set renews once and continues from the same place', async ({ page }) => {
    let valid = new Set([FAKE_TOKEN]);
    const calls = await mockDrive(page, { ...LONG, acceptToken: (t) => t !== null && valid.has(t) });
    // Popups allowed for the site: the silent renewal goes through.
    await fakeGis(page, RENEWED, true);
    await seedToken(page);
    await startAlpha(page);

    // Drive stops accepting the token before our clock says it expired.
    valid = new Set([RENEWED]);
    const before = calls.length;
    await seek(page, 250);
    await expectPlayingPast(page, 250);
    expect((await audio(page)).current).toBeLessThan(260); // resumed there, not restarted

    const after = media(calls.slice(before));
    expect(after.some((c) => c.status === 401 && c.token === FAKE_TOKEN)).toBe(true);
    expect(after.some((c) => c.status === 206 && c.token === RENEWED)).toBe(true);
    await expect(signIn(page)).toHaveCount(0);
    await expect(page.locator('.error-banner')).toHaveCount(0);
  });

  test('when renewal needs the user, it pauses with a sign-in that resumes at the same position', async ({ page }) => {
    let valid = new Set([FAKE_TOKEN]);
    await mockDrive(page, { ...LONG, acceptToken: (t) => t !== null && valid.has(t) });
    await fakeGis(page, RENEWED, false); // popups blocked outside a click
    await seedToken(page);
    await startAlpha(page);
    await waitForActivationToLapse(page);

    valid = new Set([RENEWED]);
    await seek(page, 250);

    await expect(signIn(page)).toBeVisible();
    await expect(page.locator('[data-player-signin]')).toContainText(/session expired/i);
    // Not a stack-trace error, and not a trip to /login that loses the place.
    await expect(page).toHaveURL(new RegExp(`/playlist/${PSYTECH_UUID}`));
    await expect(page.locator('.error-banner')).toHaveCount(0);
    await expect(page.locator('.player-time')).toContainText('4:10');

    await signIn(page).click();
    await expect(signIn(page)).toHaveCount(0);
    await expectPlayingPast(page, 250);
    expect((await audio(page)).current).toBeLessThan(260);
  });
});

test.describe('token expiry in gig preview', () => {
  const row = (page: Page, t: string) => page.locator('#playlist-table tbody tr', { hasText: t }).first();
  const range = (page: Page) => page.locator('[data-preview-range]');

  test('a rejected token on the first load keeps the preview range across the sign-in', async ({ page }) => {
    let valid = new Set<string>(); // Drive already rejects the stored token
    await mockDrive(page, { acceptToken: (t) => t !== null && valid.has(t) });
    // The click that starts the preview may open Google's popup; here the
    // user closes it.
    await fakeGis(page, null, false);
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await expect(row(page, 'Gig One')).toBeVisible();

    await page.locator('[data-preview-set]').click();
    await expect(signIn(page)).toBeVisible();
    await expect(page.locator('[data-preview-chip]')).toContainText('1/4');
    await expect(range(page)).toHaveAttribute('data-end', '1.5');

    valid = new Set([RENEWED]);
    await setGisNext(page, RENEWED);
    await signIn(page).click();
    // From the load cue, not from 0, and the end bound still advances it.
    await expect.poll(async () => (await audio(page)).current).toBeGreaterThanOrEqual(0.3);
    await expect(title(page)).toContainText('Gig Two', { timeout: 10_000 });
    await expect(range(page)).toHaveAttribute('data-end', '0.6');
  });

  test('a token that ran out between tracks: sign in starts the next one with its range', async ({ page }) => {
    let valid = new Set([FAKE_TOKEN]);
    await mockDrive(page, { acceptToken: (t) => t !== null && valid.has(t) });
    // Renewal fails at the advance (popup closed, or blocked a whole set after
    // the last click); it works once the user clicks sign in.
    await fakeGis(page, null, false);
    await seedToken(page);
    await page.goto(`/playlist/${GIG}`);
    await expect(row(page, 'Gig One')).toBeVisible();

    await page.locator('[data-preview-set]').click();
    await expect(title(page)).toContainText('Gig One');
    // The hour ends while Gig One plays (its 2 s file is already buffered).
    await page.evaluate(() => sessionStorage.setItem('access_token_expires_at', String(Date.now() - 1000)));
    valid = new Set([RENEWED]);

    // The advance cannot get a token silently: it waits on Gig Two, preview intact.
    await expect(signIn(page)).toBeVisible({ timeout: 10_000 });
    await expect(title(page)).toContainText('Gig Two');
    await expect(page.locator('[data-preview-chip]')).toContainText('2/4');
    await expect(range(page)).toHaveAttribute('data-start', '0');
    await expect(range(page)).toHaveAttribute('data-end', '0.6');

    await setGisNext(page, RENEWED);
    await signIn(page).click();
    await expect(signIn(page)).toHaveCount(0);
    // Gig Two plays to its 0.6 s bound, then the preview moves on.
    await expect(title(page)).toContainText('Gig Three', { timeout: 10_000 });
    await expect(range(page)).toHaveAttribute('data-start', '0.4');
  });
});
