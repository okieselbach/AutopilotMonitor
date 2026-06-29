/**
 * MCP access guard: validates user token, checks backend whitelist, enforces rate limits.
 *
 * Flow per request:
 *   1. Extract Bearer token → decode JWT claims (upn, exp)
 *   2. Check access: call backend /api/global/mcp-users/check (cached 5min per UPN)
 *   3. Rate limit: sliding window per UPN (default 60 req/min)
 *   4. Set token for pass-through to backend API
 */
import type { Request, Response, NextFunction } from 'express';
import crypto from 'node:crypto';
import { extractTokenClaims, isTokenExpired } from './auth.js';
import { runWithCaller } from './client.js';
import { API_BASE_URL } from './config.js';

const BASE_URL = API_BASE_URL;

/**
 * Derives the public base URL for the WWW-Authenticate header.
 */
function getPublicBaseUrl(req: Request): string {
  if (process.env.MCP_PUBLIC_URL) return process.env.MCP_PUBLIC_URL;
  const proto = (req.headers['x-forwarded-proto'] as string) ?? req.protocol;
  const host = (req.headers['x-forwarded-host'] as string) ?? req.headers.host;
  return `${proto}://${host}`;
}
const RATE_LIMIT = parseInt(process.env.MCP_RATE_LIMIT_PER_MINUTE ?? '60', 10);
const ACCESS_CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes

// --- Access check cache ---

interface AccessCacheEntry {
  allowed: boolean;
  reason: string;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
  // Delegated (scoped-global / MSP) scope. Cached alongside the platform flags so the 5-min-cached
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
}

export function buildCacheKey(upn: string, token: string): string {
  const h = crypto.createHash('sha256').update(token).digest('hex').slice(0, 32);
  return `${upn}:${h}`;
}

async function checkAccess(upn: string, token: string): Promise<AccessCheckResult> {
  const cacheKey = buildCacheKey(upn, token);
  const cached = accessCache.get(cacheKey);
  if (cached && Date.now() < cached.expiresAt) {
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

  try {
    const res = await fetch(`${BASE_URL}/api/auth/mcp`, {
      headers: { Authorization: `Bearer ${token}` },
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
    // scope MUST be cached too, or the 5-min-cached follow-up loses it.
    accessCache.set(cacheKey, {
      allowed: result.allowed,
      reason: result.reason,
      isGlobalAdmin: result.isGlobalAdmin,
      isGlobalReader: result.isGlobalReader,
      delegatedTenantIds: result.delegatedTenantIds,
      delegatedRole: result.delegatedRole,
      expiresAt: Date.now() + ACCESS_CACHE_TTL_MS,
    });
    return result;
  } catch (err) {
    console.error(`[access-guard] Backend check failed for ${upn}:`, err);
    // Fail-closed: deny on backend error
    return { allowed: false, reason: 'Backend access check unavailable', isGlobalAdmin: false, isGlobalReader: false, infraError: true };
  }
}

// --- Rate limiting (sliding window) ---

interface RateEntry {
  timestamps: number[];
}

const rateBuckets = new Map<string, RateEntry>();

// Cleanup stale entries every 5 minutes. Both maps share this interval —
// rateBuckets evicts empty windows, accessCache evicts expired entries
// (the cache key now includes a token hash, so cardinality is one entry per
// user×token-rotation; without proactive eviction the map would only shrink
// when a key is re-fetched).
setInterval(() => {
  const now = Date.now();
  const cutoff = now - 60_000;
  for (const [key, entry] of rateBuckets) {
    entry.timestamps = entry.timestamps.filter((t) => t > cutoff);
    if (entry.timestamps.length === 0) rateBuckets.delete(key);
  }
  for (const [key, entry] of accessCache) {
    if (entry.expiresAt <= now) accessCache.delete(key);
  }
}, 5 * 60_000);

function isRateLimited(upn: string): boolean {
  const now = Date.now();
  const windowStart = now - 60_000;

  let entry = rateBuckets.get(upn);
  if (!entry) {
    entry = { timestamps: [] };
    rateBuckets.set(upn, entry);
  }

  // Remove timestamps outside the window
  entry.timestamps = entry.timestamps.filter((t) => t > windowStart);

  if (entry.timestamps.length >= RATE_LIMIT) {
    return true;
  }

  entry.timestamps.push(now);
  return false;
}

// --- Express middleware ---

export function accessGuard(req: Request, res: Response, next: NextFunction): void {
  const baseUrl = getPublicBaseUrl(req);
  const resourceMetadataUrl = `${baseUrl}/.well-known/oauth-protected-resource`;

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

  // Access check (async — calls backend, cached). Must run BEFORE rate-limit
  // accounting: the JWT signature is not verified here (only the backend has
  // JWKS access), so an attacker can forge an unsigned token with a victim's
  // UPN. If rate-limit incremented before validation, those forgeries would
  // burn the victim's per-minute budget and 429 their legitimate calls.
  // checkAccess delegates signature verification to the backend; both
  // allow- and deny-decisions are cached for 5 min keyed on UPN+tokenHash,
  // so a forged token cannot piggyback on a legitimate user's cached
  // `allowed: true`, and forged-token floods cost one backend call per
  // distinct token.
  checkAccess(upn, token)
    .then((result) => {
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
        res.status(429).json({ error: 'Rate limit exceeded', retryAfterSeconds: 60 });
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
        },
        () => next(),
      );
    })
    .catch(() => {
      res.status(503).json({ error: 'Access check failed' });
    });
}
