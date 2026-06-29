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
import { describe, it, expect, beforeAll, afterAll, afterEach } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';

// createOAuthRouter() throws at import unless the Entra client id is present.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
const { createOAuthRouter, _registryForTest, MAX_REGISTERED_CLIENTS, MAX_REDIRECT_URIS_PER_CLIENT, MAX_REDIRECT_URI_LENGTH, MAX_CLIENT_NAME_LENGTH } =
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

afterEach(() => {
  // Reset the registry between tests (the cap test fills it to capacity).
  _registryForTest.clear();
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

  it('returns 503 once the registry is at its hard cap', async () => {
    _registryForTest.fill(MAX_REGISTERED_CLIENTS);
    expect(_registryForTest.size()).toBeGreaterThanOrEqual(MAX_REGISTERED_CLIENTS);
    const { status, json } = await register({
      client_name: 'overflow',
      redirect_uris: ['http://localhost:1/cb'],
    });
    expect(status).toBe(503);
    expect(json.error).toBe('temporarily_unavailable');
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

  it('redirects a valid S256 request to the Entra authorize endpoint', async () => {
    const res = await authorize({
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
