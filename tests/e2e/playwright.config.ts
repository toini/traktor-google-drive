import { defineConfig, devices } from '@playwright/test';

// A port of its own. 5048 is the launchSettings default, so a hand-started dev
// server or a second agent running this same suite would fight over it — which
// produced servers dying mid-run and a different set of failures every time.
const PORT = 5049;

export default defineConfig({
  testDir: '.',
  // Blazor WASM boot is ~3-8s cold; the default 5s expect timeout is too tight.
  timeout: 90_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  workers: 1,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command:
      'dotnet run --project ../../TraktorGoogleDrive/TraktorGoogleDrive.csproj '
      + `--urls http://localhost:${PORT}`,
    url: `http://localhost:${PORT}`,
    // Playwright owns the whole lifecycle: reusing a server it did not start
    // made runs depend on who started it and when.
    reuseExistingServer: false,
    timeout: 240_000,
    env: {
      GITHUB_USERNAME: process.env.GITHUB_USERNAME ?? 'toini',
      GITHUB_TOKEN: process.env.GITHUB_TOKEN ?? '',
    },
  },
});
