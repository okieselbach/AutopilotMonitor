"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";
import { useAggregatedAdminScope } from "@/hooks";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { SessionStatusBadge } from "@/components/SessionStatusBadge";
import { SegmentedControl, TIME_RANGE_OPTIONS } from "@/components/SegmentedControl";
import {
  VERDICT_PATH_ORIGIN_LABELS,
  groupPathsByOrigin,
  trendGlyph,
  type VerdictCalibrationPath,
  type VerdictCalibrationResponse,
} from "./verdictCalibrationLogic";

type DateRange = "7d" | "30d" | "90d";

const RANGE_DAYS: Record<DateRange, number> = { "7d": 7, "30d": 30, "90d": 90 };

export function SectionVerdictCalibration() {
  const { getAccessToken } = useAuth();
  const scope = useAggregatedAdminScope({ defaultAggregated: true });
  const { selectedTenantId, scopeInitialized } = scope;
  const [data, setData] = useState<VerdictCalibrationResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>("30d");

  const fetchData = useCallback(
    async (range: DateRange, tenantId: string) => {
      setLoading(true);
      setError(null);
      try {
        const res = await authenticatedFetch(
          api.metrics.globalVerdictCalibration(RANGE_DAYS[range], tenantId || undefined),
          getAccessToken
        );
        if (res.status === 404) {
          // Deploy skew: backend without the route yet — reads as "no rows", never as an error.
          setData(null);
          return;
        }
        if (!res.ok) throw new Error(`Verdict calibration: ${res.status}`);
        setData((await res.json()) as VerdictCalibrationResponse);
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          setError("Session expired. Please refresh the page.");
        } else {
          setError(err instanceof Error ? err.message : "Failed to load verdict calibration");
        }
      } finally {
        setLoading(false);
      }
    },
    [getAccessToken]
  );

  useEffect(() => {
    if (!scopeInitialized) return;
    const run = async () => {
      await fetchData(dateRange, selectedTenantId);
    };
    void run();
  }, [fetchData, dateRange, selectedTenantId, scopeInitialized]);

  const groups = data ? groupPathsByOrigin(data.paths) : [];

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Verdict Calibration</h2>
          <p className="text-sm text-gray-500">
            Which code path produced each session verdict, how often it was overridden, and whether the device came back.
            Platform-internal classifier diagnostics — correlations only, never causal claims.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <TenantScopeSelector scope={scope} allowAggregated />
          <SegmentedControl options={TIME_RANGE_OPTIONS} value={dateRange} onChange={(v) => setDateRange(v as DateRange)} />
          <button
            onClick={() => fetchData(dateRange, selectedTenantId)}
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

      {data && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          <Stat label="Sessions in window" value={data.totals.sessions.toLocaleString()} hint={`${data.totals.days} day rows · ${data.windowStart} – ${data.windowEnd}`} />
          <Stat label="Terminal" value={data.totals.terminal.toLocaleString()} hint="Succeeded / Failed / Incomplete" />
          <Stat label="Derived attribution" value={data.totals.derived.toLocaleString()} hint="rows without a stamped path (pre-instrumentation)" />
          <Stat
            label="Trend basis"
            value={`${data.trend.windowSessions.toLocaleString()} / ${data.trend.baselineSessions.toLocaleString()}`}
            hint={`last ${data.trend.windowDays}d vs prior ${data.trend.baselineDays}d sessions`}
          />
        </div>
      )}

      <div className="bg-white shadow rounded-lg p-6">
        {loading && !data ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : !data || data.paths.length === 0 ? (
          <p className="text-sm text-gray-500">
            No calibration rows yet. The maintenance sweep writes them every two hours; the first rows appear after its
            next pass.
          </p>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 text-sm">
              <thead>
                <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  <th className="px-3 py-2">Verdict path</th>
                  <th className="px-3 py-2">Status</th>
                  <th className="px-3 py-2 text-right">Sessions</th>
                  <th className="px-3 py-2 text-right">Share</th>
                  <th className="px-3 py-2 text-right" title="Share in the last 7 days vs the prior 28 days (lift = window share ÷ baseline share)">
                    7d trend
                  </th>
                  <th className="px-3 py-2 text-right" title="Of terminal sessions at least 7 days old: the same device registered another terminal session within 7 days. Blank below 20 eligible sessions.">
                    Re-enrolled ≤7d
                  </th>
                  <th className="px-3 py-2 text-right" title="Sessions that carried this path and were then overridden: by an administrator / by a late agent completion / by another writer">
                    Overridden (admin / late / other)
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {groups.map((group) => (
                  <GroupRows key={group.origin} origin={group.origin} rows={group.rows} />
                ))}
              </tbody>
            </table>
          </div>
        )}
        {data?.computedAt && (
          <p className="mt-3 text-xs text-gray-400">
            Rows computed {new Date(data.computedAt).toLocaleString()} · algorithm version{" "}
            {data.versions.join(", ") || "—"}
          </p>
        )}
      </div>
    </div>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="bg-white shadow rounded-lg p-4">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wider">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-gray-900">{value}</p>
      <p className="text-xs text-gray-400">{hint}</p>
    </div>
  );
}

