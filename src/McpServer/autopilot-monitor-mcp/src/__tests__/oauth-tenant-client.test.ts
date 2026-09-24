/**
 * Tenant-bound clients (portal registrations): a self-hosted client presents client_id
 * `amc_<registrationId>`. The registration is its allowlist entry, the flow runs at the registering
 * tenant's Entra authority, and the token endpoint discards a token for any other tenant. The backend
 * lookup and Entra are stubbed; the router runs for real on an ephemeral server.
 */
import { describe, it, expect, beforeAll, afterAll, afterEach, vi } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';

process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
process.env.MCP_OAUTH_RATE_LIMIT_PER_MINUTE = '100000';
process.env.MCP_OAUTH_TOKEN_RATE_LIMIT_PER_MINUTE = '100000';
const { createOAuthRouter, signState, clearTenantClientCache, tenantClientRegistrationId } = await import('../oauth.js');
const { API_BASE_URL } = await import('../config.js');

const REG_ID = '0123456789abcdef0123456789abcdef';
const CLIENT_ID = `amc_${REG_ID}`;
const TENANT = '11111111-1111-1111-1111-111111111111';
const OTHER_TENANT = '22222222-2222-2222-2222-222222222222';
const CALLBACK = 'https://chat.contoso.example/api/mcp/autopilot-monitor/oauth/callback';

let server: Server;
let baseUrl: string;
const realFetch = globalThis.fetch;

