import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockDiscover, discoverNml, watchCarnivor, H16_UUID, CARNIVOR, DARKER, type DiscoverMockOptions } from './discover-mock';

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

const addCarnivor = watchCarnivor;

/** "Add all" asks first: confirm the cost warning. */
async function addAll(page: Page, section: string) {
  await page.locator(`[data-section="${section}"] [data-add-all]`).first().click();
  await page.locator('[data-confirm-add-all]').click();
  await expect(page.locator('[data-refresh-report][data-running="false"]')).toBeVisible();
}

const result = (page: Page, name: string) => page.locator(`[data-refresh-results] [data-result="${name}"]`);

/** Toni's state on 2026-11-01: the starter's Carnivor seed, and Obwigszyh added from "eti lisää" with no Bandcamp URL. */
const OBWIGSZYH_WITHOUT_URL = JSON.stringify({
  t: 'seed-add', at: '2026-10-31T10:00:00+00:00', dev: '0123456789', n: 1, seed: 'artist:obwigszyh:H', kind: 'Artist',
  name: 'Obwigszyh', genre: 'H', origin: 'EtiLisaa', reason: 'KOVA LOPPU. eti lisaa',
});

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

    await addAll(page, 'eti-lisaa');
    // Carnivor's guessed page exists; Obwigszyh has no Bandcamp page, so it is not added.
    await expect(page.locator('tr[data-seed]')).toHaveCount(1);
    await expect(page.locator('tr[data-seed]').first()).toHaveAttribute('data-seed-origin', 'EtiLisaa');
    await expect(result(page, 'Obwigszyh')).toContainText('no Bandcamp page at obwigszyh.bandcamp.com');
    await expect(result(page, 'Obwigszyh')).toHaveAttribute('data-result-kind', 'NotAdded');

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

    await addAll(page, 'from-playlist');
    // Bandcamp wrote "Visit https://darkersounds.bandcamp.com" into a download: the URL is known
    // (for Hefty too, whose track it was). Carnivor's guess exists; the other guesses do not.
    await expect(seedRow(page, 'Darker Sounds').locator('[data-seed-url]')).toHaveValue(DARKER);
    await expect(seedRow(page, 'Carnivor Records')).toBeVisible();
    await expect(seedRow(page, 'Gastspiel Records')).toHaveCount(0);
    await expect(result(page, 'Gastspiel Records')).toContainText('no Bandcamp page at gastspielrecords.bandcamp.com');
    await expect(result(page, 'Darker Sounds')).toContainText('1 new release queued (2 tracks)');
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
    await expect(page.locator('[data-queue-count]')).toHaveText('6');

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
    await expect(page.locator('[data-queue-count]')).toHaveText('3');

    // A refresh fetches the list again but never re-offers what was heard.
    await page.goto('/discover/H/seeds');
    await seedRow(page, 'Carnivor Records').locator('[data-refresh]').click();
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('3');
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
    await expect(page.locator('[data-queue-count]')).toHaveText('4');
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
    await expect(other.locator('tr[data-seed]')).toHaveCount(1);
  });

  test('the starter list shows on first run, Carnivor is heard through 2026-10-25, and it is not re-applied', async ({ page }) => {
    const calls = await open(page, '/discover/H/shortlist', { realStarter: true });

    // The three picks wait as "maybe", known by URL until their release page is read.
    const liked = page.locator('[data-liked]');
    await expect(liked).toHaveCount(3);
    await expect(liked.locator('[data-liked-title]')).toHaveText(['Satanas', 'Im no immigrant', 'Madtype shadym t y remix']);
    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const firstLog = [...calls.appDataCreates.map((c) => c.body), ...calls.appDataUpdates.map((u) => u.body)].join('\n');
    // One "starter" event per batch: Toni's triage (2026-10-25.1) and the P crate (2026-11-01.1).
    expect(firstLog.split('\n').filter((l) => l.includes('"t":"starter"'))).toHaveLength(2);
    expect(firstLog).toContain('beatport:4930160');

    // Look up reads the release page on a click and resolves title and artist.
    const pick = (slug: string) => page.locator(`[data-liked="url:https://carnivorrecords.bandcamp.com/track/${slug}"]`);
    await pick('satanas').locator('[data-look-up]').click();
    await expect(pick('satanas').locator('[data-liked-title]')).toHaveText('Fixture Artist A – Satanas');
    await expect(pick('im-no-immigrant').locator('[data-liked-title]')).toHaveText("Fixture Artist B – I'm No Immigrant");
    await expect(pick('madtype-shadym-t-y-remix').locator('[data-look-up]')).toBeVisible();   // its release was not read

    // The Carnivor seed came with the starter, heard through 2026-10-25 (a day, not a month):
    // a refresh reads the list but nothing dated on or before that day is ever offered.
    await page.goto('/discover/H/seeds');
    const row = page.locator('tr[data-seed="Carnivor Records"]');
    await expect(row).toHaveAttribute('data-seed-origin', 'EtiLisaa');
    await expect(row.locator('[data-watermark]')).toHaveValue('2026-10-25');
    await row.locator('[data-refresh]').click();
    await expect(row.locator('[data-busy]')).toHaveCount(0);
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('0');
    expect(calls.storeRequests).toEqual([]);

    // Toni decides one; a second device then loads the same log with the starter file
    // still shipped: nothing is applied again, and his decision stands.
    await page.locator('[data-tab="shortlist"]').click();
    await pick('satanas').locator('[data-dont-buy]').click();
    await expect(liked).toHaveCount(2);
    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const log = calls.appDataUpdates.at(-1)?.body ?? calls.appDataCreates.at(-1)!.body;

    const other = await page.context().newPage();
    await mockDrive(other, { nml: discoverNml() });
    const otherCalls = await mockDiscover(other, { realStarter: true, appData: { [calls.appDataCreates[0].name]: log } });
    await seedToken(other);
    await other.goto('/discover/H/shortlist');
    await expect(other.locator('[data-liked]')).toHaveCount(2);
    await expect(other.locator('[data-save-status="Saved"]')).toBeVisible();
    const otherLog = [...otherCalls.appDataCreates.map((c) => c.body), ...otherCalls.appDataUpdates.map((u) => u.body)].join('\n');
    expect(otherLog).not.toContain('"t":"starter"');
  });

  test('a token without drive.appdata keeps working locally and asks to allow saving', async ({ page }) => {
    await open(page, '/discover/H/seeds', { appDataScopeMissing: true });
    await expect(page.locator('[data-save-status="NeedsConsent"]')).toBeVisible();
    await expect(page.locator('[data-grant-appdata]')).toBeVisible();
    await addAll(page, 'eti-lisaa');
    await expect(page.locator('tr[data-seed]')).toHaveCount(1);
  });

  // ---------- clarity: what is searched, what a refresh found, what an add costs ----------

  test('an empty Listen says why, offers suggestions, and Add & search queues their releases', async ({ page }) => {
    const calls = await open(page, '/discover/H/listen', { realStarter: true });
    const why = page.locator('[data-empty-why]');
    await expect(why).toContainText('your watched seed has not been checked this session (Carnivor Records, through 2026-10-25)');

    await page.locator('[data-refresh-all]').click();
    await expect(result(page, 'Carnivor Records')).toContainText('no new releases since 2026-10-25');
    await expect(why).toContainText("You've heard everything from your 1 watched seed (Carnivor Records, through 2026-10-25).");

    // "Eti lisää" first, then the top of H; Carnivor is already watched.
    const suggestions = page.locator('[data-listen-suggestions] [data-candidate]');
    await expect(suggestions.first()).toHaveAttribute('data-candidate', 'Obwigszyh');
    await expect(page.locator('[data-listen-suggestions] [data-candidate="Carnivor Records"]')).toHaveCount(0);
    await expect(page.locator('[data-to-seeds]')).toHaveAttribute('href', 'discover/H/seeds');

    // Darker Sounds' page is known from the collection: one click adds it and fills the queue.
    await page.locator('[data-listen-suggestions] [data-candidate="Darker Sounds"] [data-add]').click();
    await expect(result(page, 'Darker Sounds')).toContainText('1 new release queued (2 tracks)');
    await expect(page.locator('[data-queue-count]')).toHaveText('2');
    await expect(page.locator('[data-current] [data-track-title]')).toHaveText('Dark Matter');
    expect(calls.lists).toContain(DARKER);
    expect(calls.storeRequests).toEqual([]);
  });

  test('refresh shows its progress, then a result per seed, and names the seeds it cannot search', async ({ page }) => {
    const calls = await open(page, '/discover/H/listen', {
      realStarter: true, delayMs: 700, appData: { 'discover-0123456789-2026-10.jsonl': OBWIGSZYH_WITHOUT_URL + '\n' },
    });
    const missing = page.locator('[data-missing-url-seed="Obwigszyh"]');
    await expect(missing).toHaveAttribute('href', 'discover/H/seeds#seed-obwigszyh');

    await page.locator('[data-refresh-all]').click();
    await expect(page.locator('[data-refresh-progress]')).toContainText(/Checking Carnivor Records… (reading the release list|\d+\/2 releases)/);
    await expect(page.locator('[data-refresh-meter]')).toContainText(/Checking 2 watched seeds · seed 1 of 2 ·\s+\d+ requests? so far ·\s+up to about \d+ s left \(one Bandcamp request every 2 s\)/);

    await expect(result(page, 'Carnivor Records')).toContainText('no new releases since 2026-10-25');
    await expect(result(page, 'Carnivor Records')).toHaveAttribute('data-result-kind', 'NothingNew');
    await expect(result(page, 'Obwigszyh')).toContainText('no Bandcamp URL, so it is never searched');
    await expect(result(page, 'Obwigszyh')).toHaveAttribute('data-result-kind', 'Error');
    // The list, then the one unheard release page (2020): older than the watermark, so the walk stops.
    await expect(page.locator('[data-refresh-done]')).toContainText('2 requests');
    expect(calls.lists).toEqual([CARNIVOR]);

    // The Seeds row says how far the walk went.
    await page.locator('[data-tab="seeds"]').click();
    await expect(seedRow(page, 'Carnivor Records').locator('[data-read]'))
      .toContainText('of 2 releases (stopped at your heard-through date 2026-10-25)');

  });

  test('a watched seed without a URL: the guess is checked, a 404 says so, and it can be marked "not on Bandcamp"', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds', {
      realStarter: true, appData: { 'discover-0123456789-2026-10.jsonl': OBWIGSZYH_WITHOUT_URL + '\n' },
    });
    const row = seedRow(page, 'Obwigszyh');
    await expect(row.locator('[data-no-url]')).toBeVisible();
    await expect(row.locator('[data-guess]')).toHaveText('Try obwigszyh.bandcamp.com');
    await row.locator('[data-guess]').click();
    await expect(row.locator('[data-error]')).toContainText('no Bandcamp page at obwigszyh.bandcamp.com');
    await expect(row.locator('[data-error]')).toContainText('its releases appear via Carnivor Records, which you watch');
    await expect(row.locator('[data-seed-url]')).toHaveValue('');   // a wrong guess is not kept
    expect(calls.lists).toEqual(['https://obwigszyh.bandcamp.com']);

    await row.locator('[data-mark-not-on-bandcamp]').click();
    await expect(row.locator('[data-not-on-bandcamp]')).toContainText('Its releases appear via Carnivor Records.');
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-missing-url]')).toHaveCount(0);
    await expect(page.locator('[data-offsite]')).toContainText('Obwigszyh (releases via Carnivor Records)');
    await page.locator('[data-refresh-all]').click();
    await expect(result(page, 'Obwigszyh')).toHaveAttribute('data-result-kind', 'NotOnBandcamp');
  });

  test('a suggestion with no known Bandcamp page asks for one, and a 404 guess is flagged before adding', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds');
    await watchCarnivor(page);
    const candidate = page.locator('[data-section="eti-lisaa"] [data-candidate="Obwigszyh"]');
    await candidate.locator('[data-add]').click();
    // Asks first: no request is spent until Toni picks the guess or pastes a URL.
    await expect(candidate.locator('[data-ask-url]')).toContainText('No Bandcamp page known for Obwigszyh.');
    expect(calls.lists).toEqual([CARNIVOR]);

    await candidate.locator('[data-guess-url]').click();
    await expect(candidate.locator('[data-add-error]'))
      .toContainText('not added: no Bandcamp page at obwigszyh.bandcamp.com. Its releases already appear via Carnivor Records, which you watch.');
    await expect(seedRow(page, 'Obwigszyh')).toHaveCount(0);
    await expect(candidate.locator('[data-ask-url-input]')).toBeVisible();
    expect(calls.lists).toEqual([CARNIVOR, 'https://obwigszyh.bandcamp.com']);
  });

  test('"Add all" on a top list shows the cost first, then adds what has a page and searches it', async ({ page }) => {
    const calls = await open(page, '/discover/H/seeds', { usedToday: 480 });
    const labels = page.locator('[data-section="top-lists"] .discover-columns > div').first();
    const button = labels.locator('[data-add-all]');
    const n = Number((await button.textContent())!.replace(/\D+/g, ''));
    expect(n).toBeGreaterThan(1);

    await button.click();
    const cost = page.locator('[data-add-all-cost]');
    await expect(cost.locator('[data-cost-requests]')).toHaveText(String(n * 11));
    await expect(cost).toContainText(`${n} × one release list and up to 10 release pages`);
    await expect(cost.locator('[data-cost-time]')).toHaveText(n * 11 * 2 < 90 ? `${n * 22} s` : `${Math.ceil((n * 22) / 60)} min`);
    await expect(cost.locator('[data-cost-cap]')).toContainText('The server allows 500 Bandcamp requests a day; 480 used so far today.');
    await expect(cost.locator('[data-cost-over]')).toContainText("more than today's 20 left");
    await expect(cost.locator('[data-cost-unknown]')).toBeVisible();
    expect(calls.lists).toEqual([]);   // nothing fetched before the confirm

    await cost.locator('[data-cancel-add-all]').click();
    await expect(cost).toHaveCount(0);
    await expect(page.locator('tr[data-seed]')).toHaveCount(0);

    await button.click();
    await page.locator('[data-confirm-add-all]').click();
    await expect(page.locator('[data-refresh-report][data-running="false"]')).toBeVisible();
    await expect(page.locator('[data-refresh-results] [data-result]')).toHaveCount(n);
    await expect(seedRow(page, 'Darker Sounds')).toBeVisible();
    await expect(seedRow(page, 'Carnivor Records')).toBeVisible();
    await expect(page.locator('tr[data-seed]')).toHaveCount(2);
    await expect(page.locator('[data-refresh-results] [data-result-kind="NotAdded"]')).toHaveCount(n - 2);
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('8');
  });

  test('a spent daily cap is reported per seed and stops the rest', async ({ page }) => {
    await open(page, '/discover/H/seeds', { listStatus: { 'darkersounds.bandcamp.com': 429 } });
    const top = page.locator('[data-section="top-lists"] [data-candidate="Darker Sounds"]').first();
    await top.locator('[data-add]').click();
    await expect(top.locator('[data-add-error]')).toContainText("Today's Bandcamp request cap is spent");
    await expect(seedRow(page, 'Darker Sounds')).toHaveCount(0);
  });
});
