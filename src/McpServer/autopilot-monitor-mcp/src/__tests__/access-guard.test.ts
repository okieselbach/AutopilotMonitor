/**
 * Unit tests for the MCP access guard — the security-critical request gate
 * (review findings H1 / tests-1 / tests-2).
 *
 * accessGuard decides 401 / 403 / 429 and the next()-allow path; checkAccess
 * (exercised through it) is the fail-closed backend check. The code is correct
 * today — these tests exist purely to catch a regression that would open the
 * server: e.g. inverting `!result.allowed`, moving next() into the fail branch,
 * or dropping the fail-closed default on a backend error. With scale-to-zero,
 * "backend unreachable" is a routine condition, not an edge case.
 *
 * No live backend or real token is needed: fetch is stubbed and JWTs are
 * hand-built (the proxy does not verify signatures — the backend does).
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { Request, Response, NextFunction } from 'express';

// RATE_LIMIT is read from the environment at module import. Set a low cap so
// the 429 path is cheap to drive, BEFORE the dynamic import resolves.
process.env.MCP_RATE_LIMIT_PER_MINUTE = '2';

const { accessGuard } = await import('../access-guard.js');
// Same ESM singleton the guard uses internally — so the context set by
// runWithCaller inside next() is observable here.
const { isGlobalAdmin, hasGlobalScope, getCurrentToken, isDelegated, getDelegatedTenantIds } =
  await import('../client.js');

/** Build an unsigned JWT-shaped token carrying the given claims. */
function makeToken(claims: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
  return `${header}.${payload}.sig`;
}

const futureExp = () => Math.floor(Date.now() / 1000) + 3600;

/** A valid (well-formed, unexpired) token for the given upn. */
function validToken(upn: string): string {
  return makeToken({ upn, exp: futureExp() });
}

function mockReq(authHeader?: string): Request {
  return {
    headers: { host: 'mcp.example.com', ...(authHeader ? { authorization: authHeader } : {}) },
    protocol: 'https',
  } as unknown as Request;
}

interface CapturedRes {
  res: Response;
  statusCode: number | null;
  body: unknown;
  headers: Record<string, string>;
}

function mockRes(): CapturedRes {
  const captured: CapturedRes = { res: null as unknown as Response, statusCode: null, body: undefined, headers: {} };
  const res: Partial<Response> = {
    setHeader(name: string, value: string | number | readonly string[]) {
      captured.headers[name] = String(value);
      return this as Response;
    },
    status(code: number) {
      captured.statusCode = code;
      return this as Response;
    },
    json(payload: unknown) {
      captured.body = payload;
      return this as Response;
    },
  };
  captured.res = res as Response;
  return captured;
}

interface GuardOutcome {
  nextCalled: boolean;
  /** caller context captured INSIDE next() (only meaningful when nextCalled) */
  ctx?: { token: string | undefined; ga: boolean; scope?: boolean; delegated?: boolean; managed?: string[] };
  status: number | null;
  body: unknown;
  headers: Record<string, string>;
}

/**
 * Drives accessGuard and resolves once it either sends a response (res.json)
 * or calls next(). The guard's work is async (checkAccess), so we await.
 */
function runGuard(req: Request): Promise<GuardOutcome> {
  return new Promise<GuardOutcome>((resolve, reject) => {
    const cap = mockRes();
    let settled = false;
    const done = (o: GuardOutcome) => {
      if (settled) return;
      settled = true;
      resolve(o);
    };
    // Capture the response when json() is called.
    const origJson = cap.res.json.bind(cap.res);
    cap.res.json = ((payload: unknown) => {
      const r = origJson(payload);
      done({ nextCalled: false, status: cap.statusCode, body: cap.body, headers: cap.headers });
      return r;
    }) as Response['json'];

    const next: NextFunction = () => {
      // Read the per-request caller context from inside the async scope.
      done({
        nextCalled: true,
        ctx: {
          token: getCurrentToken(),
          ga: isGlobalAdmin(),
          scope: hasGlobalScope(),
          delegated: isDelegated(),
          managed: getDelegatedTenantIds(),
        },
        status: cap.statusCode,
        body: cap.body,
        headers: cap.headers,
      });
    };

    try {
      accessGuard(req, cap.res, next);
    } catch (err) {
      reject(err);
    }
    // Safety net so a hung guard fails the test rather than the runner timing out.
    setTimeout(() => done({ nextCalled: false, status: cap.statusCode, body: cap.body, headers: cap.headers }), 2_000);
  });
}

/** Stub global fetch to return a backend access-check response. */
function stubBackend(opts: { status?: number; body?: unknown; rawText?: string; reject?: boolean }) {
  const fn = vi.fn(async () => {
    if (opts.reject) throw new Error('network down');
    const text = opts.rawText !== undefined ? opts.rawText : JSON.stringify(opts.body ?? {});
    return { status: opts.status ?? 200, text: async () => text } as unknown as Response;
  });
  vi.stubGlobal('fetch', fn);
  return fn;
}

