/**
 * Unit tests for Client ID Metadata Documents (cimd.ts): URL recognition, the
 * SSRF address gate, document validation, and the cache. DNS is always
 * injected; the only sockets are the loopback listeners of the address-gate
 * suite, which prove what the gate lets a connection reach.
 */
import { describe, it, expect, beforeEach, beforeAll, afterAll, vi } from 'vitest';
import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Agent, fetch as undiciFetch } from 'undici';
import {
  AddressGateError,
  CIMD_MAX_DOCUMENT_BYTES,
  ClientMetadataError,
  clearClientMetadataCache,
  createAddressGateLookup,
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
  resolve.mockReset().mockResolvedValue(['1.1.1.1', '2606:4700:4700::1111']);
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

// --- IANA special-purpose registries (tests/fixtures/iana-special-purpose-addresses) ---

function registryDir(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (!existsSync(join(dir, 'AutopilotMonitor.sln'))) {
    const parent = dirname(dir);
    if (parent === dir) throw new Error('repository root (AutopilotMonitor.sln) not found');
    dir = parent;
  }
  return join(dir, 'tests', 'fixtures', 'iana-special-purpose-addresses');
}

/** RFC 4180 reader — the registry quotes cells, wraps the RFC column over lines and doubles quotes. */
function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') {
        cell += '"';
        i++;
      } else if (c === '"') quoted = false;
      else cell += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') {
      row.push(cell);
      cell = '';
    } else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
    } else cell += c;
  }
  if (cell !== '' || row.length > 0) {
    row.push(cell);
    rows.push(row);
  }
  return rows;
}

/** Every block of one registry file: footnote markers ("2002::/16 [3]") dropped, two-block cells split. */
function registryBlocks(file: string): string[] {
  const [header, ...rows] = parseCsv(readFileSync(join(registryDir(), file), 'utf-8'));
  if (header[0] !== 'Address Block') throw new Error(`${file}: unexpected header ${header[0]}`);
  return rows.flatMap((row) => row[0].split(',').map((b) => b.replace(/\[\d+\]/g, '').trim())).filter((b) => b !== '');
}

function ipv6ToBigInt(ip: string): bigint {
  const [head, tail] = ip.split('::');
  const h = head ? head.split(':') : [];
  const t = tail === undefined ? [] : tail ? tail.split(':') : [];
  const groups = tail === undefined ? h : [...h, ...Array<string>(8 - h.length - t.length).fill('0'), ...t];
  return groups.reduce((acc, g) => (acc << 16n) | BigInt(parseInt(g, 16)), 0n);
}

function bigIntToIpv6(n: bigint): string {
  return Array.from({ length: 8 }, (_, i) => ((n >> BigInt((7 - i) * 16)) & 0xffffn).toString(16)).join(':');
}

/** First and last address of a CIDR block. */
function cidrBounds(cidr: string): [string, string] {
  const [network, len] = cidr.split('/');
  const prefix = Number(len);
  if (net.isIP(network) === 4) {
    const size = 2 ** (32 - prefix);
    const first = Math.floor(network.split('.').reduce((acc, o) => acc * 256 + Number(o), 0) / size) * size;
    const fmt = (v: number) => [24, 16, 8, 0].map((s) => Math.floor(v / 2 ** s) % 256).join('.');
    return [fmt(first), fmt(first + size - 1)];
  }
  const hostBits = BigInt(128 - prefix);
  const first = (ipv6ToBigInt(network) >> hostBits) << hostBits;
  return [bigIntToIpv6(first), bigIntToIpv6(first + (1n << hostBits) - 1n)];
}

describe('isPublicAddress — every IANA special-purpose block is refused', () => {
  for (const file of ['iana-ipv4-special-registry-1.csv', 'iana-ipv6-special-registry-1.csv']) {
    const blocks = registryBlocks(file);
    it(`${file} is read completely (${blocks.length} blocks)`, () => {
      expect(blocks.length).toBeGreaterThanOrEqual(20);
      for (const block of blocks) expect(block).toMatch(/^[0-9a-f.:]+\/\d{1,3}$/);
    });
    it.each(blocks)(`${file}: %s — first and last address refused`, (block) => {
      for (const ip of cidrBounds(block)) expect(isPublicAddress(ip), `${block} -> ${ip}`).toBe(false);
    });
  }
});

