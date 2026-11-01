import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { mockDrive, noPrefetch, type MockOptions } from './drive-mock';
import { FAKE_GOOGLE_PORT } from './playwright.auth.config';

/**
 * The code flow with a server-held refresh token (ADR 20261101-38), against the
 * real ASP.NET host with the flag on and Google's token endpoint faked
 * (fake-google.mjs). Drive itself is mocked per page, as everywhere else.
 *
 * GIS is replaced by a fake that behaves like the real one in a browser: either
 * client opens a popup, and a popup with no user activation behind it is
 * blocked. Activation is modelled as Chrome's, 5 s after a trusted pointer
 * press, because Playwright's page.evaluate itself counts as a gesture.
 */

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';
const ALPHA = 'drive-alpha-sets';
const LONG: MockOptions = { wavSeconds: 300, maxSliceBytes: 256 * 1024 };
const SESSION = 'tgd_session';

const control = async (path: string) => {
  const r = await fetch(`http://localhost:${FAKE_GOOGLE_PORT}/__control/${path}`, { method: 'POST' });
  return r.json();
};
type FakeState = { codeExchanges: number; refreshes: number; revokes: number; revoked: string[] };
const google = async () => (await control('state')) as FakeState;

type Gis = { nextCode: string | null; codeCalls: { active: boolean }[]; tokenCalls: number; active: () => boolean };

/** Fake GIS with both clients. The token client must never be used with the code flow on. */
async function fakeGis(page: Page, nextCode: string | null = 'code-toni') {
  await page.route('https://accounts.google.com/**', (route) =>
    route.fulfill({ status: 200, contentType: 'text/javascript', body: '' }));
  await page.addInitScript((nextCode) => {
    let lastPress = -Infinity;
    addEventListener('pointerdown', (e) => { if (e.isTrusted) lastPress = performance.now(); }, true);
    // Counted across the sign-in's own page load, which starts a new document.
    const saved = JSON.parse(sessionStorage.getItem('__gis') ?? '{"codeCalls":[],"tokenCalls":0}');
    const gis: Gis = { nextCode, ...saved, active: () => performance.now() - lastPress < 5000 };
    const save = () => sessionStorage.setItem('__gis', JSON.stringify({ codeCalls: gis.codeCalls, tokenCalls: gis.tokenCalls }));
    (window as any).__gis = gis;
    (window as any).google = {
      accounts: {
        oauth2: {
          initCodeClient: (cfg: any) => ({
            requestCode: () => {
              const active = gis.active();
              gis.codeCalls.push({ active });
              save();
              if (!active) {
                setTimeout(() => cfg.error_callback?.({ type: 'popup_failed_to_open' }), 10);
                return;
              }
              setTimeout(() => gis.nextCode
                ? cfg.callback({ code: gis.nextCode, state: cfg.state, scope: cfg.scope })
                : cfg.error_callback?.({ type: 'popup_closed' }), 50);
            },
          }),
          initTokenClient: (cfg: any) => ({
            requestAccessToken: () => {
              gis.tokenCalls++;
              save();
              setTimeout(() => cfg.error_callback?.({ type: 'popup_failed_to_open' }), 10);
            },
          }),
        },
      },
    };
  }, nextCode);
}

const gisCalls = (page: Page) =>
  page.evaluate(() => ({ code: (window as any).__gis.codeCalls.length as number, token: (window as any).__gis.tokenCalls as number }));

const sessionCookie = async (context: BrowserContext) =>
  (await context.cookies()).find((c) => c.name === SESSION);

/** Counts the page's calls to the server's token endpoint. */
function watchTokenRequests(page: Page) {
  const seen: string[] = [];
  page.on('request', (r) => { if (r.url().endsWith('/api/auth/token')) seen.push(r.postData() ?? ''); });
  return seen;
}

/** The first sign-in: the only time the code flow's popup opens. */
async function signIn(page: Page) {
  await page.goto('/login');
  await page.getByRole('button', { name: /sign in with google/i }).click();
  await expect(page).toHaveURL(/\/music/);
  await expect(page.getByText('My Sets')).toBeVisible();
}

