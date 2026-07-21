/**
 * Unit tests for delegated (MSP) per-tool tenant enforcement + routing (Phase 6, PR3).
 *
 * A delegated caller has NO platform scope but a managed tenant set. Every tenant-boundable tool must:
 *   - route to the cross-tenant /api/global/* path (so the backend delegated-rescue can authorize it),
 *   - REQUIRE a tenantId that is in the managed set (no aggregate; out-of-scope rejected),
 * all enforced client-side by enforceDelegatedTenant before any backend call. GA/Reader behavior
 * (tenantId optional, same routing) must be unchanged.
 *
 * These are pure unit tests: fetch is stubbed, so no backend/token is needed. We invoke the registered
 * tool handler directly inside a runWithCaller scope and inspect the URL it would have fetched.
 */
import { describe, it, expect, vi, afterEach } from 'vitest';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { registerTools } from '../tools.js';
import { runWithCaller } from '../client.js';
import { tenantIdDescription } from '../tools/shared.js';
import { delegatedTenantListView } from '../tools/admin.js';

type ToolHandler = (args: Record<string, unknown>, extra: unknown) => Promise<{
  content?: Array<{ type: string; text?: string }>;
  isError?: boolean;
}>;

const MANAGED = 'aaaa-1111-managed';

/** Build a server for the given caller shape and return a tool's raw handler by name. */
function handlerFor(name: string, opts: { ga?: boolean; strictGa?: boolean; delegated?: boolean }): ToolHandler {
  const server = new McpServer({ name: 'test', version: '0.0.0' });
  registerTools(server, undefined, undefined, undefined, [], opts.ga ?? false, opts.strictGa ?? (opts.ga ?? false), opts.delegated ?? false);
  const registry = (server as unknown as { _registeredTools: Record<string, { handler: ToolHandler }> })._registeredTools;
  const tool = registry[name];
  if (!tool) throw new Error(`tool ${name} not registered for this caller shape`);
  return tool.handler;
}

/** Stub global fetch; capture every requested URL and return an empty-OK JSON body. */
function stubFetchCapture(): { urls: string[] } {
  const urls: string[] = [];
  const fn = vi.fn(async (url: string) => {
    urls.push(String(url));
    return { ok: true, status: 200, json: async () => ({ success: true, sessions: [], events: [], count: 0 }), text: async () => '{}' } as unknown as Response;
  });
  vi.stubGlobal('fetch', fn);
  return { urls };
}

/** Extract the concatenated text of a tool result (for error-message assertions). */
function resultText(r: { content?: Array<{ text?: string }> }): string {
  return (r.content ?? []).map((c) => c.text ?? '').join('\n');
}

const extra = { signal: new AbortController().signal };

afterEach(() => vi.unstubAllGlobals());

describe('delegated tenant enforcement — query_raw_sessions (single-fetch, representative)', () => {
  it('routes to /api/global/* and forwards a managed tenantId', async () => {
    const handler = handlerFor('query_raw_sessions', { delegated: true });
    const { urls } = stubFetchCapture();

    await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ tenantId: MANAGED }, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain('/api/global/raw/sessions');
    expect(urls[0]).toContain(`tenantId=${MANAGED}`);
  });

  it('rejects a call with no tenantId (no aggregate) before any backend call', async () => {
    const handler = handlerFor('query_raw_sessions', { delegated: true });
    const { urls } = stubFetchCapture();

    const r = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({}, extra));

    expect(urls).toHaveLength(0);
    expect(r.isError).toBe(true);
    expect(resultText(r)).toMatch(/tenantId is required/i);
  });

  it('rejects an out-of-scope tenantId before any backend call', async () => {
    const handler = handlerFor('query_raw_sessions', { delegated: true });
    const { urls } = stubFetchCapture();

    const r = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ tenantId: 'zzzz-not-mine' }, extra));

    expect(urls).toHaveLength(0);
    expect(r.isError).toBe(true);
    expect(resultText(r)).toMatch(/not authorized for tenant/i);
  });

  it('accepts the documented page-2 call carrying the tenant in the continuation nextLink only', async () => {
    const handler = handlerFor('query_raw_sessions', { delegated: true });
    const { urls } = stubFetchCapture();
    const nextLink = `/api/global/raw/sessions?pageSize=200&continuation=opaque-cursor&tenantId=${MANAGED}`;

    const r = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ continuation: nextLink }, extra));

    // Must NOT be rejected as "tenantId is required" — and the verbatim nextLink is what gets fetched.
    expect(r.isError).toBeFalsy();
    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain(nextLink);
  });

  it('rejects a page-2 continuation that points at an unmanaged tenant', async () => {
    const handler = handlerFor('query_raw_sessions', { delegated: true });
    const { urls } = stubFetchCapture();
    const nextLink = `/api/global/raw/sessions?continuation=opaque&tenantId=zzzz-not-mine`;

    const r = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ continuation: nextLink }, extra));

    expect(urls).toHaveLength(0);
    expect(r.isError).toBe(true);
    expect(resultText(r)).toMatch(/not authorized for tenant/i);
  });
});

