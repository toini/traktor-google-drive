import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';
import { mockDiscover, discoverNml, type DiscoverCalls } from './discover-mock';
import {
  mockCrates, itemKey, CRATE_ID, PARVATI, MUIMINA_ALBUM, CODEX_SC, BIOPORT_TRACK, type CrateCalls, type CrateMockOptions,
} from './crates-mock';

// Crates (ADR 20261025-54 §13): the friend's-set list shipped in the starter file,
// one list to find, hear and judge its tracks (Tracks and Listen combined, §13.8),
// Bandcamp and Beatport as the stores, SoundCloud behind a flag that is off, and
// the research batch of buy links. Every store endpoint is mocked; any request to
// a real store host (Bandcamp, Beatport, Discogs, SoundCloud) fails the test.

const LIST = `/discover/P/list/${CRATE_ID}`;
const item = (page: Page, artist: string, title: string) => page.locator(`[data-crate-item="${itemKey(artist, title)}"]`);
const chip = (page: Page, id: string) => page.locator(`[data-filter="${id}"]`);
const count = (page: Page, id: string) => chip(page, id).locator('[data-filter-count]');

async function open(page: Page, path = LIST, opts: CrateMockOptions = {}): Promise<{ calls: DiscoverCalls; crates: CrateCalls }> {
  await mockDrive(page, { nml: discoverNml() });
  const calls = await mockDiscover(page, { realStarter: true });
  const crates = await mockCrates(page, opts);
  await seedToken(page);
  await page.goto(path);
  return { calls, crates };
}

function noStoreCalls(calls: DiscoverCalls, crates: CrateCalls) {
  expect(calls.storeRequests).toEqual([]);
  expect(crates.storeRequests).toEqual([]);
}

/** Find ▾ → "Find it for me" on a row not looked up yet. */
async function find(page: Page, artist: string, title: string, state = 'found') {
  const row = item(page, artist, title);
  await row.locator('[data-find-menu-toggle]').click();
  await row.locator('[data-find-menu] [data-find]').click();
  await expect(row).toHaveAttribute('data-state', state);
  return row;
}

/** This device's log as last written (each write sends the whole file). */
function lastLog(calls: DiscoverCalls) {
  return calls.appDataUpdates.at(-1)?.body ?? calls.appDataCreates.at(-1)?.body ?? '';
}

function logOf(calls: DiscoverCalls) {
  return lastLog(calls).split('\n');
}

