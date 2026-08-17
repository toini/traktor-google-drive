import { test, expect, type Page } from '@playwright/test';
import { mockDrive, seedToken } from './drive-mock';

const PSYTECH_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a'; // 4 tracks
const OLD_SETS_UUID = '0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5c'; // 1 track

// Two tracks whose PLAYTIME_FLOAT deliberately crosses a minute-digit-width
// boundary (2:05 vs 11:00): a sort that compares the formatted/raw string
// instead of the numeric value would put "11:00" before "2:05".
const LENGTH_UUID = 'abcd1234abcd1234abcd1234abcd1234';
const LENGTH_TEST_NML = `<?xml version="1.0" encoding="UTF-8" standalone="no"?>
<NML VERSION="19">
  <HEAD COMPANY="www.native-instruments.com" PROGRAM="Traktor"></HEAD>
  <MUSICFOLDERS></MUSICFOLDERS>
  <COLLECTION ENTRIES="2">
    <ENTRY MODIFIED_DATE="2024/3/17" TITLE="Short One" ARTIST="Length Test">
      <LOCATION DIR="/:Music/:Sets/:" FILE="short.wav" VOLUME="Macintosh HD"></LOCATION>
      <INFO BITRATE="1411000" PLAYTIME="125" PLAYTIME_FLOAT="125.0"></INFO>
    </ENTRY>
    <ENTRY MODIFIED_DATE="2024/3/17" TITLE="Long One" ARTIST="Length Test">
      <LOCATION DIR="/:Music/:Sets/:" FILE="long.wav" VOLUME="Macintosh HD"></LOCATION>
      <INFO BITRATE="1411000" PLAYTIME="660" PLAYTIME_FLOAT="660.0"></INFO>
    </ENTRY>
  </COLLECTION>
  <PLAYLISTS>
    <NODE TYPE="FOLDER" NAME="$ROOT">
      <SUBNODES COUNT="1">
        <NODE TYPE="PLAYLIST" NAME="Length Check">
          <PLAYLIST ENTRIES="2" TYPE="LIST" UUID="${LENGTH_UUID}">
            <ENTRY><PRIMARYKEY TYPE="TRACK" KEY="Macintosh HD/:Music/:Sets/:short.wav"></PRIMARYKEY></ENTRY>
            <ENTRY><PRIMARYKEY TYPE="TRACK" KEY="Macintosh HD/:Music/:Sets/:long.wav"></PRIMARYKEY></ENTRY>
          </PLAYLIST>
        </NODE>
      </SUBNODES>
    </NODE>
  </PLAYLISTS>
</NML>`;

const titleColumn = (page: Page) => page.locator('#playlist-table tbody tr .cell-title').allTextContents();
// Class selectors, not nth-child: inserting a column shifts every index.
const artistColumn = (page: Page) => page.locator('#playlist-table tbody tr .cell-artist').allTextContents();
const labelColumn = (page: Page) => page.locator('#playlist-table tbody tr .cell-label').allTextContents();

const header = (page: Page, name: string) => page.getByRole('columnheader', { name, exact: false });