let upnCounterSeed = 0;
/** Unique upn per test → isolates the module-level access cache + rate buckets. */
function uniqueUpn(): string {
  upnCounterSeed += 1;
  return `user${upnCounterSeed}@contoso.com`;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('accessGuard — 401 (authentication failures)', () => {
  beforeEach(() => stubBackend({ body: { allowed: true } }));

  it('rejects a request with no Authorization header', async () => {
    const out = await runGuard(mockReq());
    expect(out.nextCalled).toBe(false);
    expect(out.status).toBe(401);
    expect(out.headers['WWW-Authenticate']).toContain('oauth-protected-resource');
  });

  it('rejects a non-Bearer Authorization header', async () => {
    const out = await runGuard(mockReq('Basic dXNlcjpwYXNz'));
    expect(out.status).toBe(401);
  });

  it('rejects a token missing the required upn claim', async () => {
    const out = await runGuard(mockReq(`Bearer ${makeToken({ exp: futureExp() })}`));
    expect(out.status).toBe(401);
    expect(out.headers['WWW-Authenticate']).toContain('error="invalid_token"');
  });

  it('rejects a malformed (non-decodable) token', async () => {
    const out = await runGuard(mockReq('Bearer not-a-jwt'));
    expect(out.status).toBe(401);
  });

  it('rejects an expired token before ever calling the backend', async () => {
    const fetchFn = stubBackend({ body: { allowed: true } });
    const expired = makeToken({ upn: 'alice@contoso.com', exp: Math.floor(Date.now() / 1000) - 100 });
    const out = await runGuard(mockReq(`Bearer ${expired}`));
    expect(out.status).toBe(401);
    expect(out.headers['WWW-Authenticate']).toContain('error="invalid_token"');
    expect(fetchFn).not.toHaveBeenCalled();
  });
});

describe('accessGuard — 403 (authorization + fail-closed)', () => {
  it('denies when the backend reports allowed:false and surfaces the reason', async () => {
    stubBackend({ body: { allowed: false, reason: 'not on the MCP whitelist' } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(false);
    expect(out.status).toBe(403);
    expect((out.body as { reason: string }).reason).toBe('not on the MCP whitelist');
  });

  it('gives a genuine whitelist denial an actionable message naming the user', async () => {
    const upn = uniqueUpn();
    stubBackend({ body: { allowed: false, reason: 'User not enabled for MCP usage' } });
    const out = await runGuard(mockReq(`Bearer ${validToken(upn)}`));
    expect(out.status).toBe(403);
    const body = out.body as { error: string; message: string };
    expect(body.error).toBe('User not enabled for MCP usage');
    // The message must name the account and point at the fix (get whitelisted).
    expect(body.message).toContain(upn);
    expect(body.message).toMatch(/whitelist/i);
  });

  it('does NOT label an infrastructure failure as a whitelist problem', async () => {
    // A cold/unreachable backend must not tell the user they are "not enabled".
    stubBackend({ reject: true });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.status).toBe(403);
    const body = out.body as { error: string; message?: string };
    expect(body.error).not.toBe('User not enabled for MCP usage');
    expect(body.message).toBeUndefined();
  });

  it('fail-closed: denies when the backend fetch throws (tests-2)', async () => {
    // scale-to-zero ⇒ a cold/unreachable backend is routine. checkAccess must
    // resolve allowed:false, never allowed:true.
    stubBackend({ reject: true });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(false);
    expect(out.status).toBe(403);
    expect((out.body as { reason: string }).reason).toMatch(/unavailable/i);
  });

  it('fail-closed: denies when the backend returns an empty body (tests-2)', async () => {
    stubBackend({ status: 502, rawText: '' });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(false);
    expect(out.status).toBe(403);
    expect((out.body as { reason: string }).reason).toMatch(/empty body/i);
  });

  it('fail-closed: treats allowed values other than boolean true as denied', async () => {
    // `allowed: "true"` (string) or `allowed: 1` must NOT be coerced to allow.
    stubBackend({ body: { allowed: 'true' } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.status).toBe(403);
  });
});

describe('accessGuard — allow path', () => {
  it('calls next() exactly once and scopes the caller context (token + GA flag)', async () => {
    const upn = uniqueUpn();
    stubBackend({ body: { allowed: true, accessGrant: 'whitelisted', isGlobalAdmin: true } });
    const token = validToken(upn);
    const out = await runGuard(mockReq(`Bearer ${token}`));

    expect(out.nextCalled).toBe(true);
    expect(out.status).toBeNull(); // no response sent on the allow path
    expect(out.ctx).toEqual({ token, ga: true, scope: true, delegated: false, managed: undefined });
  });

  it('propagates isGlobalAdmin:false through the caller context', async () => {
    stubBackend({ body: { allowed: true, isGlobalAdmin: false } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(true);
    expect(out.ctx?.ga).toBe(false);
    expect(out.ctx?.scope).toBe(false);
  });

  it('maps globalRole=GlobalReader to global scope WITHOUT global-admin (write) status', async () => {
    stubBackend({ body: { allowed: true, accessGrant: 'GlobalReader', globalRole: 'GlobalReader' } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(true);
    expect(out.ctx?.ga).toBe(false);   // not a Global Admin
    expect(out.ctx?.scope).toBe(true); // but has cross-tenant read scope
  });

  it('maps globalRole=GlobalAdmin to both global scope and global-admin status', async () => {
    stubBackend({ body: { allowed: true, accessGrant: 'GlobalAdmin', globalRole: 'GlobalAdmin' } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.nextCalled).toBe(true);
    expect(out.ctx?.ga).toBe(true);
    expect(out.ctx?.scope).toBe(true);
  });
});

describe('accessGuard — delegated (MSP) scope', () => {
  it('threads delegatedTenantIds (lowercased) into the caller context, without platform scope', async () => {
    stubBackend({
      body: {
        allowed: true,
        accessGrant: 'DelegatedAdmin',
        delegatedTenantIds: ['AAAA-1111', 'bbbb-2222'],
        delegatedRole: 'DelegatedReader',
      },
    });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));

    expect(out.nextCalled).toBe(true);
    expect(out.ctx?.ga).toBe(false);
    expect(out.ctx?.scope).toBe(false);    // delegated is NOT platform scope
    expect(out.ctx?.delegated).toBe(true);
    expect(out.ctx?.managed).toEqual(['aaaa-1111', 'bbbb-2222']);
  });

  it('keeps the delegated scope on the cached (second) request — one backend call', async () => {
    const token = validToken(uniqueUpn());
    const fetchFn = stubBackend({
      body: { allowed: true, accessGrant: 'DelegatedAdmin', delegatedTenantIds: ['cccc-3333'] },
    });

    const first = await runGuard(mockReq(`Bearer ${token}`));
    const second = await runGuard(mockReq(`Bearer ${token}`));

    expect(fetchFn).toHaveBeenCalledTimes(1);          // served from cache
    expect(first.ctx?.managed).toEqual(['cccc-3333']);
    expect(second.ctx?.managed).toEqual(['cccc-3333']); // scope survived caching
    expect(second.ctx?.delegated).toBe(true);
  });

  it('treats an empty delegatedTenantIds array as non-delegated', async () => {
    stubBackend({ body: { allowed: true, delegatedTenantIds: [] } });
    const out = await runGuard(mockReq(`Bearer ${validToken(uniqueUpn())}`));
    expect(out.ctx?.delegated).toBe(false);
    expect(out.ctx?.managed).toBeUndefined();
  });
});

describe('accessGuard — 429 (rate limit) and caching', () => {
  it('429s once the per-minute budget is exhausted (cap=2)', async () => {
    const upn = uniqueUpn();
    const token = validToken(upn);
    stubBackend({ body: { allowed: true } });

    const first = await runGuard(mockReq(`Bearer ${token}`));
    const second = await runGuard(mockReq(`Bearer ${token}`));
    const third = await runGuard(mockReq(`Bearer ${token}`));

    expect(first.nextCalled).toBe(true);
    expect(second.nextCalled).toBe(true);
    expect(third.nextCalled).toBe(false);
    expect(third.status).toBe(429);
    expect((third.body as { retryAfterSeconds: number }).retryAfterSeconds).toBe(60);
  });

  it('serves the cached access decision (one backend call across repeat requests)', async () => {
    const token = validToken(uniqueUpn());
    const fetchFn = stubBackend({ body: { allowed: true } });

    await runGuard(mockReq(`Bearer ${token}`));
    await runGuard(mockReq(`Bearer ${token}`));

    // Second request hits the 60s UPN+tokenHash cache — no second backend call.
    expect(fetchFn).toHaveBeenCalledTimes(1);
  });

  it('does not let a forged token reuse a legitimate token cache entry', async () => {
    // Same upn, two different token strings ⇒ distinct cache keys ⇒ two backend
    // calls. This is the F1 guarantee, observed end-to-end through the guard.
    const upn = uniqueUpn();
    const fetchFn = stubBackend({ body: { allowed: true } });

    await runGuard(mockReq(`Bearer ${makeToken({ upn, exp: futureExp(), jti: 'legit' })}`));
    await runGuard(mockReq(`Bearer ${makeToken({ upn, exp: futureExp(), jti: 'forged' })}`));

    expect(fetchFn).toHaveBeenCalledTimes(2);
  });
});
