import { defineConfig, devices } from '@playwright/test';

const PORT = 5048;

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
      'dotnet run --project ../../TraktorGoogleDrive/TraktorGoogleDrive.csproj --launch-profile http',
    url: `http://localhost:${PORT}`,
    reuseExistingServer: true,
    timeout: 180_000,
    env: {
      GITHUB_USERNAME: process.env.GITHUB_USERNAME ?? 'toini',
      GITHUB_TOKEN: process.env.GITHUB_TOKEN ?? '',
    },
  },
});