function GroupRows({ origin, rows }: { origin: string; rows: VerdictCalibrationPath[] }) {
  return (
    <>
      <tr className="bg-gray-50">
        <td colSpan={7} className="px-3 py-1.5 text-xs font-semibold text-gray-600">
          {VERDICT_PATH_ORIGIN_LABELS[origin] ?? origin}
        </td>
      </tr>
      {rows.map((row) => (
        <PathRow key={`${row.verdictPath}|${row.status}`} row={row} />
      ))}
    </>
  );
}

function PathRow({ row }: { row: VerdictCalibrationPath }) {
  const trend = trendGlyph(row);
  const overridden = row.overriddenByAdmin + row.overriddenByLateCompletion + row.overriddenOther;
  return (
    <tr>
      <td className="px-3 py-2 font-mono text-xs text-gray-800 whitespace-nowrap">
        {row.verdictPath}
        {row.derivedCount > 0 && (
          <span
            className="ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600 border border-gray-200"
            title={`${row.derivedCount} of ${row.count} attributed read-side (no stamped path)`}
          >
            derived {row.derivedCount}
          </span>
        )}
      </td>
      <td className="px-3 py-2">
        <SessionStatusBadge status={row.status} />
      </td>
      <td className="px-3 py-2 text-right tabular-nums">{row.count.toLocaleString()}</td>
      <td className="px-3 py-2 text-right tabular-nums">{row.sharePct.toFixed(1)}%</td>
      <td
        className={`px-3 py-2 text-right tabular-nums ${trend.className}`}
        title={`last 7d: ${row.window7.count} of ${row.window7.sessions} (${row.window7.sharePct.toFixed(1)}%) · prior 28d: ${row.baseline28.count} of ${row.baseline28.sessions} (${row.baseline28.sharePct.toFixed(1)}%)`}
      >
        {trend.text}
      </td>
      <td
        className="px-3 py-2 text-right tabular-nums"
        title={`${row.reEnrolled7d} of ${row.eligible7d} eligible`}
      >
        {row.reEnrollRatePct == null ? <span className="text-gray-400">— (n={row.eligible7d})</span> : `${row.reEnrollRatePct.toFixed(1)}%`}
      </td>
      <td className="px-3 py-2 text-right tabular-nums" title={overridden === 0 ? undefined : `${overridden} overridden in total`}>
        {overridden === 0 ? (
          <span className="text-gray-400">—</span>
        ) : (
          `${row.overriddenByAdmin} / ${row.overriddenByLateCompletion} / ${row.overriddenOther}`
        )}
      </td>
    </tr>
  );
}
