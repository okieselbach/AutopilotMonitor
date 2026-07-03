/**
 * Endpoint tests for the OAuth proxy router (review finding tests-5).
 *
 * The file header of security-guards.test.ts claims /oauth/register + PKCE-S256
 * enforcement are covered, but those tests only assert constant magnitudes (F2)
 * and signState/verifyState (F4) — never the route handlers themselves. This
 * suite closes that gap by mounting createOAuthRouter() on an ephemeral Express
 * server and exercising the actual request paths.
 *
 * No external network is reached: every asserted path returns before the proxy
 * would call Entra ID (the /oauth/authorize success case yields a 302 we read
 * with redirect:'manual'; the token error case returns before fetch).
 */
import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';

// createOAuthRouter() throws at import unless the Entra client id is present.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
// The /oauth/* routes now carry per-source-IP throttles (read at module init).
// This suite fires far more than the production budgets from one loopback IP
// within a minute, so pin them out of the way — the limiter itself is covered
// by oauth-rate-limit.test.ts with tiny pinned budgets in its own module.
process.env.MCP_OAUTH_RATE_LIMIT_PER_MINUTE = '100000';
process.env.MCP_OAUTH_TOKEN_RATE_LIMIT_PER_MINUTE = '100000';
const { createOAuthRouter, signClientId, verifyClientId, signState, redirectUriMatches, MAX_REDIRECT_URIS_PER_CLIENT, MAX_REDIRECT_URI_LENGTH, MAX_CLIENT_NAME_LENGTH } =
  await import('../oauth.js');

let server: Server;
let baseUrl: string;

beforeAll(async () => {
  const app = express();
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(createOAuthRouter());
  await new Promise<void>((resolve) => {
    server = app.listen(0, '127.0.0.1', () => resolve());
  });
  const { port } = server.address() as AddressInfo;
  baseUrl = `http://127.0.0.1:${port}`;
});

afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

