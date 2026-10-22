"use client";

import { Suspense, useCallback, useEffect, useMemo, useState } from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useAuth } from "../../../../contexts/AuthContext";
import { apiErrorText, fetchJson } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { tenantManagementUrl } from "@/lib/routes";
import { SegmentedControl } from "@/components/SegmentedControl";
import { CardSkeleton } from "@/components/skeletons/PageSkeleton";
import { chartColors } from "@/components/charts/chartTheme";
import { useWindowDays } from "@/hooks/useWindowDays";
import { PLATFORM_MAX_WINDOW_DAYS, WINDOW_PRESET_OPTIONS } from "@/lib/timeWindow";
import type { TenantConfiguration } from "@/lib/generated/wire-types.generated";
import { editionLabel, effectiveEdition, facetOption } from "../../components/tenantFilters";
import { customerGrowth, type GrowthBucket } from "./customerGrowthLogic";
import { Stat } from "./Stat";

function ChartPlaceholder() {
  return <div className="flex h-64 items-center justify-center text-sm text-gray-400">Loading chart…</div>;
}

// recharts (~300 kB) loads only here: SectionClient imports every metrics section statically.
const AppBarChart = dynamic(() => import("@/components/charts/AppBarChart"), { ssr: false, loading: ChartPlaceholder });
const AppLineChart = dynamic(() => import("@/components/charts/AppLineChart"), { ssr: false, loading: ChartPlaceholder });

/** The portal presets plus the year. */
const RANGE_OPTIONS = [...WINDOW_PRESET_OPTIONS, { value: PLATFORM_MAX_WINDOW_DAYS, label: `${PLATFORM_MAX_WINDOW_DAYS} Days` }];
const DEFAULT_WINDOW_DAYS = 30;

const WEEKDAY: Intl.DateTimeFormatOptions = { weekday: "short" };
const SHORT_DAY: Intl.DateTimeFormatOptions = { day: "numeric", month: "short" };
const FULL_DAY: Intl.DateTimeFormatOptions = { weekday: "short", day: "numeric", month: "short", year: "numeric" };
const RANGE_DAY: Intl.DateTimeFormatOptions = { day: "numeric", month: "short", year: "numeric" };

/** A UTC calendar day (yyyy-MM-dd) in the viewer's locale, never shifted by the viewer's time zone. */
function formatDay(key: string, options: Intl.DateTimeFormatOptions): string {
  return new Date(`${key}T00:00:00Z`).toLocaleDateString(undefined, { ...options, timeZone: "UTC" });
}

function bucketTitle(bucket: { startKey: string; endKey: string }, weekly: boolean): string {
  return weekly
    ? `${formatDay(bucket.startKey, RANGE_DAY)} – ${formatDay(bucket.endKey, RANGE_DAY)}`
    : formatDay(bucket.startKey, FULL_DAY);
}