beforeAll(async () => {
  const app = express();
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(createOAuthRouter());
  await new Promise<void>((resolve) => { server = app.listen(0, '127.0.0.1', () => resolve()); });
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

afterEach(() => {
  vi.restoreAllMocks();
  clearTenantClientCache();
});

function jwt(claims: Record<string, unknown>): string {
  const enc = (o: unknown) => Buffer.from(JSON.stringify(o)).toString('base64url');
  return `${enc({ alg: 'RS256', typ: 'JWT' })}.${enc(claims)}.sig`;
}

type Outbound = { url: string; body?: string };

/**
 * Stubs the two outbound calls the proxy makes — the backend registration lookup and the Entra token
 * endpoint — and lets requests to the local test server through.
 */
function stubOutbound(opts: { lookup: 'found' | 'missing' | 'error'; tokenTid?: string }) {
  const outbound: Outbound[] = [];
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input: string | URL | Request, init?: RequestInit) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    if (url.startsWith(baseUrl)) return realFetch(input, init);
    outbound.push({ url, body: typeof init?.body === 'string' ? init.body : undefined });
    if (url.startsWith(`${API_BASE_URL}/api/auth/mcp/client-registrations/`)) {
      if (opts.lookup === 'error') return new Response('boom', { status: 500 });
      if (opts.lookup === 'missing') return new Response(JSON.stringify({ error: 'not found', code: 'NotFound' }), { status: 404 });
      return new Response(JSON.stringify({ registrationId: REG_ID, tenantId: TENANT, redirectUri: CALLBACK, name: 'Team chat' }), { status: 200 });
    }
    if (url.includes('/oauth2/v2.0/token')) {
      return new Response(JSON.stringify({
        token_type: 'Bearer', expires_in: 3600, refresh_token: 'rt',
        access_token: jwt({ tid: opts.tokenTid ?? TENANT, aud: 'api://x', upn: 'alice@contoso.example' }),
      }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }
    throw new Error(`unexpected outbound call ${url}`);
  });
  return outbound;
}

function authorizeUrl(clientId: string, redirectUri: string): string {
  const q = new URLSearchParams({
    response_type: 'code', client_id: clientId, redirect_uri: redirectUri, state: 's1',
    code_challenge: 'x'.repeat(43), code_challenge_method: 'S256',
  });
  return `${baseUrl}/oauth/authorize?${q}`;
}

async function token(form: Record<string, string>) {
  const res = await fetch(`${baseUrl}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(form).toString(),
  });
  return { status: res.status, json: (await res.json()) as Record<string, unknown> };
}

describe('tenant client ids', () => {
  it('recognises only amc_ plus 32 lowercase hex', () => {
    expect(tenantClientRegistrationId(CLIENT_ID)).toBe(REG_ID);
    expect(tenantClientRegistrationId(`amc_${REG_ID.toUpperCase()}`)).toBeNull();
    expect(tenantClientRegistrationId(`amc_${REG_ID}0`)).toBeNull();
    expect(tenantClientRegistrationId('eyJhbGciOi.sig')).toBeNull();
    expect(tenantClientRegistrationId(undefined)).toBeNull();
  });
});

describe('/oauth/authorize with a tenant client', () => {
  it('admits the registered callback although its host is on no allowlist, at the tenant authority', async () => {
    const outbound = stubOutbound({ lookup: 'found' });
    const res = await fetch(authorizeUrl(CLIENT_ID, CALLBACK), { redirect: 'manual' });

    expect(res.status).toBe(302);
    const location = new URL(res.headers.get('location')!);
    expect(location.pathname).toBe(`/${TENANT}/oauth2/v2.0/authorize`);
    expect(location.pathname).not.toContain('organizations');
    expect(outbound.filter((o) => o.url.includes('/client-registrations/'))).toHaveLength(1);
  });

  it('refuses any other callback for the same client', async () => {
    stubOutbound({ lookup: 'found' });
    const res = await fetch(authorizeUrl(CLIENT_ID, 'https://chat.contoso.example/elsewhere'), { redirect: 'manual' });
    expect(res.status).toBe(400);
  });

  it('refuses an unknown or disabled registration', async () => {
    stubOutbound({ lookup: 'missing' });
    const res = await fetch(authorizeUrl(CLIENT_ID, CALLBACK), { redirect: 'manual' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as Record<string, unknown>).error).toBe('invalid_client');
  });

  it('fails closed with 503 when the registration cannot be checked', async () => {
    stubOutbound({ lookup: 'error' });
    const res = await fetch(authorizeUrl(CLIENT_ID, CALLBACK), { redirect: 'manual' });
    expect(res.status).toBe(503);
  });

  it('caches the lookup, so a flow costs one backend call', async () => {
    const outbound = stubOutbound({ lookup: 'found' });
    await fetch(authorizeUrl(CLIENT_ID, CALLBACK), { redirect: 'manual' });
    await fetch(authorizeUrl(CLIENT_ID, CALLBACK), { redirect: 'manual' });
    expect(outbound.filter((o) => o.url.includes('/client-registrations/'))).toHaveLength(1);
  });

  it('keeps the vendor allowlist for dynamically registered clients', async () => {
    stubOutbound({ lookup: 'found' });
    const reg = await fetch(`${baseUrl}/oauth/register`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ client_name: 'x', redirect_uris: [CALLBACK] }),
    });
    expect(reg.status).toBe(400);
  });
});

describe('/oauth/callback with a tenant client', () => {
  it('returns the code to the registered callback', async () => {
    stubOutbound({ lookup: 'found' });
    const state = signState({ originalState: 's1', redirectUri: CALLBACK, clientId: CLIENT_ID });
    const res = await fetch(`${baseUrl}/oauth/callback?code=c1&state=${encodeURIComponent(state)}`, { redirect: 'manual' });

    expect(res.status).toBe(302);
    const location = new URL(res.headers.get('location')!);
    expect(`${location.origin}${location.pathname}`).toBe(CALLBACK);
    expect(location.searchParams.get('code')).toBe('c1');
  });

  it('stops the flow when the registration was deleted after authorize', async () => {
    stubOutbound({ lookup: 'missing' });
    const state = signState({ originalState: 's1', redirectUri: CALLBACK, clientId: CLIENT_ID });
    const res = await fetch(`${baseUrl}/oauth/callback?code=c1&state=${encodeURIComponent(state)}`, { redirect: 'manual' });
    expect(res.status).toBe(400);
  });
});

describe('/oauth/token with a tenant client', () => {
  it('exchanges at the tenant authority and returns a token of that tenant', async () => {
    const outbound = stubOutbound({ lookup: 'found' });
    const r = await token({ grant_type: 'authorization_code', code: 'c1', code_verifier: 'v'.repeat(43), client_id: CLIENT_ID, redirect_uri: CALLBACK });

    expect(r.status).toBe(200);
    expect(typeof r.json.access_token).toBe('string');
    const entra = outbound.find((o) => o.url.includes('/oauth2/v2.0/token'))!;
    expect(new URL(entra.url).pathname).toBe(`/${TENANT}/oauth2/v2.0/token`);
  });

  it('discards a token of another tenant', async () => {
    stubOutbound({ lookup: 'found', tokenTid: OTHER_TENANT });
    const r = await token({ grant_type: 'refresh_token', refresh_token: 'rt', client_id: CLIENT_ID });

    expect(r.status).toBe(400);
    expect(r.json.error).toBe('invalid_grant');
    expect(r.json.access_token).toBeUndefined();
    expect(r.json.refresh_token).toBeUndefined();
  });

  it('refuses a deleted registration before calling Entra', async () => {
    const outbound = stubOutbound({ lookup: 'missing' });
    const r = await token({ grant_type: 'refresh_token', refresh_token: 'rt', client_id: CLIENT_ID });

    expect(r.status).toBe(400);
    expect(r.json.error).toBe('invalid_client');
    expect(outbound.some((o) => o.url.includes('/oauth2/v2.0/token'))).toBe(false);
  });

  it('leaves other clients on the organizations authority', async () => {
    const outbound = stubOutbound({ lookup: 'found' });
    const r = await token({ grant_type: 'refresh_token', refresh_token: 'rt', client_id: 'some-dcr-client' });

    expect(r.status).toBe(200);
    const entra = outbound.find((o) => o.url.includes('/oauth2/v2.0/token'))!;
    expect(new URL(entra.url).pathname).toBe('/organizations/oauth2/v2.0/token');
    expect(outbound.some((o) => o.url.includes('/client-registrations/'))).toBe(false);
  });
});