async function register(body: unknown) {
  const res = await fetch(`${baseUrl}/oauth/register`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  const json = await res.json().catch(() => ({}));
  return { status: res.status, json: json as Record<string, unknown> };
}

describe('/.well-known/oauth-protected-resource — RFC 9728 resource identifier', () => {
  it('serves the path-specific location with resource = <base>/mcp', async () => {
    const res = await fetch(`${baseUrl}/.well-known/oauth-protected-resource/mcp`);
    expect(res.status).toBe(200);
    const json = (await res.json()) as Record<string, unknown>;
    // resource MUST equal the MCP endpoint URL the client connects to, or strict
    // clients (VS Code) reject the metadata and never start the auth flow.
    expect(json.resource).toBe(`${baseUrl}/mcp`);
    expect(json.authorization_servers).toEqual([baseUrl]);
  });

  it('serves the bare location with the same resource value (lenient-client fallback)', async () => {
    const res = await fetch(`${baseUrl}/.well-known/oauth-protected-resource`);
    expect(res.status).toBe(200);
    expect(((await res.json()) as Record<string, unknown>).resource).toBe(`${baseUrl}/mcp`);
  });
});

describe('/oauth/register — dynamic client registration (RFC 7591)', () => {
  it('registers a client with a loopback redirect_uri and returns a client_id', async () => {
    const { status, json } = await register({
      client_name: 'Claude Code',
      redirect_uris: ['http://localhost:54321/callback'],
    });
    expect(status).toBe(201);
    expect(typeof json.client_id).toBe('string');
    expect(json.redirect_uris).toEqual(['http://localhost:54321/callback']);
    // RFC 7591 default for a public client.
    expect(json.token_endpoint_auth_method).toBe('none');
  });

  it('registers a VS Code client (loopback + vscode.dev redirect) — GitHub Copilot DCR', async () => {
    const { status, json } = await register({
      client_name: 'Visual Studio Code',
      redirect_uris: ['http://127.0.0.1:33418', 'https://vscode.dev/redirect'],
    });
    expect(status).toBe(201);
    expect(json.redirect_uris).toEqual(['http://127.0.0.1:33418', 'https://vscode.dev/redirect']);
  });

  it('registers a VS Code Insiders client (insiders.vscode.dev redirect)', async () => {
    const { status } = await register({
      client_name: 'Visual Studio Code - Insiders',
      redirect_uris: ['http://127.0.0.1:33418', 'https://insiders.vscode.dev/redirect'],
    });
    expect(status).toBe(201);
  });

  it('rejects a hostile redirect_uri host (allowlist defense-in-depth)', async () => {
    const { status, json } = await register({
      client_name: 'evil',
      redirect_uris: ['https://attacker.tld/cb'],
    });
    expect(status).toBe(400);
    expect(json.error).toBe('invalid_redirect_uri');
  });

  it('rejects more than MAX_REDIRECT_URIS_PER_CLIENT redirect_uris', async () => {
    const uris = Array.from({ length: MAX_REDIRECT_URIS_PER_CLIENT + 1 }, (_, i) => `http://localhost:${1000 + i}/cb`);
    const { status, json } = await register({ client_name: 'greedy', redirect_uris: uris });
    expect(status).toBe(400);
    expect(json.error).toBe('invalid_redirect_uri');
  });

  it('rejects an oversized redirect_uri', async () => {
    const huge = `http://localhost:1/${'a'.repeat(MAX_REDIRECT_URI_LENGTH + 1)}`;
    const { status, json } = await register({ client_name: 'x', redirect_uris: [huge] });
    expect(status).toBe(400);
    expect(json.error).toBe('invalid_redirect_uri');
  });

  it('rejects an over-long client_name', async () => {
    const { status, json } = await register({
      client_name: 'n'.repeat(MAX_CLIENT_NAME_LENGTH + 1),
      redirect_uris: ['http://localhost:1/cb'],
    });
    expect(status).toBe(400);
    expect(json.error).toBe('invalid_client_metadata');
  });

  it('returns a stateless signed client_id that verifies back to its redirect_uris', async () => {
    const uris = ['http://127.0.0.1:33418/', 'https://vscode.dev/redirect'];
    const { json } = await register({ client_name: 'Visual Studio Code', redirect_uris: uris });
    const clientId = json.client_id as string;
    // Format is `base64url(payload).hmac` — not an opaque UUID.
    expect(clientId).toContain('.');
    const verified = verifyClientId(clientId);
    expect(verified).not.toBeNull();
    expect(verified?.redirectUris).toEqual(uris);
    expect(verified?.typ).toBe('client');
  });
});

describe('signClientId / verifyClientId — stateless client registry', () => {
  it('round-trips redirect_uris through a signed token', () => {
    const token = signClientId(['http://127.0.0.1:1/cb'], 'x');
    expect(verifyClientId(token)?.redirectUris).toEqual(['http://127.0.0.1:1/cb']);
  });

  it('rejects a tampered payload (signature no longer matches)', () => {
    const token = signClientId(['http://127.0.0.1:1/cb'], 'x');
    const [, sig] = token.split('.');
    const forgedBody = Buffer.from(
      JSON.stringify({ typ: 'client', redirectUris: ['http://127.0.0.1:6666/'], name: 'evil', iat: 0 }),
      'utf-8',
    ).toString('base64url');
    expect(verifyClientId(`${forgedBody}.${sig}`)).toBeNull();
  });

  it('rejects a random / opaque client_id (no valid signature)', () => {
    expect(verifyClientId('00000000-dead-beef-0000-000000000000')).toBeNull();
    expect(verifyClientId('not.a.token')).toBeNull();
    expect(verifyClientId('')).toBeNull();
  });
});

describe('redirectUriMatches — RFC 8252 redirect binding', () => {
  it('matches across a trailing-slash difference', () => {
    expect(redirectUriMatches(['http://127.0.0.1:33418'], 'http://127.0.0.1:33418/')).toBe(true);
    expect(redirectUriMatches(['http://127.0.0.1:33418/'], 'http://127.0.0.1:33418')).toBe(true);
  });

  it('matches any loopback port (localhost and 127.0.0.1)', () => {
    expect(redirectUriMatches(['http://127.0.0.1:33418/'], 'http://127.0.0.1:51999/')).toBe(true);
    expect(redirectUriMatches(['http://localhost:1/cb'], 'http://localhost:65000/cb')).toBe(true);
  });

  it('still requires the path to match for loopback', () => {
    expect(redirectUriMatches(['http://127.0.0.1:1/cb'], 'http://127.0.0.1:2/other')).toBe(false);
  });

  it('does NOT ignore the port for non-loopback hosts', () => {
    expect(redirectUriMatches(['https://vscode.dev/redirect'], 'https://vscode.dev/redirect')).toBe(true);
    expect(redirectUriMatches(['https://vscode.dev:443/redirect'], 'https://vscode.dev:8443/redirect')).toBe(false);
  });

  it('returns false for an unparseable requested uri', () => {
    expect(redirectUriMatches(['http://127.0.0.1:1/cb'], 'not a url')).toBe(false);
  });
});

describe('/oauth/authorize — PKCE S256 enforcement', () => {
  async function authorize(query: Record<string, string>) {
    const qs = new URLSearchParams(query).toString();
    return fetch(`${baseUrl}/oauth/authorize?${qs}`, { redirect: 'manual' });
  }

  it('rejects a request with no code_challenge (PKCE mandatory)', async () => {
    const res = await authorize({ redirect_uri: 'http://localhost:1/cb' });
    expect(res.status).toBe(400);
    const json = (await res.json()) as Record<string, unknown>;
    expect(json.error).toBe('invalid_request');
    expect(String(json.error_description)).toMatch(/code_challenge is required/);
  });

  it('rejects a downgrade to code_challenge_method=plain', async () => {
    const res = await authorize({
      redirect_uri: 'http://localhost:1/cb',
      code_challenge: 'abc123',
      code_challenge_method: 'plain',
    });
    expect(res.status).toBe(400);
    const json = (await res.json()) as Record<string, unknown>;
    expect(String(json.error_description)).toMatch(/must be S256/);
  });

  it('rejects a missing redirect_uri', async () => {
    const res = await authorize({ code_challenge: 'abc123' });
    expect(res.status).toBe(400);
  });

  it('rejects a forged / unsigned client_id with invalid_client (fail closed)', async () => {
    // A signed client_id is verifiable on any replica, so an unverifiable one
    // is genuinely bogus — fail closed rather than fall back to a weaker gate.
    const res = await authorize({
      client_id: '00000000-dead-beef-0000-000000000000',
      redirect_uri: 'http://127.0.0.1:33418/',
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
    });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_client');
  });

  it('rejects a valid client_id whose registered list omits the redirect_uri (path differs)', async () => {
    // Port is loopback-agnostic, but the PATH must still match — a different
    // path is a genuine mismatch and must be refused.
    const clientId = signClientId(['http://127.0.0.1:33418/cb'], 'vscode');
    const res = await authorize({
      client_id: clientId,
      redirect_uri: 'http://127.0.0.1:9999/evil',
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
    });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_request');
  });

  it('redirects a valid S256 request (signed client_id, matching redirect_uri) to Entra', async () => {
    const clientId = signClientId(['http://localhost:54321/cb'], 'cc');
    const res = await authorize({
      client_id: clientId,
      redirect_uri: 'http://localhost:54321/cb',
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
      state: 'client-state',
    });
    expect(res.status).toBe(302);
    const location = res.headers.get('location') ?? '';
    expect(location).toContain('login.microsoftonline.com');
    expect(location).toContain('code_challenge_method=S256');
    // The client's challenge is forwarded unchanged.
    expect(location).toContain('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
    // Force the account picker so a wrong default-browser SSO session can't be
    // silently reused.
    expect(location).toContain('prompt=select_account');
  });

  it('accepts a trailing-slash variant of the registered loopback redirect (VS Code repro)', async () => {
    // VS Code registers the loopback URI without a trailing slash but authorizes
    // with the normalized form — must match, or the flow stalls at "Waiting for
    // initialize". (Reproduced live against the deployed server.)
    const clientId = signClientId(['http://127.0.0.1:33418', 'https://vscode.dev/redirect'], 'Visual Studio Code');
    const res = await authorize({
      client_id: clientId,
      redirect_uri: 'http://127.0.0.1:33418/',
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
    });
    expect(res.status).toBe(302);
    expect(res.headers.get('location') ?? '').toContain('login.microsoftonline.com');
  });

  it('accepts a different ephemeral loopback port (RFC 8252 §7.3)', async () => {
    const clientId = signClientId(['http://127.0.0.1:33418/'], 'cli');
    const res = await authorize({
      client_id: clientId,
      redirect_uri: 'http://127.0.0.1:51999/', // fresh port, not the registered one
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
    });
    expect(res.status).toBe(302);
  });

  it('rejects a request with no client_id (client_id is mandatory)', async () => {
    const res = await authorize({
      redirect_uri: 'http://127.0.0.1:33418/',
      code_challenge: 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      code_challenge_method: 'S256',
    });
    expect(res.status).toBe(400);
    const json = (await res.json()) as Record<string, unknown>;
    expect(json.error).toBe('invalid_request');
    expect(String(json.error_description)).toMatch(/client_id is required/);
  });
});

describe('/oauth/callback — signed state + client enforcement', () => {
  const REDIRECT = 'http://127.0.0.1:33418/';
  async function callback(query: Record<string, string>) {
    const qs = new URLSearchParams(query).toString();
    return fetch(`${baseUrl}/oauth/callback?${qs}`, { redirect: 'manual' });
  }

  it('forwards the code to the registered redirect_uri for a valid signed state + client', async () => {
    const clientId = signClientId([REDIRECT], 'vscode');
    const state = signState({ originalState: 'client-state', redirectUri: REDIRECT, clientId });
    const res = await callback({ code: 'ENTRA_CODE', state });
    expect(res.status).toBe(302);
    const location = res.headers.get('location') ?? '';
    expect(location.startsWith(`${REDIRECT}?`)).toBe(true);
    expect(location).toContain('code=ENTRA_CODE');
    expect(location).toContain('state=client-state');
  });

  it('rejects a forged client_id carried in the state', async () => {
    const state = signState({ redirectUri: REDIRECT, clientId: 'forged.token' });
    const res = await callback({ code: 'ENTRA_CODE', state });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_request');
  });

  it('rejects when the state redirect_uri is not registered to the signed client (path differs)', async () => {
    const clientId = signClientId(['http://127.0.0.1:1/cb'], 'vscode');
    const state = signState({ redirectUri: 'http://127.0.0.1:2/other', clientId });
    const res = await callback({ code: 'ENTRA_CODE', state });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_request');
  });

  it('rejects a tampered state (HMAC mismatch)', async () => {
    const clientId = signClientId([REDIRECT], 'vscode');
    const state = signState({ redirectUri: REDIRECT, clientId });
    const res = await callback({ code: 'ENTRA_CODE', state: `x${state}` });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_state');
  });

  it('rejects a missing state', async () => {
    const res = await callback({ code: 'ENTRA_CODE' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_state');
  });

  it('rejects an expired state (replay after the 10-min window)', async () => {
    // Fake only Date so the express server / fetch keep using real timers.
    vi.useFakeTimers({ toFake: ['Date'] });
    try {
      vi.setSystemTime(new Date('2020-01-01T00:00:00Z'));
      const clientId = signClientId([REDIRECT], 'vscode');
      const state = signState({ redirectUri: REDIRECT, clientId });
      vi.setSystemTime(new Date('2020-01-01T00:11:00Z')); // +11 min > 10-min max age
      const res = await callback({ code: 'ENTRA_CODE', state });
      expect(res.status).toBe(400);
      expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_state');
    } finally {
      vi.useRealTimers();
    }
  });

  it('rejects a callback with neither error nor code', async () => {
    const clientId = signClientId([REDIRECT], 'vscode');
    const state = signState({ redirectUri: REDIRECT, clientId });
    const res = await callback({ state });
    expect(res.status).toBe(400);
    const json = (await res.json()) as Record<string, unknown>;
    expect(json.error).toBe('invalid_request');
    expect(String(json.error_description)).toMatch(/Missing authorization code/);
  });
});

describe('/oauth/token — PKCE finishing move', () => {
  it('rejects an authorization_code grant with no code_verifier', async () => {
    const res = await fetch(`${baseUrl}/oauth/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ grant_type: 'authorization_code', code: 'abc' }).toString(),
    });
    expect(res.status).toBe(400);
    const json = (await res.json()) as Record<string, unknown>;
    expect(String(json.error_description)).toMatch(/code_verifier is required/);
  });
});

describe('/oauth/token — grant_type allowlist', () => {
  // Every request to this endpoint is forwarded to Entra with the app's
  // client_id + client_secret attached. Unlisted grants (client_credentials,
  // ROPC, device-code) previously failed only by accident of the pinned scope
  // and which fields the proxy copies — these tests pin the allowlist as
  // explicit proxy policy. All rejection paths return before any fetch, so no
  // network is reached.
  async function token(params: Record<string, string>) {
    const res = await fetch(`${baseUrl}/oauth/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams(params).toString(),
    });
    const json = (await res.json().catch(() => ({}))) as Record<string, unknown>;
    return { status: res.status, json };
  }

  it('rejects a missing grant_type as invalid_request', async () => {
    const res = await token({ code: 'abc' });
    expect(res.status).toBe(400);
    expect(res.json.error).toBe('invalid_request');
    expect(String(res.json.error_description)).toMatch(/grant_type is required/);
  });

  it.each(['client_credentials', 'password', 'urn:ietf:params:oauth:grant-type:device_code'])(
    'rejects grant_type=%s as unsupported_grant_type',
    async (grantType) => {
      const res = await token({ grant_type: grantType });
      expect(res.status).toBe(400);
      expect(res.json.error).toBe('unsupported_grant_type');
    },
  );

  it('lets an authorization_code grant pass the allowlist (fails later on PKCE, not on grant_type)', async () => {
    const res = await token({ grant_type: 'authorization_code', code: 'abc' });
    expect(res.status).toBe(400);
    // Reached the PKCE gate — i.e. the allowlist did not reject the listed grant.
    expect(String(res.json.error_description)).toMatch(/code_verifier is required/);
  });
});
