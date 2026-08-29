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
 *     is ever echoed to the caller beyond a generic error code.
 *   - Results are cached in-process (positive: per Cache-Control max-age,
 *     clamped to 10–60 min so the /oauth/callback re-check within the 10-min
 *     state window hits the cache; negative: 60 s) with a bounded entry count.
 */
import { promises as dns } from 'node:dns';
import net from 'node:net';
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

/** Injectable I/O — production uses global fetch + dns.lookup; tests substitute both. */
export interface CimdDeps {
  fetchImpl: typeof fetch;
  /** Resolves a hostname to every address it maps to (A + AAAA). */
  resolve: (hostname: string) => Promise<string[]>;
  now: () => number;
}

const productionDeps: CimdDeps = {
  fetchImpl: (input, init) => fetch(input, init),
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

/** Non-public address ranges the metadata fetch must never reach (RFC 1918/4193/3927/6598, loopback, multicast, unspecified). */
export function isPublicAddress(ip: string): boolean {
  const family = net.isIP(ip);
  if (family === 4) return isPublicIpv4(ip);
  if (family === 6) {
    const lower = ip.toLowerCase();
    // IPv4-mapped (::ffff:a.b.c.d) — judge the embedded IPv4.
    const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/.exec(lower);
    if (mapped) return isPublicIpv4(mapped[1]);
    if (lower === '::' || lower === '::1') return false;
    if (lower.startsWith('fe8') || lower.startsWith('fe9') || lower.startsWith('fea') || lower.startsWith('feb')) return false; // link-local fe80::/10
    if (lower.startsWith('fc') || lower.startsWith('fd')) return false; // unique-local fc00::/7
    if (lower.startsWith('ff')) return false; // multicast
    return true;
  }
  return false;
}

function isPublicIpv4(ip: string): boolean {
  const [a, b] = ip.split('.').map(Number);
  if (a === 0 || a === 10 || a === 127) return false;
  if (a === 100 && b >= 64 && b <= 127) return false; // CGNAT 100.64/10
  if (a === 169 && b === 254) return false; // link-local
  if (a === 172 && b >= 16 && b <= 31) return false;
  if (a === 192 && b === 168) return false;
  if (a >= 224) return false; // multicast + reserved
  return true;
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

  // SSRF gate: every address the host resolves to must be public. A host that
  // resolves to nothing is refused as well (nothing to fetch safely).
  let addresses: string[];
  try {
    addresses = await activeDeps.resolve(url.hostname);
  } catch {
    throw new ClientMetadataError('invalid_client', 'metadata document host does not resolve');
  }
  if (addresses.length === 0 || !addresses.every(isPublicAddress)) {
    throw new ClientMetadataError('invalid_client', 'metadata document host resolves to a non-public address');
  }

  const res = await activeDeps.fetchImpl(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    redirect: 'error',
    signal: AbortSignal.timeout(CIMD_FETCH_TIMEOUT_MS),
  });
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

async function readCapped(res: Response, maxBytes: number): Promise<string> {
  const declared = Number(res.headers.get('content-length') ?? '0');
  if (declared > maxBytes) throw new ClientMetadataError('invalid_client_metadata', 'metadata document exceeds the size limit');
  if (!res.body) return '';
  const reader = res.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
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