test.describe('Crates', () => {
  test('the shipped list appears under P with its 70 tracks, apart from the P seed queue', async ({ page }) => {
    const { calls, crates } = await open(page, '/discover/P');

    const crateChip = page.locator(`[data-crate-chip="${CRATE_ID}"]`);
    await expect(crateChip).toContainText('Looney Boon Hug Halloween 2026');
    await expect(page.locator(`[data-crate-nav="${CRATE_ID}"]`)).toContainText('P ›');
    await expect(page.locator('[data-queue-count]')).toHaveText('0');
    await expect(page.locator('[data-seed-count]')).toHaveText('0');
    await expect(page.locator('[data-shortlist-count]')).toHaveText('0');

    await crateChip.click();
    await expect(page).toHaveURL(new RegExp(`${LIST}$`));
    await expect(page.locator('[data-crate-name]')).toHaveText('Looney Boon Hug Halloween 2026');
    await expect(page.locator('[data-tab="tracks"]')).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('[data-tab="listen"]')).toHaveCount(0);   // one list now
    await expect(page.locator('[data-item-count]')).toHaveAttribute('data-item-count', '70');
    await expect(page.locator('[data-crate-explainer]')).toHaveText('Listen → skip or keep → rate the keepers → buy');

    // To hear by default: 69, the owned one (Gidra – Viral Virus) is not to hear.
    await expect(chip(page, 'tohear')).toHaveAttribute('aria-pressed', 'true');
    await expect(count(page, 'tohear')).toHaveAttribute('data-filter-count', '69');
    await expect(count(page, 'notfound')).toHaveAttribute('data-filter-count', '69');
    await expect(count(page, 'all')).toHaveAttribute('data-filter-count', '70');
    await expect(page.locator('[data-crate-item]')).toHaveCount(69);
    await expect(item(page, 'Gidra', 'Viral Virus')).toHaveCount(0);
    await chip(page, 'all').click();
    await expect(page.locator('[data-crate-item]')).toHaveCount(70);
    await expect(item(page, 'Gidra', 'Viral Virus')).toHaveAttribute('data-state', 'owned');

    const first = page.locator('[data-crate-item]').first();
    await expect(first).toContainText('Tengri & Jahbo – Patch Hell');
    await expect(first.locator('[data-verdict-badge]')).toHaveText('Unheard');
    // The uploader is a tooltip, not a "SoundCloud: …" line.
    await expect(first.locator('[data-item-title]')).toHaveAttribute('title', 'Uploaded by Tengri, Jahbo');
    await expect(page.locator('[data-crate-tracks]')).not.toContainText('SoundCloud');
    await expect(page.locator('[data-crate-tracks]')).not.toContainText('Discogs');

    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const log = logOf(calls);
    expect(log.filter((l) => l.includes('"t":"crate-add"'))).toHaveLength(1);
    expect(log.filter((l) => l.includes('"t":"crate-item"'))).toHaveLength(70);
    expect(crates.discogs).toEqual([]);
    noStoreCalls(calls, crates);
  });

  test('the Find menu: lookup, Bandcamp and Beatport search tabs, paste; ambiguous with a pick; ⋯ removes', async ({ page }) => {
    const { calls, crates } = await open(page);

    const patch = await find(page, 'Tengri & Jahbo', 'Patch Hell');
    await expect(patch.locator('[data-source-link]')).toHaveAttribute('data-source', 'bandcamp');
    await expect(patch.locator('[data-found]')).toContainText('Parvati Records');
    await expect(patch).not.toContainText('Discogs');
    // Found: its primary action is the player (here, the row is already on the card).
    await expect(patch.locator('[data-current]')).toBeVisible();

    // Not found: a short line, and Find ▾ holds the rest.
    const bioport = await find(page, 'Ianuaria & Muscaria', 'Bioport', 'notfound');
    await expect(bioport.locator('[data-state-label]')).toHaveText('Not found yet');
    await expect(bioport.locator('[data-state-label]')).toHaveAttribute('title', /ianuaria\.bandcamp\.com/);
    await expect(bioport).not.toContainText('Discogs');
    await bioport.locator('[data-find-menu-toggle]').click();
    const menu = bioport.locator('[data-find-menu]');
    await expect(menu.locator('[data-bandcamp-search]')).toHaveAttribute('href', 'https://bandcamp.com/search?item_type=t&q=Ianuaria%20Bioport');
    await expect(menu.locator('[data-beatport-search]')).toHaveAttribute('href', 'https://www.beatport.com/search/tracks?q=Ianuaria%20Bioport');
    await expect(menu.locator('[data-beatport-search]')).toHaveAttribute('target', '_blank');
    await expect(menu.locator('[data-paste-url]')).toHaveAttribute('placeholder', 'Paste a Bandcamp or Beatport URL');
    await expect(menu.locator('[data-find-again]')).toBeVisible();

    const nobot = await find(page, 'Nobot', 'The Future', 'ambiguous');
    await expect(nobot.locator('[data-candidates] li')).toHaveCount(2);
    await nobot.locator('[data-pick="11"]').click();
    await expect(nobot).toHaveAttribute('data-state', 'found');
    await expect(nobot.locator('[data-found]')).toContainText('picked');

    // ⋯ holds Remove.
    const black = item(page, 'Vahati & Creepy Deep', 'Black Moon');
    await black.locator('[data-more]').click();
    await black.locator('[data-more-menu] [data-remove-item]').click();
    await expect(black).toHaveCount(0);
    await expect(count(page, 'all')).toHaveAttribute('data-filter-count', '69');

    expect(crates.bandcamp.some((c) => c.includes('/search'))).toBe(false);
    noStoreCalls(calls, crates);
  });

  test('verdict badges after skip, maybe and like; chips with counts; change and undo from a row', async ({ page }) => {
    const { calls, crates } = await open(page);
    await find(page, 'Tengri & Jahbo', 'Patch Hell');
    await find(page, 'Atriohm', 'Shesharka');
    const ulul = item(page, 'Into The Screaming Abyss', 'Ululation');
    await ulul.locator('[data-find-menu-toggle]').click();
    await ulul.locator('[data-find-menu] [data-paste-url]').fill(MUIMINA_ALBUM);
    await ulul.locator('[data-find-menu] [data-use-url]').click();
    await expect(ulul).toHaveAttribute('data-state', 'found');

    // The card is on the first found track; Skip moves on, and the skipped row leaves "To hear".
    const card = page.locator('[data-current]');
    await expect(card.locator('[data-track-title]')).toHaveText('Patch Hell');
    await card.locator('[data-verdict="skip"]').click();
    await expect(item(page, 'Tengri & Jahbo', 'Patch Hell')).toHaveCount(0);
    await expect(card.locator('[data-track-title]')).toHaveText('Shesharka');
    await page.locator('[data-crate-tracks]').focus();
    await page.keyboard.press('m');   // keys still work
    await expect(card.locator('[data-track-title]')).toHaveText('Ululation');
    await card.locator('[data-verdict="like"]').click();

    await expect(count(page, 'tohear')).toHaveAttribute('data-filter-count', '66');
    await expect(count(page, 'skipped')).toHaveAttribute('data-filter-count', '1');
    await expect(count(page, 'maybe')).toHaveAttribute('data-filter-count', '1');
    await expect(count(page, 'liked')).toHaveAttribute('data-filter-count', '1');
    await expect(count(page, 'tobuy')).toHaveAttribute('data-filter-count', '0');

    await chip(page, 'all').click();
    await expect(item(page, 'Tengri & Jahbo', 'Patch Hell').locator('[data-verdict-badge]')).toHaveText('✗ Skipped');
    await expect(item(page, 'Atriohm', 'Shesharka').locator('[data-verdict-badge]')).toHaveText('? Maybe');
    await expect(item(page, 'Into The Screaming Abyss', 'Ululation').locator('[data-verdict-badge]')).toHaveText('♥ Liked');

    // Change a verdict inline: Maybe → 4 stars, then on the buy list.
    const shesharka = item(page, 'Atriohm', 'Shesharka');
    await shesharka.locator('[data-verdict-badge]').click();
    await shesharka.locator('[data-verdict-menu] [data-star="4"]').click();
    await expect(shesharka.locator('[data-verdict-badge]')).toHaveText('★4');
    await shesharka.locator('[data-verdict-menu] [data-set-verdict="buy"]').click();
    await expect(shesharka.locator('[data-verdict-badge]')).toHaveText('🛒 Buy');
    await expect(count(page, 'tobuy')).toHaveAttribute('data-filter-count', '1');

    // Undo the skip from the Skipped chip: back to To hear, Unheard.
    await chip(page, 'skipped').click();
    const patch = item(page, 'Tengri & Jahbo', 'Patch Hell');
    await patch.locator('[data-verdict-badge]').click();
    await patch.locator('[data-verdict-clear]').click();
    await expect(page.locator('[data-crate-item]')).toHaveCount(0);
    await expect(count(page, 'skipped')).toHaveAttribute('data-filter-count', '0');
    await chip(page, 'tohear').click();
    await expect(item(page, 'Tengri & Jahbo', 'Patch Hell').locator('[data-verdict-badge]')).toHaveText('Unheard');
    await expect(count(page, 'tohear')).toHaveAttribute('data-filter-count', '67');

    // The Shortlist tab is pass 2: the buy list with its link.
    await page.locator('[data-tab="shortlist"]').click();
    await expect(page.locator('[data-shortlisted]')).toContainText('Shesharka');
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveText(/Buy on Bandcamp/);
    noStoreCalls(calls, crates);
  });

  test('play, rate, buy and like on the card; the crate shortlist is its own, never P\'s', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    const { calls, crates } = await open(page);
    await find(page, 'Tengri & Jahbo', 'Patch Hell');
    await find(page, 'Atriohm', 'Shesharka');

    const card = page.locator('[data-current]');
    await expect(card.locator('[data-track-title]')).toHaveText('Patch Hell');
    await card.locator('[data-play-preview]').click();
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Pause');
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Bandcamp · Parvati Records');
    expect(calls.audio[0]).toMatch(/^\/api\/proxy\/discover\/bandcamp\/3001\?release=/);

    // Stars and Buy keep the card on the track, even under "To hear".
    await card.locator('[data-star="5"]').click();
    await card.locator('[data-buy]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Patch Hell');
    await card.locator('[data-verdict="like"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Shesharka');
    await page.locator('[data-crate-tracks]').focus();
    await page.keyboard.press('s');
    await expect(page.locator('[data-current]')).toHaveCount(0);

    await page.locator('[data-tab="shortlist"]').click();
    await expect(page.locator('[data-shortlisted]')).toHaveCount(1);
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveAttribute('href', `${PARVATI}/album/testimony-20`);
    await page.locator('[data-copy-links]').click();
    expect(await page.evaluate(() => navigator.clipboard.readText())).toContain(`${PARVATI}/album/testimony-20`);

    await page.goto('/discover/P/shortlist');
    await expect(page.locator('[data-liked]')).toHaveCount(0);
    await expect(page.locator('[data-shortlisted]')).toHaveCount(0);
    await expect(page.locator('[data-queue-count]')).toHaveText('0');
    noStoreCalls(calls, crates);
  });

  test('waiting for a verdict stops at the end of a track; with continuous play, Just played asks about it', async ({ page }) => {
    // Continuous play on in the player; the crate waits for a verdict by default.
    const { calls, crates } = await open(page, LIST, { storage: { 'player:continuous-play': '1' } });
    await find(page, 'Tengri & Jahbo', 'Patch Hell');
    await find(page, 'Atriohm', 'Shesharka');
    await expect(page.locator('[data-wait-verdict]')).toBeChecked();

    const card = page.locator('[data-current]');
    await card.locator('[data-play-preview]').click();
    // The 4 s preview ends; the player stops on it, the card stays, nothing is "just played".
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Play', { timeout: 15_000 });
    await expect(card.locator('[data-track-title]')).toHaveText('Patch Hell');
    await expect(page.locator('[data-just-played]')).toHaveCount(0);
    // The verdict starts the next one.
    await card.locator('[data-verdict="skip"]').click();
    await expect(card.locator('[data-track-title]')).toHaveText('Shesharka');
    await expect(page.locator('.player-toggle')).toHaveAttribute('aria-label', 'Pause');

    // Not waiting: continuous play moves on by itself, and Just played offers the verdict.
    await page.locator('[data-wait-verdict]').uncheck();
    await page.locator('[data-crate-tracks]').focus();
    await page.keyboard.press('u');   // undo the skip: Patch Hell is to hear again
    await expect(item(page, 'Tengri & Jahbo', 'Patch Hell').locator('[data-verdict-badge]')).toHaveText('Unheard');
    await item(page, 'Tengri & Jahbo', 'Patch Hell').locator('[data-current] [data-play-preview], [data-listen-button]').first().click();
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Parvati Records');
    const strip = page.locator('[data-just-played]');
    await expect(strip).toHaveAttribute('data-just-played', itemKey('Tengri & Jahbo', 'Patch Hell'), { timeout: 15_000 });
    await expect(strip).toContainText('Just played: Tengri & Jahbo – Patch Hell');
    await expect(card.locator('[data-track-title]')).toHaveText('Shesharka');
    await strip.locator('[data-just-played-verdict="skip"]').click();
    await expect(strip).toHaveCount(0);
    await expect(count(page, 'skipped')).toHaveAttribute('data-filter-count', '1');
    noStoreCalls(calls, crates);
  });

  test('a pasted Beatport URL is the buy link, heard on Beatport, never fetched', async ({ page }) => {
    const { calls, crates } = await open(page);
    const bioport = item(page, 'Ianuaria & Muscaria', 'Bioport');
    await bioport.locator('[data-find-menu-toggle]').click();
    await bioport.locator('[data-paste-url]').fill('https://www.beatport.com/track/bioport/12345678');
    await bioport.locator('[data-use-url]').click();
    await expect(bioport).toHaveAttribute('data-state', 'found');
    await expect(bioport.locator('[data-source-link]')).toHaveAttribute('data-source', 'beatport');
    await expect(bioport.locator('[data-source-link]')).toHaveText('Beatport ↗');
    const listen = bioport.locator('[data-listen-beatport]');
    await expect(listen).toHaveText('Listen on Beatport ↗');
    await expect(listen).toHaveAttribute('href', 'https://www.beatport.com/track/bioport/12345678');
    await expect(bioport.locator('[data-current]')).toHaveCount(0);   // no player for it here

    // A search page is not a track.
    const black = item(page, 'Vahati & Creepy Deep', 'Black Moon');
    await black.locator('[data-find-menu-toggle]').click();
    await black.locator('[data-paste-url]').fill('https://www.beatport.com/search?q=black+moon');
    await black.locator('[data-use-url]').click();
    await expect(black.locator('[data-item-error]')).toContainText('Beatport track URL');

    // Like and buy it from its badge: the crate's buy list links to Beatport.
    await bioport.locator('[data-verdict-badge]').click();
    await bioport.locator('[data-set-verdict="buy"]').click();
    await page.locator('[data-tab="shortlist"]').click();
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveText(/Buy on Beatport/);
    await expect(page.locator('[data-shortlisted] [data-buy-link]')).toHaveAttribute('href', 'https://www.beatport.com/track/bioport/12345678');
    noStoreCalls(calls, crates);   // beatport.com is aborted and recorded: nothing went there
  });

  test('no SoundCloud anywhere with the flag off; with it on, Try SoundCloud and its widget', async ({ page, browser }) => {
    const { calls, crates } = await open(page);
    const codex = item(page, 'Codex Spiralis', 'Something Different');
    await codex.locator('[data-find-menu-toggle]').click();
    await expect(codex.locator('[data-find-menu]')).toBeVisible();
    await expect(page.locator('[data-try-soundcloud], [data-soundcloud-search]')).toHaveCount(0);
    await codex.locator('[data-paste-url]').fill(CODEX_SC);
    await codex.locator('[data-use-url]').click();
    await expect(codex.locator('[data-item-error]')).toHaveText('Paste a Bandcamp or Beatport URL');
    await expect(page.locator('[data-crate-tracks]')).not.toContainText('SoundCloud');
    await page.locator('[data-find-all]').click();
    await expect(page.locator('[data-refresh-report]')).toHaveAttribute('data-running', 'false', { timeout: 60_000 });
    expect(crates.soundcloud).toEqual([]);   // Find all never asks SoundCloud
    noStoreCalls(calls, crates);

    // The same paths, switched on in another browser.
    const other = await (await browser.newContext()).newPage();
    const on = await open(other, LIST, { soundcloud: true });
    const codexOn = item(other, 'Codex Spiralis', 'Something Different');
    await codexOn.locator('[data-find-menu-toggle]').click();
    await expect(codexOn.locator('[data-paste-url]')).toHaveAttribute('placeholder', 'Paste a Bandcamp, Beatport or SoundCloud URL');
    await codexOn.locator('[data-try-soundcloud]').click();
    await expect(codexOn).toHaveAttribute('data-state', 'found');
    expect(on.crates.soundcloud).toEqual(['Something Different']);
    const card = other.locator('[data-current]');
    await card.locator('[data-play-preview]').click();
    await expect(card.locator('[data-widget-note]')).toContainText('no gapless, no waveform');
    await expect(card.locator('[data-soundcloud-widget]')).toHaveAttribute('src', /^https:\/\/w\.soundcloud\.com\/player\/\?url=https%3A%2F%2Fsoundcloud\.com%2Fcodex-spiralis/);
    noStoreCalls(on.calls, on.crates);
  });

  test('the research batch: buy links, corrected titles and notes, applied once and never over Toni\'s own', async ({ page, browser }) => {
    // First, without the batch: Toni pastes Beatport for Bioport and skips Black Moon's find.
    const { calls } = await open(page);
    const bioport = item(page, 'Ianuaria & Muscaria', 'Bioport');
    await bioport.locator('[data-find-menu-toggle]').click();
    await bioport.locator('[data-paste-url]').fill('https://www.beatport.com/track/bioport/12345678');
    await bioport.locator('[data-use-url]').click();
    await expect(bioport).toHaveAttribute('data-state', 'found');
    await expect(page.locator('[data-save-status="Saved"]')).toBeVisible();
    const log = lastLog(calls);

    // Then the deploy with the batch, on the same log.
    const other = await (await browser.newContext()).newPage();
    await mockDrive(other, { nml: discoverNml() });
    const otherCalls = await mockDiscover(other, { realStarter: true, appData: { [calls.appDataCreates[0].name]: log } });
    const crates = await mockCrates(other, { research: true });
    await seedToken(other);
    await other.goto(LIST);
    await chip(other, 'all').click();

    // Toni's paste stands.
    await expect(item(other, 'Ianuaria & Muscaria', 'Bioport').locator('[data-source-link]')).toHaveAttribute('data-source', 'beatport');
    // Corrections keep the row (its key) and say what the screenshot read.
    const self = item(other, 'Seraburayka & Fear Disorder', 'Self R… (truncated)');
    await expect(self.locator('[data-item-title]')).toHaveText('Seraburayka & Fear Disorder – Self Realization');
    await expect(self.locator('[data-found]')).toContainText('on album VA - Primitif Festival Vol.2');
    await expect(self.locator('[data-beatport-link]')).toHaveAttribute('href', 'https://www.beatport.com/release/va-primitif-festival-vol2/3581531');
    await expect(item(other, 'Into The Screaming Abyss', 'Ululation').locator('[data-item-title]')).toHaveText("Pepe's Bodega – Into The Screaming Abyss - Ululation");
    await expect(item(other, 'Koobluda', 'NOT UR KIND!').locator('[data-item-title]')).toHaveText('Koobluda – Not Your Kind!');
    await expect(item(other, 'Koobluda', 'NOT UR KIND!')).toContainText('screenshot: “Koobluda – NOT UR KIND!”');
    // Free, and the notes of what is not for sale.
    await expect(item(other, 'Ectogasmics', 'Love & Understanding').locator('[data-free]')).toHaveText('free');
    const gidra = item(other, 'Gidra', 'Spider Skunk (Edit 4)');
    await expect(gidra).toHaveAttribute('data-state', 'notfound');
    await expect(gidra.locator('[data-item-note]')).toContainText("Gidra's own edit, not for sale. The original for sale");
    await expect(gidra.locator('[data-item-link]')).toHaveAttribute('href', 'https://beatspace-parvati.bandcamp.com/track/gidra-spider-skunk');

    // Applied once: the batch's events are in the log one time, and a reload adds none.
    await expect(other.locator('[data-save-status="Saved"]')).toBeVisible();
    const resolves = () => logOf(otherCalls).filter((l) => l.includes('"t":"crate-resolve"'));
    const first = resolves().length;
    expect(first).toBeGreaterThan(40);
    expect(resolves().filter((l) => l.includes(itemKey('Ianuaria & Muscaria', 'Bioport')))).toHaveLength(0);
    expect(logOf(otherCalls).filter((l) => l.includes('"t":"starter"') && l.includes('2026-11-01.2'))).toHaveLength(1);
    await other.reload();
    await expect(other.locator('[data-save-status="Saved"]')).toBeVisible();
    await expect(chip(other, 'all')).toBeVisible();
    expect(resolves().length).toBe(first);
    noStoreCalls(otherCalls, crates);
  });

  test('a research Bandcamp link reads its page the first time it plays', async ({ page }) => {
    const { calls, crates } = await open(page, LIST, { research: true });
    const bioport = item(page, 'Ianuaria & Muscaria', 'Bioport');
    await expect(bioport).toHaveAttribute('data-state', 'found');
    await expect(bioport.locator('[data-source-link]')).toHaveAttribute('href', BIOPORT_TRACK);
    expect(crates.bandcamp).toEqual([]);   // nothing read until it plays
    await bioport.locator('[data-listen-button], [data-current] [data-play-preview]').first().click();
    await expect(page.locator('[data-player-source="bandcamp"]')).toContainText('Blue Hour Sounds');
    expect(crates.bandcamp).toEqual([`release ${BIOPORT_TRACK}`]);
    expect(calls.audio.some((a) => a.includes('/bandcamp/3020?'))).toBe(true);
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
    await expect(page.locator('[data-refresh-results]')).not.toContainText('Discogs');
    expect(crates.discogs).toHaveLength(69);

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
    // Tetrakis is in the collection: already owned, so not to hear.
    await expect(page.locator('[data-crate-item]')).toHaveCount(1);
    await chip(page, 'all').click();
    await expect(page.locator('[data-crate-item]')).toHaveCount(2);
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
