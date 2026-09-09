/**
 * Origin gate on /mcp (spec 2026-07-28 Streamable HTTP "Security & Endpoint": a present, invalid
 * Origin MUST be answered 403). Non-browser clients send no Origin and pass; only the server's own
 * origin passes with one; everything else — foreign hosts, scheme downgrades, suffix tricks, the
 * opaque `null` — is refused before any token work.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import type { Request, Response } from 'express';
import { isAllowedOrigin, originGuard } from '../origin-guard.js';

const SELF = 'https://mcp.example.com';

describe('isAllowedOrigin', () => {
  it('passes a request without Origin (every non-browser MCP client)', () => {
    expect(isAllowedOrigin(undefined, SELF)).toBe(true);
  });

  it("accepts the server's own origin, port-normalised and case-insensitive on the host", () => {
    expect(isAllowedOrigin('https://mcp.example.com', SELF)).toBe(true);
    expect(isAllowedOrigin('https://MCP.example.com:443', SELF)).toBe(true);
  });

  it.each([
    'https://evil.example',
    'http://mcp.example.com',
    'https://mcp.example.com.evil.example',
    'https://mcp.example.com:8443',
    'null',
    'not a url',
    '',
  ])('rejects a present, invalid Origin: %j', (origin) => {
    expect(isAllowedOrigin(origin, SELF)).toBe(false);
  });
});

describe('originGuard middleware', () => {
  afterEach(() => vi.restoreAllMocks());

  function run(origin?: string) {
    const req = {
      headers: { host: 'mcp.example.com', ...(origin !== undefined ? { origin } : {}) },
      protocol: 'https',
      body: { jsonrpc: '2.0', method: 'tools/list', id: 1 },
    } as unknown as Request;
    let status: number | null = null;
    let body: unknown;
    const res = {
      status(code: number) { status = code; return this; },
      json(payload: unknown) { body = payload; return this; },
    } as unknown as Response;
    const next = vi.fn();
    originGuard(req, res, next);
    return { status: status as number | null, body, next };
  }

  it('lets a request without Origin through untouched', () => {
    const { status, next } = run();
    expect(next).toHaveBeenCalledOnce();
    expect(status).toBeNull();
  });

  it("lets the server's own origin through", () => {
    const { status, next } = run('https://mcp.example.com');
    expect(next).toHaveBeenCalledOnce();
    expect(status).toBeNull();
  });

  it('answers 403 with a JSON-RPC error without id for a foreign origin, and logs it', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { status, body, next } = run('https://attacker.example');
    expect(next).not.toHaveBeenCalled();
    expect(status).toBe(403);
    expect(body).toEqual({ jsonrpc: '2.0', error: { code: -32600, message: 'Origin not allowed' }, id: null });
    expect(log).toHaveBeenCalledWith(expect.stringContaining('[mcp-auth] 403 origin-rejected origin=https://attacker.example method=tools/list'));
  });

  it('treats the opaque "null" origin as present and invalid', () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const { status, next } = run('null');
    expect(next).not.toHaveBeenCalled();
    expect(status).toBe(403);
  });

  it('renders a hostile origin value log-safe (control characters replaced, length capped)', () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    run('https://x.example/\r\nINJECTED' + 'a'.repeat(500));
    const line = log.mock.calls[0][0] as string;
    expect(line).not.toContain('\n');
    expect(line.length).toBeLessThan(320);
  });
});