function signupTime(atMs: number, weekly: boolean): string {
  const options: Intl.DateTimeFormatOptions = weekly
    ? { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", timeZone: "UTC" }
    : { hour: "2-digit", minute: "2-digit", timeZone: "UTC" };
  return `${new Date(atMs).toLocaleString(undefined, options)} UTC`;
}

export function SectionCustomers() {
  // useSearchParams (the ?days= window) requires a Suspense boundary under the static export.
  return (
    <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
      <CustomersContent />
    </Suspense>
  );
}

function CustomersContent() {
  const { getAccessToken } = useAuth();
  // Platform roles may request up to 365 days; the section has no tenant of its own.
  const { days, setDays } = useWindowDays({ defaultDays: DEFAULT_WINDOW_DAYS, tenantId: undefined });
  // The rows and the clock they were read at, set together so a render never mixes two fetches.
  const [snapshot, setSnapshot] = useState<{ tenants: TenantConfiguration[]; nowMs: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // The clicked column, tied to the window it was clicked in: another window shows no list.
  const [selection, setSelection] = useState<{ days: number; key: string } | null>(null);

  const fetchTenants = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      // config/all is a bare array of tenant configurations (deliberately untyped, D-043).
      const tenants = await fetchJson<TenantConfiguration[]>(api.config.all(), getAccessToken);
      setSnapshot({ tenants, nowMs: Date.now() });
    } catch (err) {
      setError(apiErrorText(err, "Failed to load tenants"));
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await fetchTenants();
    };
    void run();
  }, [fetchTenants]);

  const growth = useMemo(
    () => (snapshot ? customerGrowth(snapshot.tenants, days, snapshot.nowMs) : null),
    [snapshot, days],
  );
  const selectedKey = selection?.days === days ? selection.key : null;
  const selectedBucket = growth?.buckets.find((b) => b.key === selectedKey) ?? null;
  const chartRows = (growth?.buckets ?? []).map((b) => ({
    key: b.key,
    startKey: b.startKey,
    endKey: b.endKey,
    count: b.count,
    cumulative: b.cumulative,
  }));
  const formatCategory = (key: string) => formatDay(key, days <= 7 ? WEEKDAY : SHORT_DAY);

  const toggleBucket = (row: Record<string, unknown>) => {
    const key = row.key;
    if (typeof key !== "string" || !row.count) return;
    setSelection((current) => (current?.days === days && current.key === key ? null : { days, key }));
  };

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Customers</h2>
          <p className="text-sm text-gray-500">
            {growth
              ? `${formatDay(growth.startKey, RANGE_DAY)} – ${formatDay(growth.endKey, RANGE_DAY)}`
              : "Tenant signups over time"}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <SegmentedControl options={RANGE_OPTIONS} value={days} onChange={setDays} />
          <button
            onClick={() => void fetchTenants()}
            disabled={loading}
            className="px-3 py-1.5 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 text-sm rounded-md px-4 py-3">{error}</div>
      )}

      {!growth || !snapshot ? (
        loading && (
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            {[0, 1, 2, 3].map((i) => (
              <CardSkeleton key={i} />
            ))}
          </div>
        )
      ) : (
        <>
          <div className="bg-white shadow rounded-lg p-6">
            <h3 className="text-sm font-medium text-gray-500">New customers</h3>
            <div className="mt-1 flex items-baseline gap-2">
              <span className="text-3xl font-semibold text-gray-900 tabular-nums">{growth.windowCount.toLocaleString()}</span>
              <span className="text-sm text-gray-500">{days === 1 ? "today" : `in the last ${days} days`}</span>
            </div>
            <div className="mt-4">
              <AppBarChart
                data={chartRows}
                categoryKey="key"
                valueKey="count"
                height={260}
                barColor={(row) => (selectedKey == null || row.key === selectedKey ? chartColors.primary : chartColors.muted)}
                formatCategory={formatCategory}
                renderTooltip={(row) => <BucketTooltip row={row} weekly={growth.weekly} bestKey={growth.bestDay?.key ?? null} />}
                onCategoryClick={toggleBucket}
                integerTicks
              />
            </div>
            {selectedBucket && (
              <BucketSignups
                bucket={selectedBucket}
                weekly={growth.weekly}
                nowMs={snapshot.nowMs}
                onClose={() => setSelection(null)}
              />
            )}
          </div>

          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <Stat
              label="Best day"
              value={growth.bestDay ? growth.bestDay.count.toLocaleString() : "—"}
              hint={growth.bestDay ? formatDay(growth.bestDay.key, FULL_DAY) : "no signups in this window"}
            />
            <Stat
              label="Weekly average"
              value={growth.weeklyAverage.toLocaleString(undefined, { maximumFractionDigits: 1 })}
              hint="new per week"
            />
            <Stat label="Last 7 days" value={growth.last7Days.toLocaleString()} hint="new" />
            <Stat label="Total customers" value={growth.total.toLocaleString()} hint="all time" />
          </div>

          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex items-baseline justify-between gap-3">
              <h3 className="text-sm font-medium text-gray-500">Customers gained</h3>
              <span className="text-2xl font-semibold text-gray-900 tabular-nums">{growth.windowCount.toLocaleString()}</span>
            </div>
            <div className="mt-4">
              <AppLineChart
                data={chartRows}
                xKey="key"
                series={[{ dataKey: "cumulative", label: "Customers gained", color: chartColors.primary }]}
                height={220}
                formatXTick={(key) => formatCategory(String(key))}
                hideLegend
                integerTicks
              />
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function BucketTooltip({ row, weekly, bestKey }: { row: Record<string, unknown>; weekly: boolean; bestKey: string | null }) {
  return (
    <div className="space-y-1">
      <p className="font-medium">{bucketTitle({ startKey: String(row.startKey), endKey: String(row.endKey) }, weekly)}</p>
      <p className="flex justify-between gap-4">
        <span>New customers</span>
        <span className="font-semibold tabular-nums">{String(row.count)}</span>
      </p>
      {!weekly && row.key === bestKey && <p style={{ color: chartColors.success }}>● Best day</p>}
    </div>
  );
}

function BucketSignups({
  bucket,
  weekly,
  nowMs,
  onClose,
}: {
  bucket: GrowthBucket<TenantConfiguration>;
  weekly: boolean;
  nowMs: number;
  onClose: () => void;
}) {
  return (
    <div className="mt-4 border-t border-gray-100 pt-4">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <button
          onClick={onClose}
          title="Close the list"
          className="inline-flex items-center gap-1 rounded-full bg-gray-100 px-2.5 py-1 text-xs font-medium text-gray-700 hover:bg-gray-200"
        >
          <span aria-hidden="true">×</span>
          <span>{bucketTitle(bucket, weekly)}</span>
        </button>
        <span className="text-xs text-gray-500">{bucket.count === 1 ? "1 new customer" : `${bucket.count} new customers`}</span>
      </div>
      <ul className="divide-y divide-gray-100">
        {bucket.signups.map(({ row, atMs }) => {
          const edition = effectiveEdition(row, nowMs);
          return (
            <li key={row.tenantId} className="flex items-center gap-3 py-1.5 text-sm">
              <span className={`${weekly ? "w-32" : "w-20"} shrink-0 text-xs text-gray-500 tabular-nums`}>{signupTime(atMs, weekly)}</span>
              <Link
                href={tenantManagementUrl(row.tenantId)}
                title="Open in Tenant Management"
                className="min-w-0 truncate text-gray-900 hover:text-blue-700 hover:underline"
              >
                {row.domainName}
              </Link>
              {row.companyName && <span className="hidden min-w-0 truncate text-gray-500 sm:inline">{row.companyName}</span>}
              <span
                className={`ml-auto inline-flex shrink-0 items-center rounded-full px-2 py-0.5 text-xs font-medium ${facetOption("plan", edition)?.badgeClass ?? ""}`}
              >
                {editionLabel(edition)}
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
