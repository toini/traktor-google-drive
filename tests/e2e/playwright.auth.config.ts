import { defineConfig, devices } from '@playwright/test';

// The code flow with a server-held refresh token (ADR 20261101-38). It needs
// /api/auth/*, which lives in the ASP.NET host, switched on: a client secret and
// a cookie secret (both fakes), and Google's token endpoint replaced by
// fake-google.mjs. The host refuses that endpoint seam outside Development.
//
// Every other suite runs with the code flow off, so it also covers the fallback.
export const PORT = Number(process.env.AUTH_E2E_PORT ?? 5381);
export const FAKE_GOOGLE_PORT = PORT + 1;

export default defineConfig({
  testDir: '.',
  testMatch: /auth-session\.spec\.ts/,
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
  webServer: [
    {
      command: 'node fake-google.mjs',
      url: `http://localhost:${FAKE_GOOGLE_PORT}/__control/state`,
      reuseExistingServer: false,
      env: { FAKE_GOOGLE_PORT: String(FAKE_GOOGLE_PORT) },
    },
    {
      command:
        'dotnet run --project ../../TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj '
        + `--no-launch-profile --urls http://localhost:${PORT}`,
      url: `http://localhost:${PORT}`,
      reuseExistingServer: false,
      timeout: 300_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        DOTNET_ROLL_FORWARD: process.env.DOTNET_ROLL_FORWARD ?? 'Major',
        GoogleOAuth__ClientSecret: 'fake-client-secret',
        GoogleOAuth__TokenEndpoint: `http://localhost:${FAKE_GOOGLE_PORT}/token`,
        GoogleOAuth__RevokeEndpoint: `http://localhost:${FAKE_GOOGLE_PORT}/revoke`,
        AuthSession__CookieSecret: 'e2e-cookie-secret-that-is-at-least-32-characters',
        RemoteAuth__FakeUsers: 'fixture-access-token=toni;renewed-access-token=toni;other-user-token=mallory',
        RemoteAuth__AllowedUsers: 'toni',
        GITHUB_USERNAME: process.env.GITHUB_USERNAME ?? 'toini',
        GITHUB_TOKEN: process.env.GITHUB_TOKEN ?? '',
      },
    },
  ],
});
