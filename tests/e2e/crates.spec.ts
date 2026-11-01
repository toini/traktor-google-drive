import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockDiscover, discoverNml, type DiscoverCalls } from './discover-mock';
import { mockCrates, itemKey, CRATE_ID, PARVATI, MUIMINA_ALBUM, CODEX_SC, type CrateCalls } from './crates-mock';

// Crates (ADR 20261025-54, "Crates"): the friend's-set list shipped in the starter
// file, finding each track (Discogs, Bandcamp by URL, a pasted URL, SoundCloud's
// oEmbed and widget), and triage inside the crate, never mixed into P's seed queue.
// Every store endpoint is mocked; any request to a real store host fails the test.

const LIST = `/discover/P/list/${CRATE_ID}`;
const item = (page: Page, artist: string, title: string) => page.locator(`[data-crate-item="${itemKey(artist, title)}"]`);

async function open(page: Page, path = LIST): Promise<{ calls: DiscoverCalls; crates: CrateCalls }> {
  await mockDrive(page, { nml: discoverNml() });
  const calls = await mockDiscover(page, { realStarter: true });
  const crates = await mockCrates(page);
  await seedToken(page);
  await page.goto(path);
  return { calls, crates };
}

function noStoreCalls(calls: DiscoverCalls, crates: CrateCalls) {
  expect(calls.storeRequests).toEqual([]);
  expect(crates.storeRequests).toEqual([]);
}

