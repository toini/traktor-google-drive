import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockDiscover, discoverNml, H16_UUID, CARNIVOR, type DiscoverMockOptions } from './discover-mock';

// Track discovery (ADR 20261025-54), increment 1: seeds, the Bandcamp release
// walk through the server, triage, watermarks, owned tracks, the shortlist and
// the appDataFolder log. Store endpoints are mocked; nothing reaches a store.

async function open(page: Page, path = '/discover/H', opts: DiscoverMockOptions = {}) {
  await mockDrive(page, { nml: discoverNml() });
  const calls = await mockDiscover(page, opts);
  await seedToken(page);
  await page.goto(path);
  return calls;
}

const seedRow = (page: Page, name: string) => page.locator(`tr[data-seed="${name}"]`);

async function addCarnivor(page: Page) {
  await page.goto('/discover/H/seeds');
  const eti = page.locator('[data-section="eti-lisaa"]');
  await eti.locator('[data-add-all]').click();
  await seedRow(page, 'Carnivor Records').locator('[data-seed-url]').fill(CARNIVOR);
  await seedRow(page, 'Carnivor Records').locator('[data-seed-url]').press('Tab');
  await seedRow(page, 'Carnivor Records').locator('[data-refresh]').click();
  await expect(seedRow(page, 'Carnivor Records').locator('[data-release-count]')).toHaveText('2');
}

