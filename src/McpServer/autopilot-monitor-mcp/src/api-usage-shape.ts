/**
 * LLM-facing shaping of the MCP usage views of get_api_usage:
 *  - the all-tenant overview (GET /api/global/metrics/mcp-usage/overview, D-416) — one list per view, or the summary;
 *  - one tenant's organization budget (GET /api/global/metrics/mcp-usage/organization?tenantId=);
 *  - one account's quota counters (GET /api/metrics/mcp-usage/user/{userId}).
 *
 * The backend computes every figure (totals, minute shares, rankings) once for the admin page and this tool. This
 * layer only picks what a view shows, states the full length of every list the summary cuts, names tenants, folds an
 * account's counter rows into sums per tool and per day, and turns absent values into explicit nulls (the wire omits
 * them, which reads as "not applicable"). A quota window is { used, limit }: limit 0 = unlimited, used null = the
 * counter could not be read.
 */

import type {
  GetGlobalMcpUsageOverviewResponse,
  GetMcpOrganizationUsageResponse,
  GetMcpUserUsageResponse,
  McpUsageAccountItem,
  McpUsageMinuteItem,
  McpUsageQuotaHitItem,
  McpUsageTenantItem,
  McpUsageToolItem,
} from './generated/wire-types.generated.js';

export const OVERVIEW_VIEWS = ['summary', 'tools', 'tenants', 'quotaHits', 'minuteLimits', 'daily'] as const;
export type OverviewView = (typeof OVERVIEW_VIEWS)[number];

/** How many rows of each list the summary shows; the full list is one view away. */
export const SUMMARY_HEAD = { tools: 10, tenants: 10, quotaHits: 10, minuteLimits: 5 } as const;

/** The tool of an account's counter key that carries none (a request sent without a tool name). */
export const NO_TOOL = '(no tool)';

interface QuotaWindow {
  used: number | null;
  limit: number;
}

/** The wire marks an unreadable counter with -1. */
function quotaWindow(used: number, limit: number): QuotaWindow {
  return { used: used < 0 ? null : used, limit };
}

const dayKey = (d: Date) => d.toISOString().slice(0, 10).replace(/-/g, '');

/** The trailing window of `days` whole UTC days ending today, as yyyyMMdd — the overview's own window. */
export function usageWindow(days: number, now: Date = new Date()): { from: string; to: string } {
  const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - (days - 1)));
  return { from: dayKey(from), to: dayKey(now) };
}

// ── Overview ──────────────────────────────────────────────────────────────────

function overviewHead(data: GetGlobalMcpUsageOverviewResponse) {
  return {
    window: { from: data.dateFrom, to: data.dateTo },
    includesPlatformAdmins: data.includesPlatformAdmins,
    telemetry: {
      requestsAvailable: data.telemetry.requestsAvailable,
      mcpLogAvailable: data.telemetry.mcpLogAvailable,
      mcpLogFrom: data.telemetry.mcpLogFrom ?? null,
      partial: data.telemetry.partial ?? false,
    },
  };
}

function toolRow(t: McpUsageToolItem) {
  return {
    tool: t.tool,
    calls: t.calls,
    requestsPerCall: t.requestsPerCall,
    // null: the tool runs in the MCP server alone and never reaches the backend.
    accounts: t.accounts ?? null,
    tenants: t.tenants ?? null,
    errorRate: t.errorRate ?? null,
    p95DurationMs: t.p95DurationMs ?? null,
  };
}

function tenantRow(t: McpUsageTenantItem) {
  return {
    tenantId: t.tenantId,
    domainName: t.domainName ?? null,
    plan: t.quota.tenantPlan,
    today: quotaWindow(t.quota.dailyUsed, t.quota.dailyLimit),
    month: quotaWindow(t.quota.monthlyUsed, t.quota.monthlyLimit),
    calls: t.calls,
    requests: t.requests,
    quotaHits: t.quotaHits,
    lastRequestAt: t.lastRequestAt ?? null,
  };
}