test.describe('Crates', () => {
  test('the shipped list appears under P with its 70 tracks, apart from the P seed queue', async ({ page }) => {
    const { calls, crates } = await open(page, '/discover/P');

    // A chip on P's page and a link in the sidebar.
    const chip = page.locator(`[data-crate-chip="${CRATE_ID}"]`);
    await expect(chip).toContainText('Looney Boon Hug Halloween 2026');
    await expect(page.locator(`[data-crate-nav="${CRATE_ID}"]`)).toContainText('P ›');
    // P's own queue, seeds and shortlist hold none of it.
    await expect(page.locator('[data-queue-count]')).toHaveText('0');
    await expect(page.locator('[data-seed-count]')).toHaveText('0');
    await expect(page.locator('[data-shortlist-count]')).toHaveText('0');

    await chip.click();
    await expect(page).toHaveURL(new RegExp(`${LIST}$`));
    await expect(page.locator('[data-crate-name]')).toHaveText('Looney Boon Hug Halloween 2026');
    await expect(page.locator('[data-tab="tracks"]')).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('[data-item-count]')).toHaveText('70');
    await expect(page.locator('[data-crate-item]')).toHaveCount(70);
    // Gidra – Viral Virus is in the fixture collection: already owned, and not looked up.
    await expect(item(page, 'Gidra', 'Viral Virus')).toHaveAttribute('data-state', 'owned');
    await expect(page.locator('[data-summary-owned]')).toHaveAttribute('data-summary-owned', '1');
    await expect(page.locator('[data-summary-pending]')).toHaveAttribute('data-summary-pending', '69');
    await expect(page.locator('[data-crate-item]').first()).toContainText('Tengri & Jahbo – Patch Hell');
    await expect(page.locator('[data-crate-item]').first()).toContainText('SoundCloud: Tengri, Jahbo');

    // Saved as events, once: a reload applies nothing again.
    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const log = [...calls.appDataCreates.map((c) => c.body), ...calls.appDataUpdates.map((u) => u.body)].join('\n');
    expect(log.split('\n').filter((l) => l.includes('"t":"crate-add"'))).toHaveLength(1);
    expect(log.split('\n').filter((l) => l.includes('"t":"crate-item"'))).toHaveLength(70);
    expect(crates.discogs).toEqual([]);   // nothing is looked up until Toni asks
    noStoreCalls(calls, crates);
  });

  test('find: via Discogs to Bandcamp, not found with a manual search, ambiguous with a pick', async ({ page }) => {
    const { calls, crates } = await open(page);

    const patch = item(page, 'Tengri & Jahbo', 'Patch Hell');
    await patch.locator('[data-find]').click();
    await expect(patch).toHaveAttribute('data-state', 'found');
    await expect(patch.locator('[data-source-link]')).toHaveAttribute('data-source', 'bandcamp');
    await expect(patch.locator('[data-found]')).toContainText('Parvati Records');
    await expect(patch.locator('[data-found]')).toContainText('via Discogs');
    expect(crates.bandcamp).toEqual(['list beatspace-parvati.bandcamp.com', `release ${PARVATI}/album/testimony-20`]);

    // Not on Discogs, and the guessed pages do not exist: not found, says what it tried,
    // and offers Bandcamp's own search in a new tab (Toni searching, not the server).
    const bioport = item(page, 'Ianuaria & Muscaria', 'Bioport');
    await bioport.locator('[data-find]').click();
    await expect(bioport).toHaveAttribute('data-state', 'notfound');
    await expect(bioport.locator('[data-item-message]')).toContainText('ianuaria.bandcamp.com');
    const search = bioport.locator('[data-bandcamp-search]');
    await expect(search).toHaveAttribute('href', 'https://bandcamp.com/search?item_type=t&q=Ianuaria%20Bioport');
    await expect(search).toHaveAttribute('target', '_blank');
    await expect(bioport.locator('[data-paste-url]')).toBeVisible();

    // Two Discogs artists called Nobot: Toni picks; the pick resolves through its release.
    const nobot = item(page, 'Nobot', 'The Future');
    await nobot.locator('[data-find]').click();
    await expect(nobot).toHaveAttribute('data-state', 'ambiguous');
    await expect(nobot.locator('[data-candidates] li')).toHaveCount(2);
    await nobot.locator('[data-pick="11"]').click();
    await expect(nobot).toHaveAttribute('data-state', 'found');
    await expect(nobot.locator('[data-found]')).toContainText('picked');

    await expect(page.locator('[data-summary-found]')).toHaveAttribute('data-summary-found', '2');
    await expect(page.locator('[data-summary-notfound]')).toHaveAttribute('data-summary-notfound', '1');
    // Bandcamp's search was never requested by the app.
    expect(crates.bandcamp.some((c) => c.includes('/search'))).toBe(false);
    noStoreCalls(calls, crates);
  });

  test('a pasted URL, and the SoundCloud fallback playing in its widget', async ({ page }) => {
    const { calls, crates } = await open(page);

    // A pasted Bandcamp album: the track is picked from it by title.
    const ululation = item(page, 'Into The Screaming Abyss', 'Ululation');
    await ululation.locator('[data-paste-url]').fill(MUIMINA_ALBUM);
    await ululation.locator('[data-use-url]').click();
    await expect(ululation).toHaveAttribute('data-state', 'found');
    await expect(ululation.locator('[data-found]')).toContainText('pasted');
    // Something that is not a store URL says so.
    const bioport = item(page, 'Ianuaria & Muscaria', 'Bioport');
    await bioport.locator('[data-paste-url]').fill('https://example.com/x');
    await bioport.locator('[data-use-url]').click();
    await expect(bioport.locator('[data-item-error]')).toHaveText('Paste a Bandcamp or SoundCloud URL');

    // SoundCloud only on demand, through its oEmbed check.
    const codex = item(page, 'Codex Spiralis', 'Something Different');
    expect(crates.soundcloud).toEqual([]);
    await codex.locator('[data-try-soundcloud]').click();
    await expect(codex).toHaveAttribute('data-state', 'found');
    await expect(codex.locator('[data-source-link]')).toHaveAttribute('href', CODEX_SC);
    expect(crates.soundcloud).toEqual(['Something Different']);

    // Listen: list order, so Ululation (8th) comes before Codex Spiralis (34th).
    await page.locator('[data-tab="listen"]').click();
    await expect(page.locator('[data-queue-count]')).toHaveText('2');
    const card = page.locator('[data-current]');
    await expect(card.locator('[data-track-title]')).toHaveText('Ululation');
    await page.locator(`[data-queued="${itemKey('Codex Spiralis', 'Something Different')}"]`).click();
    await expect(card.locator('[data-track-title]')).toHaveText('Something Different');
    await expect(card.locator('[data-widget-note]')).toContainText('no gapless, no waveform');

    // Play: the widget loads on the card (not the player bar), the API binds it and plays.
    await card.locator('[data-play-preview]').click();
    const widget = card.locator('[data-soundcloud-widget]');
    await expect(widget).toHaveAttribute('src', /^https:\/\/w\.soundcloud\.com\/player\/\?url=https%3A%2F%2Fsoundcloud\.com%2Fcodex-spiralis%2Fsomething-different/);
    await expect(card.locator('[data-play-preview]')).toHaveText(/Pause/);
    expect(await page.evaluate(() => (window as unknown as { __sc: { calls: string[] } }).__sc.calls)).toContain('play');
    await expect(page.locator('[data-player-source]')).toHaveCount(0);

    // The widget says the track finished: the card moves on, and the next (a Bandcamp
    // find) plays in the player bar; the widget is gone.
    await page.evaluate(() => (window as unknown as { __scFire: (n: string) => void }).__scFire('finish'));
    await expect(card.locator('[data-track-title]')).toHaveText('Ululation');
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Bandcamp · Muimina Sounds');
    await expect(page.locator('[data-soundcloud-widget]')).toHaveCount(0);

    await page.locator('[data-verdict="maybe"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Something Different');
    await page.locator('[data-verdict="like"]').click();
    await expect(page.locator('[data-queue-empty]')).toBeVisible();
    noStoreCalls(calls, crates);
  });

  test('rate and skip inside the crate; its shortlist is its own, never P\'s', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const { calls, crates } = await open(page);
    await item(page, 'Tengri & Jahbo', 'Patch Hell').locator('[data-find]').click();
    await expect(item(page, 'Tengri & Jahbo', 'Patch Hell')).toHaveAttribute('data-state', 'found');
    await item(page, 'Atriohm', 'Shesharka').locator('[data-find]').click();
    await expect(item(page, 'Atriohm', 'Shesharka')).toHaveAttribute('data-state', 'found');

    await page.locator('[data-tab="listen"]').click();
    const card = page.locator('[data-current]');
    await expect(card.locator('[data-track-title]')).toHaveText('Patch Hell');

    // A Bandcamp find plays in the player bar, through our audio endpoint.
    await card.locator('[data-play-preview]').click();
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Pause');
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Bandcamp · Parvati Records');
    expect(calls.audio[0]).toMatch(/^\/api\/proxy\/discover\/bandcamp\/3001\?release=/);

    await card.locator('[data-star="5"]').click();
    await card.locator('[data-buy]').click();
    await page.locator('[data-verdict="like"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Shesharka');
    await page.locator('[data-listen]').focus();
    await page.keyboard.press('s');
    await expect(page.locator('[data-queue-empty]')).toBeVisible();
    await expect(page.locator('[data-queue-count]')).toHaveText('0');

    await page.locator('[data-tab="shortlist"]').click();
    await expect(page.locator('[data-shortlisted]')).toHaveCount(1);
    await expect(page.locator('[data-shortlisted]')).toContainText('Patch Hell');
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveAttribute('href', `${PARVATI}/album/testimony-20`);
    await page.locator('[data-copy-links]').click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toContain(`${PARVATI}/album/testimony-20`);

    // P's own page: no queue, nothing liked, nothing to buy from the crate.
    await page.goto('/discover/P/shortlist');
    await expect(page.locator('[data-liked]')).toHaveCount(0);
    await expect(page.locator('[data-shortlisted]')).toHaveCount(0);
    await expect(page.locator('[data-queue-count]')).toHaveText('0');
    noStoreCalls(calls, crates);
  });

  test('Find all goes through every track with progress and a line per track', async ({ page }) => {
    const { calls, crates } = await open(page);
    await page.locator('[data-find-all]').click();
    const report = page.locator('[data-refresh-report]');
    await expect(report).toHaveAttribute('data-running', 'false', { timeout: 60_000 });
    await expect(page.locator('[data-refresh-done]')).toContainText('Looked up 69 of 69');
    await expect(page.locator('[data-refresh-results] [data-result]')).toHaveCount(69);
    await expect(page.locator('[data-result-kind="found"]')).toHaveCount(2);        // Patch Hell, Shesharka
    await expect(page.locator('[data-result-kind="ambiguous"]')).toHaveCount(1);    // Nobot
    await expect(page.locator('[data-summary-pending]')).toHaveAttribute('data-summary-pending', '0');
    expect(crates.discogs).toHaveLength(69);   // the owned track cost nothing

    // Again: nothing left to do, and it says so instead of doing nothing.
    await page.locator('[data-find-all]').click();
    await expect(page.locator('[data-refresh-done]')).toContainText('Nothing left to look up');
    noStoreCalls(calls, crates);
  });

  test('create, rename and delete a list', async ({ page }) => {
    const { calls, crates } = await open(page, '/discover/Z');
    await page.locator('[data-new-crate]').click();
    await page.locator('[data-new-crate-name]').fill('Ozora 2027 picks');
    await page.locator('[data-create-crate]').click();
    await expect(page).toHaveURL(/\/discover\/Z\/list\/ozora-2027-picks-[0-9a-f]{6}\/tracks$/);
    await expect(page.locator('[data-crate-name]')).toHaveText('Ozora 2027 picks');

    await page.locator('[data-add-tracks]').fill('Dragon - Tetrakis\nnot a track line\nAlchemy Circle - Tranceformation');
    await page.locator('[data-add-tracks-button]').click();
    await expect(page.locator('[data-add-result]')).toContainText('Added 2, skipped 1');
    await expect(page.locator('[data-crate-item]')).toHaveCount(2);
    // Tetrakis is in the collection: already owned.
    await expect(page.locator('[data-crate-item]').first()).toHaveAttribute('data-state', 'owned');

    await page.locator('[data-rename]').click();
    await page.locator('[data-rename-input]').fill('Ozora 2027');
    await page.locator('[data-rename-save]').click();
    await expect(page.locator('[data-crate-name]')).toHaveText('Ozora 2027');
    await expect(page.locator('[data-crate-nav]').filter({ hasText: 'Ozora 2027' })).toBeVisible();

    await page.locator('[data-delete-crate]').click();
    await page.locator('[data-confirm-delete-yes]').click();
    await expect(page).toHaveURL(/\/discover\/Z$/);
    await expect(page.locator('[data-crate-chip]').filter({ hasText: 'Ozora' })).toHaveCount(0);
    noStoreCalls(calls, crates);
  });
});
