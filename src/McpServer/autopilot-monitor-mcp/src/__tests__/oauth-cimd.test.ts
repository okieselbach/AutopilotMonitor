/**
 * Router-level tests for Client ID Metadata Documents on the OAuth proxy:
 * /oauth/authorize and /oauth/callback accept an https URL as client_id,
 * resolve the document (I/O injected via the cimd test seam), and enforce the
 * same redirect_uri binding + host allowlist as dynamic registration. The
 * dynamic-registration path is re-asserted to still work next to it.
 */
import { describe, it, expect, beforeAll, afterAll, beforeEach, vi } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';
import { setClientMetadataDepsForTests } from '../cimd.js';

process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
process.env.MCP_OAUTH_RATE_LIMIT_PER_MINUTE = '100000';
process.env.MCP_OAUTH_TOKEN_RATE_LIMIT_PER_MINUTE = '100000';
const { createOAuthRouter, signClientId, signState } = await import('../oauth.js');

const CLIENT_ID = 'https://app.example.test/oauth/client.json';
const CHALLENGE = 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM';

let server: Server;
let baseUrl: string;
const fetchImpl = vi.fn<typeof fetch>();

function doc(redirectUris: string[], clientId = CLIENT_ID): Response {
  return new Response(JSON.stringify({ client_id: clientId, client_name: 'Example', redirect_uris: redirectUris }), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

beforeAll(async () => {
  const app = express();
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(createOAuthRouter());
  await new Promise<void>((resolve) => {
    server = app.listen(0, '127.0.0.1', () => resolve());
  });
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

afterAll(async () => {
  setClientMetadataDepsForTests();
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

beforeEach(() => {
  fetchImpl.mockReset();
  setClientMetadataDepsForTests({ fetchImpl, resolve: async () => ['203.0.113.10'] });
});

function authorize(params: Record<string, string>) {
  const q = new URLSearchParams({ response_type: 'code', code_challenge: CHALLENGE, code_challenge_method: 'S256', ...params });
  return fetch(`${baseUrl}/oauth/authorize?${q}`, { redirect: 'manual' });
}

describe('RFC 8414 metadata', () => {
  it('advertises client_id_metadata_document_supported next to the (fallback) registration_endpoint', async () => {
    const json = (await (await fetch(`${baseUrl}/.well-known/oauth-authorization-server`)).json()) as Record<string, unknown>;
    expect(json.client_id_metadata_document_supported).toBe(true);
    expect(json.registration_endpoint).toBe(`${baseUrl}/oauth/register`);
  });
});

describe('/oauth/authorize with a metadata-document client_id', () => {
  it('fetches the document and redirects to Entra when the redirect_uri is listed and allow-listed', async () => {
    fetchImpl.mockResolvedValue(doc(['http://127.0.0.1:3000/callback']));
    const res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:49152/callback', state: 'xyz' });
    expect(res.status).toBe(302);
    const location = new URL(res.headers.get('location') ?? '');
    expect(location.pathname).toMatch(/\/oauth2\/v2\.0\/authorize$/);
    expect(location.searchParams.get('code_challenge')).toBe(CHALLENGE);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(String(fetchImpl.mock.calls[0][0])).toBe(CLIENT_ID);
  });

  it('rejects a redirect_uri the document does not list (invalid_request)', async () => {
    fetchImpl.mockResolvedValue(doc(['http://127.0.0.1:3000/callback']));
    const res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:3000/other' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_request');
  });

  it('keeps the host allowlist in force even when the document lists the URI (open-redirect containment)', async () => {
    fetchImpl.mockResolvedValue(doc(['https://evil.example.test/callback']));
    const res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'https://evil.example.test/callback' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_request');
    // Refused by the allowlist BEFORE the document is even fetched.
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('answers invalid_client when the document is unreachable or its client_id mismatches', async () => {
    fetchImpl.mockResolvedValueOnce(new Response('nope', { status: 503 }));
    let res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:3000/callback' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_client');

    setClientMetadataDepsForTests({ fetchImpl, resolve: async () => ['203.0.113.10'] }); // clears the negative cache
    fetchImpl.mockResolvedValueOnce(doc(['http://127.0.0.1:3000/callback'], 'https://app.example.test/oauth/OTHER.json'));
    res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:3000/callback' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_client');
  });

  it('answers invalid_client_metadata for a malformed document', async () => {
    fetchImpl.mockResolvedValue(new Response('{"client_id":"' + CLIENT_ID + '"}', { status: 200, headers: { 'content-type': 'application/json' } }));
    const res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:3000/callback' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_client_metadata');
  });

  it('refuses to fetch a client_id whose host resolves privately (SSRF gate) — no request leaves the server', async () => {
    setClientMetadataDepsForTests({ fetchImpl, resolve: async () => ['10.0.0.8'] });
    const res = await authorize({ client_id: CLIENT_ID, redirect_uri: 'http://127.0.0.1:3000/callback' });
    expect(res.status).toBe(400);
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('still accepts a dynamically registered (HMAC) client_id next to CIMD', async () => {
    const res = await authorize({ client_id: signClientId(['http://127.0.0.1:3000/callback'], 'dcr'), redirect_uri: 'http://127.0.0.1:3000/callback' });
    expect(res.status).toBe(302);
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});

describe('/oauth/callback with a metadata-document client_id in the signed state', () => {
  it('re-checks the document (cache) and forwards code + iss to the registered redirect_uri', async () => {
    fetchImpl.mockResolvedValue(doc(['http://127.0.0.1:3000/callback']));
    const state = signState({ originalState: 'orig', redirectUri: 'http://127.0.0.1:3000/callback', clientId: CLIENT_ID });
    const res = await fetch(`${baseUrl}/oauth/callback?code=abc&state=${encodeURIComponent(state)}`, { redirect: 'manual' });
    expect(res.status).toBe(302);
    const location = new URL(res.headers.get('location') ?? '');
    expect(location.origin + location.pathname).toBe('http://127.0.0.1:3000/callback');
    expect(location.searchParams.get('code')).toBe('abc');
    expect(location.searchParams.get('state')).toBe('orig');
    expect(location.searchParams.get('iss')).toBe(baseUrl);
  });

  it('fails closed when the document no longer lists the redirect_uri', async () => {
    fetchImpl.mockResolvedValue(doc(['http://127.0.0.1:3000/elsewhere']));
    const state = signState({ redirectUri: 'http://127.0.0.1:3000/callback', clientId: CLIENT_ID });
    const res = await fetch(`${baseUrl}/oauth/callback?code=abc&state=${encodeURIComponent(state)}`, { redirect: 'manual' });
    expect(res.status).toBe(400);
    expect(((await res.json()) as { error: string }).error).toBe('invalid_request');
  });
});