test.beforeEach(async () => { await control('reset'); });

test('the first sign-in uses the code popup and leaves a session the page cannot read', async ({ page, context }) => {
  await mockDrive(page);
  await fakeGis(page);
  await signIn(page);

  expect(await gisCalls(page)).toEqual({ code: 1, token: 0 });
  expect((await google()).codeExchanges).toBe(1);

  const cookie = await sessionCookie(context);
  expect(cookie).toBeDefined();
  expect(cookie!.httpOnly).toBe(true);
  expect(cookie!.sameSite).toBe('Strict');
  expect(cookie!.path).toBe('/api/');
  expect(cookie!.value).not.toContain('rt-');
  // Roughly 180 days out.
  expect(cookie!.expires * 1000 - Date.now()).toBeGreaterThan(170 * 24 * 3600 * 1000);

  // Not readable by script, and the refresh token is nowhere in the page's storage.
  const visible = await page.evaluate(() => ({
    cookie: document.cookie,
    storage: JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage }),
  }));
  expect(visible.cookie).not.toContain(SESSION);
  expect(visible.storage).not.toContain('rt-');
  expect(await page.evaluate(() => sessionStorage.getItem('access_token'))).toBe('fixture-access-token');
});

test('no popup across a simulated hour: every renewal is a request to the server', async ({ page, context }) => {
  // Five-minute tokens: inside the server's refresh lead almost at once, so each
  // renewal below makes the server go to Google, as it does once an hour for real.
  await control('expires-in?s=301');
  await mockDrive(page);
  await fakeGis(page);
  await signIn(page);
  const tokenRequests = watchTokenRequests(page);
  const before = await google();

  const seen = new Set<string>();
  for (let i = 0; i < 4; i++) {
    await page.waitForTimeout(1100);
    // Three minutes left on the page's clock: inside auth.js's 4-minute lead.
    await page.evaluate(() => sessionStorage.setItem('access_token_expires_at', String(Date.now() + 3 * 60_000)));
    await page.evaluate(() => (window as any).authCheckRenewal());
    await expect.poll(async () => (await google()).refreshes).toBe(before.refreshes + i + 1);
    seen.add((await page.evaluate(() => sessionStorage.getItem('access_token')))!);
  }

  expect(tokenRequests.length).toBeGreaterThanOrEqual(4);
  expect(seen).toEqual(new Set(['fixture-access-token', 'renewed-access-token']));
  // The whole time: no popup of either kind beyond the sign-in itself.
  expect(await gisCalls(page)).toEqual({ code: 1, token: 0 });

  // An expired token at load (a laptop back from sleep) renews silently instead of /login.
  await page.evaluate(() => sessionStorage.setItem('access_token_expires_at', String(Date.now() - 60_000)));
  await page.reload();
  await expect(page.getByText('My Sets')).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);

  // A new tab has no sessionStorage at all: the server session signs it in.
  const tab = await context.newPage();
  await mockDrive(tab);
  await fakeGis(tab);
  await tab.goto('/music');
  await expect(tab.getByText('My Sets')).toBeVisible();
  expect(await gisCalls(tab)).toEqual({ code: 0, token: 0 });
});

// The server learns of a revocation at its next refresh. Five-minute tokens keep
// every request inside its refresh lead, so that is the very next request here.
// For real it is within the hour, or at once when Drive refuses the token.

