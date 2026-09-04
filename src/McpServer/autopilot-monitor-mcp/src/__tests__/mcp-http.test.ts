/**
 * End-to-end protocol tests for the dual-era HTTP entry (mcp-http.ts) with the
 * REAL production server factory (mcp-server-factory.ts), driven by the
 * official 2026-07-28 client over loopback HTTP.
 *
 * Two clients, one server:
 *   - modern  — the client's default negotiation (server/discover, per-request
 *     envelope, no session) — what 2026-spec hosts send;
 *   - legacy  — forced 2025 `initialize` handshake — what Claude Code / Claude.ai /
 *     VS Code send today.
 *
 * Both must see the same role-tailored catalog and instructions, because the
 * factory is shared; the modern leg additionally carries the 2026 cache fields
 * and must NOT advertise listChanged (a stateless server can never send it).
 *
 * No backend token needed: validate_rule is MCP-local, and catalogs are built
 * without search providers.
 */
import { describe, it, expect, beforeAll, afterAll, vi } from 'vitest';
import express from 'express';
import type { Server } from 'node:http';
import { Client } from '@modelcontextprotocol/client';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/client';
import { createMcpRequestHandler } from '../mcp-http.js';
import { createServerForCaller, type ServerDeps } from '../mcp-server-factory.js';
import { runWithCaller } from '../client.js';

const DEPS: ServerDeps = { serverVersion: '0.0.0-test', knowledgeBase: undefined, eventTypeIndex: undefined, docs: undefined };

/** Flipped per test: what accessGuard would have resolved for the caller. */
let callerIsGlobalAdmin = true;
/** Read-only Global Reader: platform scope without the strictGa surface. */
let callerIsGlobalReader = false;

let httpServer: Server;
let url: URL;
const errors: string[] = [];

