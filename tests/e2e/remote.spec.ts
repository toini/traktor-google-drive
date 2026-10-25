import { test, expect } from '@playwright/test';
import type { Browser, BrowserContext, Page } from '@playwright/test';
import * as signalR from '@microsoft/signalr';
import { mockDrive, seedToken, FAKE_TOKEN } from './drive-mock';
import type { MockOptions } from './drive-mock';
import { CAST_DEVICE_NAME, castLoads, castState, mockCastSdk } from './cast-mock';
import { PORT } from './playwright.remote.config';

// Remote control, increment 1 (ADR 20261018-63 §6): a second device sees what
// the first plays and can pause, play, seek and skip it. Every test is two (or
// three) browser contexts, i.e. separate devices with their own storage,
// against one ASP.NET host.

const PSYTECH = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';
const ORIGIN = `http://localhost:${PORT}`;
const OTHER_USER_TOKEN = 'other-user-token';

type Device = { context: BrowserContext; page: Page; frames: string[] };

type DeviceOptions = {
  name: string;
  token?: string;
  cast?: boolean;
  /** Extra Drive mock options, e.g. which tokens the proxy accepts. */
  mock?: MockOptions;
  /** Script run before the app boots, e.g. to seed localStorage. */
  init?: () => void;
};

// Every device shares one room (one fake user), so a device left over from the
// previous test would still be in it, playing. Each test closes its own.
const open: BrowserContext[] = [];
test.afterEach(async () => {
  await Promise.all(open.splice(0).map((c) => c.close().catch(() => {})));
});

/** A browser context with remote control switched on, signed in as `token`'s user. */
async function device(browser: Browser, opts: DeviceOptions): Promise<Device> {
  const context = await browser.newContext();
  open.push(context);
  const page = await context.newPage();
  // Two minutes, not the suite's usual 2 s: a remote has to catch the track
  // while it is still playing.
  await mockDrive(page, { wavSeconds: 120, ...opts.mock });
  // The real server mints the Cast tickets here, so leave its endpoints alone.
  if (opts.cast) await mockCastSdk(page, CAST_DEVICE_NAME, { realServer: true });
  await seedToken(page, opts.token ?? FAKE_TOKEN);
  await page.addInitScript((name) => {
    localStorage.setItem('remote:enabled', '1');
    localStorage.setItem('remote:device-name', name as string);
  }, opts.name);
  if (opts.init) await page.addInitScript(opts.init);

  // Every WebSocket frame either way, for the token-leak assertion.
  const frames: string[] = [];
  page.on('websocket', (ws) => {
    ws.on('framereceived', (f) => frames.push(String(f.payload)));
    ws.on('framesent', (f) => frames.push(String(f.payload)));
  });
  return { context, page, frames };
}

const strip = (page: Page) => page.locator('[data-remote-strip]');
const bar = (page: Page) => page.locator('.player-bar');
const connected = (page: Page) =>
  expect(page.locator('[data-remote-toggle]')).toHaveAttribute('data-remote-connected', 'true');

/**
 * A device's own playback, read from its player bar: the toggle offers "Pause"
 * only while its element is playing, and the clock is the element's.
 */
const playerAudio = async (page: Page) => {
  const toggle = page.locator('.player-toggle');
  if (!(await toggle.count())) return null;
  const label = await toggle.getAttribute('aria-label');
  const clock = (await page.locator('.player-time span').first().textContent()) ?? '0:00';
  const [m, sec] = clock.trim().split(':').map(Number);
  return { paused: label !== 'Pause', time: m * 60 + sec };
};

/** Starts the first row on `page` and waits for the player to report playing. */
async function playFirstRow(page: Page): Promise<string> {
  await page.goto(`/playlist/${PSYTECH}`);
  const row = page.locator('#playlist-table tbody tr').first();
  await row.locator('.play-button').click();
  await expect(bar(page)).toBeVisible();
  await expect.poll(async () => (await playerAudio(page))?.paused).toBe(false);
  return (await page.locator('.player-title').textContent())!.trim();
}

/** A controller sitting on the music page, connected to the room. */
async function controllerOn(d: Device): Promise<void> {
  await d.page.goto('/music');
  await connected(d.page);
}

