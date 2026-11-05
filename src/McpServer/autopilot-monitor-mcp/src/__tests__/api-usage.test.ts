/**
 * get_api_usage: the shaping of the MCP usage views (api-usage-shape.ts) and the tool's routing.
 *  - the summary cuts every list to its head and states the full length; nothing is cut silently;
 *  - absent wire values become explicit nulls (calls without request telemetry, unreadable counters);
 *  - tenants are named on refusal and minute rows; an account's counter keys fold into tool and day sums;
 *  - organization and account need their id; a UPN is not an object id.
 *
 * apiFetch is mocked so these run with no backend / token.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));

vi.mock('../client.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../client.js')>();
  return { ...actual, apiFetch: apiFetchMock };
});

import { registerAdminTools } from '../tools/admin.js';
import { ObjectIdSchema } from '../tools/shared.js';
import { NO_TOOL, SUMMARY_HEAD, shapeAccount, shapeOrganization, shapeOverview, usageWindow } from '../api-usage-shape.js';
import type {
  GetGlobalMcpUsageOverviewResponse,
  GetMcpOrganizationUsageResponse,
  GetMcpUserUsageResponse,
  McpUsageAccountItem,
  McpUsageMinuteItem,
  McpUsageQuotaHitItem,
  McpUsageTenantItem,
  McpUsageToolItem,
} from '../generated/wire-types.generated.js';

const TENANT_A = 'aaaaaaaa-0000-4000-8000-00000000000a';
const TENANT_B = 'bbbbbbbb-0000-4000-8000-00000000000b';
const OID = '11111111-2222-4333-8444-555555555555';
const AT = '2026-11-05T10:00:00Z';

function account(principal: string, over: Partial<McpUsageAccountItem> = {}): McpUsageAccountItem {
  return {
    userId: OID,
    principal,
    isPlatformAdmin: false,
    quota: { plan: 'community', dailyLimit: 50, monthlyLimit: 250, dailyUsed: 12, monthlyUsed: 40 },
    calls: 5,
    requests: 12,
    quotaHits: 0,
    topTools: [{ tool: 'get_session_summary', calls: 3 }],
    ...over,
  };
}

function tenant(tenantId: string, domainName: string | undefined, accounts: McpUsageAccountItem[]): McpUsageTenantItem {
  return {
    tenantId,
    ...(domainName ? { domainName } : {}),
    quota: { tenantPlan: 'community', dailyLimit: 100, monthlyLimit: 250, dailyUsed: 30, monthlyUsed: 90 },
    calls: accounts.reduce((s, a) => s + a.calls, 0),
    requests: accounts.reduce((s, a) => s + a.requests, 0),
    quotaHits: 1,
    accounts,
  };
}

const tool = (name: string, calls: number, over: Partial<McpUsageToolItem> = {}): McpUsageToolItem =>
  ({ tool: name, calls, accounts: 2, tenants: 1, requestsPerCall: 4.5, errorRate: 0.1, p95DurationMs: 900, routes: [{ route: 'GET sessions/{id}', requests: 9 }], ...over });

const hit = (tenantId: string, principal: string): McpUsageQuotaHitItem =>
  ({ date: '20261105', tenantId, principal, level: 'user', scope: 'daily', plan: 'community', limit: 50, used: 50, refused: 3, firstAt: AT, lastAt: AT });

const minute = (tenantId: string, principal: string, sharePercent: number): McpUsageMinuteItem =>
  ({ tenantId, principal, sharePercent, peakCalls: 30, peakCallsAt: AT, mcpRefused: 0, peakRequests: 60, peakRequestsAt: AT, backendLimit: 120, backendRefused: 0 });

function overview(over: Partial<GetGlobalMcpUsageOverviewResponse> = {}): GetGlobalMcpUsageOverviewResponse {
  return {
    dateFrom: '20261007',
    dateTo: '20261105',
    includesPlatformAdmins: false,
    telemetry: { requestsAvailable: true, mcpLogAvailable: true },
    totals: {
      calls: 40, mcpOnlyCalls: 4, tools: 2, requests: 24, tenants: 2, accounts: 2, servicePrincipals: 0,
      quotaHitAccounts: 1, minuteRefusedAccounts: 0, limitAccounts: 1,
    },
    daily: [{ date: '20261105', calls: 40, requests: 24, accounts: 2, callsByPlan: [{ plan: 'community', calls: 40 }] }],
    tenants: [tenant(TENANT_A, 'contoso.com', [account('alice@contoso.com')]), tenant(TENANT_B, undefined, [account('bob@fabrikam.com')])],
    tools: [tool('get_session_summary', 36), tool('search_docs', 4, { accounts: undefined, tenants: undefined, requestsPerCall: 0, routes: [] })],
    quotaHits: [hit(TENANT_A, 'alice@contoso.com')],
    minuteLimits: { mcpServerLimit: 60, accounts: [minute(TENANT_A, 'alice@contoso.com', 50), minute(TENANT_B, 'bob@fabrikam.com', 25)] },
    ...over,
  };
}

describe('usageWindow', () => {
  it('is the trailing window of whole UTC days ending today, as yyyyMMdd', () => {
    const now = new Date('2026-11-05T23:30:00Z');
    expect(usageWindow(1, now)).toEqual({ from: '20261105', to: '20261105' });
    expect(usageWindow(30, now)).toEqual({ from: '20261007', to: '20261105' });
  });
});

describe('shapeOverview — summary', () => {
  it('carries the backend totals and every list head with its full length', () => {
    const out = shapeOverview(overview(), 'summary');

    expect(out.view).toBe('summary');
    expect(out.window).toEqual({ from: '20261007', to: '20261105' });
    expect(out.telemetry).toEqual({ requestsAvailable: true, mcpLogAvailable: true, mcpLogFrom: null, partial: false });
    expect(out.totals).toMatchObject({ calls: 40, mcpOnlyCalls: 4, requests: 24, limitAccounts: 1 });
    expect(out.lists).toEqual({ tools: 2, tenants: 2, quotaHits: 1, minuteLimits: 2 });
    expect(out).not.toHaveProperty('note'); // nothing was cut

    const [summaryTool, docs] = out.tools as Array<Record<string, unknown>>;
    expect(summaryTool).toEqual({ tool: 'get_session_summary', calls: 36, requestsPerCall: 4.5, accounts: 2, tenants: 1, errorRate: 0.1, p95DurationMs: 900 });
    expect(docs).toMatchObject({ tool: 'search_docs', accounts: null, tenants: null }); // runs in the MCP server alone

    const [contoso, unnamed] = out.tenants as Array<Record<string, unknown>>;
    expect(contoso).toMatchObject({ domainName: 'contoso.com', plan: 'community', accounts: 1, today: { used: 30, limit: 100 }, month: { used: 90, limit: 250 } });
    expect(unnamed.domainName).toBeNull();

    expect((out.quotaHits as Array<Record<string, unknown>>)[0]).toMatchObject({ domainName: 'contoso.com', principal: 'alice@contoso.com', used: 50, limit: 50 });
    const minuteLimits = out.minuteLimits as { mcpServerLimit: number; accounts: Array<Record<string, unknown>> };
    expect(minuteLimits.mcpServerLimit).toBe(60);
    expect(minuteLimits.accounts.map((m) => [m.principal, m.domainName, m.sharePercent])).toEqual([
      ['alice@contoso.com', 'contoso.com', 50],
      ['bob@fabrikam.com', null, 25],
    ]);
  });

  it('cuts a long list to its head and says so', () => {
    const tools = Array.from({ length: SUMMARY_HEAD.tools + 3 }, (_, i) => tool(`tool_${i}`, 100 - i));
    const out = shapeOverview(overview({ tools }), 'summary');

    expect(out.tools).toHaveLength(SUMMARY_HEAD.tools);
    expect((out.lists as Record<string, number>).tools).toBe(SUMMARY_HEAD.tools + 3);
    expect(out.note).toMatch(/cut to their first rows/);
  });

  it('shows calls as null, not 0, when the request telemetry did not answer', () => {
    const out = shapeOverview(overview({
      telemetry: { requestsAvailable: false, mcpLogAvailable: false, mcpLogFrom: '20261020', partial: true },
      totals: { tools: 0, requests: 24, tenants: 2, accounts: 2, servicePrincipals: 0, quotaHitAccounts: 0, minuteRefusedAccounts: 0, limitAccounts: 0 },
    }), 'summary');

    expect(out.totals).toMatchObject({ calls: null, mcpOnlyCalls: null, requests: 24 });
    expect(out.telemetry).toEqual({ requestsAvailable: false, mcpLogAvailable: false, mcpLogFrom: '20261020', partial: true });
  });
});

describe('shapeOverview — one list in full', () => {
  it('tenants carry every account with explicit windows; an unreadable counter is null', () => {
    const unreadable = account('carol@contoso.com', { quota: { plan: 'community', dailyLimit: 50, monthlyLimit: 250, dailyUsed: -1, monthlyUsed: -1 } });
    const out = shapeOverview(overview({ tenants: [tenant(TENANT_A, 'contoso.com', [account('alice@contoso.com'), unreadable])] }), 'tenants');

    const [only] = out.tenants as Array<{ accounts: Array<Record<string, unknown>> }>;
    expect(only.accounts.map((a) => a.principal)).toEqual(['alice@contoso.com', 'carol@contoso.com']);
    expect(only.accounts[0]).toMatchObject({ plan: 'community', today: { used: 12, limit: 50 }, month: { used: 40, limit: 250 }, role: null });
    expect(only.accounts[1]).toMatchObject({ today: { used: null, limit: 50 }, month: { used: null, limit: 250 } });
  });

  it('tools keep their routes and quality figures; the other views return their one list', () => {
    const data = overview();
    const tools = shapeOverview(data, 'tools').tools as Array<Record<string, unknown>>;
    expect(tools[0]).toMatchObject({ routes: [{ route: 'GET sessions/{id}', requests: 9 }], avgResultChars: null, overCapCalls: null });

    expect(shapeOverview(data, 'quotaHits').quotaHits).toHaveLength(1);
    expect(shapeOverview(data, 'minuteLimits')).toMatchObject({ mcpServerLimit: 60 });
    expect(shapeOverview(data, 'daily').daily).toEqual(data.daily);
    for (const view of ['tools', 'tenants', 'quotaHits', 'minuteLimits', 'daily'] as const) {
      expect(shapeOverview(data, view)).not.toHaveProperty('totals');
    }
  });
});

describe('shapeOrganization', () => {
  it('names the windows and sums the accounts of the range', () => {
    const data: GetMcpOrganizationUsageResponse = {
      tenantId: TENANT_A,
      dateFrom: '20261101',
      dateTo: '20261105',
      users: [
        { userId: OID, userPrincipalName: 'alice@contoso.com', requestsToday: 10, requestsThisMonth: 40, requestsInRange: 40, lastRequestAt: AT },
        { userId: 'oid-2', requestsToday: 0, requestsThisMonth: 2, requestsInRange: 2 },
      ],
      quota: { tenantPlan: 'community', dailyLimit: 100, monthlyLimit: 250, dailyUsed: 10, monthlyUsed: 42 },
      daily: [{ date: '20261105', requests: 10 }],
    };

    const out = shapeOrganization(data);
    expect(out).toMatchObject({ view: 'organization', plan: 'community', today: { used: 10, limit: 100 }, month: { used: 42, limit: 250 }, requests: 42 });
    expect((out.accounts as Array<Record<string, unknown>>)[1]).toEqual(
      { principal: null, userId: 'oid-2', requestsInRange: 2, requestsToday: 0, requestsThisMonth: 2, lastRequestAt: null });
  });
});

describe('shapeAccount', () => {
  it('folds the counter rows into tools with their paths and into days', () => {
    const row = (date: string, endpoint: string, requestCount: number, lastRequestAt = AT) =>
      ({ userId: OID, userPrincipalName: 'alice@contoso.com', tenantId: TENANT_A, endpoint, date, requestCount, lastRequestAt });
    const data: GetMcpUserUsageResponse = {
      userId: OID,
      records: [
        row('20261104', 'get_session_summary:sessions/{id}', 6),
        row('20261105', 'get_session_summary:sessions/{id}/events', 9),
        row('20261105', 'get_session_summary:sessions/{id}', 3),
        row('20261105', 'auth/me', 1),
      ],
    };

    const out = shapeAccount(data, { from: '20261101', to: '20261105' });
    expect(out).toMatchObject({ userId: OID, principal: 'alice@contoso.com', tenantId: TENANT_A, requests: 19 });
    expect(out.byTool).toEqual([
      { tool: 'get_session_summary', requests: 18, paths: [{ path: 'sessions/{id}', requests: 9 }, { path: 'sessions/{id}/events', requests: 9 }] },
      { tool: NO_TOOL, requests: 1, paths: [{ path: 'auth/me', requests: 1 }] },
    ]);
    expect(out.byDay).toEqual([{ date: '20261104', requests: 6 }, { date: '20261105', requests: 13 }]);
  });

  it('answers an account without rows with zeros, not an error', () => {
    expect(shapeAccount({ userId: OID, records: [] }, { from: '20261105', to: '20261105' }))
      .toEqual({ view: 'account', userId: OID, principal: null, tenantId: null, window: { from: '20261105', to: '20261105' }, requests: 0, byTool: [], byDay: [] });
  });
});

// ── The tool ──────────────────────────────────────────────────────────────────

type Handler = (args: Record<string, unknown>) => Promise<{ content: Array<{ text: string }>; isError?: boolean }>;

function handler(): Handler {
  const handlers: Record<string, Handler> = {};
  const fake = { registerTool: (name: string, _def: unknown, h: Handler) => { handlers[name] = h; } };
  registerAdminTools(fake as never, true, true, false);
  return handlers.get_api_usage;
}

describe('get_api_usage — routing', () => {
  beforeEach(() => apiFetchMock.mockReset());

  it('reads the overview for the overview views, with the platform-admin switch and a long timeout', async () => {
    apiFetchMock.mockResolvedValueOnce(overview());
    const result = await handler()({ view: 'summary', days: 7, includePlatformAdmins: true });

    expect(result.isError).toBeUndefined();
    const [path, init] = apiFetchMock.mock.calls[0] as [string, { signal?: AbortSignal }];
    expect(path).toBe('/api/global/metrics/mcp-usage/overview?days=7&platformAdmins=include');
    expect(init.signal).toBeDefined();
    expect(JSON.parse(result.content[0].text).view).toBe('summary');
  });

  it('reads one tenant and one account over the trailing window', async () => {
    apiFetchMock.mockResolvedValue({ tenantId: TENANT_A, dateFrom: '20261105', dateTo: '20261105', users: [], quota: { tenantPlan: 'pro', dailyLimit: 0, monthlyLimit: 0, dailyUsed: 0, monthlyUsed: 0 }, daily: [] });
    await handler()({ view: 'organization', days: 1, tenantId: TENANT_A });
    expect(apiFetchMock.mock.calls[0][0]).toMatch(new RegExp(`^/api/global/metrics/mcp-usage/organization\\?tenantId=${TENANT_A}&dateFrom=\\d{8}&dateTo=\\d{8}$`));

    apiFetchMock.mockResolvedValue({ userId: OID, records: [] });
    await handler()({ view: 'account', days: 1, userId: OID });
    expect(apiFetchMock.mock.calls[1][0]).toMatch(new RegExp(`^/api/metrics/mcp-usage/user/${OID}\\?dateFrom=\\d{8}&dateTo=\\d{8}$`));
  });

  it('refuses organization without tenantId and account without userId, before any request', async () => {
    const noTenant = await handler()({ view: 'organization', days: 30 });
    const noUser = await handler()({ view: 'account', days: 30 });

    expect(noTenant.isError).toBe(true);
    expect(noTenant.content[0].text).toMatch(/tenantId is required for view=organization/);
    expect(noUser.isError).toBe(true);
    expect(noUser.content[0].text).toMatch(/userId .* is required for view=account/);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });

  it('a UPN is not an object id', () => {
    expect(ObjectIdSchema.safeParse(OID).success).toBe(true);
    expect(ObjectIdSchema.safeParse('alice@contoso.com').success).toBe(false);
    expect(ObjectIdSchema.safeParse('../global/metrics').success).toBe(false);
  });
});
