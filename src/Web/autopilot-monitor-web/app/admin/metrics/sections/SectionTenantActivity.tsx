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
import type { GetTenantActivityResponse } from "@/lib/generated/wire-types.generated";
import { formatDayKey } from "../lib/format";
import { Stat } from "./Stat";
import { LifecycleTab } from "./tenantActivity/LifecycleTab";
import { RankingTab } from "./tenantActivity/RankingTab";
import {
  changeText,
  dailyChartRows,
  parseView,
  percentText,
  TENANT_ACTIVITY_MAX_DAYS,
  TENANT_ACTIVITY_VIEW_PARAM,
  type TenantActivityView,
} from "./tenantActivity/tenantActivityLogic";

function ChartLoading() {
  return <div className="flex h-56 items-center justify-center text-sm text-gray-400">Loading chart…</div>;
}

// recharts loads only when this section renders: SectionClient imports every metrics section statically.
const AppStackedBarChart = dynamic(() => import("@/components/charts/AppStackedBarChart"), { ssr: false, loading: ChartLoading });

const DEFAULT_WINDOW_DAYS = 30;
const LONG_DAY: Intl.DateTimeFormatOptions = { day: "numeric", month: "short", year: "numeric" };
const DAY_STACKS = [
  { dataKey: "succeeded", label: "Succeeded", color: chartColors.success },
  { dataKey: "failed", label: "Failed", color: chartColors.danger },
  { dataKey: "other", label: "Running or incomplete", color: chartColors.muted },
];

/**
 * GA metrics: how every customer tenant uses the product (GET global/metrics/tenant-activity) — the most active
 * tenants, their trend against the window before and the tenants that need a look. The window lives in ?days= (at
 * most 90 full UTC days, through the last aggregated day), the tab in ?view=.
 */
export function SectionTenantActivity() {
  // useSearchParams (?days=, ?view=) requires a Suspense boundary under the static export.
  return (
    <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
      <TenantActivityContent />
    </Suspense>
  );
}