describe('isPublicAddress — boundaries and forms', () => {
  it('refuses multicast, reserved, broadcast and every address outside IPv6 global unicast', () => {
    for (const ip of ['224.0.0.1', '239.255.255.255', '240.0.0.1', '255.255.255.255', '::ffff:8.8.8.8', '::ffff:10.0.0.1', '::8.8.8.8', '64:ff9b::808:808', 'fec0::1', 'ff02::1', '4000::1', 'e000::1']) {
      expect(isPublicAddress(ip), ip).toBe(false);
    }
  });

  it('refuses an address with a zone id, even a global one', () => {
    for (const ip of ['fe80::1%eth0', 'fe80::1%1', '2606:4700:4700::1111%1']) {
      expect(isPublicAddress(ip), ip).toBe(false);
    }
  });

  it('refuses what is not an IP address', () => {
    for (const ip of ['', 'app.example.test', '1.2.3', '1.2.3.4.5', '::g']) {
      expect(isPublicAddress(ip), ip).toBe(false);
    }
  });

  it('accepts public addresses, including the neighbours of refused blocks', () => {
    for (const ip of ['1.1.1.1', '8.8.8.8', '9.255.255.255', '11.0.0.0', '100.63.255.255', '100.128.0.0', '172.15.255.255', '172.32.0.0', '192.0.1.0', '192.167.255.255', '192.169.0.0', '198.17.255.255', '198.20.0.0', '223.255.255.255', '2606:4700:4700::1111', '2a00:1450:4001::1', '2001:200::1', '2003::1', '2001:db9::1']) {
      expect(isPublicAddress(ip), ip).toBe(true);
    }
  });
});

describe('createAddressGateLookup — the address gate', () => {
  type LookupResult = { err: NodeJS.ErrnoException | null; address: unknown; family?: number };
  function runLookup(lookup: net.LookupFunction, options: Parameters<net.LookupFunction>[1]): Promise<LookupResult> {
    return new Promise((done) => lookup('app.example.test', options, (err, address, family) => done({ err, address, family })));
  }

  it('answers the socket with the resolved public addresses, in both callback shapes, resolving once per call', async () => {
    const gateResolve = vi.fn(async () => ['1.1.1.1', '2606:4700:4700::1111']);
    const lookup = createAddressGateLookup(gateResolve);
    expect(await runLookup(lookup, { all: true })).toEqual({
      err: null,
      address: [{ address: '1.1.1.1', family: 4 }, { address: '2606:4700:4700::1111', family: 6 }],
      family: undefined,
    });
    expect(await runLookup(lookup, {})).toEqual({ err: null, address: '1.1.1.1', family: 4 });
    expect(gateResolve).toHaveBeenCalledTimes(2);
    expect(gateResolve).toHaveBeenCalledWith('app.example.test');
  });

  it('honours a requested address family', async () => {
    const lookup = createAddressGateLookup(async () => ['1.1.1.1', '2606:4700:4700::1111']);
    expect(await runLookup(lookup, { family: 6 })).toEqual({ err: null, address: '2606:4700:4700::1111', family: 6 });
    expect(await runLookup(lookup, { family: 'IPv4', all: true })).toEqual({ err: null, address: [{ address: '1.1.1.1', family: 4 }], family: undefined });
    const { err } = await runLookup(createAddressGateLookup(async () => ['1.1.1.1']), { family: 6 });
    expect(err).toBeInstanceOf(AddressGateError);
    expect((err as AddressGateError).reason).toBe('unresolvable');
  });

  it('refuses the connection when ANY resolved address is non-public', async () => {
    const { err } = await runLookup(createAddressGateLookup(async () => ['1.1.1.1', '169.254.169.254']), { all: true });
    expect(err).toBeInstanceOf(AddressGateError);
    expect((err as AddressGateError).reason).toBe('non-public');
  });

  it('refuses a host that resolves to nothing or fails to resolve, keeping the resolver error as cause', async () => {
    const empty = await runLookup(createAddressGateLookup(async () => []), { all: true });
    expect((empty.err as AddressGateError).reason).toBe('unresolvable');
    const cause = new Error('ENOTFOUND');
    const failed = await runLookup(createAddressGateLookup(async () => { throw cause; }), { all: true });
    expect((failed.err as AddressGateError).reason).toBe('unresolvable');
    expect((failed.err as AddressGateError).cause).toBe(cause);
  });
});