test.describe('Discover', () => {
  test('the sidebar has a Discover entry per genre, and an empty genre opens on Seeds', async ({ page }) => {
    const calls = await open(page, '/music');
    const nav = page.getByRole('navigation', { name: 'Discover' });
    await expect(nav.locator('[data-discover-genre]')).toHaveText(['Z', 'H', 'P', 'C']);
    await nav.locator('[data-discover-genre="H"]').click();
    await expect(page).toHaveURL(/\/discover\/H$/);
    await expect(page.locator('[data-tab="seeds"]')).toHaveAttribute('aria-selected', 'true');
    expect(calls.storeRequests).toEqual([]);
  });

  test('"eti lisää" comments are the first seeds, and "lisää luuppi" is not one', async ({ page }) => {
    await open(page, '/discover/H/seeds');
    const eti = page.locator('[data-section="eti-lisaa"]');
    await expect(eti.locator('[data-candidate]')).toHaveCount(2);
    await expect(eti.locator('[data-candidate="Obwigszyh"]')).toContainText('KOVA LOPPU. eti lisaa');
    await expect(eti.locator('[data-candidate="Carnivor Records"]')).toBeVisible();

    await eti.locator('[data-add-all]').click();
    await expect(page.locator('tr[data-seed]')).toHaveCount(2);
    await expect(page.locator('tr[data-seed]').first()).toHaveAttribute('data-seed-origin', 'EtiLisaa');

    // Dragon's "1-3 lisää luuppi!" (add a loop) in Z proposes nothing.
    await page.goto('/discover/Z/seeds');
    await expect(page.locator('[data-section="eti-lisaa"] [data-candidate]')).toHaveCount(0);
  });

  test('seeding from the H16 playlist takes the artists and labels of its 4-star tracks', async ({ page }) => {
    await open(page, '/discover/H/seeds');
    const section = page.locator('[data-section="from-playlist"]');
    await section.locator('[data-playlist]').selectOption(H16_UUID);
    // ≥4★: Carnivor, Darker Sounds, Gastspiel, RAW.; Obwigszyh and Hefty. Various Artists is stop-listed,
    // Noseda's 3-star track is below the threshold.
    await expect(section.locator('[data-playlist-summary]')).toContainText('4 labels, 2 artists');
    await expect(section.locator('[data-candidate="Various Artists"]')).toHaveCount(0);
    await expect(section.locator('[data-candidate="Noseda"]')).toHaveCount(0);

    await section.locator('[data-min-stars]').selectOption('3');
    await expect(section.locator('[data-candidate="Noseda"]')).toBeVisible();
    await section.locator('[data-min-stars]').selectOption('4');

    await section.locator('[data-add-all]').click();
    await expect(page.locator('tr[data-seed]')).toHaveCount(6);
    // Bandcamp wrote "Visit https://darkersounds.bandcamp.com" into a download: the URL is known.
    await expect(seedRow(page, 'Darker Sounds').locator('[data-seed-url]')).toHaveValue('https://darkersounds.bandcamp.com');
  });

  test('listen: oldest release first, owned tracks never, verdicts move on, nothing twice', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    expect(calls.lists).toEqual([CARNIVOR]);
    expect(calls.releases).toHaveLength(2);

    await page.locator('[data-tab="listen"]').click();
    const card = page.locator('[data-current]');
    // Internal Motion (2020) before Feeding Generation Z II (2024); 1616 is in the collection.
    await expect(card.locator('[data-track-title]')).toHaveText('Internal Motion (Original Mix)');
    await expect(page.locator('[data-queue-count]')).toHaveText('4');

    await page.locator('[data-verdict="skip"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Burnt Side (Original Mix)');

    // A rating and Buy stay on the card; Like moves on.
    await card.locator('[data-star="4"]').click();
    await card.locator('[data-buy]').click();
    await expect(card.locator('[data-buy]')).toHaveClass(/on/);
    await card.locator('[data-note]').fill('kova loppu');
    await card.locator('[data-note]').press('Enter');
    await page.locator('[data-verdict="like"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Hunger');

    // Keyboard: M is maybe.
    await page.locator('[data-listen]').focus();
    await page.keyboard.press('m');
    await expect(card.locator('[data-track-title]')).toHaveText('Machine Choir');
    await expect(page.locator('[data-queue-count]')).toHaveText('1');

    // A refresh fetches the list again but never re-offers what was heard.
    await page.goto('/discover/H/seeds');
    await seedRow(page, 'Carnivor Records').locator('[data-refresh]').click();
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('1');
    expect(calls.storeRequests).toEqual([]);
  });

  test('"heard through" is offered once a month is done and the whole list was read', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    await expect(page.locator('[data-heard-through]')).toHaveCount(0);

    await page.locator('[data-tab="listen"]').click();
    await page.locator('[data-verdict="skip"]').click();   // Internal Motion (Original Mix)
    await page.locator('[data-verdict="maybe"]').click();  // Burnt Side; 1616 is owned
    await page.locator('[data-tab="seeds"]').click();

    const offer = seedRow(page, 'Carnivor Records').locator('[data-heard-through]');
    await expect(offer).toHaveAttribute('data-heard-through', '2020-08');
    await offer.click();
    await expect(seedRow(page, 'Carnivor Records').locator('[data-watermark]')).toHaveValue('2020-08');
    await expect(offer).toHaveCount(0);
    expect(calls.storeRequests).toEqual([]);
  });

  test('undo brings the track back to the card without re-queueing it', async ({ page }) => {
    await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    await page.locator('[data-tab="listen"]').click();
    const card = page.locator('[data-current]');
    await page.locator('[data-verdict="skip"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Burnt Side (Original Mix)');
    await page.locator('[data-undo]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Internal Motion (Original Mix)');
  });

  test('a watermark hides releases on or before it', async ({ page }) => {
    await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    await seedRow(page, 'Carnivor Records').locator('[data-watermark]').fill('2020-08');
    await seedRow(page, 'Carnivor Records').locator('[data-watermark]').dispatchEvent('change');
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('2');
    await expect(page.locator('[data-current] [data-track-title]')).toHaveText('Hunger');
  });

  test('shortlist: liked tracks to decide, buy list with links, copy and bought', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    await page.locator('[data-tab="listen"]').click();
    await page.locator('[data-verdict="like"]').click();          // Internal Motion: liked, undecided
    const card = page.locator('[data-current]');
    await card.locator('[data-star="5"]').click();               // Burnt Side: 5 stars, buy
    await card.locator('[data-buy]').click();
    await page.locator('[data-verdict="like"]').click();

    await page.locator('[data-tab="shortlist"]').click();
    await expect(page.locator('[data-liked]')).toHaveCount(1);
    await expect(page.locator('[data-shortlisted]')).toHaveCount(1);
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveAttribute('href', `${CARNIVOR}/album/internal-motion`);

    await page.locator('[data-copy-links]').click();
    await expect(page.locator('[data-copied]')).toBeVisible();
    const text = await page.evaluate(() => navigator.clipboard.readText());
    expect(text).toContain(`${CARNIVOR}/album/internal-motion`);
    expect(text).toContain('obwigszyh – Burnt Side (Original Mix) ★★★★★');

    // Pass 2 on the liked one: buy it.
    await page.locator('[data-liked] [data-buy]').click();
    await expect(page.locator('[data-shortlisted]')).toHaveCount(2);

    await page.locator('[data-shortlisted]').first().locator('[data-bought]').click();
    await expect(page.locator('[data-shortlisted]')).toHaveCount(1);
  });

  test('decisions are saved to this device\'s log in appDataFolder and come back on reload', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds');
    await addCarnivor(page);
    await page.locator('[data-tab="listen"]').click();
    await page.locator('[data-verdict="like"]').click();

    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const written = [...calls.appDataCreates.map((c) => c.body), ...calls.appDataUpdates.map((u) => u.body)].join('\n');
    expect(calls.appDataCreates[0].name).toMatch(/^discover-[0-9a-f]{10}-\d{4}-\d{2}\.jsonl$/);
    expect(written).toContain('"t":"seed-add"');
    expect(written).toContain('"pass1":"Like"');

    // A second device: a fresh page with only the Drive log. The seeds and verdicts are there.
    const log = calls.appDataUpdates.at(-1)?.body ?? calls.appDataCreates.at(-1)!.body;
    const other = await page.context().newPage();
    await mockDrive(other, { nml: discoverNml() });
    await mockDiscover(other, { appData: { [calls.appDataCreates[0].name]: log } });
    await seedToken(other);
    await other.goto('/discover/H/seeds');
    await expect(other.locator('tr[data-seed]')).toHaveCount(2);
  });

  test('a token without drive.appdata keeps working locally and asks to allow saving', async ({ page }) => {
    await open(page, '/discover/H/seeds', { appDataScopeMissing: true });
    await expect(page.locator('[data-save-status="NeedsConsent"]')).toBeVisible();
    await expect(page.locator('[data-grant-appdata]')).toBeVisible();
    await page.locator('[data-section="eti-lisaa"] [data-add-all]').click();
    await expect(page.locator('tr[data-seed]')).toHaveCount(2);
  });
});
