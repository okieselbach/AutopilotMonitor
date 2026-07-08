"use client";

import { useEffect, useState, useRef, useMemo } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import FleetStatCard from "./components/FleetStatCard";
import { useFleetHealth } from "./hooks/useFleetHealth";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";

interface AppMetric {
  appName: string;
  totalInstalls: number;
  succeeded: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
  maxDurationSeconds: number;
  avgDownloadBytes: number;
  topFailureCodes: { code: string; count: number }[];
}

interface AppMetricsResponse {
  success: boolean;
  totalApps: number;
  totalInstalls: number;
  slowestApps: AppMetric[];
  topFailingApps: AppMetric[];
}

export default function FleetHealthPage() {
  const [appMetrics, setAppMetrics] = useState<AppMetricsResponse | null>(null);
  const [timeRange, setTimeRange] = useState<"7d" | "30d" | "90d">("7d");

  const hasJoinedGroup = useRef(false);

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  // Global admin tenant scope (aggregated-capable): tenant list, selection ("" = all tenants),
  // scope flags, and effectiveTenantId (empty in aggregated mode → skips the SignalR group).
  const scope = useAggregatedAdminScope();
  const { isGlobalAdmin, selectedTenantId, effectiveTenantId, scopeInitialized, scopeKey } = scope;

  const days = timeRange === "7d" ? 7 : timeRange === "30d" ? 30 : 90;

  // Server-aggregated fleet data (stats, timeline, model + failure breakdowns).
  // Replaces the old client path that drained up to 200k raw sessions into the
  // browser and aggregated on the main thread.
  const { data, error } = useFleetHealth({
    isGlobalAdmin,
    selectedTenantId,
    tenantId,
    scopeInitialized,
    scopeKey,
    days,
    getAccessToken,
    addNotification,
    signalR: { on, off, isConnected },
  });

  // App install metrics are already backend-aggregated; fetch alongside fleet data.
  useEffect(() => {
    if (!scopeInitialized) return;
    fetchAppMetrics(timeRange);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, timeRange, scopeKey]);

  // Join this tenant's SignalR group so its session events reach the client; the
  // useFleetHealth hook listens for newSession/newevents to trigger a debounced
  // refetch. No group in GA-aggregated mode (effectiveTenantId empty).
  useEffect(() => {
    if (!isConnected) return;
    if (!effectiveTenantId) return;
    if (hasJoinedGroup.current) return;
    const group = `tenant-${effectiveTenantId}`;
    joinGroup(group);
    hasJoinedGroup.current = true;
    return () => {
      if (hasJoinedGroup.current) {
        leaveGroup(group);
        hasJoinedGroup.current = false;
      }
    };
  }, [isConnected, effectiveTenantId]);

  const fetchAppMetrics = async (range: "7d" | "30d" | "90d" = timeRange) => {
    try {
      const d = range === "7d" ? 7 : range === "30d" ? 30 : 90;
      const endpoint = isGlobalAdmin
        ? api.metrics.globalApp(d, selectedTenantId || undefined)
        : api.metrics.app(tenantId, d);
      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (response.ok) {
        const json = await response.json();
        setAppMetrics(json);
      } else {
        addNotification('error', 'Backend Error', `Failed to load app metrics: ${response.statusText}`, 'fleet-health-metrics-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch app metrics:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load app metrics. Please check your connection.', 'fleet-health-metrics-error');
      }
    }
  };

  // Server payload, with presentation-friendly defaults so the JSX renders
  // without null guards once loading clears. `avgDuration` keeps the card's
  // original field name (server sends avgDurationMinutes).
  const s = data?.stats;
  const stats = {
    total: s?.total ?? 0,
    succeeded: s?.succeeded ?? 0,
    failed: s?.failed ?? 0,
    inProgress: s?.inProgress ?? 0,
    incomplete: s?.incomplete ?? 0,
    successRate: s?.successRate ?? 0,
    avgDuration: s?.avgDurationMinutes ?? 0,
  };
  const modelHealth = data?.modelHealth ?? [];
  const slowestModels = data?.slowestModels ?? [];
  const topFailingModels = data?.topFailingModels ?? [];
  const failureReasons = data?.failureReasons ?? [];

  // Timeline points with presentation labels derived from the UTC date string.
  const dailyData = useMemo(() => {
    const points = data?.dailyData ?? [];
    return points.map((p) => {
      const d = new Date(p.date + "T00:00:00");
      return {
        date: p.date,
        label:
          days <= 7
            ? d.toLocaleDateString(undefined, { weekday: "short" })
            : d.toLocaleDateString(undefined, { month: "short", day: "numeric" }),
        success: p.success,
        failed: p.failed,
      };
    });
  }, [data, days]);

  const maxDaily = useMemo(
    () => Math.max(1, ...dailyData.map((d) => d.success + d.failed)),
    [dailyData],
  );

  const maxFailureCount = useMemo(
    () => (failureReasons.length > 0 ? Math.max(...failureReasons.map((f) => f.count)) : 1),
    [failureReasons],
  );

  // Spinner on the initial load and while a scope/range switch refetches (data is
  // reset to null). On error we fall through and render the page shell — the hook
  // surfaces the failure via a notification, matching the previous behavior.
  if (!data && !error) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-600">Loading fleet health data...</div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <div className="flex items-center space-x-3">
                  <svg
                    className="w-8 h-8 text-blue-600"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z"
                    />
                  </svg>
                  <h1 className="text-2xl font-normal text-gray-900">
                    Fleet Health
                  </h1>
                </div>
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
                    {range === "7d"
                      ? "Last 7 Days"
                      : range === "30d"
                      ? "Last 30 Days"
                      : "Last 90 Days"}
                  </button>
                ))}
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {/* Top Stats */}
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-5 mb-8">
            <FleetStatCard
              title="Success Rate"
              value={`${stats.successRate.toFixed(1)}%`}
              subtitle={`${stats.succeeded} of ${stats.total} enrollments`}
              color={
                stats.successRate >= 95
                  ? "green"
                  : stats.successRate >= 80
                  ? "yellow"
                  : "red"
              }
            />
            <FleetStatCard
              title="Avg. Enrollment Time"
              value={`${stats.avgDuration} min`}
              subtitle="Completed enrollments"
              color="blue"
            />
            <FleetStatCard
              title="Failed"
              value={stats.failed.toString()}
              subtitle="Needs attention"
              color="red"
            />
            <FleetStatCard
              title="Incomplete"
              value={stats.incomplete.toString()}
              subtitle="No completion signal — not a failure"
              color="slate"
            />
            <FleetStatCard
              title="Active Now"
              value={stats.inProgress.toString()}
              subtitle="Currently enrolling"
              color="blue"
            />
          </div>

          {/* Enrollments Timeline */}
          <div className="bg-white shadow rounded-lg p-6 mb-8">
            <h2 className="text-lg font-semibold text-gray-900 mb-4">
              Enrollments Timeline
            </h2>
            {stats.total === 0 ? (
              <div className="text-center py-8 text-gray-500">
                No enrollments in this time range.
              </div>
            ) : (
              <div>
                <div className="flex items-end space-x-1 h-48">
                  {dailyData.map((day) => {
                    const total = day.success + day.failed;
                    const heightPct =
                      maxDaily > 0 ? (total / maxDaily) * 100 : 0;
                    const successPct =
                      total > 0 ? (day.success / total) * heightPct : 0;
                    const failedPct = heightPct - successPct;

                    return (
                      <div
                        key={day.date}
                        className="flex-1 flex flex-col items-center justify-end h-full group relative"
                      >
                        {/* Tooltip */}
                        {total > 0 && (
                          <div className="absolute -top-8 bg-gray-900 text-white text-xs px-2 py-1 rounded opacity-0 group-hover:opacity-100 transition-opacity whitespace-nowrap z-10">
                            {day.success} ok, {day.failed} failed
                          </div>
                        )}
                        <div
                          className="w-full flex flex-col justify-end"
                          style={{ height: `${heightPct}%` }}
                        >
                          {day.failed > 0 && (
                            <div
                              className="w-full bg-red-400 rounded-t"
                              style={{
                                height: `${
                                  heightPct > 0
                                    ? (failedPct / heightPct) * 100
                                    : 0
                                }%`,
                                minHeight:
                                  day.failed > 0 ? "2px" : undefined,
                              }}
                            />
                          )}
                          {day.success > 0 && (
                            <div
                              className={`w-full bg-green-500 ${
                                day.failed === 0 ? "rounded-t" : ""
                              }`}
                              style={{
                                height: `${
                                  heightPct > 0
                                    ? (successPct / heightPct) * 100
                                    : 0
                                }%`,
                                minHeight:
                                  day.success > 0 ? "2px" : undefined,
                              }}
                            />
                          )}
                        </div>
                      </div>
                    );
                  })}
                </div>
                {/* X-axis labels - show subset */}
                <div className="flex space-x-1 mt-2">
                  {dailyData.map((day, i) => {
                    const showLabel =
                      dailyData.length <= 14 ||
                      i % Math.ceil(dailyData.length / 10) === 0 ||
                      i === dailyData.length - 1;
                    return (
                      <div
                        key={day.date}
                        className="flex-1 text-center text-[10px] text-gray-500"
                      >
                        {showLabel ? day.label : ""}
                      </div>
                    );
                  })}
                </div>
                <div className="flex items-center space-x-4 mt-3 text-xs text-gray-500">
                  <div className="flex items-center space-x-1">
                    <div className="w-3 h-3 bg-green-500 rounded" />
                    <span>Success ({stats.succeeded})</span>
                  </div>
                  <div className="flex items-center space-x-1">
                    <div className="w-3 h-3 bg-red-400 rounded" />
                    <span>Failed ({stats.failed})</span>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* Three column: Failure Reasons + Slowest Models + Top Failing Models */}
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 mb-8">
            {/* Top Failure Reasons */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">
                Top Failure Reasons
              </h2>
              {failureReasons.length === 0 ? (
                <div className="text-center py-6 text-gray-400 text-sm">
                  No failures in this time range
                </div>
              ) : (
                <div className="space-y-3">
                  {failureReasons.map((fr, i) => (
                    <div key={fr.reason} className="flex items-center space-x-3">
                      <span className="text-xs text-gray-400 w-4">
                        {i + 1}.
                      </span>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-gray-700 truncate pr-2">
                            {fr.reason}
                          </span>
                          <span className="text-sm font-medium text-gray-900 flex-shrink-0">
                            ({fr.count})
                          </span>
                        </div>
                        <div className="w-full h-2 bg-gray-100 rounded-full overflow-hidden">
                          <div
                            className="h-full bg-red-400 rounded-full"
                            style={{
                              width: `${(fr.count / maxFailureCount) * 100}%`,
                            }}
                          />
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Slowest Device Models */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">
                Slowest Models
              </h2>
              {slowestModels.length === 0 ? (
                <div className="text-center py-6 text-gray-400 text-sm">
                  No completed enrollments
                </div>
              ) : (
                <div className="space-y-3">
                  {slowestModels.map((m, i) => (
                    <div key={m.model} className="flex items-center space-x-3">
                      <span className="text-xs text-gray-400 w-4">
                        {i + 1}.
                      </span>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-gray-700 truncate pr-2">
                            {m.model}
                          </span>
                          <span className="text-sm font-medium text-gray-900 flex-shrink-0">
                            {m.avgMinutes} min
                          </span>
                        </div>
                        <div className="text-xs text-gray-400">
                          {m.count} enrollments
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>

            {/* Top Failing Models */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">
                Top Failing Models
              </h2>
              {topFailingModels.length === 0 ? (
                <div className="text-center py-6 text-gray-400 text-sm">
                  No failures in this time range
                </div>
              ) : (
                <div className="space-y-3">
                  {topFailingModels.map((m, i) => (
                    <div key={m.model} className="flex items-center space-x-3">
                      <span className="text-xs text-gray-400 w-4">
                        {i + 1}.
                      </span>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-gray-700 truncate pr-2">
                            {m.model}
                          </span>
                          <span className="text-sm font-medium text-red-600 flex-shrink-0">
                            {m.failed} failed
                          </span>
                        </div>
                        <div className="flex items-center space-x-2">
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div
                              className="h-full bg-red-400 rounded-full"
                              style={{ width: `${m.failureRate}%` }}
                            />
                          </div>
                          <span className="text-xs text-gray-400 flex-shrink-0">
                            {m.failureRate}%
                          </span>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Slowest Apps + Top Failing Apps */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-8">
            {/* Slowest Apps */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">
                Slowest Apps
              </h2>
              {!appMetrics || appMetrics.slowestApps.length === 0 ? (
                <div className="text-center py-6 text-gray-400 text-sm">
                  No app install data available
                </div>
              ) : (
                <div className="space-y-3">
                  {appMetrics.slowestApps.map((app, i) => {
                    const avgSec = app.avgDurationSeconds;
                    const avgLabel = avgSec >= 60
                      ? `${Math.round(avgSec / 60)} min`
                      : avgSec > 0
                      ? `${Math.round(avgSec)}s`
                      : "< 1s";
                    const maxSec = app.maxDurationSeconds;
                    const maxLabel = maxSec >= 60
                      ? `${Math.round(maxSec / 60)}m`
                      : maxSec > 0
                      ? `${Math.round(maxSec)}s`
                      : "< 1s";
                    const maxAvg = Math.max(
                      1,
                      ...appMetrics!.slowestApps.map((a) => a.avgDurationSeconds)
                    );
                    return (
                      <div
                        key={app.appName}
                        className="flex items-center space-x-3"
                      >
                        <span className="text-xs text-gray-400 w-4">
                          {i + 1}.
                        </span>
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center justify-between mb-1">
                            <span className="text-sm text-gray-700 truncate pr-2">
                              {app.appName}
                            </span>
                            <span className="text-sm font-medium text-gray-900 flex-shrink-0">
                              {avgLabel} avg
                            </span>
                          </div>
                          <div className="flex items-center space-x-2">
                            <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                              <div
                                className="h-full bg-amber-400 rounded-full"
                                style={{
                                  width: `${(avgSec / maxAvg) * 100}%`,
                                  minWidth: avgSec > 0 ? "2px" : "0",
                                }}
                              />
                            </div>
                            <span className="text-xs text-gray-400 flex-shrink-0">
                              max {maxLabel} | {app.succeeded} installs
                            </span>
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>

            {/* Top Failing Apps */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">
                Top Failing Apps
              </h2>
              {!appMetrics || appMetrics.topFailingApps.length === 0 ? (
                <div className="text-center py-6 text-gray-400 text-sm">
                  No app failures recorded
                </div>
              ) : (
                <div className="space-y-3">
                  {appMetrics.topFailingApps.map((app, i) => (
                    <div
                      key={app.appName}
                      className="flex items-center space-x-3"
                    >
                      <span className="text-xs text-gray-400 w-4">
                        {i + 1}.
                      </span>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-gray-700 truncate pr-2">
                            {app.appName}
                          </span>
                          <span className="text-sm font-medium text-red-600 flex-shrink-0">
                            {app.failed} failed
                          </span>
                        </div>
                        <div className="flex items-center space-x-2">
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div
                              className="h-full bg-red-400 rounded-full"
                              style={{ width: `${app.failureRate}%` }}
                            />
                          </div>
                          <span className="text-xs text-gray-400 flex-shrink-0">
                            {app.failureRate}% of {app.totalInstalls}
                          </span>
                        </div>
                        {app.topFailureCodes.length > 0 && (
                          <div className="text-xs text-gray-400 mt-1">
                            {app.topFailureCodes
                              .map((fc) => `${fc.code} (${fc.count}x)`)
                              .join(", ")}
                          </div>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          {/* Health by Device Model */}
          <div className="bg-white shadow rounded-lg p-6">
            <h2 className="text-lg font-semibold text-gray-900 mb-4">
              Health by Device Model
            </h2>
            {modelHealth.length === 0 ? (
              <div className="text-center py-6 text-gray-400 text-sm">
                No data available
              </div>
            ) : (
              <div className="space-y-3">
                {modelHealth.map((m) => {
                  const successRate =
                    m.total > 0
                      ? Math.round((m.succeeded / m.total) * 100)
                      : 0;
                  return (
                    <div key={m.model}>
                      <div className="flex items-baseline justify-between mb-1">
                        <span className="text-sm text-gray-700 break-words leading-snug">{m.model}</span>
                        <span className="ml-3 flex-shrink-0 text-sm font-medium text-gray-900">
                          {successRate}% <span className="text-xs font-normal text-gray-400">({m.total} devices)</span>
                        </span>
                      </div>
                      <div className="w-full h-2 bg-gray-100 rounded-full overflow-hidden">
                        <div
                          className={`h-full rounded-full transition-all ${
                            successRate >= 95
                              ? "bg-green-500"
                              : successRate >= 80
                              ? "bg-yellow-500"
                              : "bg-red-500"
                          }`}
                          style={{ width: `${successRate}%` }}
                        />
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
