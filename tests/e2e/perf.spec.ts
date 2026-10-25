import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken, type DriveCall, type MockOptions } from './drive-mock';
import { BIG_A, BIG_B, LARGE_FOLDERS, largeDriveFiles, largeNml } from './large-fixture';

/**
 * Switching playlists on a collection the size of the real one (1,600 tracks,
 * 193 playlists, ~200-entry sets). The mock answers every Drive call after a
 * fixed delay, so a timing here mostly counts round trips, which is what a
 * weak train network turns into seconds.
 *
 * PERF_REPORT=1 also prints a step-by-step timing table (the numbers in the PR),
 * at two latencies and with the CPU slowed down 4x for the tablet.
 */

const nml = largeNml();
const driveFiles = largeDriveFiles();

function largeOptions(extra: MockOptions = {}): MockOptions {
  return { nml, driveFiles, driveFolders: LARGE_FOLDERS, latencyMs: 150, ...extra };
}

const rows = (page: Page) => page.locator('#playlist-table tbody tr');

/** Clicks the sidebar link and times it, in the page, until every row is on screen. */
async function timeSwitch(page: Page, to: { name: string; entries: number }): Promise<number> {
  return page.evaluate(async ({ name, entries }) => {
    const link = [...document.querySelectorAll<HTMLAnchorElement>('a.tree-row')]
      .find((a) => a.querySelector('.tree-label')?.textContent?.trim() === name);
    if (!link) throw new Error(`no sidebar link for ${name}`);
    const start = performance.now();
    link.click();
    await new Promise<void>((resolve) => {
      const check = () => {
        const title = document.querySelector('h1.playlist-title')?.textContent;
        const count = document.querySelectorAll('#playlist-table tbody tr').length;
        // Every row present AND resolved: a play button or a missing marker each.
        const settled = document.querySelectorAll(
          '#playlist-table tbody .play-button:not([disabled]), #playlist-table tbody .missing-marker').length;
        if (title === name && count === entries && settled === entries) resolve();
        else requestAnimationFrame(check);
      };
      check();
    });
    return performance.now() - start;
  }, to);
}

/** Milliseconds from navigation start until every row of the playlist is on screen. */
async function timeOpen(page: Page, to: { uuid: string; entries: number }): Promise<number> {
  await page.goto(`/playlist/${to.uuid}`);
  await page.waitForFunction(
    (entries) => document.querySelectorAll('#playlist-table tbody tr').length === entries
      && document.querySelectorAll(
        '#playlist-table tbody .play-button:not([disabled]), #playlist-table tbody .missing-marker').length === entries,
    to.entries,
    { polling: 'raf', timeout: 60_000 },
  );
  return page.evaluate(() => performance.now());
}

/**
 * Resolves once the app has stored the parsed collection, which it does a
 * couple of seconds after the page is up so it never competes with the render.
 * A reload before that would find no cache, and pass or fail for nothing.
 */
const cacheWritten = (page: Page) =>
  page.waitForEvent('console', { predicate: (m) => m.text().includes('[perf] nml.cache.write'), timeout: 60_000 });

const since = (calls: DriveCall[], from: number) => calls.slice(from);
const count = (calls: DriveCall[], kind: DriveCall['kind']) => calls.filter((c) => c.kind === kind).length;

test.describe('large collection', () => {
  test.beforeEach(async ({ page }) => {
    await seedToken(page);
  });

  test('switching between two large playlists makes no Drive lookups after the first', async ({ page }) => {
    const calls = await mockDrive(page, largeOptions());
    await page.goto(`/playlist/${BIG_A.uuid}`);
    await expect(rows(page)).toHaveCount(BIG_A.entries);

    const before = calls.length;
    const toB = await timeSwitch(page, BIG_B);
    const back = await timeSwitch(page, BIG_A);
    const again = await timeSwitch(page, BIG_B);
    const after = since(calls, before);

    // Not one Drive call of any kind: the index answers every name.
    expect(after.filter((c) => c.kind !== 'media')).toEqual([]);
    // Generous for a CI box; it is ~100 ms locally. Before the index each
    // switch was 5+ sequential name queries, i.e. over a second at 150 ms.
    for (const ms of [toB, back, again]) expect(ms).toBeLessThan(1_500);

    // Still the real rows: the right number, and resolved where Drive has them.
    await expect(rows(page)).toHaveCount(BIG_B.entries);
    await expect(page.locator('#playlist-table tbody .play-button:not([disabled])').first()).toBeVisible();
  });

  test('a reload reuses the cached collection and file index', async ({ page }) => {
    const calls = await mockDrive(page, largeOptions());
    const written = cacheWritten(page);
    await page.goto(`/playlist/${BIG_A.uuid}`);
    await expect(rows(page)).toHaveCount(BIG_A.entries);
    expect(count(calls, 'collection')).toBe(1);
    expect(count(calls, 'list')).toBeGreaterThan(0);
    await written;

    const before = calls.length;
    await page.reload();
    await expect(rows(page)).toHaveCount(BIG_A.entries);
    await expect(page.locator('#playlist-table tbody .play-button:not([disabled])').first()).toBeVisible();
    const after = since(calls, before);

    expect(count(after, 'collection')).toBe(0); // not downloaded again
    expect(count(after, 'list')).toBe(0); // the index came from IndexedDB
    expect(count(after, 'query')).toBeLessThanOrEqual(1); // only discovery of collection.nml
  });

  test('a new collection.nml in Drive is downloaded, not served from the cache', async ({ page }) => {
    const opts = largeOptions({ collectionModifiedTime: '2026-10-01T10:00:00.000Z' });
    const calls = await mockDrive(page, opts);
    const written = cacheWritten(page);
    await page.goto(`/playlist/${BIG_A.uuid}`);
    await expect(rows(page)).toHaveCount(BIG_A.entries);
    await written;

    // Traktor synced: a new modifiedTime and a playlist renamed in the new file.
    opts.collectionModifiedTime = '2026-10-02T10:00:00.000Z';
    opts.nml = nml.replace(`NAME="${BIG_A.name}"`, 'NAME="Big Set A (edited)"');
    const before = calls.length;
    await page.reload();
    await expect(page.locator('h1.playlist-title')).toHaveText('Big Set A (edited)');
    expect(count(since(calls, before), 'collection')).toBe(1);
  });

  test('a file added to Drive since the index was built is found on the next load', async ({ page }) => {
    // Track 50 is in the collection but not in Drive (every 50th is missing).
    const opts = largeOptions({ changes: [] });
    const calls = await mockDrive(page, opts);
    await page.goto(`/playlist/${BIG_A.uuid}`);
    await expect(rows(page)).toHaveCount(BIG_A.entries);
    const missingBefore = Number(await page.locator('[data-not-playable]').getAttribute('data-not-playable'));
    expect(missingBefore).toBeGreaterThan(0);

    // Upload every missing one; Drive reports them on the change feed.
    for (let i = 50; i <= 1600; i += 50) {
      const file = { id: `lf-new-${i}`, name: `Track ${i} - Artist ${i % 97}.mp3`, mimeType: 'audio/mpeg', parents: [`lf-genre-${i % 12}`] };
      opts.changes!.push({ fileId: file.id, file });
    }
    const before = calls.length;
    await page.reload();
    await expect(rows(page)).toHaveCount(BIG_A.entries);
    await expect(page.locator('[data-not-playable]')).toHaveCount(0);
    expect(count(since(calls, before), 'list')).toBe(0); // incremental, not a rebuild
    expect(count(since(calls, before), 'changes')).toBeGreaterThan(0);
  });
});

