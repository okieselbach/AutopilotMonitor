/**
 * Pure folds behind the GA MCP Usage page (/admin/metrics/mcp-usage). The route
 * (GET global/metrics/mcp-usage/overview) delivers the numbers; this module only joins tenant names,
 * filters, sorts and formats. "Calls" are tool calls, "requests" are backend requests charged to the
 * quota — the page never mixes the two in one figure.
 */
import { effectiveEdition, type PlanEdition } from "../../../components/tenantFilters";
import { isApplicationKey } from "@/lib/principalKeys";
import type {
  GetGlobalMcpUsageOverviewResponse,
  McpUsageAccountItem,
  McpUsageMinuteItem,
  McpUsageTenantItem,
  McpUsageToolItem,
  PlanTierDefinitionsResponse,
  TenantConfiguration,
} from "@/lib/generated/wire-types.generated";

// ── View (?view=) ─────────────────────────────────────────────────────────────

export type McpUsageView = "tenants" | "tools" | "limits";

export const MCP_USAGE_VIEW_PARAM = "view";

/** The tab a link asks for; anything unknown is the default tab. */
export function parseView(raw: string | null | undefined): McpUsageView {
  return raw === "tools" || raw === "limits" ? raw : "tenants";
}

/** The telemetry keeps 90 days; a longer ?days= window shows 90. */
export const MCP_USAGE_MAX_DAYS = 90;

// ── Tenant names ──────────────────────────────────────────────────────────────

/** What the page shows about a tenant beyond the overview: name and edition from config/all. */
export interface TenantInfo {
  domainName?: string;
  companyName?: string;
  edition: PlanEdition;
  mcpDisabled: boolean;
}

/** config/all by lower-cased tenant id — the overview's ids are lower-cased too. */
export function tenantInfoMap(tenants: readonly TenantConfiguration[], nowMs: number): Map<string, TenantInfo> {
  const map = new Map<string, TenantInfo>();
  for (const t of tenants) {
    map.set(t.tenantId.toLowerCase(), {
      domainName: t.domainName || undefined,
      companyName: t.companyName || undefined,
      edition: effectiveEdition(t, nowMs),
      mcpDisabled: !!t.mcpDisabled,
    });
  }
  return map;
}

/** Display name of a tenant: its domain, else the first block of its id. */
export function tenantLabel(tenantId: string, info: ReadonlyMap<string, TenantInfo>): string {
  return info.get(tenantId.toLowerCase())?.domainName ?? `${tenantId.slice(0, 8)}…`;
}

// ── Windows and limits ────────────────────────────────────────────────────────

/** Share of a window in whole percent; null when the window is unlimited (0) or the counter was unavailable (−1). */
export function usagePercent(used: number, limit: number): number | null {
  if (limit <= 0 || used < 0) return null;
  return Math.round((used / limit) * 100);
}

/** The bars turn amber here (QuotaBar) — the same threshold marks "near a limit". */
export const NEAR_LIMIT_PERCENT = 80;

interface WindowPair {
  dailyUsed: number;
  dailyLimit: number;
  monthlyUsed: number;
  monthlyLimit: number;
}

function dailyShare(q: WindowPair | undefined): number {
  return q ? usagePercent(q.dailyUsed, q.dailyLimit) ?? -1 : -1;
}

function monthlyShare(q: WindowPair | undefined): number {
  return q ? usagePercent(q.monthlyUsed, q.monthlyLimit) ?? -1 : -1;
}

export function accountNearLimit(a: McpUsageAccountItem): boolean {
  return a.quotaHits > 0 || Math.max(dailyShare(a.quota), monthlyShare(a.quota)) >= NEAR_LIMIT_PERCENT;
}

export function tenantNearLimit(t: McpUsageTenantItem): boolean {
  return t.quotaHits > 0
    || Math.max(dailyShare(t.quota), monthlyShare(t.quota)) >= NEAR_LIMIT_PERCENT
    || t.accounts.some(accountNearLimit);
}

// ── Sorting ───────────────────────────────────────────────────────────────────

export type UsageSortKey = "calls" | "requests" | "today" | "month" | "lastActive";
export type SortDir = "asc" | "desc";

function lastActiveMs(iso: string | undefined): number {
  return iso ? new Date(iso).getTime() : -1;
}

function sortValue(item: { calls: number; requests: number; lastRequestAt?: string }, quota: WindowPair | undefined, key: UsageSortKey): number {
  switch (key) {
    case "calls": return item.calls;
    case "requests": return item.requests;
    case "today": return dailyShare(quota);
    case "month": return monthlyShare(quota);
    case "lastActive": return lastActiveMs(item.lastRequestAt);
  }
}

function compare(a: number, b: number, dir: SortDir): number {
  return dir === "asc" ? a - b : b - a;
}

