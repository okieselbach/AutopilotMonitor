"use client";

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { formatBytes, formatDuration } from "@/lib/formatting";
import DoBreakdownBar from "@/components/DoBreakdownBar";
import { CalculatingInline } from "@/components/CalculatingCard";
import { useFetchProgress } from "@/hooks/useFetchProgress";
import type { SoftwareTabScope, TimeRange } from "./types";
import { rangeToDays } from "./types";

// A cross-tenant apps aggregation can take tens of seconds server-side; the default 30s fetch
// timeout would abort it client-side while the server keeps computing.
const APPS_FETCH_TIMEOUT_MS = 180_000;

interface AppRow {
  appName: string;
  appType: string;
  totalInstalls: number;
  succeeded: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
  maxDurationSeconds: number;
  avgDownloadBytes: number;
  trend: "improving" | "worsening" | "stable";
  trendDelta: number | null;
  lastSeenAt: string;
}

interface AppsListResponse {
  success: boolean;
  totalApps: number;
  totalInstalls: number;
  windowDays: number;
  apps: AppRow[];
}

/** Delivery Optimization rollup served by /api/metrics/app (and the global variant). */
interface DeliveryOptimizationRollup {
  totalBytesDownloaded: number;
  fromPeers: number;
  fromCacheServer: number;
  fromHttp: number;
  peerOffloadPercent: number;
}

interface AppMetricsResponse {
  success: boolean;
  deliveryOptimization?: DeliveryOptimizationRollup;
}

type SortKey =
  | "appName"
  | "totalInstalls"
  | "succeeded"
  | "failed"
  | "failureRate"
  | "avgDurationSeconds"
  | "trend";
type SortDir = "asc" | "desc";

const PAGE_SIZE = 20;

interface InstallsTabProps {
  scope: SoftwareTabScope;
  timeRange: TimeRange;
}

