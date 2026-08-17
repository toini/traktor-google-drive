import { test, expect } from '@playwright/test';
import { mockDrive, seedToken, DISCOVERED_COLLECTION_ID, EXPECTED_COLLECTION_ID } from './drive-mock';

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

test.describe('collection sidebar', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('renders the playlist folders from the NML', async ({ page }) => {
    await page.goto('/music');
    await expect(page.getByText('My Sets')).toBeVisible();
    await expect(page.getByText('Archive')).toBeVisible();
  });

  test('does not surface the $ROOT pseudo-folder', async ({ page }) => {
    await page.goto('/music');
    await expect(page.getByText('My Sets')).toBeVisible();
    // Collection.FromXml returns $ROOT as a Folder alongside its children.
    await expect(page.getByText('$ROOT')).toHaveCount(0);
  });

  test('links through to a playlist', async ({ page }) => {
    await page.goto('/music');
    // Wait for the tree to actually render before probing visibility —
    // isVisible() does not wait, so checking too early collapses the folder.
    await expect(page.getByText('My Sets')).toBeVisible();

    const link = page.getByRole('link', { name: 'Psytech 2024' });
    // Folders render expanded when there are only a few; only click the
    // summary if this one happens to be collapsed.
    if (!(await link.isVisible())) await page.getByText('My Sets').click();
    await link.click();
    await expect(page).toHaveURL(new RegExp(PSYTECH_UUID));
  });
});

test.describe('playlist', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('lists every track in the playlist', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5); // header + 4 tracks
    await expect(page.getByText('Alpha Drift', { exact: true })).toBeVisible();
    await expect(page.getByText('Beta Pulse')).toBeVisible();
    await expect(page.getByText('Gamma Echo')).toBeVisible();
  });

  test('formats playtime as mm:ss rather than raw seconds', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    // PLAYTIME_FLOAT 245.5 -> 4:05, not "245.5 s"
    await expect(page.getByText('4:05')).toBeVisible();
    await expect(page.getByText(/245\.5 s/)).toHaveCount(0);
  });

  test('resolves each track to a distinct Drive file', async ({ page }) => {
    // Two tracks share the filename alpha.wav in different folders; matching
    // by bare filename makes both resolve to the same Drive id.
    const calls = await mockDrive(page);
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    const ids = await page.locator('[data-drive-file-id]').evaluateAll((els) =>
      els.map((e) => e.getAttribute('data-drive-file-id')),
    );
    // Guard against a vacuous pass if the attribute ever disappears.
    expect(ids.length).toBe(4);
    expect(new Set(ids).size).toBe(ids.length);
    expect(calls.filter((c) => c.kind === 'query').length).toBeGreaterThan(0);

    // The two alpha.wav tracks must resolve to different Drive files, chosen by
    // their parent folder rather than by filename alone.
    expect(ids).toContain('drive-alpha-sets');
    expect(ids).toContain('drive-alpha-archive');
  });
});

test.describe('playback', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('plays only one track at a time', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    const buttons = page.getByRole('button', { name: /play/i });
    await buttons.nth(0).click();
    await expect(page.locator('.currently-playing')).toHaveCount(1);

    await buttons.nth(1).click();
    // The first track must have been stopped, not left running.
    await expect(page.locator('.currently-playing')).toHaveCount(1);

    const playing = await page.evaluate(
      () => document.querySelectorAll('audio').length &&
        [...document.querySelectorAll('audio')].filter((a) => !a.paused).length,
    );
    expect(playing).toBeLessThanOrEqual(1);
  });

  test('stops audio when navigating away from the playlist', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await page.getByRole('button', { name: /play/i }).first().click();
    await page.goto('/music');
    const stillPlaying = await page.evaluate(
      () => [...document.querySelectorAll('audio')].filter((a) => !a.paused).length,
    );
    expect(stillPlaying).toBe(0);
  });
});

test.describe('auth', () => {
  test('redirects to login when no token is present', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await expect(page).toHaveURL(/\/login/);
    // Asserting the URL alone is not enough: the app once changed URL but left
    // the Router unrendered, sitting on "Checking authentication..." forever.
    await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible();
    await expect(page.getByText('Checking authentication')).toHaveCount(0);
  });

  test('does not claim the session expired for a first-time visitor', async ({ page }) => {
    await mockDrive(page);
    await page.goto('/music');
    await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible();
    await expect(page.getByText(/session expired/i)).toHaveCount(0);
  });

  test('surfaces an expired token instead of failing silently', async ({ page }) => {
    await mockDrive(page, { collectionStatus: 401 });
    await seedToken(page);
    await page.goto('/music');
    await expect(page.getByText(/sign in|expired|session/i).first()).toBeVisible();
  });
});

