/**
 * The Origin gate in front of the dual-era MCP entry, driven over loopback HTTP the way index.ts
 * mounts it (express.json → originGuard → handler): a foreign Origin is refused with 403 on a 2025
 * initialize and on a 2026-07-28 request alike; the server's own origin and no Origin pass.
 */
import { describe, it, expect, beforeAll, afterAll, vi, afterEach } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import { createMcpRequestHandler } from '../mcp-http.js';
import { createServerForCaller, type ServerDeps } from '../mcp-server-factory.js';
import { runWithCaller } from '../client.js';
import { originGuard } from '../origin-guard.js';

const DEPS: ServerDeps = { serverVersion: '0.0.0-test', knowledgeBase: undefined, eventTypeIndex: undefined, docs: undefined };

let httpServer: Server;
let url: URL;

beforeAll(async () => {
  const app = express();
  app.use('/mcp', express.json({ limit: '256kb' }));
  app.use('/mcp', originGuard);
  const handler = createMcpRequestHandler(() => createServerForCaller(DEPS), {});
  app.post('/mcp', (req, res) =>
    runWithCaller({ token: 'test-token', isGlobalAdmin: true, isGlobalReader: false, upn: 'caller@contoso.com' }, () => handler(req, res)),
  );
  await new Promise<void>((resolve) => {
    httpServer = app.listen(0, '127.0.0.1', () => resolve());
  });
  const addr = httpServer.address();
  if (typeof addr !== 'object' || addr === null) throw new Error('no address');
  url = new URL(`http://127.0.0.1:${addr.port}/mcp`);
});

afterAll(async () => {
  await new Promise<void>((resolve) => httpServer.close(() => resolve()));
});

afterEach(() => vi.restoreAllMocks());

const LEGACY_INITIALIZE = {
  jsonrpc: '2.0',
  id: 1,
  method: 'initialize',
  params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'origin-test', version: '1.0.0' } },
};

const MODERN_TOOLS_LIST = {
  jsonrpc: '2.0',
  id: 1,
  method: 'tools/list',
  params: {
    _meta: {
      'io.modelcontextprotocol/protocolVersion': '2026-07-28',
      'io.modelcontextprotocol/clientInfo': { name: 'origin-test', version: '1.0.0' },
      'io.modelcontextprotocol/clientCapabilities': {},
    },
  },
};

function post(body: unknown, headers: Record<string, string>): Promise<Response> {
  return fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream', ...headers },
    body: JSON.stringify(body),
  });
}

describe('Origin gate on /mcp — both protocol eras', () => {
  it('refuses a foreign Origin on a 2025 initialize: 403 + JSON-RPC error without id, and a log line', async () => {
    const log = vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = await post(LEGACY_INITIALIZE, { origin: 'https://attacker.example' });
    expect(res.status).toBe(403);
    expect(await res.json()).toMatchObject({ jsonrpc: '2.0', error: { code: -32600 }, id: null });
    expect(log).toHaveBeenCalledWith(expect.stringContaining('403 origin-rejected origin=https://attacker.example method=initialize'));
  });

  it('refuses a foreign Origin on a 2026-07-28 request', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const res = await post(MODERN_TOOLS_LIST, {
      origin: 'https://attacker.example',
      'mcp-protocol-version': '2026-07-28',
      'mcp-method': 'tools/list',
    });
    expect(res.status).toBe(403);
  });

  it("serves the server's own origin", async () => {
    const res = await post(LEGACY_INITIALIZE, { origin: url.origin, 'mcp-protocol-version': '2025-11-25' });
    expect(res.status).toBe(200);
  });

  it('serves a request without Origin (every non-browser client)', async () => {
    const res = await post(LEGACY_INITIALIZE, { 'mcp-protocol-version': '2025-11-25' });
    expect(res.status).toBe(200);
  });
});
