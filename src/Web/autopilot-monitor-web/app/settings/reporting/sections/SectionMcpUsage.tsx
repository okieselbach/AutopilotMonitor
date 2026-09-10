"use client";

import { SegmentedControl, TIME_RANGE_OPTIONS } from "@/components/SegmentedControl";
import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { ApiError, apiErrorText, fetchJson } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { scopedApi } from "@/lib/scopedApi";
import { useGlobalAdminScope } from "@/hooks";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type {
  GetMcpOrganizationUsageResponse,
  GetMyMcpUsageResponse,
  McpOrganizationQuotaNode,
  McpUsageQuotaNode,
} from "@/utils/wire-types.generated";
import { isApplicationKey, principalLabel } from "@/utils/principalKeys";

interface UsageRecord {
  userId: string;
  userPrincipalName: string;
  tenantId: string;
  endpoint: string;
  date: string;
  requestCount: number;
  lastRequestAt: string;
}

interface DailyAggregate {
  date: string;
  totalRequests: number;
  endpoints: number;
}

// GetMyMcpUsageResponse.quota — the caller's own windows plus the organization-wide windows every
// member shares. 0 = unlimited; -1 used = counters temporarily unavailable.
type QuotaState = McpUsageQuotaNode;

function formatLastRequest(iso: string | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleString();
}