export default function InstallsTab({ scope, timeRange }: InstallsTabProps) {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { isGlobalAdmin, routeGlobal, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [data, setData] = useState<AppsListResponse | null>(null);
  const [doRollup, setDoRollup] = useState<DeliveryOptimizationRollup | null>(null);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [sortKey, setSortKey] = useState<SortKey>("failureRate");
  const [sortDir, setSortDir] = useState<SortDir>("desc");
  const [page, setPage] = useState(0);

  const progress = useFetchProgress("appsInstalls.lastFetchMs");
  const { begin: progressBegin, finish: progressFinish } = progress;

  useEffect(() => {
    if (!scopeInitialized) return;
    if (!isGlobalAdmin && !tenantId) return;
    let cancelled = false;
    const days = rangeToDays(timeRange);

    const run = async () => {
      let succeeded = false;
      try {
        setLoading(true);
        progressBegin();
        const listUrl = routeGlobal
          ? api.apps.globalList(days, selectedTenantId || undefined)
          : api.apps.list(tenantId, days);
        const metricsUrl = routeGlobal
          ? api.metrics.globalApp(days, selectedTenantId || undefined)
          : api.metrics.app(tenantId, days);

        const [listRes, metricsRes] = await Promise.all([
          authenticatedFetch(listUrl, getAccessToken, { signal: AbortSignal.timeout(APPS_FETCH_TIMEOUT_MS) }),
          authenticatedFetch(metricsUrl, getAccessToken, { signal: AbortSignal.timeout(APPS_FETCH_TIMEOUT_MS) }),
        ]);

        if (cancelled) return;
        if (listRes.ok) {
          setData((await listRes.json()) as AppsListResponse);
          succeeded = true;
        } else {
          addNotification("error", "Backend Error", `Failed to load apps: ${listRes.statusText}`, "apps-list-error");
        }
        if (metricsRes.ok) {
          const m = (await metricsRes.json()) as AppMetricsResponse;
          setDoRollup(m.deliveryOptimization ?? null);
        }
      } catch (err) {
        if (cancelled) return;
        if (err instanceof TokenExpiredError) {
          addNotification("error", "Session Expired", err.message, "session-expired-error");
        } else {
          console.error("Failed to fetch app installs", err);
          addNotification("error", "Backend Not Reachable", "Unable to load app dashboard data.", "apps-list-error");
        }
      } finally {
        progressFinish(succeeded);
        if (!cancelled) setLoading(false);
      }
    };

    void run();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, timeRange, scopeKey]);

  const filteredAndSorted = useMemo<AppRow[]>(() => {
    if (!data?.apps) return [];
    const q = search.trim().toLowerCase();
    const rows = q ? data.apps.filter((a) => a.appName.toLowerCase().includes(q)) : [...data.apps];

    const dir = sortDir === "asc" ? 1 : -1;
    rows.sort((a, b) => {
      if (sortKey === "appName") return a.appName.localeCompare(b.appName) * dir;
      if (sortKey === "trend") {
        const order: Record<string, number> = { worsening: 0, stable: 1, improving: 2 };
        return (order[a.trend] - order[b.trend]) * dir;
      }
      return ((a[sortKey] as number) - (b[sortKey] as number)) * dir;
    });
    return rows;
  }, [data, search, sortKey, sortDir]);

  // Reset to the first page whenever the filtered/sorted set or the loaded data changes.
  useEffect(() => { setPage(0); }, [search, sortKey, sortDir, data]);

  const pageCount = Math.max(1, Math.ceil(filteredAndSorted.length / PAGE_SIZE));
  const pageRows = filteredAndSorted.slice(page * PAGE_SIZE, page * PAGE_SIZE + PAGE_SIZE);

  const stats = useMemo(() => {
    if (!data) return { totalApps: 0, totalInstalls: 0, avgFailureRate: 0 };
    const apps = data.apps ?? [];
    const totalFailed = apps.reduce((acc, a) => acc + a.failed, 0);
    // Terminal-only convention: rate over finished installs (succeeded + failed), so
    // in-flight/orphaned InProgress rows don't dilute it. Matches the per-app failureRate.
    const totalSucceeded = apps.reduce((acc, a) => acc + a.succeeded, 0);
    const totalFinished = totalFailed + totalSucceeded;
    return {
      totalApps: data.totalApps,
      totalInstalls: data.totalInstalls,
      avgFailureRate: totalFinished > 0 ? Math.round((totalFailed / totalFinished) * 1000) / 10 : 0,
    };
  }, [data]);

  function toggleSort(key: SortKey) {
    if (sortKey === key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir(key === "appName" ? "asc" : "desc");
    }
  }

  function sortIndicator(key: SortKey) {
    if (sortKey !== key) return null;
    return <span className="ml-1 text-xs">{sortDir === "asc" ? "↑" : "↓"}</span>;
  }

  function trendBadge(row: AppRow) {
    if (row.trend === "improving") {
      return <span className="inline-flex items-center text-emerald-700 text-xs">↓ {Math.abs(row.trendDelta ?? 0).toFixed(1)} pp</span>;
    }
    if (row.trend === "worsening") {
      return <span className="inline-flex items-center text-red-700 text-xs">↑ {Math.abs(row.trendDelta ?? 0).toFixed(1)} pp</span>;
    }
    return <span className="text-xs text-gray-500">—</span>;
  }

  function appTypeBadge(type: string) {
    if (!type) return null;
    const color =
      type === "WinGet" ? "bg-blue-100 text-blue-800"
      : type === "MSI" ? "bg-purple-100 text-purple-800"
      : type === "Win32" ? "bg-gray-100 text-gray-800"
      : "bg-amber-100 text-amber-800";
    return <span className={`ml-2 inline-block px-1.5 py-0.5 rounded text-xs ${color}`}>{type}</span>;
  }

  function openApp(appName: string) {
    const days = rangeToDays(timeRange);
    const params = new URLSearchParams({ days: String(days) });
    // Tenant scope is carried in sessionStorage (see useAggregatedAdminScope), so no scope params needed.
    router.push(`/apps/${encodeURIComponent(appName)}?${params.toString()}`);
  }

  return (
    <>
      {/* Stat cards */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
        <div className="bg-white rounded-lg shadow p-4">
          <div className="text-sm text-gray-500">Total Apps</div>
          <div className="text-2xl font-semibold text-gray-900 mt-1">{stats.totalApps}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4">
          <div className="text-sm text-gray-500">Total Installs</div>
          <div className="text-2xl font-semibold text-gray-900 mt-1">{stats.totalInstalls}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4">
          <div className="text-sm text-gray-500">Avg Failure Rate</div>
          <div className="text-2xl font-semibold text-gray-900 mt-1">{stats.avgFailureRate.toFixed(1)}%</div>
        </div>
      </div>

      {/* Delivery Optimization rollup */}
      <DoCard rollup={doRollup} days={rangeToDays(timeRange)} />

      {/* Search */}
      <div className="bg-white rounded-lg shadow mb-4 p-4">
        <input
          type="text"
          placeholder="Search apps by name…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent text-sm"
        />
      </div>

      {/* Table */}
      <div className="bg-white rounded-lg shadow overflow-hidden">
        {loading ? (
          <CalculatingInline
            label="Aggregating app installs…"
            elapsedMs={progress.elapsedMs}
            estimateMs={progress.estimateMs}
          />
        ) : filteredAndSorted.length === 0 ? (
          <div className="p-8 text-center text-gray-500">
            {search ? "No apps match your search." : "No app install data in this window."}
          </div>
        ) : (
          <>
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                <th className="px-4 py-3 cursor-pointer select-none" onClick={() => toggleSort("appName")}>App {sortIndicator("appName")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("totalInstalls")}>Installs {sortIndicator("totalInstalls")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("succeeded")}>Succeeded {sortIndicator("succeeded")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("failed")}>Failed {sortIndicator("failed")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("failureRate")}>Failure Rate {sortIndicator("failureRate")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("avgDurationSeconds")}>Avg Duration {sortIndicator("avgDurationSeconds")}</th>
                <th className="px-4 py-3 cursor-pointer select-none text-right" onClick={() => toggleSort("trend")}>Trend {sortIndicator("trend")}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200">
              {pageRows.map((row) => (
                <tr key={row.appName} onClick={() => openApp(row.appName)} className="hover:bg-gray-50 cursor-pointer text-sm">
                  <td className="px-4 py-3 text-gray-900">
                    <span className="font-medium">{row.appName}</span>
                    {appTypeBadge(row.appType)}
                  </td>
                  <td className="px-4 py-3 text-right text-gray-700">{row.totalInstalls}</td>
                  <td className="px-4 py-3 text-right text-emerald-700">{row.succeeded}</td>
                  <td className="px-4 py-3 text-right text-red-700">{row.failed}</td>
                  <td className="px-4 py-3 text-right">
                    <span className={row.failureRate >= 20 ? "text-red-700 font-semibold" : row.failureRate >= 5 ? "text-amber-700" : "text-gray-700"}>
                      {row.failureRate.toFixed(1)}%
                    </span>
                  </td>
                  <td className="px-4 py-3 text-right text-gray-700">{formatDuration(row.avgDurationSeconds, "—")}</td>
                  <td className="px-4 py-3 text-right">{trendBadge(row)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {pageCount > 1 && (
            <div className="flex items-center justify-between px-4 py-3 text-sm text-gray-600 border-t border-gray-200">
              <span>{filteredAndSorted.length} apps · page {page + 1} of {pageCount}</span>
              <div className="flex gap-2">
                <button onClick={() => setPage((p) => Math.max(0, p - 1))} disabled={page === 0}
                  className="px-3 py-1 rounded-md bg-gray-100 hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed">Previous</button>
                <button onClick={() => setPage((p) => Math.min(pageCount - 1, p + 1))} disabled={page >= pageCount - 1}
                  className="px-3 py-1 rounded-md bg-gray-100 hover:bg-gray-200 disabled:opacity-50 disabled:cursor-not-allowed">Next</button>
              </div>
            </div>
          )}
          </>
        )}
      </div>
    </>
  );
}

/** Delivery Optimization summary: peer/MCC offload % + bytes saved + sourcing bar. */
function DoCard({ rollup, days }: { rollup: DeliveryOptimizationRollup | null; days: number }) {
  if (!rollup) return null;
  // Fall back to the sum of sources so legacy rows that report peer/HTTP bytes but no
  // total (the backend already applies the same fallback) still render the card.
  const total = Math.max(rollup.totalBytesDownloaded, rollup.fromPeers + rollup.fromCacheServer + rollup.fromHttp);
  if (total <= 0) return null;
  // Pure CDN = everything not served by peers or the Connected Cache. Keeps the bar's HTTP
  // segment from double-counting MCC (which DO also reports inside BytesFromHttp).
  const pureCdn = Math.max(0, total - rollup.fromPeers - rollup.fromCacheServer);
  const offload = rollup.peerOffloadPercent;
  const offloadClass = offload >= 30 ? "text-emerald-700" : offload > 0 ? "text-amber-700" : "text-gray-500";

  return (
    <div className="bg-white rounded-lg shadow p-4 mb-6">
      <div className="flex items-center justify-between mb-3">
        <h2 className="text-sm font-medium text-gray-700">Delivery Optimization</h2>
        <span className="text-xs text-gray-400">peer / Connected Cache offload · last {days}d</span>
      </div>
      <div className="flex flex-wrap items-end gap-x-8 gap-y-2 mb-3">
        <div>
          <div className="text-xs text-gray-500">Peer offload</div>
          <div className={`text-2xl font-semibold ${offloadClass}`}>{offload.toFixed(1)}%</div>
        </div>
        <div>
          <div className="text-xs text-gray-500">Saved from peers + MCC</div>
          <div className="text-2xl font-semibold text-gray-900">{formatBytes(rollup.fromPeers + rollup.fromCacheServer)}</div>
        </div>
        <div>
          <div className="text-xs text-gray-500">Total downloaded</div>
          <div className="text-2xl font-semibold text-gray-900">{formatBytes(total)}</div>
        </div>
      </div>
      <DoBreakdownBar peers={rollup.fromPeers} cacheServer={rollup.fromCacheServer} http={pureCdn} total={total} showLegend />
    </div>
  );
}
