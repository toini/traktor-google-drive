// A stand-in for Google's OAuth token and revoke endpoints, for the code-flow
// e2e suite (playwright.auth.config.ts). The ASP.NET host is pointed here with
// GoogleOAuth__TokenEndpoint / GoogleOAuth__RevokeEndpoint, a seam it refuses
// outside Development. The specs read and steer it through /__control/*.
//
// Codes are `code-<user>`. Access tokens alternate between the two the host's
// RemoteAuth__FakeUsers maps to "toni", so a refresh visibly changes the token.
import { createServer } from 'node:http';

const PORT = Number(process.env.FAKE_GOOGLE_PORT ?? 5382);
const CLIENT_SECRET = 'fake-client-secret';
const TOKENS = ['fixture-access-token', 'renewed-access-token'];

let state;
const reset = () => {
  // Grant numbers never restart: the host caches access tokens per refresh
  // token, so a reused refresh token would get the previous test's entry.
  const grants = state?.grants ?? 0;
  state = { issued: 0, grants, revokedUpTo: grants, expiresIn: 3600, revoked: new Set(), calls: [] };
};
reset();

const json = (res, status, body) => {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
};

const readBody = (req) =>
  new Promise((resolve) => {
    let data = '';
    req.on('data', (c) => { data += c; });
    req.on('end', () => resolve(data));
  });

const nextToken = () => TOKENS[state.issued++ % TOKENS.length];

createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  const body = await readBody(req);

  if (url.pathname === '/__control/state') {
    return json(res, 200, {
      codeExchanges: state.calls.filter((c) => c.grant === 'authorization_code').length,
      refreshes: state.calls.filter((c) => c.grant === 'refresh_token').length,
      revokes: state.calls.filter((c) => c.path === '/revoke').length,
      revoked: [...state.revoked],
    });
  }
  if (url.pathname === '/__control/reset') { reset(); return json(res, 200, {}); }
  // Every grant so far is revoked (as at myaccount.google.com); a new sign-in works.
  if (url.pathname === '/__control/revoke-all') { state.revokedUpTo = state.grants; return json(res, 200, {}); }
  if (url.pathname === '/__control/expires-in') {
    state.expiresIn = Number(url.searchParams.get('s') ?? 3600);
    return json(res, 200, {});
  }

  const form = Object.fromEntries(new URLSearchParams(body));

  if (url.pathname === '/revoke' && req.method === 'POST') {
    state.calls.push({ path: '/revoke' });
    state.revoked.add(form.token);
    return json(res, 200, {});
  }

  if (url.pathname === '/token' && req.method === 'POST') {
    state.calls.push({ path: '/token', grant: form.grant_type });
    if (form.client_secret !== CLIENT_SECRET) return json(res, 401, { error: 'invalid_client' });

    if (form.grant_type === 'authorization_code') {
      if (form.redirect_uri !== 'postmessage') return json(res, 400, { error: 'redirect_uri_mismatch' });
      const user = /^code-(\w+)$/.exec(form.code ?? '')?.[1];
      if (!user) return json(res, 400, { error: 'invalid_grant' });
      const refresh = `rt-${user}-${++state.grants}`;
      return json(res, 200, {
        access_token: user === 'toni' ? TOKENS[0] : 'other-user-token',
        expires_in: state.expiresIn,
        refresh_token: refresh,
        token_type: 'Bearer',
      });
    }

    if (form.grant_type === 'refresh_token') {
      const grant = Number(/-(\d+)$/.exec(form.refresh_token ?? '')?.[1] ?? 0);
      if (!grant || grant <= state.revokedUpTo || state.revoked.has(form.refresh_token)) {
        return json(res, 400, { error: 'invalid_grant', error_description: 'Token has been expired or revoked.' });
      }
      return json(res, 200, { access_token: nextToken(), expires_in: state.expiresIn, token_type: 'Bearer' });
    }
    return json(res, 400, { error: 'unsupported_grant_type' });
  }

  json(res, 404, { error: 'not_found' });
}).listen(PORT, () => console.log(`fake Google on ${PORT}`));
