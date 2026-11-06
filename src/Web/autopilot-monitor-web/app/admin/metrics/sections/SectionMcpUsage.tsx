"use client";

import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import dynamic from "next/dynamic";
import { useSearchParams } from "next/navigation";
import { useAuth } from "../../../../contexts/AuthContext";
import { apiErrorText, fetchJson } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { SegmentedControl } from "@/components/SegmentedControl";
import { CalculatingDialog } from "@/components/CalculatingCard";
import { chartColors } from "@/components/charts/chartTheme";
import { useFetchProgress } from "@/hooks/useFetchProgress";
import { useWindowDays } from "@/hooks/useWindowDays";
import { WINDOW_PRESET_OPTIONS } from "@/lib/timeWindow";
import type {
  GetGlobalMcpUsageOverviewResponse,
  PlanTierDefinitionsResponse,
  TenantConfiguration,
} from "@/lib/generated/wire-types.generated";
import { formatDayKey } from "../lib/format";
import { Stat } from "./Stat";
import {
  dailyChart,
  editionCounts,
  MCP_USAGE_MAX_DAYS,
  MCP_USAGE_VIEW_PARAM,
  parseView,
  planLegend,
  tenantInfoMap,
  type McpUsageView,
  type TenantInfo,
} from "./mcpUsage/mcpUsageLogic";
import { TenantsTab } from "./mcpUsage/TenantsTab";
import { ToolsTab } from "./mcpUsage/ToolsTab";
import { LimitsTab } from "./mcpUsage/LimitsTab";

function ChartPlaceholder() {
  return <div className="flex h-56 items-center justify-center text-sm text-gray-400">Loading chart…</div>;
}

// recharts (~300 kB) loads only here: SectionClient imports every metrics section statically.
const AppStackedBarChart = dynamic(() => import("@/components/charts/AppStackedBarChart"), { ssr: false, loading: ChartPlaceholder });

const DEFAULT_WINDOW_DAYS = 30;
const RANGE_DAY: Intl.DateTimeFormatOptions = { day: "numeric", month: "short", year: "numeric" };
const PLAN_COLORS: Record<string, string> = { community: chartColors.success, pro: chartColors.primary, msp: chartColors.warning };
const NO_TENANT_INFO: ReadonlyMap<string, TenantInfo> = new Map();

/**
 * GA metrics: MCP usage over every tenant (GET global/metrics/mcp-usage/overview). Who uses MCP (tenants,
 * accounts, service principals), which tools and at what cost per call, and where the quota and the two
 * per-minute limits bite. The window lives in ?days= (at most 90, the telemetry's reach), the tab in ?view=.
 */
export function SectionMcpUsage() {
  // useSearchParams (?days=, ?view=) requires a Suspense boundary under the static export.
  return (
    <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
      <McpUsageContent />
    </Suspense>
  );
}