test.describe('playlist column sorting', () => {
  test.beforeEach(async ({ page }) => {
    await mockDrive(page);
    await seedToken(page);
  });

  test('defaults to ascending by Title', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'ascending');
    await expect(titleColumn(page)).resolves.toEqual([
      'Alpha Drift',
      'Alpha Drift (Archive Cut)',
      'Beta Pulse',
      'Gamma Echo',
    ]);
  });

  test('cycles ascending -> descending -> Traktor\'s original order -> ascending', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    // Already ascending on load; first click flips to descending.
    await header(page, 'Title').click();
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'descending');
    await expect(titleColumn(page)).resolves.toEqual([
      'Gamma Echo',
      'Beta Pulse',
      'Alpha Drift (Archive Cut)',
      'Alpha Drift',
    ]);

    // Second click: back to the order the user actually mixed in Traktor.
    await header(page, 'Title').click();
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'none');
    await expect(titleColumn(page)).resolves.toEqual([
      'Alpha Drift',
      'Beta Pulse',
      'Gamma Echo',
      'Alpha Drift (Archive Cut)',
    ]);

    // Third click: the cycle restarts at ascending.
    await header(page, 'Title').click();
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'ascending');
    await expect(titleColumn(page)).resolves.toEqual([
      'Alpha Drift',
      'Alpha Drift (Archive Cut)',
      'Beta Pulse',
      'Gamma Echo',
    ]);
  });

  test('sorts Length by the numeric PLAYTIME_FLOAT value', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    await header(page, 'Length').click();
    await expect(header(page, 'Length')).toHaveAttribute('aria-sort', 'ascending');
    // 180.25 / 245.5 / 250.0 / 312.0 seconds.
    await expect(titleColumn(page)).resolves.toEqual([
      'Gamma Echo',
      'Alpha Drift',
      'Alpha Drift (Archive Cut)',
      'Beta Pulse',
    ]);

    await header(page, 'Length').click();
    await expect(header(page, 'Length')).toHaveAttribute('aria-sort', 'descending');
    await expect(titleColumn(page)).resolves.toEqual([
      'Beta Pulse',
      'Alpha Drift (Archive Cut)',
      'Alpha Drift',
      'Gamma Echo',
    ]);
  });

  test('does not fall back to a lexical string sort for Length', async ({ page }) => {
    // 125s formats as "2:05" and 660s as "11:00" — a string/lexical compare
    // ("11:00" < "2:05") would wrongly put the 11-minute track first.
    await mockDrive(page, { nml: LENGTH_TEST_NML });
    await seedToken(page);
    await page.goto(`/playlist/${LENGTH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(3);

    await header(page, 'Length').click();
    await expect(titleColumn(page)).resolves.toEqual(['Short One', 'Long One']);

    await header(page, 'Length').click();
    await expect(titleColumn(page)).resolves.toEqual(['Long One', 'Short One']);
  });

  test('keeps blank Label values last regardless of direction', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    await header(page, 'Label').click();
    let labels = await labelColumn(page);
    expect(labels.slice(0, 2).every((l) => l !== '')).toBe(true);
    expect(labels.slice(2)).toEqual(['', '']);

    await header(page, 'Label').click();
    labels = await labelColumn(page);
    expect(labels.slice(0, 2).every((l) => l !== '')).toBe(true);
    expect(labels.slice(2)).toEqual(['', '']);
  });

  test('remembers the chosen sort across navigating away and back', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    await header(page, 'Artist').click();
    await expect(header(page, 'Artist')).toHaveAttribute('aria-sort', 'ascending');
    await expect(artistColumn(page)).resolves.toEqual([
      'Test Artist One',
      'Test Artist One',
      'Test Artist Three',
      'Test Artist Two',
    ]);

    await page.goto('/music');
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);

    await expect(header(page, 'Artist')).toHaveAttribute('aria-sort', 'ascending');
    await expect(artistColumn(page)).resolves.toEqual([
      'Test Artist One',
      'Test Artist One',
      'Test Artist Three',
      'Test Artist Two',
    ]);
  });

  test('remembers a different sort per playlist', async ({ page }) => {
    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);
    await header(page, 'Title').click(); // ascending -> descending
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'descending');

    await page.goto(`/playlist/${OLD_SETS_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(2);
    // A playlist visited for the first time still gets the plain default.
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'ascending');
    await header(page, 'Artist').click();
    await expect(header(page, 'Artist')).toHaveAttribute('aria-sort', 'ascending');

    await page.goto(`/playlist/${PSYTECH_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(5);
    await expect(header(page, 'Title')).toHaveAttribute('aria-sort', 'descending');
    await expect(header(page, 'Artist')).toHaveAttribute('aria-sort', 'none');

    await page.goto(`/playlist/${OLD_SETS_UUID}`);
    await expect(page.getByRole('row')).toHaveCount(2);
    await expect(header(page, 'Artist')).toHaveAttribute('aria-sort', 'ascending');
  });
});