export function sortTenants(tenants: readonly McpUsageTenantItem[], key: UsageSortKey, dir: SortDir): McpUsageTenantItem[] {
  return [...tenants].sort((a, b) => compare(sortValue(a, a.quota, key), sortValue(b, b.quota, key), dir) || b.calls - a.calls);
}

export function sortAccounts(accounts: readonly McpUsageAccountItem[], key: UsageSortKey, dir: SortDir): McpUsageAccountItem[] {
  return [...accounts].sort((a, b) => compare(sortValue(a, a.quota, key), sortValue(b, b.quota, key), dir) || b.calls - a.calls);
}

export type ToolSortKey = "calls" | "requestsPerCall" | "errorRate" | "p95" | "size";

/** Tools by a column; a figure the MCP log did not deliver sorts below every real value. */
export function sortTools(tools: readonly McpUsageToolItem[], key: ToolSortKey, dir: SortDir): McpUsageToolItem[] {
  const value = (t: McpUsageToolItem): number => {
    switch (key) {
      case "calls": return t.calls;
      case "requestsPerCall": return t.requestsPerCall;
      case "errorRate": return t.errorRate ?? -1;
      case "p95": return t.p95DurationMs ?? -1;
      case "size": return t.avgResultChars ?? -1;
    }
  };
  return [...tools].sort((a, b) => compare(value(a), value(b), dir) || b.calls - a.calls || a.tool.localeCompare(b.tool));
}

// ── Search ────────────────────────────────────────────────────────────────────

export interface TenantMatch {
  tenant: McpUsageTenantItem;
  /** The accounts to list: all of them when the tenant matched, else only the matching ones. */
  accounts: McpUsageAccountItem[];
  /** The query matched an account, not the tenant — the row opens by itself. */
  matchedByAccount: boolean;
}

/**
 * Tenants matching the query by domain, company or id; or holding an account whose principal (UPN or
 * app:<client-id>) matches. Case-insensitive substring; an empty query keeps everything.
 */
export function searchTenants(
  tenants: readonly McpUsageTenantItem[],
  info: ReadonlyMap<string, TenantInfo>,
  query: string,
  nearLimitOnly: boolean,
): TenantMatch[] {
  const q = query.trim().toLowerCase();
  const result: TenantMatch[] = [];
  for (const tenant of tenants) {
    if (nearLimitOnly && !tenantNearLimit(tenant)) continue;
    const accounts = nearLimitOnly ? tenant.accounts.filter(accountNearLimit) : tenant.accounts;
    if (!q) {
      result.push({ tenant, accounts: [...accounts], matchedByAccount: false });
      continue;
    }
    const t = info.get(tenant.tenantId.toLowerCase());
    const tenantHit = [tenant.tenantId, t?.domainName, t?.companyName].some((v) => v?.toLowerCase().includes(q));
    if (tenantHit) {
      result.push({ tenant, accounts: [...accounts], matchedByAccount: false });
      continue;
    }
    const hits = accounts.filter((a) => a.principal.toLowerCase().includes(q));
    if (hits.length > 0) result.push({ tenant, accounts: hits, matchedByAccount: true });
  }
  return result;
}

// ── Totals (the tiles) ────────────────────────────────────────────────────────

export interface OverviewTotals {
  calls: number;
  /** Calls of tools that run in the MCP server alone — counted in `calls`, attributed to no account. */
  mcpOnlyCalls: number;
  toolCount: number;
  requests: number;
  tenants: number;
  communityTenants: number;
  proTenants: number;
  accounts: number;
  apps: number;
  /** Accounts that met any limit in the window. */
  limitAccounts: number;
  quotaHitAccounts: number;
  minuteRefusedAccounts: number;
}

export function overviewTotals(overview: GetGlobalMcpUsageOverviewResponse, info: ReadonlyMap<string, TenantInfo>): OverviewTotals {
  const accounts = overview.tenants.flatMap((t) => t.accounts);
  const editionOf = (tenantId: string) => info.get(tenantId.toLowerCase())?.edition;
  const quotaHit = new Set(overview.quotaHits.map((h) => `${h.tenantId}|${h.principal ?? ""}`));
  const minuteRefused = new Set(
    overview.minuteLimits.accounts.filter((m) => m.mcpRefused > 0 || m.backendRefused > 0).map((m) => `${m.tenantId}|${m.principal}`),
  );
  return {
    calls: overview.tools.reduce((sum, t) => sum + t.calls, 0),
    mcpOnlyCalls: overview.tools.filter((t) => t.accounts == null).reduce((sum, t) => sum + t.calls, 0),
    toolCount: overview.tools.length,
    requests: overview.tenants.reduce((sum, t) => sum + t.requests, 0),
    tenants: overview.tenants.length,
    communityTenants: overview.tenants.filter((t) => editionOf(t.tenantId) === "community").length,
    proTenants: overview.tenants.filter((t) => editionOf(t.tenantId)?.startsWith("pro")).length,
    accounts: accounts.length,
    apps: accounts.filter((a) => isApplicationKey(a.principal)).length,
    limitAccounts: new Set([...quotaHit, ...minuteRefused]).size,
    quotaHitAccounts: quotaHit.size,
    minuteRefusedAccounts: minuteRefused.size,
  };
}

