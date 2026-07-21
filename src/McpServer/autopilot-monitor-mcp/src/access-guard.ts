/**
 * MCP access guard: validates user token, checks backend whitelist, enforces rate limits.
 *
 * Flow per request:
 *   1. Extract Bearer token → decode JWT claims (upn, exp)
 *   2. Check access: call backend /api/global/mcp-users/check (cached 60s per UPN+token)
 *   3. Rate limit: sliding window per UPN (default 60 req/min)
 *   4. Set token for pass-through to backend API
 */
import type { Request, Response, NextFunction } from 'express';
import crypto from 'node:crypto';
import { extractTokenClaims, isTokenExpired } from './auth.js';
import { runWithCaller } from './client.js';
import { API_BASE_URL, getPublicBaseUrl, parsePositiveInt } from './config.js';

const BASE_URL = API_BASE_URL;

// Per-UPN request budget (sliding 60s window), applied AFTER a token is
// validated. parsePositiveInt guards against a non-numeric override: a bare
// parseInt would yield NaN, and `count >= NaN` is always false — silently
// disabling the limiter.
const RATE_LIMIT = parsePositiveInt(process.env.MCP_RATE_LIMIT_PER_MINUTE, 60);

// Per-source-IP budget for UNvalidated requests (sliding 60s window), applied
// only on the cache-MISS path that would call the backend. Bounds forged- /
// distinct-token floods at the source without ever touching a victim's per-UPN
// budget (different key space). It counts ONLY cache misses — a legitimate user
// misses at most ~once per token per 60s TTL (~1/min), and a NAT'd org of N
// active users ~N per min — so the default 120 (= 2× the per-UPN rate) clears
// a ~120-concurrently-active-user shared-IP org while capping flood
// amplification to ~2 backend calls/sec/IP. NOTE: this is per-replica; with maxReplicas=1 it is effectively
// global, but if the app is ever scaled out the effective per-IP ceiling becomes
// 120×replicas (a shared store would be needed for a true global cap). Tune via
// MCP_PRE_AUTH_RATE_LIMIT_PER_MINUTE; raise it (or allowlist the egress IP) only
// if a very large shared-IP org reports false 429s.
const PRE_AUTH_RATE_LIMIT = parsePositiveInt(process.env.MCP_PRE_AUTH_RATE_LIMIT_PER_MINUTE, 120);

// Per-source-IP budgets for the UNauthenticated /oauth/* endpoints (sliding 60s
// window). These routes sit OUTSIDE accessGuard (they exist to obtain the token
// accessGuard demands), so without their own throttle they are open amplifiers:
// every anonymous POST /oauth/token triggers an outbound Entra call carrying the
// app's client_secret — a flood makes Entra throttle the app REGISTRATION,
// breaking token refresh for every legitimate MCP user (org-wide auth DoS with
// zero credentials). The other /oauth routes are local-only (HMAC/allowlist
// work) but remain CPU/log-spam vectors. Budgets are far above a legitimate
// flow's needs: a full OAuth dance is ~4 requests, token refresh ~1/hour/client.
const OAUTH_RATE_LIMIT = parsePositiveInt(process.env.MCP_OAUTH_RATE_LIMIT_PER_MINUTE, 60);
const OAUTH_TOKEN_RATE_LIMIT = parsePositiveInt(process.env.MCP_OAUTH_TOKEN_RATE_LIMIT_PER_MINUTE, 20);

// Hard ceiling on access-cache cardinality. The key is UPN+tokenHash, so a flood
// of distinct (valid-signature) tokens would otherwise grow the map unbounded
// between the 5-min reaper passes — a memory-exhaustion vector on the 0.5 GiB
// container. Oldest entries are evicted on write once the cap is hit.
const MAX_ACCESS_CACHE_ENTRIES = parsePositiveInt(process.env.MCP_ACCESS_CACHE_MAX_ENTRIES, 10_000);

// Hard ceiling on rate-bucket cardinality (the per-UPN, per-source-IP and /oauth/* maps).
// A bucket is created per distinct key on first sight and only removed by the 5-min
// reaper (empty-window prune), so a distinct-key flood — spoof-resistant IPs are hard,
// but forged UPNs and NAT churn are not — could grow either map unbounded between
// reaper passes. Same memory-exhaustion vector, same fix as the access cache: evict the
// oldest bucket on write once the cap is hit. The cap is far above any legitimate
// per-minute active-key count, so real callers are never evicted mid-window.
const MAX_RATE_BUCKET_ENTRIES = parsePositiveInt(process.env.MCP_RATE_BUCKET_MAX_ENTRIES, 10_000);