test.describe('collection file resolution', () => {
  test('recovers when the hardcoded collection id 404s', async ({ page }) => {
    // The real failure: the id the app shipped with no longer exists, the old
    // code fed the 404 JSON body to the XML parser, and the page went blank.
    const calls = await mockDrive(page, { legacyCollectionMissing: true });
    await seedToken(page);
    await page.goto('/music');

    await expect(page.getByText('My Sets')).toBeVisible();
    expect(calls.some((c) => c.url.includes(DISCOVERED_COLLECTION_ID))).toBe(true);
  });

  test('reports a readable error when no collection.nml exists', async ({ page }) => {
    await mockDrive(page, { legacyCollectionMissing: true, noCollectionFound: true });
    await seedToken(page);
    await page.goto('/music');

    await expect(page.getByText(/No file named collection\.nml/i)).toBeVisible();
  });

  test('shows the error banner with expandable detail on failure', async ({ page }) => {
    await mockDrive(page, { legacyCollectionMissing: true, noCollectionFound: true });
    await seedToken(page);
    await page.goto('/music');

    const banner = page.locator('.error-banner');
    await expect(banner).toBeVisible();
    await banner.getByRole('button', { name: /show details/i }).click();
    // The detail must carry the real exception text, not a generic message.
    await expect(page.locator('.error-detail')).toContainText(/DriveRequestException|collection\.nml/i);
  });

  test('does not trigger the framework unhandled-error bar for handled failures', async ({ page }) => {
    // Blazor WASM shows #blazor-error-ui on any .NET write to Console.Error, so
    // logging a caught error to stderr made handled failures look like crashes.
    await mockDrive(page, { legacyCollectionMissing: true, noCollectionFound: true });
    await seedToken(page);
    await page.goto('/music');
    await expect(page.locator('.error-banner')).toBeVisible();
    await expect(page.locator('#blazor-error-ui')).toBeHidden();
  });

  test('never renders a raw XML parse failure to the user', async ({ page }) => {
    await mockDrive(page, { nml: '{"error":{"code":404,"message":"File not found"}}' });
    await seedToken(page);
    await page.goto('/music');

    await expect(page.getByText(/not Traktor XML/i)).toBeVisible();
    await expect(page.getByText(/XmlException|Xml_InvalidRootData/)).toHaveCount(0);
  });
});

test.describe('stale cached auth.js', () => {
  // Reproduces the live regression: a browser holding the previous auth.js
  // (which only defined googleLogin) against newer WASM. The app used to throw
  // an unhandled JSException in App.OnInitializedAsync and sit forever on
  // "Checking authentication...".
  const OLD_AUTH_JS = `window.googleLogin = () => {};`;

  test('still reaches the login page instead of hanging', async ({ page }) => {
    await mockDrive(page);
    await page.route('**/auth.js', (r) =>
      r.fulfill({ status: 200, contentType: 'application/javascript', body: OLD_AUTH_JS }));

    await page.goto('/music');

    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible();
    await expect(page.getByText('Checking authentication')).toHaveCount(0);
  });

  test('falls back to sessionStorage when authGetToken is missing', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.route('**/auth.js', (r) =>
      r.fulfill({ status: 200, contentType: 'application/javascript', body: OLD_AUTH_JS }));

    await page.goto('/music');

    // The raw sessionStorage token is still honoured, so the collection loads.
    await expect(page.getByText('My Sets')).toBeVisible();
  });
});

test.describe('choosing among many collection.nml files', () => {
  test('prefers the newest Traktor version folder over a newer backup', async ({ page }) => {
    // The backup has the most recent modifiedTime, so ordering by date alone
    // picks the wrong file. The live collection is the one under the
    // highest-versioned "Traktor N.N.N" folder.
    const calls = await mockDrive(page, { legacyCollectionMissing: true, manyCollections: true });
    await seedToken(page);
    await page.goto('/music');

    await expect(page.getByText('My Sets')).toBeVisible();
    const downloaded = calls.filter((c) => c.kind === 'collection').map((c) => c.url);
    expect(downloaded.some((u) => u.includes(EXPECTED_COLLECTION_ID))).toBe(true);
    expect(downloaded.some((u) => u.includes('nml-backup'))).toBe(false);
  });

  test('says which file it used, as a notice rather than an error', async ({ page }) => {
    await mockDrive(page, { legacyCollectionMissing: true, manyCollections: true });
    await seedToken(page);
    await page.goto('/music');

    const banner = page.locator('.error-banner');
    await expect(banner).toBeVisible();
    await expect(banner).toHaveClass(/level-info/);
    await expect(banner).not.toHaveClass(/level-error/);
    await expect(banner).toContainText('Traktor 4.4.1');
  });

  test('offers a picker listing every candidate', async ({ page }) => {
    await mockDrive(page, { legacyCollectionMissing: true, manyCollections: true });
    await seedToken(page);
    await page.goto('/music');

    const select = page.locator('#collection-select');
    await expect(select).toBeVisible();
    await expect(select.locator('option')).toHaveCount(4);
    // Backups rank last.
    const texts = await select.locator('option').allTextContents();
    expect(texts[0]).toContain('Traktor 4.4.1');
    expect(texts[texts.length - 1]).toContain('backup');
  });
});