function TenantActivityContent() {
  const { getAccessToken } = useAuth();
  const searchParams = useSearchParams();
  // Platform roles may ask for more; the daily rows and the window before fit 90 days, so the page shows at most that.
  const { days: requestedDays, setDays } = useWindowDays({ defaultDays: DEFAULT_WINDOW_DAYS, tenantId: undefined });
  const days = Math.min(requestedDays, TENANT_ACTIVITY_MAX_DAYS);
  const view = parseView(searchParams?.get(TENANT_ACTIVITY_VIEW_PARAM));
  // The answer and the clock it was read at, set together so a render never mixes two fetches.
  const [snapshot, setSnapshot] = useState<{ data: GetTenantActivityResponse; nowMs: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const progress = useFetchProgress("tenantActivity.estimateMs", 5_000);
  const { begin, finish } = progress;
  // An answer for an older window must never replace a newer one.
  const latestRequest = useRef(0);

  const load = useCallback(async () => {
    const request = ++latestRequest.current;
    setLoading(true);
    setError(null);
    begin();
    let answered = false;
    try {
      const data = await fetchJson<GetTenantActivityResponse>(api.metrics.globalTenantActivity(days), getAccessToken);
      if (request === latestRequest.current) {
        setSnapshot({ data, nowMs: Date.now() });
        answered = true;
      }
    } catch (err) {
      if (request === latestRequest.current) setError(apiErrorText(err, "Failed to load tenant activity"));
    } finally {
      if (request === latestRequest.current) {
        setLoading(false);
        finish(answered);
      }
    }
  }, [days, getAccessToken, begin, finish]);

  useEffect(() => {
    const run = async () => {
      await load();
    };
    void run();
  }, [load]);

  const showView = useCallback((next: TenantActivityView) => {
    const url = new URL(window.location.href);
    if (next === "ranking") url.searchParams.delete(TENANT_ACTIVITY_VIEW_PARAM);
    else url.searchParams.set(TENANT_ACTIVITY_VIEW_PARAM, next);
    window.history.replaceState(null, "", url.toString());
  }, []);

  const chartRows = useMemo(() => (snapshot ? dailyChartRows(snapshot.data.daily) : []), [snapshot]);
  const data = snapshot?.data;
  const stages = data?.totals.stages;
  const needLook = stages ? stages.new + stages.declining + stages.dormant + stages.neverEnrolled : 0;
  const tabs: { value: TenantActivityView; label: string }[] = [
    { value: "ranking", label: "Ranking" },
    { value: "lifecycle", label: needLook > 0 ? `Lifecycle (${needLook.toLocaleString()})` : "Lifecycle" },
  ];

  return (
    <div className="mx-auto max-w-7xl space-y-6 px-4 py-8 sm:px-6 lg:px-8">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Tenant Activity</h2>
          <p className="text-sm text-gray-500">
            {data
              ? `${formatDayKey(data.windowStart, LONG_DAY)} – ${formatDayKey(data.usageDaysThrough, LONG_DAY)} · full UTC days, today not yet counted`
              : "How every customer tenant uses the product"}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <SegmentedControl options={WINDOW_PRESET_OPTIONS} value={days} onChange={setDays} />
          <button
            type="button"
            onClick={() => void load()}
            disabled={loading}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div role="alert" className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
          {error}
        </div>
      )}

      {loading ? (
        <div className="flex justify-center py-8">
          <CalculatingDialog
            title="Reading tenant activity"
            subtitle="Daily usage rows, the tenant counters and the portal sign-in history"
            elapsedMs={progress.elapsedMs}
            estimateMs={progress.estimateMs}
          />
        </div>
      ) : snapshot && data && stages ? (
        <>
          <div className="grid grid-cols-2 gap-4 md:grid-cols-3 lg:grid-cols-5">
            <Stat
              label="Active tenants"
              value={data.totals.activeTenants.toLocaleString()}
              hint={`of ${data.totals.signedUpTenants.toLocaleString()} signed up · ${stages.new.toLocaleString()} new`}
            />
            <Stat
              label="Enrollments"
              value={data.totals.enrollments.toLocaleString()}
              hint={changeText(data.totals.enrollmentsTrendPercent, data.windowDays)}
            />
            <Stat
              label="Success rate"
              value={percentText(data.totals.successPercent)}
              hint={`${data.totals.failed.toLocaleString()} failed`}
            />
            <Stat label="Top 10 share" value={percentText(data.totals.top10SharePercent)} hint="of the enrollments" />
            <Stat
              label="Portal users"
              value={data.totals.portalUsers == null ? "—" : data.totals.portalUsers.toLocaleString()}
              hint={data.totals.portalTenants == null ? "sign-in history unavailable" : `in ${data.totals.portalTenants.toLocaleString()} tenants`}
            />
          </div>

          <div className="rounded-lg bg-white p-6 shadow">
            <h3 className="text-sm font-medium text-gray-500">Enrollments per day</h3>
            {data.totals.enrollments === 0 ? (
              <p className="py-10 text-center text-sm text-gray-500">No enrollments in this window.</p>
            ) : (
              <div className="mt-4">
                <AppStackedBarChart
                  data={chartRows}
                  xKey="day"
                  stacks={DAY_STACKS}
                  height={220}
                  formatXTick={(value) => formatDayKey(String(value))}
                  renderTooltip={(row) => <EnrollmentDayTooltip row={row} />}
                />
              </div>
            )}
          </div>

          <SegmentedControl options={tabs} value={view} onChange={showView} className="w-fit max-w-full overflow-x-auto" />

          {view === "ranking" ? (
            <RankingTab
              tenants={data.tenants}
              totalEnrollments={data.totals.enrollments}
              windowStart={data.windowStart}
              windowDays={data.windowDays}
              portalAvailable={data.portalActivityAvailable}
              nowMs={snapshot.nowMs}
            />
          ) : (
            <LifecycleTab tenants={data.tenants} portalAvailable={data.portalActivityAvailable} nowMs={snapshot.nowMs} />
          )}
        </>
      ) : null}
    </div>
  );
}

const TOOLTIP_LINES: ReadonlyArray<[string, string]> = [
  ["Enrollments", "enrollments"],
  ["Succeeded", "succeeded"],
  ["Failed", "failed"],
  ["Running or incomplete", "other"],
  ["Active tenants", "activeTenants"],
];

function EnrollmentDayTooltip({ row }: { row: Record<string, unknown> }) {
  return (
    <div className="space-y-1">
      <p className="font-medium">{formatDayKey(String(row.day), { weekday: "short", day: "numeric", month: "short" })}</p>
      {TOOLTIP_LINES.map(([label, key]) => (
        <p key={key} className="flex justify-between gap-4">
          <span>{label}</span>
          <span className="font-semibold tabular-nums">{Number(row[key] ?? 0).toLocaleString()}</span>
        </p>
      ))}
    </div>
  );
}
