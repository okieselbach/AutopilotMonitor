/**
 * OAuth Client ID Metadata Documents (CIMD) — MCP spec 2026-07-28 client
 * registration (draft-ietf-oauth-client-id-metadata-document-00).
 *
 * A client identifies itself with an HTTPS URL as `client_id`; the URL serves a
 * JSON document (`client_id`, `client_name`, `redirect_uris`, …). The
 * authorization server fetches that document on demand and validates the
 * redirect_uri of an authorization request against it — no registration call,
 * no server-side registry, and the client_id is portable across authorization
 * servers. This is the mechanism the 2026-07-28 revision recommends; Dynamic
 * Client Registration (RFC 7591) is deprecated and stays available as the
 * fallback (oauth.ts keeps the HMAC-signed client_id path unchanged).
 *
 * Trust model — what the document does and does NOT decide:
 *   - It only asserts WHICH redirect_uris a client claims. The requested
 *     redirect_uri must additionally pass the same host/path allowlist that
 *     gates dynamic registration (isAllowedRedirectUri in oauth.ts), so a
 *     self-hosted document cannot widen the set of destinations an
 *     authorization code may be sent to. Loopback stays allowed for every
 *     client (RFC 8252 §7.3) — `application_type: "native"` is informational.
 *   - Fetching a caller-chosen URL from the server is an SSRF surface. The
 *     fetch is therefore: https only, host must not be loopback / an IP literal
 *     / resolve to any non-public address, no redirect following, 5 s budget,
 *     16 KB body cap, JSON media type required, and nothing from the response
 *     is ever echoed to the caller beyond a generic error code. The address
 *     check runs inside the socket's own DNS lookup (createAddressGateLookup),
 *     never as a separate pre-flight resolution.
 *   - Results are cached in-process (positive: per Cache-Control max-age,
 *     clamped to 10–60 min so the /oauth/callback re-check within the 10-min
 *     state window hits the cache; negative: 60 s) with a bounded entry count.
 */
import { promises as dns } from 'node:dns';
import net from 'node:net';
import { Agent, fetch as undiciFetch } from 'undici';
import { MAX_CLIENT_NAME_LENGTH, MAX_REDIRECT_URIS_PER_CLIENT, MAX_REDIRECT_URI_LENGTH } from './oauth-limits.js';

export interface ClientMetadata {
  /** The document URL — equals the `client_id` field inside the document (verified). */
  clientId: string;
  clientName: string;
  redirectUris: string[];
  /** "native" | "web" when the document declares it (OIDC registration vocabulary). */
  applicationType?: string;
}

/** Why a metadata document was rejected. `code` is the OAuth error the caller should answer with. */
export class ClientMetadataError extends Error {
  constructor(
    readonly code: 'invalid_client' | 'invalid_client_metadata',
    message: string,
  ) {
    super(message);
    this.name = 'ClientMetadataError';
  }
}

export const CIMD_FETCH_TIMEOUT_MS = 5_000;
export const CIMD_MAX_DOCUMENT_BYTES = 16 * 1024;
const POSITIVE_TTL_MIN_MS = 10 * 60 * 1000;
const POSITIVE_TTL_MAX_MS = 60 * 60 * 1000;
const NEGATIVE_TTL_MS = 60 * 1000;
const CACHE_MAX_ENTRIES = 256;

/**
 * The part of a fetch response the metadata fetch reads. Structural so that both
 * undici's Response (production) and the global one (tests) satisfy it.
 */
export interface MetadataResponse {
  status: number;
  headers: { get(name: string): string | null };
  body: {
    getReader(): { read(): Promise<{ done: boolean; value?: Uint8Array }>; cancel(): Promise<void> };
  } | null;
}

export interface MetadataRequestInit {
  method: 'GET';
  headers: Record<string, string>;
  redirect: 'error';
  signal: AbortSignal;
}

/** Injectable I/O — production uses undici fetch through the address gate + dns.lookup; tests substitute either. */
export interface CimdDeps {
  /** The whole network leg. A substitute bypasses the address gate, which only sees `resolve`. */
  fetchImpl: (url: URL, init: MetadataRequestInit) => Promise<MetadataResponse>;
  /** Resolves a hostname to every address it maps to (A + AAAA); feeds the address gate. */
  resolve: (hostname: string) => Promise<string[]>;
  now: () => number;
}

// Node never calls `lookup` for an IP-literal host, so isClientIdMetadataUrl
// refusing every literal is part of this gate. `activeDeps` is read per
// connection so the test seam reaches the gate.
const metadataDispatcher = new Agent({
  connect: { lookup: createAddressGateLookup((hostname) => activeDeps.resolve(hostname)) },
});