test.describe('remote control', () => {
  test('a second device sees what the first is playing', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });

    const title = await playFirstRow(laptop.page);
    await controllerOn(tablet);

    await expect(strip(tablet.page)).toContainText('Playing on Laptop · Chrome');
    await expect(tablet.page.locator('.player-title')).toHaveText(title);
    await expect(bar(tablet.page)).toHaveAttribute('data-remote', 'true');
    // The laptop's own bar is not a remote.
    await expect(bar(laptop.page)).toHaveAttribute('data-remote', 'false');
  });

  test('pause, play, seek and next from the tablet drive the laptop', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    const firstTitle = await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toBeVisible();

    const toggle = tablet.page.locator('.player-toggle');

    await toggle.click(); // pause
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(true);
    await expect(toggle).toHaveAttribute('aria-label', 'Play');

    await toggle.click(); // play
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(false);

    // Seek: the tablet has no waveform yet (increment 2), so it gets the slider.
    await tablet.page.locator('.player-seek').evaluate((el: HTMLInputElement) => {
      el.value = '500';
      el.dispatchEvent(new Event('change', { bubbles: true }));
    });
    // Half way through the 2-minute fixture.
    await expect.poll(async () => (await playerAudio(laptop.page))?.time ?? 0).toBeGreaterThanOrEqual(55);

    await tablet.page.getByRole('button', { name: 'Next track' }).click();
    await expect(laptop.page.locator('.player-title')).not.toHaveText(firstTitle);
    await expect(tablet.page.locator('.player-title')).toHaveText(
      (await laptop.page.locator('.player-title').textContent())!.trim(),
    );
  });

  test('a controller never receives the player\'s token', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toBeVisible();
    await tablet.page.locator('.player-toggle').click();
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(true);

    expect(tablet.frames.length).toBeGreaterThan(0);
    for (const frame of [...tablet.frames, ...laptop.frames]) {
      expect(frame).not.toContain(FAKE_TOKEN);
      expect(frame).not.toContain('token=');
    }
  });

  test('another Google account sees nothing and cannot reach the player', async ({ browser, request }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    await playFirstRow(laptop.page);

    // Another user, in the app.
    const stranger = await device(browser, { name: 'Stranger', token: OTHER_USER_TOKEN });
    await controllerOn(stranger);
    await stranger.page.waitForTimeout(2000);
    await expect(strip(stranger.page)).toHaveCount(0);
    await expect(bar(stranger.page)).toHaveCount(0);

    // And outside it: a hand-built client with the other user's ticket.
    const ticket = await request.post('/api/remote/ticket', {
      headers: { Authorization: `Bearer ${OTHER_USER_TOKEN}`, Origin: ORIGIN },
    });
    expect(ticket.ok()).toBe(true);
    const { ticket: t } = await ticket.json();
    const hub = new signalR.HubConnectionBuilder()
      .withUrl(`${ORIGIN}/api/remote/hub`, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
        accessTokenFactory: () => t,
        headers: { Origin: ORIGIN },
      })
      .configureLogging(signalR.LogLevel.None)
      .build();
    await hub.start();
    const room = await hub.invoke('Join', { deviceId: 'x', tabId: 'y', name: 'crafted' });
    expect(room.snapshot).toBeNull();
    expect(room.devices.map((d) => d.name)).not.toContain('Laptop · Chrome');
    const result = await hub.invoke('Send', { commandId: 'c1', kind: 'pause', seconds: null });
    expect(result).toMatchObject({ ok: false, reason: 'no-player' });
    await hub.stop();

    expect((await playerAudio(laptop.page))?.paused).toBe(false);
  });

  test('the hub refuses forged tickets, unknown tokens and foreign origins', async ({ request }) => {
    expect((await request.post('/api/remote/ticket', {
      headers: { Authorization: 'Bearer not-a-known-token', Origin: ORIGIN },
    })).status()).toBe(401);
    expect((await request.post('/api/remote/ticket', {
      headers: { Authorization: `Bearer ${FAKE_TOKEN}`, Origin: 'https://evil.example' },
    })).status()).toBe(403);
    expect((await request.post('/api/remote/ticket', {
      headers: { Authorization: `Bearer ${FAKE_TOKEN}` },
    })).status()).toBe(403);

    const tryHub = async (ticket: string, origin: string) => {
      const hub = new signalR.HubConnectionBuilder()
        .withUrl(`${ORIGIN}/api/remote/hub`, {
          skipNegotiation: true,
          transport: signalR.HttpTransportType.WebSockets,
          accessTokenFactory: () => ticket,
          headers: { Origin: origin },
        })
        .configureLogging(signalR.LogLevel.None)
        .build();
      try { await hub.start(); await hub.stop(); return 'connected'; } catch { return 'refused'; }
    };
    const good = (await (await request.post('/api/remote/ticket', {
      headers: { Authorization: `Bearer ${FAKE_TOKEN}`, Origin: ORIGIN },
    })).json()).ticket as string;

    expect(await tryHub(good, ORIGIN)).toBe('connected');
    expect(await tryHub(good, 'https://evil.example')).toBe('refused');
    expect(await tryHub(good.slice(0, -2) + (good.endsWith('A') ? 'BB' : 'AA'), ORIGIN)).toBe('refused');
    expect(await tryHub(FAKE_TOKEN, ORIGIN)).toBe('refused'); // the Google token itself is not a ticket
  });

  test('the tablet finds the laptop again after the laptop drops off the network', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toBeVisible();

    await laptop.context.setOffline(true);
    // The hub notices the dead connection and the tablet loses its player.
    await expect(strip(tablet.page)).toHaveCount(0, { timeout: 60_000 });

    await laptop.context.setOffline(false);
    // The laptop reconnects and republishes; the tablet picks it up again, and
    // its commands reach the laptop.
    await expect(strip(tablet.page)).toContainText('Laptop · Chrome', { timeout: 60_000 });
    const pausedBefore = (await playerAudio(laptop.page))?.paused;
    await tablet.page.locator('.player-toggle').click();
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(!pausedBefore);
  });

  test('last press wins: playing on the tablet pauses the laptop', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toBeVisible();

    // A different row, played on the tablet itself.
    await tablet.page.goto(`/playlist/${PSYTECH}`);
    await tablet.page.locator('#playlist-table tbody tr').nth(1).locator('.play-button').click();
    await expect.poll(async () => (await playerAudio(tablet.page))?.paused).toBe(false);

    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(true);
    await expect(bar(tablet.page)).toHaveAttribute('data-remote', 'false');
    // And the laptop, now idle-but-loaded, keeps its own bar rather than a remote one.
    await expect(bar(laptop.page)).toHaveAttribute('data-remote', 'false');
  });

  test('shows the TV when the laptop is casting, and pauses the TV', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome', cast: true });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await laptop.page.goto(`/playlist/${PSYTECH}`);
    await expect(laptop.page.locator('.cast-button')).toHaveAttribute('data-cast-state', 'ready');
    await laptop.page.locator('.cast-button').click();
    await expect(laptop.page.locator('.cast-button')).toHaveAttribute('data-cast-state', 'connected');
    await laptop.page.locator('#playlist-table tbody tr').first().locator('.play-button').click();
    await expect.poll(async () => (await castState(laptop.page)).playerState).toBe('PLAYING');

    await controllerOn(tablet);
    await expect(strip(tablet.page)).toContainText(`→ ${CAST_DEVICE_NAME}`);

    await tablet.page.locator('.player-toggle').click();
    await expect.poll(async () => (await castState(laptop.page)).playerState).toBe('PAUSED');
  });

  test('the real server hands the TV a ticket that works only for its own file', async ({ browser, request }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome', cast: true });
    await laptop.page.goto(`/playlist/${PSYTECH}`);
    await laptop.page.locator('.cast-button').click();
    await expect(laptop.page.locator('.cast-button')).toHaveAttribute('data-cast-state', 'connected');
    await laptop.page.locator('#playlist-table tbody tr').first().locator('.play-button').click();
    await expect.poll(async () => (await castLoads(laptop.page)).length).toBe(1);

    const url = new URL((await castLoads(laptop.page))[0].url);
    expect(url.toString()).not.toContain(FAKE_TOKEN);
    expect(url.pathname).toMatch(/^\/api\/proxy\/cast\/[\w-]+$/);
    const ticket = url.searchParams.get('ticket');
    expect(ticket).toBeTruthy();

    // As the TV fetches: no Origin, no Referer, no cookie. Its own file gets past
    // the ticket check (Google then judges the fixture token, so only "not
    // refused by us" is asserted); any other file is refused outright.
    const own = await request.get(url.toString(), { headers: { Range: 'bytes=0-' } });
    expect(own.status()).not.toBe(403);
    expect(own.headers()['x-cast-ticket']).toBeUndefined();

    const other = new URL(url);
    other.pathname = '/api/proxy/cast/some-other-file';
    expect((await request.get(other.toString())).status()).toBe(403);
  });

  test('a controller does not restore its own last track over the device that is playing', async ({ browser }) => {
    const laptop = await device(browser, { name: 'Laptop · Chrome' });
    await playFirstRow(laptop.page);

    // The tablet played something else last time.
    const tablet = await device(browser, {
      name: 'Tablet · Chrome',
      init: () => localStorage.setItem('playback:v1', JSON.stringify({
        v: 1, fileId: 'drive-delta', title: 'Tablet Leftover', isWav: true,
        duration: 2, position: 1, savedAt: Date.now(),
      })),
    });
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toContainText('Laptop · Chrome');
    await tablet.page.waitForTimeout(4000); // past the restore's wait for the room
    await expect(tablet.page.locator('.player-title')).not.toHaveText('Tablet Leftover');
    await expect(bar(tablet.page)).toHaveAttribute('data-remote', 'true');
  });

  test('an idle player hidden for long enough drops off the tablet', async ({ browser }) => {
    const laptop = await device(browser, {
      name: 'Laptop · Chrome',
      init: () => {
        localStorage.setItem('remote:test-player-idle-ms', '1000');
        // Headless pages are always "visible"; let the test hide this one.
        Object.defineProperty(document, 'visibilityState', {
          configurable: true,
          get: () => (window as any).__hidden ? 'hidden' : 'visible',
        });
      },
    });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toBeVisible();

    await tablet.page.locator('.player-toggle').click();
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(true);
    await laptop.page.evaluate(() => {
      (window as any).__hidden = true;
      document.dispatchEvent(new Event('visibilitychange'));
    });

    await expect(strip(tablet.page)).toHaveCount(0, { timeout: 30_000 });
    await expect(laptop.page.locator('[data-remote-toggle]')).toHaveAttribute('data-remote-connected', 'false');
  });
});