// Timeout for the backend access-check fetch. Without it a hung/cold backend
// stalls the path EVERY request must traverse. Generous enough to survive a
// cold Functions backend (→ no false 403s), bounded so an unresponsive backend
// cannot pile up held requests.
const ACCESS_CHECK_TIMEOUT_MS = parsePositiveInt(process.env.MCP_ACCESS_CHECK_TIMEOUT_MS, 15_000);

// The access verdict carries the operator kill-switch (a Disabled McpUsers row) and the delegated
// (MSP) tenant set, so this TTL IS the revocation lag of this layer. The backend caches its own
// verdict for 30s (McpUserService) with the explicit goal that a revoke self-heals in seconds —
// a long TTL here would quietly stretch that to minutes for MCP callers. Keep it ≤60s: worst-case
// revocation = this TTL + the backend's 30s. Do NOT raise it back "for performance" — a cache miss
// is one backend point-read per user per minute.
const ACCESS_CACHE_TTL_MS = 60 * 1000; // 60 seconds

// --- Access check cache ---

interface AccessCacheEntry {
  allowed: boolean;
  reason: string;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
  // Delegated (scoped-global / MSP) scope. Cached alongside the platform flags so a cached
  // second request keeps the managed tenant set — without this the follow-up would silently drop the
  // scope and downgrade the caller to home-tenant-only routing.
  delegatedTenantIds?: string[];
  delegatedRole?: string;
  expiresAt: number;
}

// Cache key includes a hash of the token, not just the UPN. The JWT signature
// is verified by the backend, not here — so two tokens with the same UPN must
// be treated as distinct cache entries. Without this, a forged token carrying
// a victim's UPN would hit the legitimate user's cached `allowed: true` and
// bypass the backend signature check for any local-only MCP operation
// (tools/list, resources/list, etc.).
const accessCache = new Map<string, AccessCacheEntry>();

interface AccessCheckResult {
  allowed: boolean;
  reason: string;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
  // Managed tenant IDs (lowercase) for a delegated (MSP) caller, else undefined. Drives cross-tenant
  // routing bounded to this set; every delegated tool call must name one of these tenants.
  delegatedTenantIds?: string[];
  delegatedRole?: string;
  // Distinguishes a genuine authorization denial (the backend reached a verdict
  // and said "no" — e.g. user not on the MCP whitelist) from an infrastructure
  // failure (backend unreachable / malformed response). Both fail closed, but
  // only the former should be surfaced to the user as "you are not enabled —
  // ask to be whitelisted"; an infra error is not the user's fault.
  infraError: boolean;
  // Set when the per-source-IP pre-auth budget was exhausted on a cache-miss:
  // the middleware maps this to 429 (not 403), since it is throttling, not a
  // verdict. Never set on a cache hit (a hit consumes no pre-auth budget).
  rateLimited?: boolean;
}

export function buildCacheKey(upn: string, token: string): string {
  const h = crypto.createHash('sha256').update(token).digest('hex').slice(0, 32);
  return `${upn}:${h}`;
}

/**
 * Insert into a size-bounded Map, evicting the oldest entry (insertion order)
 * when the cap is reached. Keeps memory bounded under a distinct-key flood.
 * Exported for unit testing. Re-inserting an existing key updates in place
 * without growing the map (the size check is skipped when the key already exists).
 */
export function boundedSet<K, V>(map: Map<K, V>, key: K, value: V, cap: number): void {
  if (!map.has(key) && map.size >= cap) {
    const oldest = map.keys().next().value as K | undefined;
    if (oldest !== undefined) map.delete(oldest);
  }
  map.set(key, value);
}

