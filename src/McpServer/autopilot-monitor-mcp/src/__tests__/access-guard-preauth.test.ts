/**
 * Regression test for the 2026-06-30 review finding: cached DENY verdicts must
 * also count against the per-source-IP pre-auth limiter.
 *
 * Deny verdicts are cached for 5 min. Before the fix, the cache-hit path
 * returned the cached 403 BEFORE the pre-auth check ran, so an attacker could
 * repeat one forged/denied token and farm unlimited cheap 403s for the whole
 * TTL without ever being throttled. The fix counts cached denials against the
 * per-IP budget (→ 429 once exhausted) while keeping cached ALLOWs free.
 *
 * Pre-auth limit is pinned to 1/min BEFORE access-guard loads (it reads the env
 * at module init). Own file so this tiny limit cannot bleed into other suites.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import type { Request, Response, NextFunction } from 'express';

process.env.MCP_PRE_AUTH_RATE_LIMIT_PER_MINUTE = '1';
const { accessGuard } = await import('../access-guard.js');

function makeToken(claims: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
  return `${header}.${payload}.sig`;
}
const validToken = (upn: string) => makeToken({ upn, exp: Math.floor(Date.now() / 1000) + 3600 });

/** Mock request carrying a Bearer token and a fixed client IP (→ getClientIp). */
function mockReq(token: string, ip: string): Request {
  return {
    headers: { host: 'mcp.example.com', authorization: `Bearer ${token}` },
    protocol: 'https',
    ip,
    socket: { remoteAddress: ip },
  } as unknown as Request;
}

interface Outcome { nextCalled: boolean; status: number | null; }

function runGuard(req: Request): Promise<Outcome> {
  return new Promise<Outcome>((resolve) => {
    let statusCode: number | null = null;
    let settled = false;
    const done = (o: Outcome) => { if (!settled) { settled = true; resolve(o); } };
    const res: Partial<Response> = {
      setHeader() { return this as Response; },
      status(code: number) { statusCode = code; return this as Response; },
      json() { done({ nextCalled: false, status: statusCode }); return this as Response; },
      on() { return this as Response; },
    };
    const next: NextFunction = () => done({ nextCalled: true, status: statusCode });
    accessGuard(req, res as Response, next);
    setTimeout(() => done({ nextCalled: false, status: statusCode }), 2_000);
  });
}

function stubBackend(body: unknown): ReturnType<typeof vi.fn> {
  const fn = vi.fn(async () => ({ status: 200, text: async () => JSON.stringify(body) } as unknown as Response));
  vi.stubGlobal('fetch', fn);
  return fn;
}

afterEach(() => vi.unstubAllGlobals());

describe('pre-auth limiter — cached deny path', () => {
  it('throttles repeated cached DENY to 429 after the budget (one backend call)', async () => {
    const fetchFn = stubBackend({ allowed: false, reason: 'not on the MCP whitelist' });
    const req = mockReq(validToken('attacker@evil.example'), '198.51.100.7');

    const r1 = await runGuard(req); // miss → backend → cached deny
    const r2 = await runGuard(req); // cached deny, budget exhausted → 429
    const r3 = await runGuard(req);
    const r4 = await runGuard(req);

    expect(fetchFn).toHaveBeenCalledTimes(1); // backend hit once; rest served from cache
    expect(r1.status).toBe(403);              // first denial surfaces the real 403
    expect([r2.status, r3.status, r4.status]).toEqual([429, 429, 429]);
  });

  it('does NOT charge cached ALLOW against the pre-auth budget', async () => {
    // limit=1: if a cached allow consumed pre-auth budget, the 2nd request would
    // 429. It must not — legitimate users ride the cache fast-path for free.
    const fetchFn = stubBackend({ allowed: true, accessGrant: 'whitelisted' });
    const req = mockReq(validToken('legit@contoso.com'), '198.51.100.8');

    const outs = [await runGuard(req), await runGuard(req), await runGuard(req), await runGuard(req)];

    expect(fetchFn).toHaveBeenCalledTimes(1);
    expect(outs.every((o) => o.nextCalled)).toBe(true);
    expect(outs.every((o) => o.status === null)).toBe(true); // no 429/403 sent
  });
});
