import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockDiscover, discoverNml, CARNIVOR, type DiscoverCalls } from './discover-mock';

// Discover previews in the player bar (ADR 20261025-54 §2): the Listen queue is
// the player's queue, audio comes from our endpoint (the real one redirects to the
// store CDN; here the mock serves a silent MP3 itself, see discover-mock.ts), and
// nothing Drive-only touches it:
// no whole-file download, no waveform, no buffer bar.

const card = (page: Page) => page.locator('[data-current]');
const cardTitle = (page: Page) => card(page).locator('[data-track-title]');
const barTitle = (page: Page) => page.locator('.player-title');
const playing = (page: Page) => expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Pause');

async function openListen(page: Page, audioSeconds = 4): Promise<{ calls: DiscoverCalls; driveForDiscover: string[] }> {
  await mockDrive(page, { nml: discoverNml() });
  const calls = await mockDiscover(page, { audioSeconds });
  await seedToken(page);
  // A whole-file download of a preview would go to Drive with its synthetic id.
  const driveForDiscover: string[] = [];
  page.on('request', (r) => { if (r.url().includes('googleapis.com') && /discover(%3A|:)/.test(r.url())) driveForDiscover.push(r.url()); });

  await page.goto('/discover/H/seeds');
  await page.locator('[data-section="eti-lisaa"] [data-add-all]').click();
  const row = page.locator('tr[data-seed="Carnivor Records"]');
  await row.locator('[data-seed-url]').fill(CARNIVOR);
  await row.locator('[data-seed-url]').press('Tab');
  await row.locator('[data-refresh]').click();
  await expect(row.locator('[data-release-count]')).toHaveText('2');
  await page.locator('[data-tab="listen"]').click();
  await expect(cardTitle(page)).toHaveText('Internal Motion (Original Mix)');
  return { calls, driveForDiscover };
}

test.describe('Discover playback', () => {
  test('plays the card through our audio endpoint, in the player bar, with no download, waveform or buffer bar', async ({ page }) => {
    const { calls, driveForDiscover } = await openListen(page);

    await page.locator('[data-play-preview]').click();
    await playing(page);
    await expect(barTitle(page)).toContainText('obwigszyh – Internal Motion (Original Mix)');
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Bandcamp · Carnivor Records');
    await expect(page.locator('[data-play-preview]')).toHaveText(/Pause/);

    // Our endpoint; the media URL carries the release, never a stream URL.
    expect(calls.audio[0]).toMatch(/^\/api\/proxy\/discover\/bandcamp\/776289323\?release=/);
    expect(calls.cdn).toContain('776289323');
    await expect(page.locator('.player-seek')).toBeVisible();          // the plain bar, not a waveform
    await expect(page.locator('.waveform')).toHaveCount(0);
    await expect(page.locator('[data-buffer-state]')).toHaveCount(0);
    expect(driveForDiscover).toEqual([]);
    const cache = await page.evaluate(async () => (await import('./player.js')).bufferState().cache);
    expect(JSON.stringify(cache)).not.toContain('discover:');
    expect(calls.storeRequests).toEqual([]);

    // P pauses.
    await page.locator('[data-listen]').focus();
    await page.keyboard.press('p');
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Play');
  });

  test('a verdict while playing starts the next track, and the bar\'s Next moves the card', async ({ page }) => {
    await openListen(page, 60);
    await page.locator('[data-play-preview]').click();
    await playing(page);

    await page.locator('[data-verdict="like"]').click();
    await expect(cardTitle(page)).toHaveText('Burnt Side (Original Mix)');
    await expect(barTitle(page)).toContainText('Burnt Side (Original Mix)');
    await playing(page);

    await page.getByRole('button', { name: 'Next track' }).click();
    await expect(barTitle(page)).toContainText('Hunger');
    await expect(cardTitle(page)).toHaveText('Hunger');
  });

  test('continuous play walks the Listen queue and the card follows', async ({ page }) => {
    await openListen(page, 3);
    await page.locator('[data-play-preview]').click();
    await playing(page);
    await page.locator('[data-continuous-play]').check();
    await expect(barTitle(page)).toContainText('Burnt Side (Original Mix)', { timeout: 30_000 });
    await expect(cardTitle(page)).toHaveText('Burnt Side (Original Mix)');
    // The next one is primed by src on the spare element, never downloaded.
    const primed = await page.evaluate(async () => (await import('./player.js')).primedState());
    if (primed) expect(primed.source).toBe('stream');
  });

  test('a failed stream is probed with ?probe=1 and reloaded once, not taken for an expired token', async ({ page }) => {
    const { calls } = await openListen(page);
    // The first audio response fails, as an expired signed URL at the CDN would.
    let failed = false;
    await page.route('**/api/proxy/discover/bandcamp/**', (route) => {
      const u = new URL(route.request().url());
      if (!failed && u.searchParams.get('probe') !== '1') { failed = true; return route.fulfill({ status: 403, body: '' }); }
      return route.fallback();
    });
    await page.locator('[data-play-preview]').click();
    await playing(page);
    expect(failed).toBe(true);
    expect(calls.audio.some((a) => a.includes('probe=1'))).toBe(true);
    await expect(page.locator('[data-player-signin]')).toHaveCount(0);
  });

  test('a reload brings back the last collection track, not a preview', async ({ page }) => {
    await openListen(page);
    await page.locator('[data-play-preview]').click();
    await playing(page);
    const remembered = await page.evaluate(() => localStorage.getItem('playback:v1'));
    expect(remembered ?? '').not.toContain('discover:');
  });
});