/** The table in the PR. Skipped unless PERF_REPORT is set: it takes a minute or two. */
test.describe('performance report', () => {
  test.skip(!process.env.PERF_REPORT, 'set PERF_REPORT=1 to print the timing table');
  test.setTimeout(600_000);

  // The dev server's first request pays for its own startup; not the app's cost.
  test.beforeAll(async ({ browser }) => {
    const page = await browser.newPage();
    await page.goto('/login');
    await page.waitForLoadState('networkidle');
    await page.close();
  });

  for (const scenario of [
    { label: 'laptop, 150 ms Drive', latencyMs: 150, cpu: 1 },
    { label: 'train, 400 ms Drive', latencyMs: 400, cpu: 1 },
    { label: 'tablet CPU (4x), 150 ms Drive', latencyMs: 150, cpu: 4 },
  ]) {
    test(scenario.label, async ({ page }) => {
      const perf: string[] = [];
      page.on('console', (m) => { if (m.text().startsWith('[perf]')) perf.push(m.text()); });
      await seedToken(page);
      const calls = await mockDrive(page, largeOptions({ latencyMs: scenario.latencyMs }));
      const cdp = await page.context().newCDPSession(page);
      if (scenario.cpu > 1) await cdp.send('Emulation.setCPUThrottlingRate', { rate: scenario.cpu });

      const written = cacheWritten(page);
      const firstOpen = await timeOpen(page, BIG_A);
      const firstCalls = calls.length;
      const switches: number[] = [];
      let switchCalls = calls.length;
      for (const to of [BIG_B, BIG_A, BIG_B, BIG_A]) switches.push(await timeSwitch(page, to));
      switchCalls = calls.length - switchCalls;

      // Bounded, so the same report also runs against a build without the cache.
      written.catch(() => {});
      await Promise.race([written, page.waitForTimeout(15_000)]);
      const beforeReload = calls.length;
      const reload = await timeOpen(page, BIG_A);
      const reloadCalls = calls.slice(beforeReload);

      // Main-thread time while a track plays: every second of progress used to
      // re-render the whole table.
      await page.locator('#playlist-table tbody .play-button:not([disabled])').first().click();
      await expect(page.locator('.currently-playing')).toHaveCount(1);
      const m0 = await cdp.send('Performance.enable').then(() => cdp.send('Performance.getMetrics'));
      await page.waitForTimeout(5_000);
      const m1 = await cdp.send('Performance.getMetrics');
      const metric = (m: typeof m0, name: string) => m.metrics.find((x) => x.name === name)?.value ?? 0;
      const busyPerSecond = ((metric(m1, 'TaskDuration') - metric(m0, 'TaskDuration')) / 5) * 1000;

      const kinds = (cs: DriveCall[]) =>
        Object.entries(cs.reduce<Record<string, number>>((a, c) => ((a[c.kind] = (a[c.kind] ?? 0) + 1), a), {}))
          .map(([k, n]) => `${k}=${n}`).join(' ');

      console.log([
        `\n=== ${scenario.label} ===`,
        `first open of a 196-entry playlist: ${firstOpen.toFixed(0)} ms, Drive calls: ${kinds(calls.slice(0, firstCalls))}`,
        `switches (B, A, B, A): ${switches.map((s) => s.toFixed(0)).join(', ')} ms, Drive calls during them: ${switchCalls}`,
        `reload: ${reload.toFixed(0)} ms, Drive calls: ${kinds(reloadCalls)}`,
        `main thread busy while playing: ${busyPerSecond.toFixed(0)} ms per second`,
        ...perf.map((p) => `  ${p}`),
      ].join('\n'));
    });
  }
});