function accountRow(a: McpUsageAccountItem) {
  return {
    principal: a.principal,
    userId: a.userId ?? null,
    role: a.role ?? null,
    isPlatformAdmin: a.isPlatformAdmin,
    plan: a.quota?.plan ?? null,
    today: a.quota ? quotaWindow(a.quota.dailyUsed, a.quota.dailyLimit) : null,
    month: a.quota ? quotaWindow(a.quota.monthlyUsed, a.quota.monthlyLimit) : null,
    calls: a.calls,
    requests: a.requests,
    peakDay: a.peakDay ?? null,
    peakDayRequests: a.peakDayRequests ?? null,
    quotaHits: a.quotaHits,
    topTools: a.topTools,
    lastRequestAt: a.lastRequestAt ?? null,
  };
}

function quotaHitRow(h: McpUsageQuotaHitItem, domainName: string | null) {
  return {
    date: h.date,
    tenantId: h.tenantId,
    domainName,
    principal: h.principal ?? null,
    level: h.level,
    scope: h.scope,
    plan: h.plan,
    used: h.used ?? null,
    limit: h.limit ?? null,
    refused: h.refused,
    firstAt: h.firstAt,
    lastAt: h.lastAt,
  };
}

function minuteRow(m: McpUsageMinuteItem, domainName: string | null) {
  return {
    tenantId: m.tenantId,
    domainName,
    principal: m.principal,
    sharePercent: m.sharePercent,
    peakCalls: m.peakCalls,
    peakCallsAt: m.peakCallsAt ?? null,
    mcpRefused: m.mcpRefused,
    peakRequests: m.peakRequests,
    peakRequestsAt: m.peakRequestsAt ?? null,
    backendLimit: m.backendLimit,
    backendRefused: m.backendRefused,
  };
}

/** One view of the overview: the summary (every list cut to its head, full lengths stated) or one list in full. */
export function shapeOverview(data: GetGlobalMcpUsageOverviewResponse, view: OverviewView): Record<string, unknown> {
  const names = new Map(data.tenants.filter((t) => t.domainName).map((t) => [t.tenantId.toLowerCase(), t.domainName!]));
  const nameOf = (tenantId: string) => names.get(tenantId.toLowerCase()) ?? null;
  const head = { view, ...overviewHead(data) };
  const minute = data.minuteLimits;

  switch (view) {
    case 'summary': {
      const lists = {
        tools: data.tools.length,
        tenants: data.tenants.length,
        quotaHits: data.quotaHits.length,
        minuteLimits: minute.accounts.length,
      };
      const cut = (Object.keys(lists) as Array<keyof typeof lists>).some((k) => lists[k] > SUMMARY_HEAD[k]);
      return {
        ...head,
        totals: { ...data.totals, calls: data.totals.calls ?? null, mcpOnlyCalls: data.totals.mcpOnlyCalls ?? null },
        tools: data.tools.slice(0, SUMMARY_HEAD.tools).map(toolRow),
        tenants: data.tenants.slice(0, SUMMARY_HEAD.tenants).map((t) => ({ ...tenantRow(t), accounts: t.accounts.length })),
        quotaHits: data.quotaHits.slice(0, SUMMARY_HEAD.quotaHits).map((h) => quotaHitRow(h, nameOf(h.tenantId))),
        minuteLimits: {
          mcpServerLimit: minute.mcpServerLimit ?? null,
          accounts: minute.accounts.slice(0, SUMMARY_HEAD.minuteLimits).map((m) => minuteRow(m, nameOf(m.tenantId))),
        },
        lists,
        ...(cut ? { note: 'Lists are cut to their first rows; lists gives each full length, the view of the same name shows it in full.' } : {}),
      };
    }
    case 'tools':
      return { ...head, tools: data.tools.map((t) => ({ ...toolRow(t), avgResultChars: t.avgResultChars ?? null, overCapCalls: t.overCapCalls ?? null, routes: t.routes })) };
    case 'tenants':
      return { ...head, tenants: data.tenants.map((t) => ({ ...tenantRow(t), accounts: t.accounts.map(accountRow) })) };
    case 'quotaHits':
      return { ...head, quotaHits: data.quotaHits.map((h) => quotaHitRow(h, nameOf(h.tenantId))) };
    case 'minuteLimits':
      return {
        ...head,
        mcpServerLimit: minute.mcpServerLimit ?? null,
        accounts: minute.accounts.map((m) => ({
          ...minuteRow(m, nameOf(m.tenantId)),
          mcpLastRefusedAt: m.mcpLastRefusedAt ?? null,
          backendLastRefusedAt: m.backendLastRefusedAt ?? null,
        })),
      };
    case 'daily':
      return { ...head, daily: data.daily };
  }
}