function McpUsageContent() {
  const { getAccessToken } = useAuth();
  const searchParams = useSearchParams();
  // Platform roles may ask for more; the telemetry keeps 90 days, so the page shows at most that.
  const { days: requestedDays, setDays } = useWindowDays({ defaultDays: DEFAULT_WINDOW_DAYS, tenantId: undefined });
  const days = Math.min(requestedDays, MCP_USAGE_MAX_DAYS);
  const view = parseView(searchParams?.get(MCP_USAGE_VIEW_PARAM));
  const [includePlatformAdmins, setIncludePlatformAdmins] = useState(false);
  const [overview, setOverview] = useState<GetGlobalMcpUsageOverviewResponse | null>(null);
  const [tenantRows, setTenantRows] = useState<{ tenants: TenantConfiguration[]; nowMs: number } | null>(null);
  const [planTiers, setPlanTiers] = useState<PlanTierDefinitionsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const { begin, finish, elapsedMs, estimateMs } = useFetchProgress("mcpUsageOverview.estimateMs", 10_000);
  // A slower answer for an older window must never replace a newer one.
  const requestRef = useRef(0);

  const setView = useCallback((next: McpUsageView) => {
    const url = new URL(window.location.href);
    if (next === "tenants") url.searchParams.delete(MCP_USAGE_VIEW_PARAM);
    else url.searchParams.set(MCP_USAGE_VIEW_PARAM, next);
    window.history.replaceState(null, "", url.toString());
  }, []);

  const fetchOverview = useCallback(async () => {
    const request = ++requestRef.current;
    setLoading(true);
    setError(null);
    begin();
    let fresh = false;
    try {
      const data = await fetchJson<GetGlobalMcpUsageOverviewResponse>(
        api.mcpUsage.overview(days, includePlatformAdmins ? "include" : "exclude"),
        getAccessToken,
      );
      if (request === requestRef.current) {
        setOverview(data);
        fresh = true;
      }
    } catch (err) {
      if (request === requestRef.current) setError(apiErrorText(err, "Failed to load MCP usage"));
    } finally {
      if (request === requestRef.current) {
        setLoading(false);
        finish(fresh);
      }
    }
  }, [days, includePlatformAdmins, getAccessToken, begin, finish]);

  // Names, editions and the plan legend: once per visit. Without them the page shows tenant ids and no legend.
  const fetchReferenceData = useCallback(async () => {
    const [tenants, tiers] = await Promise.allSettled([
      fetchJson<TenantConfiguration[]>(api.config.all(), getAccessToken),
      fetchJson<PlanTierDefinitionsResponse>(api.mcpUsage.planTiers(), getAccessToken),
    ]);
    if (tenants.status === "fulfilled") setTenantRows({ tenants: tenants.value, nowMs: Date.now() });
    if (tiers.status === "fulfilled") setPlanTiers(tiers.value);
  }, [getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await fetchOverview();
    };
    void run();
  }, [fetchOverview]);

  useEffect(() => {
    const run = async () => {
      await fetchReferenceData();
    };
    void run();
  }, [fetchReferenceData]);

  const info = useMemo(
    () => (tenantRows ? tenantInfoMap(tenantRows.tenants, tenantRows.nowMs) : NO_TENANT_INFO),
    [tenantRows],
  );
  const editions = useMemo(() => (overview ? editionCounts(overview, info) : null), [overview, info]);
  const chart = useMemo(() => (overview ? dailyChart(overview) : null), [overview]);
  const plans = useMemo(() => planLegend(planTiers), [planTiers]);
  const totals = overview?.totals;

  const viewOptions: { value: McpUsageView; label: string }[] = [
    { value: "tenants", label: "Tenants & accounts" },
    { value: "tools", label: "Tools" },
    { value: "limits", label: totals && totals.limitAccounts > 0 ? `Limits (${totals.limitAccounts})` : "Limits" },
  ];

  return (
    // Self-provides the max-w container the metrics layout used to supply.
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">MCP Usage</h2>
          <p className="text-sm text-gray-500">
            {overview
              ? `${formatDayKey(overview.dateFrom, RANGE_DAY)} – ${formatDayKey(overview.dateTo, RANGE_DAY)} · ${overview.includesPlatformAdmins ? "platform admins included" : "customers only"}`
              : "MCP usage across all tenants"}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <label className="inline-flex items-center gap-2 text-sm text-gray-600" title="Global Admins and Global Readers">
            <input
              type="checkbox"
              checked={includePlatformAdmins}
              onChange={(e) => setIncludePlatformAdmins(e.target.checked)}
              className="rounded border-gray-300"
            />
            Include platform admins
          </label>
          <SegmentedControl options={WINDOW_PRESET_OPTIONS} value={days} onChange={setDays} />
          <button
            onClick={() => void fetchOverview()}
            disabled={loading}
            className="px-3 py-1.5 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-lg text-sm">{error}</div>
      )}

      {loading ? (
        <div className="flex justify-center py-8">
          <CalculatingDialog
            title="Reading MCP usage"
            subtitle="Quota counters and the request and MCP telemetry of every tenant"
            elapsedMs={elapsedMs}
            estimateMs={estimateMs}
          />
        </div>
      ) : overview && totals && editions && chart ? (
        <>
          <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
            <Stat
              label="Tool calls"
              value={totals.calls == null ? "—" : totals.calls.toLocaleString()}
              hint={totals.mcpOnlyCalls ? `${totals.tools} tools · ${totals.mcpOnlyCalls.toLocaleString()} MCP-only` : `${totals.tools} tools`}
            />
            <Stat label="Requests" value={totals.requests.toLocaleString()} hint="charged to the quota" />
            <Stat label="Tenants" value={totals.tenants.toLocaleString()} hint={`${editions.community} Community · ${editions.pro} Pro`} />
            <Stat
              label="Accounts"
              value={totals.accounts.toLocaleString()}
              hint={`${totals.accounts - totals.servicePrincipals} people · ${totals.servicePrincipals} ${totals.servicePrincipals === 1 ? "app" : "apps"}`}
            />
            <button
              type="button"
              onClick={() => setView("limits")}
              title="Show the limits"
              className="text-left rounded-lg focus:outline-none focus:ring-2 focus:ring-indigo-500"
            >
              <Stat
                label="Limit hits"
                value={totals.limitAccounts.toLocaleString()}
                hint={`accounts · ${totals.quotaHitAccounts} quota · ${totals.minuteRefusedAccounts} per minute`}
              />
            </button>
          </div>

          <div className="bg-white shadow rounded-lg p-6">
            <h3 className="text-sm font-medium text-gray-500">Calls per day</h3>
            {!overview.telemetry.requestsAvailable ? (
              <p className="py-10 text-center text-sm text-gray-500">The request telemetry did not answer: the calls per day are missing.</p>
            ) : chart.plans.length === 0 ? (
              <p className="py-10 text-center text-sm text-gray-500">No tool calls in this window.</p>
            ) : (
              <div className="mt-4">
                <AppStackedBarChart
                  data={chart.rows}
                  xKey="date"
                  stacks={chart.plans.map((plan) => ({ dataKey: `plan:${plan}`, label: plan, color: PLAN_COLORS[plan] ?? chartColors.muted }))}
                  height={220}
                  formatXTick={(value) => formatDayKey(String(value))}
                  renderTooltip={(row) => <DayTooltip row={row} plans={chart.plans} />}
                />
              </div>
            )}
          </div>

          <SegmentedControl options={viewOptions} value={view} onChange={setView} className="w-fit max-w-full overflow-x-auto" />

          {view === "tenants" && <TenantsTab tenants={overview.tenants} info={info} />}
          {view === "tools" && <ToolsTab tools={overview.tools} telemetry={overview.telemetry} dateTo={overview.dateTo} />}
          {view === "limits" && (
            <LimitsTab
              quotaHits={overview.quotaHits}
              minuteLimits={overview.minuteLimits}
              telemetry={overview.telemetry}
              plans={plans}
              info={info}
            />
          )}
        </>
      ) : null}
    </div>
  );
}

function DayTooltip({ row, plans }: { row: Record<string, unknown>; plans: readonly string[] }) {
  const line = (label: string, value: unknown, indent = false) => (
    <p key={label} className={`flex justify-between gap-4 ${indent ? "pl-3 text-gray-300" : ""}`}>
      <span>{label}</span>
      <span className="font-semibold tabular-nums">{Number(value ?? 0).toLocaleString()}</span>
    </p>
  );
  return (
    <div className="space-y-1">
      <p className="font-medium">{formatDayKey(String(row.date), { weekday: "short", day: "numeric", month: "short" })}</p>
      {line("Tool calls", row.calls)}
      {plans.map((plan) => line(plan, row[`plan:${plan}`], true))}
      {line("Requests", row.requests)}
      {line("Accounts", row.accounts)}
    </div>
  );
}