function QuotaBar({ label, used, limit }: { label: string; used: number; limit: number }) {
  const unavailable = used < 0;
  const unlimited = limit <= 0;
  const pct = unavailable || unlimited ? 0 : Math.min(100, Math.round((used / limit) * 100));
  const tone = pct >= 100 ? "bg-red-500" : pct >= 80 ? "bg-amber-500" : "bg-indigo-500";
  return (
    <div>
      <div className="flex items-center justify-between text-xs text-gray-600 mb-1">
        <span>{label}</span>
        <span className="font-mono">
          {unavailable ? "n/a" : used.toLocaleString()}
          {" / "}
          {unlimited ? "unlimited" : limit.toLocaleString()}
        </span>
      </div>
      <div className="bg-gray-100 rounded-full h-2">
        <div className={`${tone} h-2 rounded-full transition-all`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

type DateRange = "7d" | "30d" | "90d";

function formatDate(yyyymmdd: string): string {
  if (yyyymmdd.length !== 8) return yyyymmdd;
  return `${yyyymmdd.slice(0, 4)}-${yyyymmdd.slice(4, 6)}-${yyyymmdd.slice(6, 8)}`;
}

function getDateFrom(range: DateRange): string {
  const d = new Date();
  const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10).replace(/-/g, "");
}

function getDateTo(): string {
  return new Date().toISOString().slice(0, 10).replace(/-/g, "");
}

export function SectionMcpUsage() {
  const { getAccessToken, user } = useAuth();
  const canSeeOrganization = !!(user?.isTenantAdmin || user?.isGlobalAdmin);

  // Global-admin tenant scope (override-only: always a concrete tenant, defaulting to the caller's own).
  // Only the organization cards follow the selection — the caller's own quota and request history never
  // do. A delegated (MSP) caller keeps the member path: the organization route never lists a managed
  // tenant's accounts, so the selector stays hidden for them.
  const scope = useGlobalAdminScope();
  const crossTenant = scope.routeGlobal && !scope.isDelegatedScope;
  const { effectiveTenantId, isGlobalOverride } = scope;

  const [records, setRecords] = useState<UsageRecord[]>([]);
  // Organization budget by account + the tenant's windows — tenant admins / GA only; null = not loaded / not permitted.
  const [orgUsage, setOrgUsage] = useState<GetMcpOrganizationUsageResponse | null>(null);
  const [usagePlan, setUsagePlan] = useState<string | null>(null);
  const [effectivePlan, setEffectivePlan] = useState<string | null>(null);
  const [quota, setQuota] = useState<QuotaState | null>(null);
  const [upn, setUpn] = useState<string>("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>("30d");

  // Latest-wins guard: a tenant switch starts a new fetch while an older one may still be in flight;
  // only the most recently started request may write state.
  const fetchSeqRef = useRef(0);

  const fetchUsage = useCallback(async (range: DateRange) => {
    // The cross-tenant path needs the selected tenant; the member path is JWT-bound.
    if (crossTenant && !effectiveTenantId) return;
    const seq = ++fetchSeqRef.current;
    const isCurrent = () => fetchSeqRef.current === seq;
    setLoading(true);
    setError(null);
    try {
      const dateFrom = getDateFrom(range);
      const dateTo = getDateTo();
      const orgSelection = { routeGlobal: crossTenant, selectedTenantId: effectiveTenantId, effectiveTenantId };
      const [data, org] = await Promise.all([
        fetchJson<GetMyMcpUsageResponse>(api.mcpUsage.me(dateFrom, dateTo), getAccessToken),
        canSeeOrganization
          ? // Every account charged to the tenant's organization budget — including delegated (MSP)
            // administrators reading it. A 403 (role changed mid-session) just hides the card.
            fetchJson<GetMcpOrganizationUsageResponse>(
              scopedApi.mcpOrganizationUsage(orgSelection, dateFrom, dateTo),
              getAccessToken
            ).catch((err: unknown) => {
              if (err instanceof ApiError) return null;
              throw err;
            })
          : Promise.resolve(null),
      ]);
      if (!isCurrent()) return;
      setRecords(data.records || []);
      setUsagePlan(data.usagePlan || null);
      setEffectivePlan(data.effectivePlan || null);
      setQuota(data.quota ?? null);
      setUpn(data.upn || "");
      setOrgUsage(org);
    } catch (err) {
      if (!isCurrent()) return;
      setError(apiErrorText(err, "Failed to fetch usage data"));
    } finally {
      if (isCurrent()) setLoading(false);
    }
  }, [getAccessToken, canSeeOrganization, crossTenant, effectiveTenantId]);

  useEffect(() => {
    const run = async () => {
      await fetchUsage(dateRange);
    };
    void run();
  }, [fetchUsage, dateRange]);

  const orgUsers = orgUsage?.users ?? null;
  const delegatedReaders = orgUsers?.filter((u) => u.delegated).length ?? 0;

  // Organization windows: from the organization response when the caller may read it; otherwise
  // (members without an admin role) from the caller's own quota node — but only on the own tenant,
  // because under a tenant override that node describes the CALLER's tenant, not the selected one.
  const orgQuota: McpOrganizationQuotaNode | null =
    orgUsage?.quota ??
    (!crossTenant && quota
      ? {
          tenantPlan: quota.tenantPlan,
          dailyLimit: quota.tenantDailyLimit,
          monthlyLimit: quota.tenantMonthlyLimit,
          dailyUsed: quota.tenantDailyUsed,
          monthlyUsed: quota.tenantMonthlyUsed,
        }
      : null);

  // Wording follows the viewpoint: the caller's own tenant, or the tenant a global admin picked.
  const tenantNoun = isGlobalOverride ? "this tenant" : "your tenant";
  const budgetNoun = isGlobalOverride ? "this tenant's organization budget" : "your organization budget";
  const planNoun = isGlobalOverride ? "its plan" : "your plan";

  // Aggregate records by date
  const dailyAggregates: DailyAggregate[] = (() => {
    const byDate = new Map<string, { total: number; endpoints: Set<string> }>();
    for (const r of records) {
      const existing = byDate.get(r.date);
      if (existing) {
        existing.total += r.requestCount;
        existing.endpoints.add(r.endpoint);
      } else {
        byDate.set(r.date, { total: r.requestCount, endpoints: new Set([r.endpoint]) });
      }
    }
    return Array.from(byDate.entries())
      .map(([date, v]) => ({ date, totalRequests: v.total, endpoints: v.endpoints.size }))
      .sort((a, b) => b.date.localeCompare(a.date));
  })();

  const totalRequests = dailyAggregates.reduce((sum, d) => sum + d.totalRequests, 0);
  const todayStr = getDateTo();
  const todayRequests = dailyAggregates.find(d => d.date === todayStr)?.totalRequests ?? 0;
  const maxDaily = Math.max(...dailyAggregates.map(d => d.totalRequests), 1);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">MCP Usage</h2>
          {upn && <p className="text-sm text-gray-500">{upn}</p>}
        </div>
        <div className="flex flex-wrap items-center gap-3">
          {!scope.isDelegatedScope && <TenantScopeSelector scope={scope} />}
          {/* Plan Badge */}
          {usagePlan && (
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-indigo-100 text-indigo-800">
              Plan: {usagePlan}
            </span>
          )}
          {!usagePlan && (
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600">
              Plan: {effectivePlan ? `${effectivePlan} (inherited)` : "inherited"}
            </span>
          )}
          {/* Date Range Selector */}
          <SegmentedControl
            options={TIME_RANGE_OPTIONS}
            value={dateRange}
            onChange={(v) => setDateRange(v as DateRange)}
          />
          <button
            onClick={() => fetchUsage(dateRange)}
            disabled={loading}
            className="px-3 py-1.5 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            {loading ? "Loading..." : "Refresh"}
          </button>
          <DocsLink path={DOCS_PATHS.mcpUsage} />
        </div>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-lg text-sm">
          {error}
        </div>
      )}

      {/* Quota: the caller's own windows and the organization-wide windows shared by every member */}
      {(quota || orgQuota) && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {quota && (
            <div className="bg-white rounded-lg shadow p-4 sm:p-6 space-y-3">
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-medium text-gray-900">Your quota</h3>
                <span className="text-xs text-gray-500">plan {effectivePlan ?? "—"}</span>
              </div>
              <QuotaBar label="Today" used={quota.dailyUsed} limit={quota.dailyLimit} />
              <QuotaBar label="This month" used={quota.monthlyUsed} limit={quota.monthlyLimit} />
            </div>
          )}
          {orgQuota && (
            <div className="bg-white rounded-lg shadow p-4 sm:p-6 space-y-3">
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-medium text-gray-900">Organization quota</h3>
                <span className="text-xs text-gray-500">tenant plan {orgQuota.tenantPlan}</span>
              </div>
              <QuotaBar label="Today (all members)" used={orgQuota.dailyUsed} limit={orgQuota.dailyLimit} />
              <QuotaBar label="This month (all members)" used={orgQuota.monthlyUsed} limit={orgQuota.monthlyLimit} />
              <p className="text-xs text-gray-500">
                Shared by every account in {tenantNoun} and by delegated (MSP) administrators reading it. A personal
                plan override widens only the account&apos;s own windows, never these.
              </p>
              {user?.isDelegated && (
                <p className="text-xs text-gray-500">
                  Your reads into tenants you manage are charged to that tenant&apos;s own plan, not to these windows.
                </p>
              )}
            </div>
          )}
        </div>
      )}

      {/* Organization usage by account (tenant admins) */}
      {orgUsers && (
        <div className="bg-white rounded-lg shadow p-4 sm:p-6 space-y-3">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-medium text-gray-900">Organization usage by account</h3>
            <span className="text-xs text-gray-500">
              {orgUsers.length} account{orgUsers.length === 1 ? "" : "s"}
              {delegatedReaders > 0 && ` · ${delegatedReaders} delegated`}
            </span>
          </div>
          {orgUsers.length === 0 ? (
            <p className="text-sm text-gray-500">No requests have been charged to {budgetNoun} yet.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="min-w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-gray-500 border-b border-gray-100">
                    <th className="py-1.5 pr-3 font-medium">Account</th>
                    <th className="py-1.5 pr-3 font-medium text-right">Today</th>
                    <th className="py-1.5 pr-3 font-medium text-right">This month</th>
                    <th className="py-1.5 pr-3 font-medium text-right">Range ({dateRange})</th>
                    <th className="py-1.5 font-medium">Last request</th>
                  </tr>
                </thead>
                <tbody>
                  {orgUsers.map((u) => (
                    <tr key={u.userId} className="border-b border-gray-50 last:border-0">
                      <td className="py-1.5 pr-3 min-w-0">
                        <span className="text-gray-900 break-all">{u.userPrincipalName ? principalLabel(u.userPrincipalName) : u.userId}</span>
                        {isApplicationKey(u.userPrincipalName) && (
                          <span
                            className="ml-2 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-700 whitespace-nowrap"
                            title="Service principal — automation calling with an app-only token (read-only member)"
                          >
                            App
                          </span>
                        )}
                        {u.delegated && (
                          <span
                            className="ml-2 inline-flex items-center px-1.5 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800 whitespace-nowrap"
                            title={u.homeTenantId ? `Home tenant ${u.homeTenantId}` : undefined}
                          >
                            Delegated (MSP)
                          </span>
                        )}
                      </td>
                      <td className="py-1.5 pr-3 text-right font-mono text-gray-700">{u.requestsToday.toLocaleString()}</td>
                      <td className="py-1.5 pr-3 text-right font-mono text-gray-700">{u.requestsThisMonth.toLocaleString()}</td>
                      <td className="py-1.5 pr-3 text-right font-mono text-gray-700">{u.requestsInRange.toLocaleString()}</td>
                      <td className="py-1.5 text-xs text-gray-500 whitespace-nowrap">{formatLastRequest(u.lastRequestAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="text-xs text-gray-500">
            Every request counted against the organization windows above, by the account that made it. Delegated
            (MSP) administrators reading {tenantNoun} appear here too — their reads draw on {planNoun}.
          </p>
        </div>
      )}

      {/* Summary Cards */}
      <div className="grid grid-cols-2 md:grid-cols-3 gap-4">
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Today&apos;s Requests</div>
          <div className="text-2xl sm:text-3xl font-bold text-indigo-600">{todayRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Total Requests ({dateRange})</div>
          <div className="text-2xl sm:text-3xl font-bold text-blue-600">{totalRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Active Days</div>
          <div className="text-2xl sm:text-3xl font-bold text-green-600">{dailyAggregates.length}</div>
        </div>
      </div>

      {/* Daily Usage Chart (bar chart via divs) */}
      {dailyAggregates.length > 0 && (
        <div className="bg-white rounded-lg shadow p-6">
          <h3 className="text-sm font-medium text-gray-900 mb-4">Daily Requests</h3>
          <div className="space-y-2">
            {dailyAggregates.slice(0, 30).map((day) => (
              <div key={day.date} className="flex items-center gap-3">
                <div className="w-24 text-xs text-gray-500 font-mono shrink-0">
                  {formatDate(day.date)}
                </div>
                <div className="flex-1 bg-gray-100 rounded-full h-5 relative">
                  <div
                    className="bg-indigo-500 h-5 rounded-full transition-all"
                    style={{ width: `${Math.max((day.totalRequests / maxDaily) * 100, 2)}%` }}
                  />
                </div>
                <div className="w-16 text-xs text-gray-600 text-right shrink-0">
                  {day.totalRequests.toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty State */}
      {!loading && records.length === 0 && !error && (
        <div className="bg-white rounded-lg shadow p-12 text-center">
          <p className="text-gray-500">No usage data found for the selected period.</p>
        </div>
      )}
    </div>
  );
}