const productionDeps: CimdDeps = {
  fetchImpl: (url, init) => undiciFetch(url, { ...init, dispatcher: metadataDispatcher }),
  resolve: async (hostname) => (await dns.lookup(hostname, { all: true })).map((a) => a.address),
  now: () => Date.now(),
};

let activeDeps: CimdDeps = productionDeps;

/** Test seam: swap the I/O for a suite; call with no argument to restore production I/O. */
export function setClientMetadataDepsForTests(overrides?: Partial<CimdDeps>): void {
  activeDeps = overrides ? { ...productionDeps, ...overrides } : productionDeps;
  clearClientMetadataCache();
}

/**
 * True when a client_id is shaped like a Client ID Metadata Document URL and is
 * one this server is willing to fetch. The draft requires https + a path
 * component and forbids a fragment; userinfo is rejected as a host-confusion
 * primitive (same reasoning as the redirect_uri allowlist), and loopback / IP
 * literals are refused up front so the SSRF gate never even resolves them.
 * Anything else (in particular our own HMAC-signed DCR client_ids, which
 * contain no scheme) is not a metadata URL and takes the registration path.
 */
export function isClientIdMetadataUrl(clientId: string | undefined | null): boolean {
  if (!clientId) return false;
  let u: URL;
  try {
    u = new URL(clientId);
  } catch {
    return false;
  }
  if (u.protocol !== 'https:') return false;
  if (u.pathname === '' || u.pathname === '/') return false;
  if (u.hash !== '' || u.username !== '' || u.password !== '') return false;
  const host = u.hostname.toLowerCase();
  if (host === 'localhost' || host.endsWith('.localhost')) return false;
  if (net.isIP(host.replace(/^\[|\]$/g, '')) !== 0) return false;
  return true;
}

/**
 * Address space the metadata fetch must never connect to. IPv4: every block of
 * the IANA IPv4 Special-Purpose Address Registry plus multicast and 240/4 (which
 * holds the limited broadcast). IPv6: only global unicast 2000::/3 is allowed at
 * all — one rule that refuses loopback, unspecified, IPv4-mapped/-compatible,
 * NAT64, discard, unique-local, link-/site-local and multicast — and inside it
 * the IANA special-purpose blocks are refused.
 */
const NON_PUBLIC_IPV4: ReadonlyArray<readonly [string, number]> = [
  ['0.0.0.0', 8], // "this network"
  ['10.0.0.0', 8], // private use
  ['100.64.0.0', 10], // shared address space (CGNAT)
  ['127.0.0.0', 8], // loopback
  ['169.254.0.0', 16], // link-local, holds the cloud metadata endpoints
  ['172.16.0.0', 12], // private use
  ['192.0.0.0', 24], // IETF protocol assignments
  ['192.0.2.0', 24], // documentation (TEST-NET-1)
  ['192.31.196.0', 24], // AS112-v4
  ['192.52.193.0', 24], // AMT
  ['192.88.99.0', 24], // deprecated 6to4 relay anycast
  ['192.168.0.0', 16], // private use
  ['192.175.48.0', 24], // direct delegation AS112
  ['198.18.0.0', 15], // benchmarking
  ['198.51.100.0', 24], // documentation (TEST-NET-2)
  ['203.0.113.0', 24], // documentation (TEST-NET-3)
  ['224.0.0.0', 4], // multicast
  ['240.0.0.0', 4], // reserved, limited broadcast
];
const NON_PUBLIC_IPV6: ReadonlyArray<readonly [string, number]> = [
  // everything outside global unicast 2000::/3
  ['::', 3],
  ['4000::', 2],
  ['8000::', 1],
  // special-purpose blocks inside it
  ['2001::', 23], // IETF protocol assignments (Teredo, benchmarking, ORCHID, AMT, AS112, …)
  ['2001:db8::', 32], // documentation
  ['2002::', 16], // 6to4
  ['2620:4f:8000::', 48], // direct delegation AS112
  ['3fff::', 20], // documentation
];

// Two lists on purpose: net.BlockList matches an IPv4 address against IPv6
// rules through its mapped form (::/3 would refuse 8.8.8.8), so each address
// is checked only against the list of its own family.
function blockListOf(ranges: ReadonlyArray<readonly [string, number]>, type: 'ipv4' | 'ipv6'): net.BlockList {
  const list = new net.BlockList();
  for (const [network, prefix] of ranges) list.addSubnet(network, prefix, type);
  return list;
}
const NON_PUBLIC_V4 = blockListOf(NON_PUBLIC_IPV4, 'ipv4');
const NON_PUBLIC_V6 = blockListOf(NON_PUBLIC_IPV6, 'ipv6');

