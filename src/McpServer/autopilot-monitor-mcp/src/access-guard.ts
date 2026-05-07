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
import { extractTokenClaims, isTokenExpired } from './auth.js';
import { runWithCaller } from './client.js';

const BASE_URL = process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net';

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
  expiresAt: number;
}

const accessCache = new Map<string, AccessCacheEntry>();

interface AccessCheckResult {
  allowed: boolean;
  reason: string;
  isGlobalAdmin: boolean;
}

async function checkAccess(upn: string, token: string): Promise<AccessCheckResult> {
  const cached = accessCache.get(upn);
  if (cached && Date.now() < cached.expiresAt) {
    return { allowed: cached.allowed, reason: cached.reason, isGlobalAdmin: cached.isGlobalAdmin };
  }

  try {
    const res = await fetch(`${BASE_URL}/api/auth/mcp`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    const text = await res.text();
    if (!text) {
      console.error(`[access-guard] Backend returned empty body for ${upn} (status=${res.status})`);
      return { allowed: false, reason: `Backend returned ${res.status} with empty body`, isGlobalAdmin: false };
    }

    const data = JSON.parse(text) as {
      allowed: boolean;
      reason?: string;
      accessGrant?: string;
      isGlobalAdmin?: boolean;
    };
    const result: AccessCheckResult = {
      allowed: data.allowed === true,
      reason: data.allowed ? (data.accessGrant ?? 'allowed') : (data.reason ?? 'denied'),
      isGlobalAdmin: data.isGlobalAdmin === true,
    };

    accessCache.set(upn, { ...result, expiresAt: Date.now() + ACCESS_CACHE_TTL_MS });
    return result;
  } catch (err) {
    console.error(`[access-guard] Backend check failed for ${upn}:`, err);
    // Fail-closed: deny on backend error
    return { allowed: false, reason: 'Backend access check unavailable', isGlobalAdmin: false };
  }
}

// --- Rate limiting (sliding window) ---

interface RateEntry {
  timestamps: number[];
}

const rateBuckets = new Map<string, RateEntry>();

// Cleanup stale entries every 5 minutes
setInterval(() => {
  const cutoff = Date.now() - 60_000;
  for (const [key, entry] of rateBuckets) {
    entry.timestamps = entry.timestamps.filter((t) => t > cutoff);
    if (entry.timestamps.length === 0) rateBuckets.delete(key);
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

  const authHeader = req.headers.authorization;
  if (!authHeader?.startsWith('Bearer ')) {
    res.setHeader('WWW-Authenticate', `Bearer resource_metadata="${resourceMetadataUrl}"`);
    res.status(401).json({ error: 'Missing or invalid Authorization header' });
    return;
  }

  const token = authHeader.slice(7);
  const claims = extractTokenClaims(token);
  if (!claims || !claims.upn) {
    res.setHeader('WWW-Authenticate', `Bearer resource_metadata="${resourceMetadataUrl}", error="invalid_token"`);
    res.status(401).json({ error: 'Invalid token: missing required claims' });
    return;
  }

  if (isTokenExpired(claims)) {
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
  // allow- and deny-decisions are cached for 5min per UPN, so forged-token
  // floods cost at most one backend call per fake UPN.
  checkAccess(upn, token)
    .then((result) => {
      if (!result.allowed) {
        res.status(403).json({ error: 'MCP access denied', reason: result.reason });
        return;
      }

      // Rate-limit only validated identities. A forged JWT with someone
      // else's UPN was already rejected above; only a real, allow-listed
      // user reaches this counter.
      if (isRateLimited(upn)) {
        res.status(429).json({ error: 'Rate limit exceeded', retryAfterSeconds: 60 });
        return;
      }

      // Scope the caller context (token + GA status) to this async context
      // so concurrent sessions cannot overwrite each other on the event loop,
      // and so tools can route based on role without re-checking the JWT.
      runWithCaller({ token, isGlobalAdmin: result.isGlobalAdmin }, () => next());
    })
    .catch(() => {
      res.status(503).json({ error: 'Access check failed' });
    });
}