test.describe('added-at column', () => {
  const PSY = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a';

  const addedColumn = (page) =>
    page.locator('#playlist-table tbody td.col-added').allTextContents();

  test('renders the import date as ISO, and a dash when absent', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSY}`);
    await expect(page.getByRole('columnheader', { name: /Added/ })).toBeVisible();
    await expect(page.locator('td.col-added').filter({ hasText: '2023-03-12' })).toHaveCount(1);
    // The Archive Cut track has no IMPORT_DATE.
    await expect(page.locator('td.col-added').filter({ hasText: '—' })).toHaveCount(1);
  });

  test('sorts chronologically, not lexically', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSY}`);
    await page.getByRole('columnheader', { name: /Added/ }).click();

    const dates = (await addedColumn(page)).filter((d) => d !== '—');
    // A string sort would put 2023-10-10 and 2023-11-06 before 2023-03-12,
    // because Traktor stores them unpadded as 2023/10/10 vs 2023/3/12.
    expect(dates).toEqual(['2023-03-12', '2023-10-10', '2023-11-06']);
    expect(dates).toEqual([...dates].sort());
  });

  test('reverses, and keeps undated tracks last in both directions', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${PSY}`);
    const header = page.getByRole('columnheader', { name: /Added/ });

    await header.click();
    let all = await addedColumn(page);
    expect(all[all.length - 1]).toBe('—');

    await header.click();
    all = await addedColumn(page);
    expect(all.slice(0, 3)).toEqual(['2023-11-06', '2023-10-10', '2023-03-12']);
    expect(all[all.length - 1]).toBe('—');
  });
});

test.describe('recording -> playlist match', () => {
  const RECORDED = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62';

  test('expands a recording to the playlist built for that set', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);
    await expect(page.getByText('Z11-3 2021-09-17')).toBeVisible();

    // Target the row by name: the playlist holds several recordings and the
    // default Title sort decides which one is first.
    await page.locator('tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.expander').click();
    await expect(page.locator('.set-detail')).toBeVisible();

    // ".rec" is the order actually played, so it must be the default pick over
    // the base "Z11-3" playlist.
    await expect(page.locator('#playlist-table select')).toHaveValue(
      '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f61',
    );
    await expect(page.locator('.set-tracklist li')).toHaveCount(2);
  });

  test('lets the user override the match, and remembers it', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto(`/playlist/${RECORDED}`);
    const z11 = () => page.locator('tr', { hasText: 'Z11-3 2021-09-17' }).first().locator('.expander');
    await z11().click();

    const select = page.locator('#playlist-table select');
    await select.selectOption('0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f60'); // the base playlist
    await expect(page.locator('.set-tracklist li')).toHaveCount(3);

    await page.reload();
    await z11().click();
    await expect(page.locator('#playlist-table select')).toHaveValue(
      '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f60',
    );
  });

  test('shows no expander on an ordinary track', async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
    await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a');
    await expect(page.getByText('Beta Pulse')).toBeVisible();
    // Those tracks are not in a recordings folder.
    await expect(page.locator('.expander')).toHaveCount(0);
  });
});

test('picks the playlist from the same year when a set code is reused', async ({ page }) => {
  // "C4" exists for both 2025 and 2026; prefix matching alone cannot choose.
  await mockDrive(page);
  await seedToken(page);
  await page.goto('/playlist/0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f62');
  await expect(page.getByText('C4 2026-08-16')).toBeVisible();

  const row = page.locator('tr', { hasText: 'C4 2026-08-16' }).first();
  await row.locator('.expander').click();

  const select = page.locator('#playlist-table select');
  await expect(select).toHaveValue('0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f64'); // the 2026 one
  await expect(page.locator('.set-tracklist li')).toHaveCount(2);
});