// ── Daily chart ───────────────────────────────────────────────────────────────

export interface DailyChart {
  rows: Array<Record<string, string | number>>;
  /** Usage plans with calls in the window, most calls first — one stack each. */
  plans: string[];
}

/** One row per day: date, calls, requests, accounts and one column per usage plan (its calls). */
export function dailyChart(overview: GetGlobalMcpUsageOverviewResponse): DailyChart {
  const totals = new Map<string, number>();
  for (const day of overview.daily) for (const p of day.callsByPlan) totals.set(p.plan, (totals.get(p.plan) ?? 0) + p.calls);
  const plans = [...totals.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([plan]) => plan);
  const rows = overview.daily.map((day) => {
    const row: Record<string, string | number> = { date: day.date, calls: day.calls, requests: day.requests, accounts: day.accounts };
    for (const plan of plans) row[`plan:${plan}`] = day.callsByPlan.find((p) => p.plan === plan)?.calls ?? 0;
    return row;
  });
  return { rows, plans };
}

// ── Per-minute limits ─────────────────────────────────────────────────────────

/** A peak counts as close from here; below it an account only shows with a refusal. */
export const MINUTE_SHOW_PERCENT = 50;

/** The higher of an account's two per-minute shares (MCP server, backend), in whole percent. */
export function minuteShare(row: McpUsageMinuteItem, mcpLimit: number | undefined): number {
  const mcp = mcpLimit && mcpLimit > 0 ? (row.peakCalls / mcpLimit) * 100 : 0;
  const backend = row.backendLimit > 0 ? (row.peakRequests / row.backendLimit) * 100 : 0;
  return Math.round(Math.max(mcp, backend));
}

/** Rows worth a look: a refusal, or a peak at half a limit or more. */
export function minuteRowsToShow(rows: readonly McpUsageMinuteItem[], mcpLimit: number | undefined): McpUsageMinuteItem[] {
  return rows.filter((r) => r.mcpRefused > 0 || r.backendRefused > 0 || minuteShare(r, mcpLimit) >= MINUTE_SHOW_PERCENT);
}

// ── Plan legend ───────────────────────────────────────────────────────────────

export interface PlanLegendRow {
  name: string;
  daily: number;
  monthly: number;
  /** Organization windows; absent when the plan leaves them to the tenant's edition. */
  tenantDaily?: number;
  tenantMonthly?: number;
}

/**
 * The base windows of each usage plan (0 = unlimited). A blank organization window of the community
 * or pro plan falls back to that edition's catalog value; another plan's blank window follows the
 * tenant's edition, so it stays open here.
 */
export function planLegend(response: PlanTierDefinitionsResponse | null): PlanLegendRow[] {
  if (!response) return [];
  return response.tiers.map((tier) => {
    const catalog = response.catalog.find((c) => c.edition === tier.name.toLowerCase());
    return {
      name: tier.name,
      daily: tier.dailyRequestLimit,
      monthly: tier.monthlyRequestLimit,
      tenantDaily: tier.tenantDailyRequestLimit ?? catalog?.tenantDailyRequestLimit,
      tenantMonthly: tier.tenantMonthlyRequestLimit ?? catalog?.tenantMonthlyRequestLimit,
    };
  });
}

// ── Formatting ────────────────────────────────────────────────────────────────

/** A UTC day (yyyyMMdd) in the viewer's locale, never shifted by the viewer's time zone. */
export function formatDayKey(key: string, options: Intl.DateTimeFormatOptions = { day: "numeric", month: "short" }): string {
  if (!/^\d{8}$/.test(key)) return key;
  const iso = `${key.slice(0, 4)}-${key.slice(4, 6)}-${key.slice(6, 8)}T00:00:00Z`;
  return new Date(iso).toLocaleDateString(undefined, { ...options, timeZone: "UTC" });
}

/** "0.8 s" from 812 ms, "95 ms" below a second, "—" without a value. */
export function formatDuration(ms: number | undefined): string {
  if (ms == null) return "—";
  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
}

/** Answer size in KB (one character ≈ one byte of JSON), "—" without a value. */
export function formatChars(chars: number | undefined): string {
  if (chars == null) return "—";
  return chars < 1000 ? `${chars} B` : `${(chars / 1000).toFixed(1)} KB`;
}

/** A 0–1 share as "12.5 %", "0 %" for none, "—" without a value. */
export function formatShare(share: number | undefined): string {
  if (share == null) return "—";
  if (share === 0) return "0 %";
  const pct = share * 100;
  return `${pct < 10 ? pct.toFixed(1) : Math.round(pct)} %`;
}