test('a revoked refresh token lands on a clean sign-in, and signing in restores the session', async ({ page, context }) => {
  await control('expires-in?s=301');
  await mockDrive(page);
  await fakeGis(page);
  await signIn(page);

  // Revoked at myaccount.google.com (or six months unused): Google says invalid_grant.
  await control('revoke-all');
  await page.waitForTimeout(1100);
  await page.evaluate(() => sessionStorage.setItem('access_token_expires_at', String(Date.now() - 60_000)));
  await page.reload();

  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByRole('button', { name: /sign in with google/i })).toBeVisible();
  expect(await sessionCookie(context)).toBeUndefined();
  expect(await page.evaluate(() => localStorage.getItem('auth:server-session'))).toBeNull();
  // No popup came up on its own: the one code request is the first sign-in.
  expect(await gisCalls(page)).toEqual({ code: 1, token: 0 });

  await page.getByRole('button', { name: /sign in with google/i }).click();
  await expect(page).toHaveURL(/\/music/);
  expect(await sessionCookie(context)).toBeDefined();
});

test('revoked mid-set: playback pauses with "Sign in again" and resumes at the same place', async ({ page }) => {
  await control('expires-in?s=301');
  let valid = new Set(['fixture-access-token', 'renewed-access-token']);
  await mockDrive(page, { ...LONG, acceptToken: (t) => t !== null && valid.has(t) });
  await noPrefetch(page);
  await fakeGis(page);
  await signIn(page);

  await page.goto(`/playlist/${PSYTECH_UUID}`);
  await page.locator(`.play-button[data-drive-file-id="${ALPHA}"]`).click();
  const audio = () => page.evaluate(async () => {
    const a = (await import('/player.js')).element();
    return { current: a?.currentTime ?? -1, paused: a?.paused ?? true };
  });
  await expect.poll(async () => (await audio()).current).toBeGreaterThan(0.3);
  await expect(page.locator('.waveform')).toHaveAttribute('data-waveform-state', 'ready');
  // Let the play click's activation run out: what follows happens an hour in.
  await expect.poll(() => page.evaluate(() => (window as any).__gis.active()), { timeout: 10_000 }).toBe(false);

  // The grant is revoked, so Drive refuses the token and Google refuses the refresh.
  await control('revoke-all');
  valid = new Set();
  await page.evaluate(async (s) => (await import('/player.js')).seek(s), 250);

  const signInAgain = page.locator('[data-player-signin-button]');
  await expect(signInAgain).toBeVisible();
  await expect(page.locator('[data-player-signin]')).toContainText(/session expired/i);
  await expect(page).toHaveURL(new RegExp(`/playlist/${PSYTECH_UUID}`));
  await expect(page.locator('.error-banner')).toHaveCount(0);
  // Nothing opened by itself: the only code request so far is the sign-in's.
  const calls = await page.evaluate(() => (window as any).__gis.codeCalls as { active: boolean }[]);
  expect(calls.filter((c) => c.active)).toHaveLength(1);

  valid = new Set(['fixture-access-token', 'renewed-access-token']);
  await signInAgain.click();
  await expect(signInAgain).toHaveCount(0);
  await expect.poll(async () => (await audio()).current, { timeout: 15_000 }).toBeGreaterThan(250.5);
  expect((await audio()).current).toBeLessThan(260);
  expect((await google()).codeExchanges).toBe(2);
});

test('sign out revokes at Google and the next visit needs a sign-in', async ({ page, context }) => {
  await mockDrive(page);
  await fakeGis(page);
  await signIn(page);

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login/);
  expect((await google()).revokes).toBe(1);
  expect(await sessionCookie(context)).toBeUndefined();
  expect(await page.evaluate(() => localStorage.getItem('auth:server-session'))).toBeNull();

  await page.goto('/music');
  await expect(page).toHaveURL(/\/login/);
  expect(await gisCalls(page)).toEqual({ code: 1, token: 0 });
});

test('a user off the allowlist gets no session', async ({ page, context }) => {
  await mockDrive(page);
  await fakeGis(page, 'code-mallory');
  await page.goto('/login');
  await page.getByRole('button', { name: /sign in with google/i }).click();

  await expect(page.getByRole('alert')).toContainText(/cancelled or failed/i);
  await expect(page).toHaveURL(/\/login/);
  expect(await sessionCookie(context)).toBeUndefined();
  expect((await google()).revokes).toBe(1);
});