describe('address gate on real sockets', () => {
  let listener: net.Server;
  let listenerPort = 0;
  let listenerConnections = 0;
  let httpServer: http.Server;
  let httpPort = 0;
  let httpConnections = 0;

  beforeAll(async () => {
    listener = net.createServer((socket) => {
      listenerConnections++;
      socket.destroy();
    });
    await new Promise<void>((done) => listener.listen(0, '127.0.0.1', done));
    listenerPort = (listener.address() as net.AddressInfo).port;
    httpServer = http.createServer((_req, res) => res.end('ok'));
    httpServer.on('connection', () => {
      httpConnections++;
    });
    await new Promise<void>((done) => httpServer.listen(0, '127.0.0.1', done));
    httpPort = (httpServer.address() as net.AddressInfo).port;
  });

  afterAll(async () => {
    await new Promise<void>((done) => listener.close(() => done()));
    await new Promise<void>((done) => httpServer.close(() => done()));
  });

  it('a host that resolves to a private address fails closed before any socket is opened (production fetch path)', async () => {
    listenerConnections = 0;
    const rebinding = vi.fn(async () => ['127.0.0.1']);
    setClientMetadataDepsForTests({ resolve: rebinding, now: () => clock });
    const err = await resolveClientMetadata(`https://rebind.example.test:${listenerPort}/client.json`).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ClientMetadataError);
    expect(err).toMatchObject({ code: 'invalid_client', message: 'metadata document host resolves to a non-public address' });
    expect(rebinding).toHaveBeenCalledTimes(1);
    expect(rebinding).toHaveBeenCalledWith('rebind.example.test');
    expect(listenerConnections).toBe(0);
  });

  it('a host that does not resolve fails closed (production fetch path)', async () => {
    setClientMetadataDepsForTests({ resolve: async () => { throw new Error('ENOTFOUND'); }, now: () => clock });
    await expect(resolveClientMetadata(`https://gone.example.test:${listenerPort}/client.json`)).rejects.toMatchObject({
      code: 'invalid_client',
      message: 'metadata document host does not resolve',
    });
  });

  it('the socket connects to exactly the address the gate handed out — the host is never resolved a second time', async () => {
    // rebind.example.test does not exist in real DNS: reaching the listener proves the socket used the gate's answer.
    httpConnections = 0;
    const gateResolve = vi.fn(async () => ['127.0.0.1']);
    const dispatcher = new Agent({ connect: { lookup: createAddressGateLookup(gateResolve, () => true) } });
    try {
      const res = await undiciFetch(`http://rebind.example.test:${httpPort}/client.json`, { dispatcher });
      expect(res.status).toBe(200);
      expect(await res.text()).toBe('ok');
    } finally {
      await dispatcher.close();
    }
    expect(gateResolve).toHaveBeenCalledTimes(1);
    expect(httpConnections).toBe(1);
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
    const [url, init] = fetchImpl.mock.calls[0];
    expect(String(url)).toBe(CLIENT_ID);
    expect(init?.redirect).toBe('error');
    expect(init?.signal).toBeInstanceOf(AbortSignal);
  });

  it('refuses a host with any non-public address through the production fetch path (SSRF gate)', async () => {
    resolve.mockResolvedValue(['1.1.1.1', '10.0.0.5']);
    setClientMetadataDepsForTests({ resolve, now: () => clock });
    await expect(resolveClientMetadata(CLIENT_ID)).rejects.toMatchObject({
      code: 'invalid_client',
      message: 'metadata document host resolves to a non-public address',
    });
    expect(resolve).toHaveBeenCalledTimes(1);
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
