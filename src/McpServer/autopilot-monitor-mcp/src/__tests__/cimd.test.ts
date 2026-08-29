/**
 * Unit tests for Client ID Metadata Documents (cimd.ts): URL recognition, the
 * SSRF gate, document validation, and the cache. All I/O is injected — no DNS,
 * no network.
 */
import { describe, it, expect, beforeEach, afterAll, vi } from 'vitest';
import {
  CIMD_MAX_DOCUMENT_BYTES,
  ClientMetadataError,
  clearClientMetadataCache,
  isClientIdMetadataUrl,
  isPublicAddress,
  resolveClientMetadata,
  setClientMetadataDepsForTests,
  ttlFromCacheControl,
  validateDocument,
} from '../cimd.js';
import { MAX_REDIRECT_URIS_PER_CLIENT } from '../oauth-limits.js';

const CLIENT_ID = 'https://app.example.test/oauth/client.json';

function jsonResponse(body: unknown, init: { status?: number; headers?: Record<string, string> } = {}): Response {
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  return new Response(text, {
    status: init.status ?? 200,
    headers: { 'content-type': 'application/json', ...(init.headers ?? {}) },
  });
}

function goodDoc(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    client_id: CLIENT_ID,
    client_name: 'Example MCP Client',
    redirect_uris: ['http://127.0.0.1:3000/callback', 'https://app.example.test/callback'],
    application_type: 'native',
    ...overrides,
  };
}

let clock = 1_000_000;
const fetchImpl = vi.fn<typeof fetch>();
const resolve = vi.fn<(h: string) => Promise<string[]>>();

beforeEach(() => {
  clock = 1_000_000;
  fetchImpl.mockReset();
  resolve.mockReset().mockResolvedValue(['203.0.113.10', '2001:db8::10']);
  setClientMetadataDepsForTests({ fetchImpl, resolve, now: () => clock });
});

afterAll(() => setClientMetadataDepsForTests());

describe('isClientIdMetadataUrl — what counts as a metadata document URL', () => {
  it('accepts an https URL with a path component', () => {
    expect(isClientIdMetadataUrl(CLIENT_ID)).toBe(true);
    expect(isClientIdMetadataUrl('https://example.test/x')).toBe(true);
  });

  it('rejects http, a bare origin, fragments and userinfo (draft requirements + host-confusion)', () => {
    expect(isClientIdMetadataUrl('http://app.example.test/client.json')).toBe(false);
    expect(isClientIdMetadataUrl('https://app.example.test')).toBe(false);
    expect(isClientIdMetadataUrl('https://app.example.test/')).toBe(false);
    expect(isClientIdMetadataUrl('https://app.example.test/c.json#frag')).toBe(false);
    expect(isClientIdMetadataUrl('https://user:pw@app.example.test/c.json')).toBe(false);
  });

  it('rejects loopback and IP-literal hosts before any resolution happens', () => {
    expect(isClientIdMetadataUrl('https://localhost/c.json')).toBe(false);
    expect(isClientIdMetadataUrl('https://foo.localhost/c.json')).toBe(false);
    expect(isClientIdMetadataUrl('https://127.0.0.1/c.json')).toBe(false);
    expect(isClientIdMetadataUrl('https://[::1]/c.json')).toBe(false);
    expect(isClientIdMetadataUrl('https://169.254.169.254/latest/meta-data')).toBe(false);
  });

  it('is false for our HMAC-signed dynamic-registration client_ids and garbage', () => {
    expect(isClientIdMetadataUrl('eyJ0eXAiOiJjbGllbnQifQ.abc')).toBe(false);
    expect(isClientIdMetadataUrl('')).toBe(false);
    expect(isClientIdMetadataUrl(undefined)).toBe(false);
  });
});

describe('isPublicAddress — SSRF address classes', () => {
  it('rejects every private / special-use range', () => {
    for (const ip of ['10.1.2.3', '172.16.0.1', '172.31.255.255', '192.168.1.1', '127.0.0.1', '0.0.0.0', '169.254.169.254', '100.64.0.1', '224.0.0.1', '::1', '::', 'fe80::1', 'fd00::1', 'fc00::1', 'ff02::1', '::ffff:10.0.0.1', '::ffff:127.0.0.1']) {
      expect(isPublicAddress(ip), ip).toBe(false);
    }
  });

  it('accepts public addresses', () => {
    for (const ip of ['203.0.113.10', '8.8.8.8', '172.32.0.1', '100.128.0.1', '2001:db8::10', '::ffff:203.0.113.10']) {
      expect(isPublicAddress(ip), ip).toBe(true);
    }
  });
});