beforeAll(async () => {
  const app = express();
  app.use('/mcp', express.json({ limit: '256kb' }));
  const handler = createMcpRequestHandler(() => createServerForCaller(DEPS), {
    onerror: (e) => errors.push(e.message),
  });
  // Mirrors index.ts: the guard scopes the caller via AsyncLocalStorage around
  // the dispatch; the factory must observe it through the SDK entry.
  app.post('/mcp', (req, res) =>
    runWithCaller({ token: 'test-token', isGlobalAdmin: callerIsGlobalAdmin, isGlobalReader: callerIsGlobalReader, upn: 'caller@contoso.com' }, () => handler(req, res)),
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

async function connect(mode: 'auto' | 'legacy'): Promise<{ client: Client; transport: StreamableHTTPClientTransport }> {
  const client = new Client({ name: 'test-client', version: '1.0.0' }, { versionNegotiation: { mode } });
  const transport = new StreamableHTTPClientTransport(url);
  await client.connect(transport);
  return { client, transport };
}

describe('2026-07-28 (modern) client', () => {
  it('negotiates the 2026-07-28 revision without a session', async () => {
    const { client, transport } = await connect('auto');
    try {
      expect(transport.protocolVersion).toBe('2026-07-28');
      expect(transport.sessionId).toBeUndefined();
    } finally {
      await client.close();
    }
  });

  it('delivers role-aware instructions and serverInfo via server/discover', async () => {
    callerIsGlobalAdmin = true;
    const { client } = await connect('auto');
    try {
      expect(client.getInstructions()).toContain('as a Global Admin you additionally have tenant-configuration write tools');
      expect(client.getInstructions()).toContain('omit tenantId for cross-tenant queries');
      expect(client.getServerVersion()).toEqual({ name: 'Autopilot-Monitor', version: '0.0.0-test' });
    } finally {
      await client.close();
    }
  });

  it('does NOT advertise listChanged — a stateless server can never send it', async () => {
    const { client } = await connect('auto');
    try {
      const caps = client.getServerCapabilities();
      expect(caps?.tools).toBeDefined();
      expect(caps?.tools?.listChanged).toBe(false);
      expect(caps?.resources?.listChanged).toBe(false);
      expect(caps?.resources?.subscribe).toBeFalsy();
      expect(caps?.prompts?.listChanged).toBe(false);
      expect(caps?.logging).toBeUndefined();
    } finally {
      await client.close();
    }
  });

  it('stamps private cache hints on every list result (catalog is role-dependent)', async () => {
    const { client } = await connect('auto');
    try {
      for (const result of [await client.listTools(), await client.listPrompts(), await client.listResources()]) {
        expect(result.cacheScope).toBe('private');
        expect(result.ttlMs).toBe(5 * 60 * 1000);
      }
      const read = await client.readResource({ uri: 'autopilot://event-types' });
      expect(read.cacheScope).toBe('private');
      expect(read.ttlMs).toBe(60 * 60 * 1000);
    } finally {
      await client.close();
    }
  });

  it('executes a tool through the modern path (Mcp-Method / Mcp-Name routed)', async () => {
    const { client } = await connect('auto');
    try {
      const result = await client.callTool({ name: 'validate_rule', arguments: { rule: { id: 'x' } } });
      expect(result.isError).toBeFalsy();
      const text = (result.content as Array<{ type: string; text?: string }>)[0]?.text ?? '';
      expect(JSON.parse(text)).toMatchObject({ valid: false });
      expect(result._meta).toMatchObject({ 'anthropic/maxResultSizeChars': 50_000 });
    } finally {
      await client.close();
    }
  });
});

describe('2025-era (legacy initialize) client', () => {
  it('still negotiates via initialize and receives the same instructions', async () => {
    callerIsGlobalAdmin = true;
    const { client, transport } = await connect('legacy');
    try {
      expect(transport.protocolVersion).not.toBe('2026-07-28');
      expect(transport.sessionId).toBeUndefined();
      expect(client.getInstructions()).toContain('as a Global Admin you additionally have tenant-configuration write tools');
      const tools = await client.listTools();
      expect(tools.tools.map((t) => t.name)).toContain('get_session_summary');
    } finally {
      await client.close();
    }
  });

  it('answers with a single buffered application/json body (gzip-able), not SSE', async () => {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
      body: JSON.stringify({
        jsonrpc: '2.0',
        id: 1,
        method: 'initialize',
        params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'raw', version: '0' } },
      }),
    });
    expect(res.status).toBe(200);
    expect(res.headers.get('content-type')).toMatch(/^application\/json/);
    expect(res.headers.get('mcp-session-id')).toBeNull();
    const body = (await res.json()) as { result?: { protocolVersion?: string; instructions?: string } };
    expect(body.result?.protocolVersion).toBe('2025-11-25');
    expect(body.result?.instructions).toContain('Autopilot-Monitor');
  });
});