describe('delegated tenant enforcement — session-id tool (get_session_summary)', () => {
  it('requires a managed tenantId on the /api/sessions/{id} path', async () => {
    const handler = handlerFor('get_session_summary', { delegated: true });
    const { urls } = stubFetchCapture();

    await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ sessionId: '11111111-1111-1111-1111-111111111111' }, extra).catch(() => undefined));

    // Reject path: tenantId omitted ⇒ enforce throws ⇒ no backend call.
    expect(urls).toHaveLength(0);
  });

  it('forwards the managed tenantId to every backend sub-call', async () => {
    const handler = handlerFor('get_session_summary', { delegated: true });
    const { urls } = stubFetchCapture();

    await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ sessionId: '11111111-1111-1111-1111-111111111111', tenantId: MANAGED }, extra));

    expect(urls.length).toBeGreaterThan(0);
    for (const u of urls) {
      expect(u).toContain('/api/sessions/11111111-1111-1111-1111-111111111111');
      expect(u).toContain(`tenantId=${MANAGED}`);
    }
  });
});

describe('GA routing is unchanged by the delegated enforcement (regression)', () => {
  it('a Global Admin still routes to /api/global/* and may omit tenantId', async () => {
    const handler = handlerFor('query_raw_sessions', { ga: true });
    const { urls } = stubFetchCapture();

    await runWithCaller({ token: 'ga', isGlobalAdmin: true }, () => handler({}, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain('/api/global/raw/sessions');
    expect(urls[0]).not.toContain('tenantId=');
  });
});

describe('tenantId arg description is role-aware (delegated = required, not optional)', () => {
  const GA = 'Tenant ID. Omit for cross-tenant view (Global Admin only).';
  const TENANT = 'Optional tenant ID. Defaults to your tenant.';

  it('tells a delegated caller the tenantId is required and must be a managed tenant', () => {
    const d = tenantIdDescription(false, true, GA, TENANT);
    expect(d).toMatch(/required/i);
    expect(d).toMatch(/managed/i);
    // Must NOT inherit the misleading "defaults to your tenant" / GA-omit wording.
    expect(d).not.toContain('Defaults to your tenant');
    expect(d).not.toContain('Omit for cross-tenant');
  });

  it('leaves GA and plain-tenant wording unchanged', () => {
    expect(tenantIdDescription(true, false, GA, TENANT)).toBe(GA);
    expect(tenantIdDescription(false, false, GA, TENANT)).toBe(TENANT);
  });
});

describe('list_tenants is available to delegated callers (display-name resolution)', () => {
  const HOME = 'bbbb-2222-home';

  /** Stub fetch with a config/all-shaped envelope (paginated { tenants } shape). */
  function stubTenantsFetch(tenants: unknown[]): { urls: string[] } {
    const urls: string[] = [];
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      urls.push(String(url));
      return { ok: true, status: 200, json: async () => ({ count: tenants.length, tenants, nextLink: null }), text: async () => '{}' } as unknown as Response;
    }));
    return { urls };
  }

  it('is registered for a delegated caller and NOT for a plain tenant user', () => {
    expect(() => handlerFor('list_tenants', { delegated: true })).not.toThrow();
    expect(() => handlerFor('list_tenants', {})).toThrow(/not registered/);
  });

  it('returns the managed subset with domain names, plus a synthesized isHome entry labeled with the UPN domain', async () => {
    const handler = handlerFor('list_tenants', { delegated: true });
    const { urls } = stubTenantsFetch([
      { tenantId: MANAGED, domainName: 'fabrikam.com', planTier: 'Enterprise' },
      // Backend must already bound the response; an out-of-scope row here must still be dropped (defense-in-depth).
      { tenantId: 'zzzz-not-mine', domainName: 'other-customer.com' },
    ]);

    const r = await runWithCaller(
      { token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED], homeTenantId: HOME, upn: 'alice@contoso.com' },
      () => handler({}, extra));

    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain('/api/config/all');
    const body = JSON.parse(resultText(r)) as { count: number; tenants: Array<Record<string, unknown>> };
    expect(body.tenants).toHaveLength(2);
    expect(body.count).toBe(2);
    const ids = body.tenants.map((t) => t.tenantId);
    expect(ids).toContain(MANAGED);
    expect(ids).toContain(HOME);
    expect(ids).not.toContain('zzzz-not-mine');
    const home = body.tenants.find((t) => t.tenantId === HOME)!;
    expect(home.isHome).toBe(true);
    expect(home.domainName).toBe('contoso.com');
  });

  it('keeps the real config row (flagged isHome) when the home tenant is itself managed', async () => {
    const handler = handlerFor('list_tenants', { delegated: true });
    stubTenantsFetch([{ tenantId: HOME, domainName: 'contoso-real.com' }]);

    const r = await runWithCaller(
      { token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED, HOME], homeTenantId: HOME, upn: 'alice@contoso.com' },
      () => handler({}, extra));

    const body = JSON.parse(resultText(r)) as { tenants: Array<Record<string, unknown>> };
    const homes = body.tenants.filter((t) => t.isHome === true);
    expect(homes).toHaveLength(1);
    expect(homes[0].domainName).toBe('contoso-real.com');
  });

  it('does not duplicate the home entry on a paginated follow-up call', async () => {
    const handler = handlerFor('list_tenants', { delegated: true });
    stubTenantsFetch([{ tenantId: MANAGED, domainName: 'fabrikam.com' }]);

    const r = await runWithCaller(
      { token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED], homeTenantId: HOME, upn: 'alice@contoso.com' },
      () => handler({ continuation: '/api/config/all?pageSize=100&continuation=opaque' }, extra));

    const body = JSON.parse(resultText(r)) as { tenants: Array<Record<string, unknown>> };
    expect(body.tenants.map((t) => t.tenantId)).toEqual([MANAGED]);
  });

  it('GA behavior is unchanged: no isHome synthesis, out-of-managed rows kept', async () => {
    const handler = handlerFor('list_tenants', { ga: true });
    stubTenantsFetch([
      { tenantId: 'any-tenant', domainName: 'any.com', teamsWebhookUrl: 'https://secret' },
    ]);

    const r = await runWithCaller(
      { token: 'ga', isGlobalAdmin: true, homeTenantId: HOME, upn: 'admin@contoso.com' },
      () => handler({}, extra));

    const body = JSON.parse(resultText(r)) as { tenants: Array<Record<string, unknown>> };
    expect(body.tenants).toHaveLength(1);
    expect(body.tenants[0].tenantId).toBe('any-tenant');
    expect(body.tenants[0].isHome).toBeUndefined();
    // Keep-list projection still strips secrets on the GA path.
    expect(body.tenants[0].teamsWebhookUrl).toBeUndefined();
  });
});

