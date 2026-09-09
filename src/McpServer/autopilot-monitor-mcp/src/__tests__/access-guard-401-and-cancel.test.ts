/**
 * Two request-gate contracts (2026-09-10):
 *  - a token the backend refuses (401) is answered 401 + WWW-Authenticate error="invalid_token"
 *    (spec: invalid or expired tokens MUST receive 401), cached like a deny, never 403 "not enabled";
 *  - the caller context carries a cancellation signal that fires when the client closes the request
 *    before the response is written (Streamable HTTP: closing the response stream cancels the request).
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import type { Request, Response, NextFunction } from 'express';
import { accessGuard } from '../access-guard.js';
import { getCallerSignal } from '../client.js';

function makeToken(claims: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
  return `${header}.${payload}.sig`;
}

let seed = 0;
/** A fresh, unexpired token per call — the guard caches verdicts per UPN+token. */
function request(): Request {
  seed += 1;
  const token = makeToken({ upn: `user${seed}@contoso.com`, exp: Math.floor(Date.now() / 1000) + 3600 });
  return {
    headers: { host: 'mcp.example.com', authorization: `Bearer ${token}` },
    protocol: 'https',
    body: { jsonrpc: '2.0', method: 'tools/list', id: 1 },
  } as unknown as Request;
}

interface Outcome {
  nextCalled: boolean;
  status: number | null;
  body: unknown;
  headers: Record<string, string>;
  /** The caller context's signal, read INSIDE next(). */
  signal?: AbortSignal;
  res: { writableFinished: boolean };
  /** Emits the response's 'close' event the way Node does when the socket goes away. */
  close: () => void;
}

function run(req: Request): Promise<Outcome> {
  return new Promise((resolve) => {
    const headers: Record<string, string> = {};
    const closeHandlers: Array<() => void> = [];
    let status: number | null = null;
    let settled = false;
    const state = { writableFinished: false };
    const close = () => closeHandlers.forEach((h) => h());
    const finish = (o: Omit<Outcome, 'res' | 'close'>) => {
      if (settled) return;
      settled = true;
      resolve({ ...o, res: state, close });
    };
    const res = {
      get writableFinished() { return state.writableFinished; },
      setHeader(name: string, value: string) { headers[name] = String(value); return this; },
      status(code: number) { status = code; return this; },
      json(payload: unknown) { finish({ nextCalled: false, status, body: payload, headers }); return this; },
      on(event: string, handler: () => void) { if (event === 'close') closeHandlers.push(handler); return this; },
    } as unknown as Response;
    const next: NextFunction = () => finish({ nextCalled: true, status, body: undefined, headers, signal: getCallerSignal() });
    accessGuard(req, res, next);
    setTimeout(() => finish({ nextCalled: false, status, body: undefined, headers }), 2_000);
  });
}

function stubBackend(status: number, body: unknown) {
  const fn = vi.fn(async () => ({ status, text: async () => JSON.stringify(body) } as unknown as Response));
  vi.stubGlobal('fetch', fn);
  return fn;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('accessGuard — a backend 401 becomes 401 + challenge', () => {
  it('answers 401 with error="invalid_token" and the backend reason, not 403', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    stubBackend(401, { error: 'Token signature is invalid', code: 'unauthorized' });
    const out = await run(request());
    expect(out.nextCalled).toBe(false);
    expect(out.status).toBe(401);
    expect(out.headers['WWW-Authenticate']).toContain('error="invalid_token"');
    expect(out.headers['WWW-Authenticate']).toContain('oauth-protected-resource/mcp');
    expect(out.body).toMatchObject({ error: 'Invalid token', reason: 'Token signature is invalid' });
  });

  it('caches the rejection per token: a replay costs no second backend call and stays 401', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const fetchMock = stubBackend(401, { error: 'Token expired' });
    const req = request();
    const first = await run(req);
    const second = await run(req);
    expect(first.status).toBe(401);
    expect(second.status).toBe(401);
    expect(second.headers['WWW-Authenticate']).toContain('error="invalid_token"');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('keeps a 401 without an envelope body at 401 with a generic reason', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    stubBackend(401, 'Unauthorized');
    const out = await run(request());
    expect(out.status).toBe(401);
    expect(out.body).toMatchObject({ reason: 'The backend rejected the token' });
  });

  it('keeps a genuine authorization deny (200, allowed:false) at 403 without a challenge', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    stubBackend(200, { allowed: false, upn: 'x', accessGrant: '', reason: 'not on the MCP allowlist' });
    const out = await run(request());
    expect(out.status).toBe(403);
    expect(out.headers['WWW-Authenticate']).toBeUndefined();
  });
});

describe('accessGuard — cancellation signal', () => {
  it('hands the caller context a signal that fires when the client closes the request early', async () => {
    stubBackend(200, { allowed: true, upn: 'x', accessGrant: 'ok' });
    const out = await run(request());
    expect(out.nextCalled).toBe(true);
    expect(out.signal).toBeInstanceOf(AbortSignal);
    expect(out.signal?.aborted).toBe(false);
    out.close();
    expect(out.signal?.aborted).toBe(true);
  });

  it('does not fire when the response completed normally before the socket closed', async () => {
    stubBackend(200, { allowed: true, upn: 'x', accessGrant: 'ok' });
    const out = await run(request());
    out.res.writableFinished = true;
    out.close();
    expect(out.signal?.aborted).toBe(false);
  });
});