async function checkAccess(upn: string, token: string, clientIp: string): Promise<AccessCheckResult> {
  const cacheKey = buildCacheKey(upn, token);
  const cached = accessCache.get(cacheKey);
  if (cached && Date.now() < cached.expiresAt) {
    // A cached DENY is cheap to serve but still costs request processing — and
    // deny verdicts ARE cached, so without this an attacker could repeat one
    // forged/denied token and farm unlimited cached 403s for the whole TTL
    // while never tripping the pre-auth limiter (it only ran on the miss path).
    // Count cached denials against the per-source-IP budget too, returning 429
    // once it is exhausted. Cached ALLOWs stay free, so a legitimate user never
    // burns the pre-auth budget on their cached fast-path.
    if (!cached.allowed && isPreAuthRateLimited(clientIp)) {
      return { allowed: false, reason: 'Pre-auth rate limit exceeded', isGlobalAdmin: false, isGlobalReader: false, infraError: false, rateLimited: true };
    }
    return {
      allowed: cached.allowed,
      reason: cached.reason,
      isGlobalAdmin: cached.isGlobalAdmin,
      isGlobalReader: cached.isGlobalReader,
      delegatedTenantIds: cached.delegatedTenantIds,
      delegatedRole: cached.delegatedRole,
      // Only genuine backend verdicts are ever cached (infra errors return early
      // below without caching), so a cache hit is never an infra error.
      infraError: false,
    };
  }

  // Cache miss → this request is about to hit the backend. Bound the cost per
  // source IP so a forged-/distinct-token flood cannot drive unbounded backend
  // calls (the JWT signature is not verified here, so anyone can mint a
  // well-formed token). Keyed on IP, not UPN, so it never burns a victim's
  // per-UPN budget.
  if (isPreAuthRateLimited(clientIp)) {
    return { allowed: false, reason: 'Pre-auth rate limit exceeded', isGlobalAdmin: false, isGlobalReader: false, infraError: false, rateLimited: true };
  }

  try {
    const res = await fetch(`${BASE_URL}/api/auth/mcp`, {
      headers: { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(ACCESS_CHECK_TIMEOUT_MS),
    });

    const text = await res.text();
    if (!text) {
      console.error(`[access-guard] Backend returned empty body for ${upn} (status=${res.status})`);
      return { allowed: false, reason: `Backend returned ${res.status} with empty body`, isGlobalAdmin: false, isGlobalReader: false, infraError: true };
    }

    const data = JSON.parse(text) as {
      allowed: boolean;
      reason?: string;
      accessGrant?: string;
      isGlobalAdmin?: boolean;
      // Platform role: "GlobalAdmin" | "GlobalReader" (absent → no platform role). The read-only
      // GlobalReader gets the same cross-tenant routing as GA because this server is read-only.
      globalRole?: string;
      // Delegated (scoped-global / MSP) scope: the managed tenant IDs (lowercase) and strongest role.
      // Present only for a caller that holds a delegated assignment.
      delegatedTenantIds?: unknown;
      delegatedRole?: string;
    };
    // Normalize defensively: accept only a non-empty array of strings, lowercased. Anything else
    // (missing, wrong type, empty) collapses to undefined → the caller is treated as non-delegated.
    const delegatedTenantIds = Array.isArray(data.delegatedTenantIds)
      ? data.delegatedTenantIds.filter((t): t is string => typeof t === 'string').map((t) => t.toLowerCase())
      : undefined;
    const result: AccessCheckResult = {
      allowed: data.allowed === true,
      reason: data.allowed ? (data.accessGrant ?? 'allowed') : (data.reason ?? 'denied'),
      isGlobalAdmin: data.isGlobalAdmin === true || data.globalRole === 'GlobalAdmin',
      isGlobalReader: data.globalRole === 'GlobalReader',
      delegatedTenantIds: delegatedTenantIds && delegatedTenantIds.length > 0 ? delegatedTenantIds : undefined,
      delegatedRole: data.delegatedRole,
      // The backend reached a verdict — a deny here is a genuine authorization
      // decision (e.g. not whitelisted), not an infrastructure problem.
      infraError: false,
    };

    // Cache only the persisted fields (infraError is request-derived, always
    // false for a cached verdict — see the cache-hit path above). The delegated
    // scope MUST be cached too, or the cached follow-up loses it.
    // boundedSet caps the map so a distinct-token flood cannot exhaust memory.
    boundedSet(accessCache, cacheKey, {
      allowed: result.allowed,
      reason: result.reason,
      isGlobalAdmin: result.isGlobalAdmin,
      isGlobalReader: result.isGlobalReader,
      delegatedTenantIds: result.delegatedTenantIds,
      delegatedRole: result.delegatedRole,
      expiresAt: Date.now() + ACCESS_CACHE_TTL_MS,
    }, MAX_ACCESS_CACHE_ENTRIES);
    return result;
  } catch (err) {
    console.error('[access-guard] Backend check failed for %s:', upn, err);
    // Fail-closed: deny on backend error
    return { allowed: false, reason: 'Backend access check unavailable', isGlobalAdmin: false, isGlobalReader: false, infraError: true };
  }
}

// --- Rate limiting (sliding window) ---

interface RateEntry {
  timestamps: number[];
}

// Post-validation, per-UPN budget.
const rateBuckets = new Map<string, RateEntry>();
// Pre-validation, per-source-IP budget (cache-miss path only). Separate key
// space from rateBuckets so throttling unvalidated floods never affects a
// validated user's per-UPN budget.
const preAuthBuckets = new Map<string, RateEntry>();
// Per-source-IP budgets for the unauthenticated /oauth/* endpoints. Own map
// (keys are `${kind}:${ip}`) so an OAuth flood never burns an IP's /mcp
// pre-auth budget and vice versa.
const oauthBuckets = new Map<string, RateEntry>();

/** Drop timestamps outside the 60s window from a bucket map; delete empty buckets. */
function pruneBuckets(buckets: Map<string, RateEntry>, cutoff: number): void {
  for (const [key, entry] of buckets) {
    entry.timestamps = entry.timestamps.filter((t) => t > cutoff);
    if (entry.timestamps.length === 0) buckets.delete(key);
  }
}

// Cleanup stale entries every 5 minutes. All three maps share this interval —
// the rate buckets evict empty windows, accessCache evicts expired entries
// (the cache key includes a token hash, so cardinality is one entry per
// user×token-rotation; without proactive eviction the map would only shrink
// when a key is re-fetched or the size cap evicts it).
setInterval(() => {
  const now = Date.now();
  const cutoff = now - 60_000;
  pruneBuckets(rateBuckets, cutoff);
  pruneBuckets(preAuthBuckets, cutoff);
  pruneBuckets(oauthBuckets, cutoff);
  for (const [key, entry] of accessCache) {
    if (entry.expiresAt <= now) accessCache.delete(key);
  }
}, 5 * 60_000);

/**
 * Sliding-window check against a bucket map. Returns true when the key has
 * already used its full budget in the last 60s; otherwise records the hit.
 */
function isWindowExceeded(buckets: Map<string, RateEntry>, key: string, limit: number): boolean {
  const now = Date.now();
  const windowStart = now - 60_000;

  let entry = buckets.get(key);
  if (!entry) {
    entry = { timestamps: [] };
    // boundedSet caps the map so a distinct-key flood cannot grow it unbounded
    // between the 5-min reaper passes (evicts the oldest bucket at the cap).
    boundedSet(buckets, key, entry, MAX_RATE_BUCKET_ENTRIES);
  }
  entry.timestamps = entry.timestamps.filter((t) => t > windowStart);

  if (entry.timestamps.length >= limit) return true;
  entry.timestamps.push(now);
  return false;
}

/**
 * Per-source-IP throttle for UNvalidated, cache-missing requests. Exported for
 * unit testing. 'unknown' (no resolvable IP) shares a single bucket — acceptable
 * since the limit is generous and this is a backstop, not the primary gate.
 */
export function isPreAuthRateLimited(clientIp: string): boolean {
  return isWindowExceeded(preAuthBuckets, clientIp, PRE_AUTH_RATE_LIMIT);
}

/** Current cardinality of the rate-bucket maps. Exported for unit testing the size cap. */
export function getRateBucketSizes(): { rate: number; preAuth: number; oauth: number } {
  return { rate: rateBuckets.size, preAuth: preAuthBuckets.size, oauth: oauthBuckets.size };
}

/**
 * Best-effort client IP for the pre-auth throttle. With `app.set('trust proxy', 1)`
 * (index.ts) and the single Container Apps Envoy ingress hop, req.ip is the real
 * client address and is not spoofable by prepending X-Forwarded-For entries.
 * Falls back to the socket address, then 'unknown'.
 */
function getClientIp(req: Request): string {
  return req.ip ?? req.socket?.remoteAddress ?? 'unknown';
}

function isRateLimited(upn: string): boolean {
  return isWindowExceeded(rateBuckets, upn, RATE_LIMIT);
}

/**
 * Emit a 429 with the conventional Retry-After header, a structured + greppable
 * log line, and an actionable message. `kind` distinguishes the throttles
 * (per-source-IP pre-auth, per-UPN, per-IP /oauth/*) so a Log Analytics alert
 * can break them out and so an unexpected false-positive surfaces to the user
 * (who is told to contact the administrator).
 */
type RateLimitKind = 'pre-auth' | 'upn' | 'oauth' | 'oauth-token';

function rateLimitMessage(kind: RateLimitKind): string {
  switch (kind) {
    case 'upn':
      return 'You are sending requests faster than the per-user limit. Wait ~60s and retry. If this keeps ' +
        'happening unexpectedly, contact the MCP server administrator.';
    case 'oauth':
    case 'oauth-token':
      return 'Too many authentication requests from your network in a short window. Wait ~60s and retry. ' +
        'If this keeps happening unexpectedly, you may be sharing an IP via a corporate proxy/NAT — ' +
        'contact the MCP server administrator.';
    default:
      return 'Too many requests from your network in a short window. Wait ~60s and retry. If this keeps ' +
        'happening unexpectedly, you may be sharing an IP via a corporate proxy/NAT — contact the MCP ' +
        'server administrator.';
  }
}

function respondRateLimited(res: Response, kind: RateLimitKind, key: string, rpcMethod: string): void {
  console.error(`[mcp-auth] 429 kind=${kind} key=${key} method=${rpcMethod}`);
  res.setHeader('Retry-After', '60');
  res.status(429).json({
    error: 'Rate limit exceeded',
    retryAfterSeconds: 60,
    message: rateLimitMessage(kind),
  });
}

/**
 * Per-source-IP throttle middleware for the unauthenticated /oauth/* routes.
 * Same sliding-window machinery as the /mcp pre-auth limiter, separate bucket
 * map keyed `${kind}:${ip}` so the two surfaces cannot starve each other and
 * the strict token budget stays independent of the general one. Applied
 * per-route inside createOAuthRouter so any future /oauth endpoint must opt in
 * consciously rather than inherit silence.
 */
function makeOAuthIpLimiter(kind: 'oauth' | 'oauth-token', limit: number) {
  return (req: Request, res: Response, next: NextFunction): void => {
    const clientIp = getClientIp(req);
    if (isWindowExceeded(oauthBuckets, `${kind}:${clientIp}`, limit)) {
      respondRateLimited(res, kind, clientIp, req.path);
      return;
    }
    next();
  };
}

/** General /oauth/* budget (register/authorize/callback — local HMAC/allowlist work only). */
export const oauthRateLimit = makeOAuthIpLimiter('oauth', OAUTH_RATE_LIMIT);
/** Strict /oauth/token budget — every request triggers an outbound Entra call with the client secret. */
export const oauthTokenRateLimit = makeOAuthIpLimiter('oauth-token', OAUTH_TOKEN_RATE_LIMIT);

// --- Express middleware ---

export function accessGuard(req: Request, res: Response, next: NextFunction): void {
  const baseUrl = getPublicBaseUrl(req);
  // Point at the resource-path-specific metadata (RFC 9728 §3.1). The protected
  // resource is <base>/mcp, so its metadata lives at the /mcp-suffixed
  // well-known URL; strict clients (VS Code) validate the document's `resource`
  // against the URL they connect to and reject a base-URL mismatch.
  const resourceMetadataUrl = `${baseUrl}/.well-known/oauth-protected-resource/mcp`;

  // Lightweight auth-flow trace. The success path stays quiet (runWithCaller →
  // next), but every 401/403 logs one line so a stuck client (e.g. VS Code
  // looping unauthenticated `initialize`) is visible in container logs — the
  // happy paths otherwise emit nothing and the flow is a black box.
  const rpcMethod = (req.body as { method?: string } | undefined)?.method ?? '?';

  const authHeader = req.headers.authorization;
  if (!authHeader?.startsWith('Bearer ')) {
    console.error(`[mcp-auth] 401 no-bearer (method=${rpcMethod}) — client has not attached a token yet`);
    res.setHeader('WWW-Authenticate', `Bearer resource_metadata="${resourceMetadataUrl}"`);
    res.status(401).json({ error: 'Missing or invalid Authorization header' });
    return;
  }

  const token = authHeader.slice(7);
  const claims = extractTokenClaims(token);
  if (!claims || !claims.upn) {
    console.error(`[mcp-auth] 401 invalid-token-claims (method=${rpcMethod})`);
    res.setHeader('WWW-Authenticate', `Bearer resource_metadata="${resourceMetadataUrl}", error="invalid_token"`);
    res.status(401).json({ error: 'Invalid token: missing required claims' });
    return;
  }

  if (isTokenExpired(claims)) {
    console.error(`[mcp-auth] 401 token-expired (method=${rpcMethod}, upn=${claims.upn})`);
    res.setHeader('WWW-Authenticate', `Bearer resource_metadata="${resourceMetadataUrl}", error="invalid_token"`);
    res.status(401).json({ error: 'Token expired' });
    return;
  }

  const upn = claims.upn.toLowerCase();
  const clientIp = getClientIp(req);

  // Access check (async — calls backend, cached). Must run BEFORE the per-UPN
  // rate-limit accounting: the JWT signature is not verified here (only the
  // backend has JWKS access), so an attacker can forge an unsigned token with a
  // victim's UPN. If the per-UPN limiter incremented before validation, those
  // forgeries would burn the victim's per-minute budget and 429 their
  // legitimate calls. checkAccess delegates signature verification to the
  // backend; both allow- and deny-decisions are cached for 60s keyed on
  // UPN+tokenHash, so a forged token cannot piggyback on a legitimate user's
  // cached `allowed: true`. On a cache MISS, checkAccess additionally applies a
  // per-source-IP throttle (keyed on clientIp, not UPN) so a distinct-token
  // flood cannot drive unbounded backend calls.
  checkAccess(upn, token, clientIp)
    .then((result) => {
      if (result.rateLimited) {
        respondRateLimited(res, 'pre-auth', clientIp, rpcMethod);
        return;
      }
      if (!result.allowed) {
        if (result.infraError) {
          // Backend could not reach a verdict (unreachable / malformed). Fail
          // closed, but do NOT tell the user they are "not whitelisted" — it is
          // not their fault and would send them chasing the wrong fix.
          console.error(`[mcp-auth] 403 access-check-unavailable (method=${rpcMethod}, upn=${upn})`);
          res.status(403).json({ error: 'MCP access check unavailable', reason: result.reason });
          return;
        }
        console.error(`[mcp-auth] 403 not-whitelisted (method=${rpcMethod}, upn=${upn})`);
        // Genuine authorization denial — most commonly the user's account is not
        // on the MCP whitelist. Spell that out and tell them what to do, so they
        // can ask the MCP server owner to enable their account instead of being
        // left guessing why authentication "failed".
        res.status(403).json({
          error: 'User not enabled for MCP usage',
          reason: result.reason,
          message:
            `Your account (${upn}) is not enabled to use this Autopilot Monitor MCP server. ` +
            'Ask the MCP server owner/administrator to whitelist your account for MCP access, ' +
            'then reconnect.',
        });
        return;
      }

      // Rate-limit only validated identities. A forged JWT with someone
      // else's UPN was already rejected above; only a real, allow-listed
      // user reaches this counter.
      if (isRateLimited(upn)) {
        respondRateLimited(res, 'upn', upn, rpcMethod);
        return;
      }

      // Scope the caller context (token + platform role) to this async context
      // so concurrent sessions cannot overwrite each other on the event loop,
      // and so tools can route based on role without re-checking the JWT.
      runWithCaller(
        {
          token,
          isGlobalAdmin: result.isGlobalAdmin,
          isGlobalReader: result.isGlobalReader,
          delegatedTenantIds: result.delegatedTenantIds,
          delegatedRole: result.delegatedRole,
          // Home tenant from the JWT `tid` (lowercased). Lets a delegated (MSP) admin who is also a member
          // of their own home tenant keep reading it via the tenant-scoped member path — see client.ts
          // enforceDelegatedTenant / pickGlobalOrTenantPath. Harmless for GA / plain tenant callers.
          homeTenantId: claims.tid?.toLowerCase(),
          // UPN domain labels the synthesized home-tenant entry in list_tenants for a delegated
          // caller (the home tenant is never in the backend-bounded config/all subset).
          upn,
        },
        () => next(),
      );
    })
    .catch(() => {
      res.status(503).json({ error: 'Access check failed' });
    });
}
