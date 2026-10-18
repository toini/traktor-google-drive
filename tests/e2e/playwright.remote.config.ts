import { defineConfig, devices } from '@playwright/test';

// Remote control (ADR 20261018-63) needs the hub, which lives in the ASP.NET
// host, so these specs run against TraktorGoogleDrive.Server rather than the
// client dev server the rest of the suite uses. Drive is still mocked per page.
//
// The server cannot reach Google from a test, so RemoteAuth:FakeUsers maps the
// fixture tokens to users. The server refuses to start with it outside
// Development.
export const PORT = Number(process.env.REMOTE_E2E_PORT ?? 5361);

export default defineConfig({
  testDir: '.',
  testMatch: /remote.*\.spec\.ts/,
  timeout: 120_000,
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
      'dotnet run --project ../../TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj '
      + `--no-launch-profile --urls http://localhost:${PORT}`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: false,
    timeout: 300_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      // The server targets net9.0; let it run on a newer installed runtime
      // (the client dev server already does, via its own rollForward).
      DOTNET_ROLL_FORWARD: process.env.DOTNET_ROLL_FORWARD ?? 'Major',
      RemoteAuth__FakeUsers: 'fixture-access-token=toni;other-user-token=mallory',
      GITHUB_USERNAME: process.env.GITHUB_USERNAME ?? 'toini',
      GITHUB_TOKEN: process.env.GITHUB_TOKEN ?? '',
    },
  },
});