// ── One tenant ────────────────────────────────────────────────────────────────

/** One tenant's organization budget: its windows, every account charged to it, and the days. */
export function shapeOrganization(data: GetMcpOrganizationUsageResponse): Record<string, unknown> {
  return {
    view: 'organization',
    tenantId: data.tenantId,
    window: { from: data.dateFrom, to: data.dateTo },
    plan: data.quota.tenantPlan,
    today: quotaWindow(data.quota.dailyUsed, data.quota.dailyLimit),
    month: quotaWindow(data.quota.monthlyUsed, data.quota.monthlyLimit),
    requests: data.users.reduce((sum, u) => sum + u.requestsInRange, 0),
    accounts: data.users.map((u) => ({
      principal: u.userPrincipalName ?? null,
      userId: u.userId,
      requestsInRange: u.requestsInRange,
      requestsToday: u.requestsToday,
      requestsThisMonth: u.requestsThisMonth,
      lastRequestAt: u.lastRequestAt ?? null,
    })),
    daily: data.daily,
  };
}

// ── One account ───────────────────────────────────────────────────────────────

/** A counter key is `tool:path`, or the bare path when the request carried no tool name. */
function splitEndpoint(endpoint: string): { tool: string; path: string } {
  const colon = endpoint.indexOf(':');
  return colon > 0 ? { tool: endpoint.slice(0, colon), path: endpoint.slice(colon + 1) } : { tool: NO_TOOL, path: endpoint };
}

const byRequestsThenName = <T extends { requests: number }>(name: (row: T) => string) =>
  (a: T, b: T) => b.requests - a.requests || name(a).localeCompare(name(b));

/** One account's charged requests in the window, by tool (with the paths it requested) and by day. */
export function shapeAccount(data: GetMcpUserUsageResponse, window: { from: string; to: string }): Record<string, unknown> {
  const tools = new Map<string, Map<string, number>>();
  const days = new Map<string, number>();
  let newest: (typeof data.records)[number] | undefined;

  for (const record of data.records) {
    const { tool, path } = splitEndpoint(record.endpoint);
    const paths = tools.get(tool) ?? new Map<string, number>();
    paths.set(path, (paths.get(path) ?? 0) + record.requestCount);
    tools.set(tool, paths);
    days.set(record.date, (days.get(record.date) ?? 0) + record.requestCount);
    if (!newest || record.lastRequestAt > newest.lastRequestAt) newest = record;
  }

  const byTool = [...tools.entries()]
    .map(([tool, paths]) => ({
      tool,
      requests: [...paths.values()].reduce((sum, n) => sum + n, 0),
      paths: [...paths.entries()].map(([path, requests]) => ({ path, requests })).sort(byRequestsThenName((p) => p.path)),
    }))
    .sort(byRequestsThenName((t) => t.tool));

  return {
    view: 'account',
    userId: data.userId,
    principal: newest?.userPrincipalName || null,
    tenantId: newest?.tenantId || null,
    window,
    requests: byTool.reduce((sum, t) => sum + t.requests, 0),
    byTool,
    byDay: [...days.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([date, requests]) => ({ date, requests })),
  };
}