/** True only for an address the metadata fetch may connect to. Anything unparseable is non-public. */
export function isPublicAddress(ip: string): boolean {
  // A zone id (fe80::1%eth0) passes net.isIP, but BlockList matches no rule for it.
  if (ip.includes('%')) return false;
  switch (net.isIP(ip)) {
    case 4:
      return !NON_PUBLIC_V4.check(ip, 'ipv4');
    case 6:
      return !NON_PUBLIC_V6.check(ip, 'ipv6');
    default:
      return false;
  }
}

/** Why the address gate refused a connection; travels as the `cause` of the failed fetch. */
export class AddressGateError extends Error {
  constructor(
    readonly reason: 'unresolvable' | 'non-public',
    options?: ErrorOptions,
  ) {
    super(
      reason === 'unresolvable'
        ? 'metadata document host does not resolve'
        : 'metadata document host resolves to a non-public address',
      options,
    );
    this.name = 'AddressGateError';
  }
}

/**
 * The SSRF gate: a `lookup` for the metadata fetch's sockets. It resolves the
 * host exactly once per connection and hands the socket only addresses that
 * passed `isAllowed`, so the address checked is the address connected to — a
 * separate pre-flight resolution would let a DNS-rebinding host answer the
 * check and the connect differently. Every resolved address must pass, not
 * only the one used. `isAllowed` is widened only by tests that need to reach a
 * loopback listener.
 */
export function createAddressGateLookup(
  resolve: (hostname: string) => Promise<string[]>,
  isAllowed: (ip: string) => boolean = isPublicAddress,
): net.LookupFunction {
  return (hostname, options, callback) => {
    resolve(hostname).then(
      (addresses) => {
        if (addresses.length === 0) return callback(new AddressGateError('unresolvable'), '');
        if (!addresses.every((ip) => isAllowed(ip))) return callback(new AddressGateError('non-public'), '');
        const family = options.family === 4 || options.family === 'IPv4' ? 4 : options.family === 6 || options.family === 'IPv6' ? 6 : 0;
        const usable = addresses
          .map((address) => ({ address, family: net.isIP(address) }))
          .filter((a) => family === 0 || a.family === family);
        if (usable.length === 0) return callback(new AddressGateError('unresolvable'), '');
        if (options.all) callback(null, usable);
        else callback(null, usable[0].address, usable[0].family);
      },
      (err: unknown) => callback(new AddressGateError('unresolvable', { cause: err }), ''),
    );
  };
}

function addressGateErrorOf(err: unknown): AddressGateError | undefined {
  let e: unknown = err;
  for (let depth = 0; e instanceof Error && depth < 5; depth++, e = e.cause) {
    if (e instanceof AddressGateError) return e;
  }
  return undefined;
}

interface CacheEntry {
  expiresAt: number;
  value?: ClientMetadata;
  error?: ClientMetadataError;
}

const cache = new Map<string, CacheEntry>();

export function clearClientMetadataCache(): void {
  cache.clear();
}

/**
 * Resolves the metadata document for a URL-shaped client_id (cached). Throws
 * ClientMetadataError for every rejection — the caller answers 400 with the
 * error's `code`; the message is for the server log only.
 */
export async function resolveClientMetadata(clientId: string): Promise<ClientMetadata> {
  const now = activeDeps.now();
  const hit = cache.get(clientId);
  if (hit && hit.expiresAt > now) {
    if (hit.error) throw hit.error;
    if (hit.value) return hit.value;
  }
  try {
    const { value, ttlMs } = await fetchAndValidate(clientId);
    remember(clientId, { expiresAt: activeDeps.now() + ttlMs, value });
    return value;
  } catch (err) {
    const error = err instanceof ClientMetadataError
      ? err
      : new ClientMetadataError('invalid_client', `metadata document fetch failed: ${err instanceof Error ? err.name : 'error'}`);
    remember(clientId, { expiresAt: activeDeps.now() + NEGATIVE_TTL_MS, error });
    throw error;
  }
}

function remember(key: string, entry: CacheEntry): void {
  cache.delete(key);
  if (cache.size >= CACHE_MAX_ENTRIES) {
    const oldest = cache.keys().next().value;
    if (oldest !== undefined) cache.delete(oldest);
  }
  cache.set(key, entry);
}

