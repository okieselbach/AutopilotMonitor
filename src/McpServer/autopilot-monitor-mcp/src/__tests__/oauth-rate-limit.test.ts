/**
 * Regression tests for the 2026-07-02 review finding R-3: the unauthenticated
 * /oauth/* routes had NO rate limit (accessGuard covers /mcp only), making
 * /oauth/token an anonymous amplifier for outbound Entra calls carrying the
 * client secret — a flood would get the app registration itself throttled by
 * Entra (org-wide auth DoS with zero credentials).
 *
 * Both budgets are pinned tiny BEFORE oauth.js loads (access-guard reads the
 * env at module init). Own file so the tiny limits cannot bleed into other
 * suites. No network is reached: every request fails validation (unsupported
 * grant / missing code) before the handler would call Entra, and the limiter
 * runs before the handler anyway.
 */
import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import type { AddressInfo } from 'node:net';

process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
process.env.MCP_OAUTH_RATE_LIMIT_PER_MINUTE = '3';
process.env.MCP_OAUTH_TOKEN_RATE_LIMIT_PER_MINUTE = '2';
const { createOAuthRouter } = await import('../oauth.js');

let server: Server;
let baseUrl: string;

beforeAll(async () => {
  const app = express();
  app.use(express.json());
  app.use(express.urlencoded({ extended: true }));
  app.use(createOAuthRouter());
  await new Promise<void>((resolve) => {
    server = app.listen(0, '127.0.0.1', () => resolve());
  });
  const { port } = server.address() as AddressInfo;
  baseUrl = `http://127.0.0.1:${port}`;
});

afterAll(async () => {
  await new Promise<void>((resolve) => server.close(() => resolve()));
});

/** POST /oauth/token with an unsupported grant — 400s in the handler, never reaches Entra. */
async function token() {
  const res = await fetch(`${baseUrl}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'client_credentials' }).toString(),
  });
  return res;
}

/** GET /oauth/callback with no params — 400s in the handler (missing code). */
async function callback() {
  return fetch(`${baseUrl}/oauth/callback`);
}

describe('/oauth/* per-source-IP rate limiting (R-3)', () => {
  it('throttles /oauth/token after its budget, independently of the general /oauth budget', async () => {
    // Budget pinned to 2: the first two reach the handler (400), the third is throttled.
    const statuses = [(await token()).status, (await token()).status];
    const third = await token();
    statuses.push(third.status);
    expect(statuses).toEqual([400, 400, 429]);

    expect(third.headers.get('Retry-After')).toBe('60');
    const body = (await third.json()) as Record<string, unknown>;
    expect(body.error).toBe('Rate limit exceeded');
    expect(body.retryAfterSeconds).toBe(60);
    expect(String(body.message)).toMatch(/authentication requests/);

    // Separate bucket: exhausting the token budget must NOT consume the
    // general /oauth budget — callback still reaches its handler (400).
    expect((await callback()).status).toBe(400);
  });

  it('throttles the general /oauth routes after their shared budget', async () => {
    // Budget pinned to 3; the independence probe above already spent 1.
    expect((await callback()).status).toBe(400);
    expect((await callback()).status).toBe(400);
    const throttled = await callback();
    expect(throttled.status).toBe(429);
    expect(throttled.headers.get('Retry-After')).toBe('60');

    // And the throttled general budget must not leak back into the token
    // bucket's key space: token stays at ITS 429 (already exhausted above),
    // proving the two 429s come from distinct buckets rather than one shared
    // counter (which would have tripped far earlier).
    expect((await token()).status).toBe(429);
  });
});
