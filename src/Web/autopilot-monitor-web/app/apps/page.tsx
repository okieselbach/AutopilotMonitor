"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";

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

type SortKey =
  | "appName"
  | "totalInstalls"
  | "succeeded"
  | "failed"
  | "failureRate"
  | "avgDurationSeconds"
  | "trend";
type SortDir = "asc" | "desc";

export default function AppsPage() {
  const router = useRouter();
  const [data, setData] = useState<AppsListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [timeRange, setTimeRange] = useState<"7d" | "30d" | "90d">("30d");
  const [search, setSearch] = useState("");
  const [sortKey, setSortKey] = useState<SortKey>("failureRate");
  const [sortDir, setSortDir] = useState<SortDir>("desc");

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  // Global admin tenant scope (aggregated-capable): tenant list, selection ("" = all tenants),
  // and scope flags. Default selection is the GA's own tenant; user can opt into aggregated.
  const scope = useAggregatedAdminScope();
  const { isGlobalAdmin, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const isTimeRangeMount = useRef(true);

  const fetchApps = async (range: "7d" | "30d" | "90d" = timeRange) => {
    // Global admin can fetch aggregated view without selecting a tenant;
    // regular users must wait for their own tenantId.
    if (!isGlobalAdmin && !tenantId) return;
    try {
      setLoading(true);
      const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
      const url = isGlobalAdmin
        ? api.apps.globalList(days, selectedTenantId || undefined)
        : api.apps.list(tenantId, days);
      const response = await authenticatedFetch(url, getAccessToken);
      if (response.ok) {
        const json = (await response.json()) as AppsListResponse;
        setData(json);
      } else {
        addNotification(
          "error",
          "Backend Error",
          `Failed to load apps: ${response.statusText}`,
          "apps-list-error"
        );
      }
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        console.error("Failed to fetch apps", err);
        addNotification(
          "error",
          "Backend Not Reachable",
          "Unable to load app dashboard data.",
          "apps-list-error"
        );
      }
    } finally {
      setLoading(false);
    }
  };

  // Fetch whenever the effective scope or time range changes — gated on
  // scopeInitialized so we don't do an initial aggregated-fetch before the
  // GA default-to-own-tenant kicks in (would otherwise cause a wasted backend hit).
  useEffect(() => {
    if (!scopeInitialized) return;
    if (isTimeRangeMount.current) {
      isTimeRangeMount.current = false;
    }
    fetchApps(timeRange);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, timeRange, scopeKey]);

  const filteredAndSorted = useMemo<AppRow[]>(() => {
    if (!data?.apps) return [];
    const q = search.trim().toLowerCase();
    let rows = q
      ? data.apps.filter((a) => a.appName.toLowerCase().includes(q))
      : [...data.apps];

    const dir = sortDir === "asc" ? 1 : -1;
    rows.sort((a, b) => {
      if (sortKey === "appName") {
        return a.appName.localeCompare(b.appName) * dir;
      }
      if (sortKey === "trend") {
        const order: Record<string, number> = { worsening: 0, stable: 1, improving: 2 };
        return (order[a.trend] - order[b.trend]) * dir;
      }
      const av = a[sortKey] as number;
      const bv = b[sortKey] as number;
      return (av - bv) * dir;
    });
    return rows;
  }, [data, search, sortKey, sortDir]);

  const stats = useMemo(() => {
    if (!data) return { totalApps: 0, totalInstalls: 0, avgFailureRate: 0 };
    const apps = data.apps ?? [];
    const totalFailed = apps.reduce((acc, a) => acc + a.failed, 0);
    const totalInstalls = data.totalInstalls;
    return {
      totalApps: data.totalApps,
      totalInstalls,
      avgFailureRate:
        totalInstalls > 0 ? Math.round((totalFailed / totalInstalls) * 1000) / 10 : 0,
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
      return (
        <span className="inline-flex items-center text-emerald-700 text-xs">
          ↓ {Math.abs(row.trendDelta ?? 0).toFixed(1)} pp
        </span>
      );
    }
    if (row.trend === "worsening") {
      return (
        <span className="inline-flex items-center text-red-700 text-xs">
          ↑ {Math.abs(row.trendDelta ?? 0).toFixed(1)} pp
        </span>
      );
    }
    return <span className="text-xs text-gray-500">—</span>;
  }

  function appTypeBadge(type: string) {
    if (!type) return null;
    const color =
      type === "WinGet"
        ? "bg-blue-100 text-blue-800"
        : type === "MSI"
        ? "bg-purple-100 text-purple-800"
        : type === "Win32"
        ? "bg-gray-100 text-gray-800"
        : "bg-amber-100 text-amber-800";
    return (
      <span className={`ml-2 inline-block px-1.5 py-0.5 rounded text-xs ${color}`}>{type}</span>
    );
  }

  function formatDuration(s: number) {
    if (!s) return "—";
    if (s < 60) return `${Math.round(s)}s`;
    if (s < 3600) return `${Math.round(s / 60)}m`;
    return `${(s / 3600).toFixed(1)}h`;
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner show={scope.isGlobalAdmin} subtitle={globalAdminSubtitle(scope)} />
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <h1 className="text-2xl font-normal text-gray-900">App Health</h1>
                <p className="text-sm text-gray-500 mt-1">
                  All apps observed across enrollments, sortable by failure rate, duration, and trend.
                </p>
              </div>
              <div className="flex items-center gap-3">
                <TenantScopeSelector scope={scope} allowAggregated />
                {(["7d", "30d", "90d"] as const).map((range) => (
                  <button
                    key={range}
                    onClick={() => setTimeRange(range)}
                    className={`px-4 py-2 text-sm rounded-md transition-colors ${
                      timeRange === range
                        ? "bg-blue-600 text-white"
                        : "bg-gray-100 text-gray-700 hover:bg-gray-200"
                    }`}
                  >
                    {range === "7d" ? "7 Days" : range === "30d" ? "30 Days" : "90 Days"}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
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
              <div className="text-2xl font-semibold text-gray-900 mt-1">
                {stats.avgFailureRate.toFixed(1)}%
              </div>
            </div>
          </div>

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
              <div className="p-8 text-center text-gray-500">Loading…</div>
            ) : filteredAndSorted.length === 0 ? (
              <div className="p-8 text-center text-gray-500">
                {search ? "No apps match your search." : "No app install data in this window."}
              </div>
            ) : (
              <table className="min-w-full divide-y divide-gray-200">
                <thead className="bg-gray-50">
                  <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    <th
                      className="px-4 py-3 cursor-pointer select-none"
                      onClick={() => toggleSort("appName")}
                    >
                      App {sortIndicator("appName")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("totalInstalls")}
                    >
                      Installs {sortIndicator("totalInstalls")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("succeeded")}
                    >
                      Succeeded {sortIndicator("succeeded")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("failed")}
                    >
                      Failed {sortIndicator("failed")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("failureRate")}
                    >
                      Failure Rate {sortIndicator("failureRate")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("avgDurationSeconds")}
                    >
                      Avg Duration {sortIndicator("avgDurationSeconds")}
                    </th>
                    <th
                      className="px-4 py-3 cursor-pointer select-none text-right"
                      onClick={() => toggleSort("trend")}
                    >
                      Trend {sortIndicator("trend")}
                    </th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-200">
                  {filteredAndSorted.map((row) => (
                    <tr
                      key={row.appName}
                      onClick={() => {
                        const days = timeRange === "7d" ? 7 : timeRange === "30d" ? 30 : 90;
                        const params = new URLSearchParams({ days: String(days) });
                        // In Global Admin mode, propagate scope:
                        // - aggregated view → ?global=1
                        // - specific tenant → ?global=1&tenantId=...
                        if (isGlobalAdmin) {
                          params.set("global", "1");
                          if (selectedTenantId) params.set("tenantId", selectedTenantId);
                        }
                        router.push(`/apps/${encodeURIComponent(row.appName)}?${params.toString()}`);
                      }}
                      className="hover:bg-gray-50 cursor-pointer text-sm"
                    >
                      <td className="px-4 py-3 text-gray-900">
                        <span className="font-medium">{row.appName}</span>
                        {appTypeBadge(row.appType)}
                      </td>
                      <td className="px-4 py-3 text-right text-gray-700">{row.totalInstalls}</td>
                      <td className="px-4 py-3 text-right text-emerald-700">{row.succeeded}</td>
                      <td className="px-4 py-3 text-right text-red-700">{row.failed}</td>
                      <td className="px-4 py-3 text-right">
                        <span
                          className={
                            row.failureRate >= 20
                              ? "text-red-700 font-semibold"
                              : row.failureRate >= 5
                              ? "text-amber-700"
                              : "text-gray-700"
                          }
                        >
                          {row.failureRate.toFixed(1)}%
                        </span>
                      </td>
                      <td className="px-4 py-3 text-right text-gray-700">
                        {formatDuration(row.avgDurationSeconds)}
                      </td>
                      <td className="px-4 py-3 text-right">{trendBadge(row)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