describe('delegatedTenantListView — pure projection of the delegated tenant list', () => {
  const HOME = 'bbbb-2222-home';

  it('intersects case-insensitively with the managed set and sorts by domainName', () => {
    const view = delegatedTenantListView(
      [
        { tenantId: MANAGED.toUpperCase(), domainName: 'zeta.com' },
        { tenantId: 'not-mine', domainName: 'alpha.com' },
      ],
      [MANAGED], undefined, undefined, true);
    expect(view.map((t) => t.tenantId)).toEqual([MANAGED.toUpperCase()]);
  });

  it('synthesizes the home entry only on the first page and only when absent', () => {
    const first = delegatedTenantListView([], [MANAGED], HOME, 'contoso.com', true);
    expect(first).toEqual([{ tenantId: HOME, domainName: 'contoso.com', isHome: true }]);

    const followUp = delegatedTenantListView([], [MANAGED], HOME, 'contoso.com', false);
    expect(followUp).toEqual([]);
  });

  it('falls back to an empty label when no UPN domain is known', () => {
    const view = delegatedTenantListView([], [MANAGED], HOME, undefined, true);
    expect(view[0].domainName).toBe('');
  });
});

describe('get_software_inventory exposes a required tenantId selector to delegated callers', () => {
  it('is registered for a delegated caller and requires a managed tenantId', async () => {
    const handler = handlerFor('get_software_inventory', { delegated: true });
    const { urls } = stubFetchCapture();

    const denied = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({}, extra));
    expect(urls).toHaveLength(0);
    expect(denied.isError).toBe(true);

    const ok = await runWithCaller({ token: 'msp', isGlobalAdmin: false, delegatedTenantIds: [MANAGED] }, () =>
      handler({ tenantId: MANAGED }, extra).then((r) => r).catch(() => undefined));
    // With a managed tenantId the call proceeds to the per-tenant inventory endpoint.
    expect(urls.some((u) => u.includes('software-inventory') && u.includes(`tenantId=${MANAGED}`))).toBe(true);
    expect(ok).toBeDefined();
  });
});