describe('role tailoring is identical across eras (privilege-leak guard)', () => {
  async function toolNames(mode: 'auto' | 'legacy'): Promise<string[]> {
    const { client } = await connect(mode);
    try {
      return (await client.listTools()).tools.map((t) => t.name).sort();
    } finally {
      await client.close();
    }
  }

  it('a plain tenant user sees no platform tool and no cross-tenant wording on either path', async () => {
    callerIsGlobalAdmin = false;
    try {
      const modern = await toolNames('auto');
      const legacy = await toolNames('legacy');
      expect(modern).toEqual(legacy);
      expect(modern).not.toContain('list_tenants');
      expect(modern).not.toContain('query_table');
      expect(modern).not.toContain('update_tenant_config');
      const { client } = await connect('auto');
      try {
        expect(client.getInstructions()).toContain('READ-ONLY');
        expect(client.getInstructions()).not.toContain('cross-tenant');
      } finally {
        await client.close();
      }
    } finally {
      callerIsGlobalAdmin = true;
    }
  });

  it('a Global Admin sees the platform + write tools on both paths', async () => {
    callerIsGlobalAdmin = true;
    const modern = await toolNames('auto');
    const legacy = await toolNames('legacy');
    expect(modern).toEqual(legacy);
    expect(modern).toContain('list_tenants');
    expect(modern).toContain('update_tenant_config');
  });

  it('a Global Reader sees the platform reads but none of the strictGa tools on either path', async () => {
    callerIsGlobalAdmin = false;
    callerIsGlobalReader = true;
    try {
      const modern = await toolNames('auto');
      const legacy = await toolNames('legacy');
      expect(modern).toEqual(legacy);
      expect(modern).toContain('list_tenants');
      expect(modern).toContain('get_ops_events');
      for (const name of ['query_backend_logs', 'query_table', 'list_tables', 'update_tenant_config', 'get_tenant_config', 'annotate_session']) {
        expect(modern, `${name} leaked to a Global Reader`).not.toContain(name);
      }
      const { client } = await connect('auto');
      try {
        // The debug prompt must not point a Reader at tools it cannot see.
        const prompt = await client.getPrompt({ name: 'debug-session', arguments: { sessionId: 'e259c121-1234-4abc-9def-0123456789ab' } });
        const text = prompt.messages.map((m) => (m.content as { text?: string }).text ?? '').join(' ');
        expect(text).not.toContain('query_backend_logs');
      } finally {
        await client.close();
      }
    } finally {
      callerIsGlobalAdmin = true;
      callerIsGlobalReader = false;
    }
  });

  it('a Global Admin is pointed at the raw tools by the debug prompt', async () => {
    callerIsGlobalAdmin = true;
    const { client } = await connect('auto');
    try {
      const prompt = await client.getPrompt({ name: 'debug-session', arguments: { sessionId: 'e259c121-1234-4abc-9def-0123456789ab' } });
      const text = prompt.messages.map((m) => (m.content as { text?: string }).text ?? '').join(' ');
      expect(text).toContain('query_backend_logs');
    } finally {
      await client.close();
    }
  });
});

describe('assume-breach: a non-GA call to a GA-only tool', () => {
  it('is refused as not-found AND fires the backend access probe with the tool name', async () => {
    callerIsGlobalAdmin = false;
    const realFetch = globalThis.fetch;
    // The MCP client transport and the server-side probe share globalThis.fetch: pass the client's
    // own POSTs to the loopback server through untouched and answer only the backend probe.
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/api/global/raw/access-probe')) {
        return new Response('{"error":"Forbidden"}', { status: 403 });
      }
      return realFetch(input, init);
    });
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const { client } = await connect('auto');
      globalThis.fetch = fetchMock as unknown as typeof fetch;
      try {
        // The SDK refuses before any handler: the 2026 client surfaces that as a thrown protocol error.
        await expect(client.callTool({ name: 'query_backend_logs', arguments: { query: 'traces | take 1' } }))
          .rejects.toThrow(/query_backend_logs not found/);
      } finally {
        globalThis.fetch = realFetch;
        await client.close();
      }
      await new Promise((r) => setTimeout(r, 20));
      const probe = fetchMock.mock.calls.find(([u]) => String(u).endsWith('/api/global/raw/access-probe'));
      expect(probe, 'access probe was not fired').toBeDefined();
      const headers = (probe![1] as RequestInit).headers as Record<string, string>;
      expect(headers['X-MCP-Tool-Name']).toBe('query_backend_logs');
      expect(headers['Authorization']).toBe('Bearer test-token');
      const line = errorSpy.mock.calls.map((c) => String(c[0])).find((l) => l.startsWith('[mcp-security] ga-tool-denied'));
      expect(line).toContain('tool=query_backend_logs');
      expect(line).toContain('upn=caller@contoso.com');
    } finally {
      errorSpy.mockRestore();
      callerIsGlobalAdmin = true;
    }
  });
});

describe('transport hygiene', () => {
  it('reports no transport errors across the suite', () => {
    expect(errors).toEqual([]);
  });
});