describe('resolveClientMetadata — fetch + validation', () => {
  it('fetches the document without following redirects, with a timeout, and returns the metadata', async () => {
    fetchImpl.mockResolvedValue(jsonResponse(goodDoc()));
    const md = await resolveClientMetadata(CLIENT_ID);
    expect(md).toEqual({
      clientId: CLIENT_ID,
      clientName: 'Example MCP Client',
      redirectUris: ['http://127.0.0.1:3000/callback', 'https://app.example.test/callback'],
      applicationType: 'native',
    });
    expect(resolve).toHaveBeenCalledWith('app.example.test');
    const [url, init] = fetchImpl.mock.calls[0];
    expect(String(url)).toBe(CLIENT_ID);
    expect(init?.redirect).toBe('error');
    expect(init?.signal).toBeInstanceOf(AbortSignal);
  });

  it('never fetches when the host resolves to a non-public address (SSRF gate)', async () => {
    resolve.mockResolvedValue(['203.0.113.10', '10.0.0.5']);
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client' });
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('never fetches when the host does not resolve', async () => {
    resolve.mockRejectedValue(new Error('ENOTFOUND'));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client' });
    expect(fetchImpl).not.toHaveBeenCalled();
  });

  it('rejects a client_id that is not a metadata URL without any I/O', async () => {
    await expect(resolveClientMetadata('https://127.0.0.1/c.json')).rejects.toMatchObject({ code: 'invalid_client' });
    expect(resolve).not.toHaveBeenCalled();
  });

  it('rejects a non-200 answer and a redirect (fetch throws with redirect: error)', async () => {
    fetchImpl.mockResolvedValueOnce(jsonResponse(goodDoc(), { status: 404 }));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client' });
    clearClientMetadataCache();
    fetchImpl.mockRejectedValueOnce(new TypeError('unexpected redirect'));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client' });
  });

  it('rejects a non-JSON media type and invalid JSON', async () => {
    fetchImpl.mockResolvedValueOnce(new Response('{}', { status: 200, headers: { 'content-type': 'text/html' } }));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client_metadata' });
    clearClientMetadataCache();
    fetchImpl.mockResolvedValueOnce(jsonResponse('{not json'));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client_metadata' });
  });

  it('accepts a structured-syntax JSON media type (application/vnd.x+json)', async () => {
    fetchImpl.mockResolvedValue(jsonResponse(goodDoc(), { headers: { 'content-type': 'application/vnd.example+json; charset=utf-8' } }));
    await expect(resolveClientMetadata(CLIENT_ID)).resolves.toMatchObject({ clientId: CLIENT_ID });
  });

  it('caps the document size (declared and streamed)', async () => {
    fetchImpl.mockResolvedValueOnce(jsonResponse(goodDoc(), { headers: { 'content-length': String(CIMD_MAX_DOCUMENT_BYTES + 1) } }));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client_metadata' });
    clearClientMetadataCache();
    const huge = JSON.stringify(goodDoc({ padding: 'x'.repeat(CIMD_MAX_DOCUMENT_BYTES) }));
    fetchImpl.mockResolvedValueOnce(new Response(huge, { status: 200, headers: { 'content-type': 'application/json' } }));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client_metadata' });
  });

  it('rejects a document whose client_id does not equal the URL exactly', async () => {
    fetchImpl.mockResolvedValue(jsonResponse(goodDoc({ client_id: 'https://app.example.test/oauth/client.json/' })));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({ code: 'invalid_client' });
  });
});

describe('validateDocument — structure', () => {
  it('requires redirect_uris and bounds them like a dynamic registration', () => {
    expect(() => validateDocument(CLIENT_ID, goodDoc({ redirect_uris: undefined }))).toThrow(ClientMetadataError);
    expect(() => validateDocument(CLIENT_ID, goodDoc({ redirect_uris: [] }))).toThrow(/no redirect_uris/);
    expect(() => validateDocument(CLIENT_ID, goodDoc({ redirect_uris: Array(MAX_REDIRECT_URIS_PER_CLIENT + 1).fill('https://a.test/cb') }))).toThrow(/more than/);
    expect(() => validateDocument(CLIENT_ID, goodDoc({ redirect_uris: ['not a url'] }))).toThrow(/not a URL/);
    expect(() => validateDocument(CLIENT_ID, goodDoc({ redirect_uris: [42] }))).toThrow(/bounded string/);
  });

  it('rejects non-object documents', () => {
    expect(() => validateDocument(CLIENT_ID, [])).toThrow(/not a JSON object/);
    expect(() => validateDocument(CLIENT_ID, null)).toThrow(/not a JSON object/);
  });

  it('falls back to the host for a missing client_name and ignores an unknown application_type type', () => {
    const md = validateDocument(CLIENT_ID, goodDoc({ client_name: undefined, application_type: 7 }));
    expect(md.clientName).toBe('app.example.test');
    expect(md.applicationType).toBeUndefined();
  });
});

describe('cache', () => {
  it('serves a second lookup from cache and honours the clamped Cache-Control max-age', async () => {
    fetchImpl.mockImplementation(async () => jsonResponse(goodDoc(), { headers: { 'cache-control': 'public, max-age=30' } }));
    await resolveClientMetadata(CLIENT_ID);
    await resolveClientMetadata(CLIENT_ID);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    // max-age=30 is clamped UP to the 10-min floor so the /oauth/callback re-check hits the cache.
    clock += 9 * 60 * 1000;
    await resolveClientMetadata(CLIENT_ID);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    clock += 2 * 60 * 1000;
    await resolveClientMetadata(CLIENT_ID);
    expect(fetchImpl).toHaveBeenCalledTimes(2);
  });

  it('caches a rejection for 60 s', async () => {
    fetchImpl.mockImplementation(async () => jsonResponse(goodDoc(), { status: 500 }));
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toBeInstanceOf(ClientMetadataError);
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toBeInstanceOf(ClientMetadataError);
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    clock += 61_000;
    fetchImpl.mockResolvedValue(jsonResponse(goodDoc()));
    await expect(resolveClientMetadata(CLIENT_ID)).resolves.toMatchObject({ clientId: CLIENT_ID });
  });

  it('clamps ttlFromCacheControl to [10 min, 60 min]', () => {
    expect(ttlFromCacheControl(null)).toBe(10 * 60 * 1000);
    expect(ttlFromCacheControl('max-age=5')).toBe(10 * 60 * 1000);
    expect(ttlFromCacheControl('no-cache, max-age=1800')).toBe(30 * 60 * 1000);
    expect(ttlFromCacheControl('max-age=86400')).toBe(60 * 60 * 1000);
  });
});