test.describe('remote control when the laptop\'s Google session runs out', () => {
  test('the tablet says the laptop needs a sign-in, and keeps showing it', async ({ browser }) => {
    let accepted = true;
    const laptop = await device(browser, {
      name: 'Laptop · Chrome',
      // Small slices, so a far seek needs a fresh request, which then 401s.
      mock: { maxSliceBytes: 64 * 1024, acceptToken: () => accepted },
    });
    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await playFirstRow(laptop.page);
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toContainText('Laptop · Chrome');

    // Drive stops accepting the laptop's token, and no silent renewal is
    // possible in the test (there is no Google sign-in to renew against).
    accepted = false;
    await tablet.page.locator('.player-seek').evaluate((el: HTMLInputElement) => {
      el.value = '900';
      el.dispatchEvent(new Event('change', { bubbles: true }));
    });

    await expect(laptop.page.locator('[data-player-signin-button]')).toBeVisible();
    await expect(tablet.page.locator('[data-remote-problem]')).toContainText('sign in again');
    // The tablet must not offer to sign in for the laptop: only the laptop can.
    await expect(tablet.page.locator('[data-player-signin-button]')).toHaveCount(0);
  });
});

test.describe('remote control when the laptop\'s browser refuses to autoplay', () => {
  // Whether Chrome refuses is the browser's call, and not reproducible under
  // automation: Playwright's own calls count as user gestures, and the ADR's
  // spike found every reload variant allowed there. So the refusal is simulated
  // (play() rejects with NotAllowedError, as Chrome's does) and what is tested
  // is the app's handling of it: the laptop reports it, the tablet says so.
  test('a laptop that needs a click says so on the tablet', async ({ browser }) => {
    const laptop = await device(browser, {
      name: 'Laptop · Chrome',
      init: () => {
        const play = HTMLMediaElement.prototype.play;
        HTMLMediaElement.prototype.play = function () {
          return (window as any).__refuseAutoplay
            ? Promise.reject(new DOMException('play() needs a user gesture', 'NotAllowedError'))
            : play.call(this);
        };
      },
    });
    await playFirstRow(laptop.page);
    await laptop.page.locator('.player-toggle').click();
    await expect.poll(async () => (await playerAudio(laptop.page))?.paused).toBe(true);
    await laptop.page.evaluate(() => { (window as any).__refuseAutoplay = true; });

    const tablet = await device(browser, { name: 'Tablet · Chrome' });
    await controllerOn(tablet);
    await expect(strip(tablet.page)).toContainText('Laptop · Chrome');

    await tablet.page.locator('.player-toggle').click();
    await expect(tablet.page.locator('[data-remote-problem]')).toContainText('needs a click');
    expect((await playerAudio(laptop.page))?.paused).toBe(true);

    // A click on the laptop, i.e. what the message asks for, clears it.
    await laptop.page.evaluate(() => { (window as any).__refuseAutoplay = false; });
    await laptop.page.locator('.player-toggle').click();
    await expect(tablet.page.locator('[data-remote-problem]')).toHaveCount(0);
  });
});