async function fetchAndValidate(clientId: string): Promise<{ value: ClientMetadata; ttlMs: number }> {
  if (!isClientIdMetadataUrl(clientId)) {
    throw new ClientMetadataError('invalid_client', 'client_id is not an acceptable metadata document URL');
  }
  const url = new URL(clientId);

  let res: MetadataResponse;
  try {
    res = await activeDeps.fetchImpl(url, {
      method: 'GET',
      headers: { Accept: 'application/json' },
      redirect: 'error',
      signal: AbortSignal.timeout(CIMD_FETCH_TIMEOUT_MS),
    });
  } catch (err) {
    const refused = addressGateErrorOf(err);
    if (refused) throw new ClientMetadataError('invalid_client', refused.message);
    throw err;
  }
  if (res.status !== 200) {
    throw new ClientMetadataError('invalid_client', `metadata document responded ${res.status}`);
  }
  const contentType = res.headers.get('content-type') ?? '';
  if (!/^application\/([a-z0-9.+-]*\+)?json\b/i.test(contentType.trim())) {
    throw new ClientMetadataError('invalid_client_metadata', 'metadata document is not application/json');
  }
  const text = await readCapped(res, CIMD_MAX_DOCUMENT_BYTES);

  let doc: unknown;
  try {
    doc = JSON.parse(text);
  } catch {
    throw new ClientMetadataError('invalid_client_metadata', 'metadata document is not valid JSON');
  }
  return { value: validateDocument(clientId, doc), ttlMs: ttlFromCacheControl(res.headers.get('cache-control')) };
}

async function readCapped(res: MetadataResponse, maxBytes: number): Promise<string> {
  const declared = Number(res.headers.get('content-length') ?? '0');
  if (declared > maxBytes) throw new ClientMetadataError('invalid_client_metadata', 'metadata document exceeds the size limit');
  if (!res.body) return '';
  const reader = res.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done || !value) break;
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel().catch(() => {});
      throw new ClientMetadataError('invalid_client_metadata', 'metadata document exceeds the size limit');
    }
    chunks.push(value);
  }
  return Buffer.concat(chunks).toString('utf-8');
}

/** Positive-cache lifetime from the document's Cache-Control, clamped to the window the callback re-check needs. */
export function ttlFromCacheControl(header: string | null): number {
  const m = /(?:^|,)\s*max-age\s*=\s*(\d+)/i.exec(header ?? '');
  const declared = m ? Number(m[1]) * 1000 : POSITIVE_TTL_MIN_MS;
  return Math.min(POSITIVE_TTL_MAX_MS, Math.max(POSITIVE_TTL_MIN_MS, declared));
}

/**
 * Structural validation per the draft + MCP spec: `client_id` MUST equal the
 * document URL exactly (simple string comparison, no normalization);
 * `redirect_uris` MUST be present and is bounded like a DCR registration.
 * `client_name` is required by the MCP spec but purely cosmetic here (it only
 * ever reaches a log line), so a missing one falls back to the host rather
 * than failing a login over a label.
 */
export function validateDocument(clientId: string, doc: unknown): ClientMetadata {
  if (typeof doc !== 'object' || doc === null || Array.isArray(doc)) {
    throw new ClientMetadataError('invalid_client_metadata', 'metadata document is not a JSON object');
  }
  const d = doc as Record<string, unknown>;
  if (d.client_id !== clientId) {
    throw new ClientMetadataError('invalid_client', 'metadata document client_id does not match its URL');
  }
  const uris = d.redirect_uris;
  if (!Array.isArray(uris) || uris.length === 0) {
    throw new ClientMetadataError('invalid_client_metadata', 'metadata document has no redirect_uris');
  }
  if (uris.length > MAX_REDIRECT_URIS_PER_CLIENT) {
    throw new ClientMetadataError('invalid_client_metadata', `metadata document lists more than ${MAX_REDIRECT_URIS_PER_CLIENT} redirect_uris`);
  }
  for (const u of uris) {
    if (typeof u !== 'string' || u.length === 0 || u.length > MAX_REDIRECT_URI_LENGTH) {
      throw new ClientMetadataError('invalid_client_metadata', 'metadata document redirect_uris entry is not a bounded string');
    }
    try {
      new URL(u);
    } catch {
      throw new ClientMetadataError('invalid_client_metadata', 'metadata document redirect_uris entry is not a URL');
    }
  }
  let clientName = new URL(clientId).hostname;
  if (typeof d.client_name === 'string' && d.client_name.length > 0) {
    if (d.client_name.length > MAX_CLIENT_NAME_LENGTH) {
      throw new ClientMetadataError('invalid_client_metadata', `client_name exceeds ${MAX_CLIENT_NAME_LENGTH} characters`);
    }
    clientName = d.client_name;
  }
  return {
    clientId,
    clientName,
    redirectUris: uris as string[],
    applicationType: typeof d.application_type === 'string' ? d.application_type : undefined,
  };
}
