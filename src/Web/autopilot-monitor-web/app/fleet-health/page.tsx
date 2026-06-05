"use client";

import { useEffect, useState, useRef, useMemo } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation, MAX_EAGER_PAGES } from "@/lib/paginationLink";
import FleetStatCard from "./components/FleetStatCard";
import { Session } from "@/types";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";

// Backend caps pageSize at 1000 — use the maximum so a 90-day window on a busy
// install (10k+ sessions) drains in a handful of round-trips instead of 50+.
const FLEET_PAGE_SIZE = 1000;

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
  const router = useRouter();

  const [sessions, setSessions] = useState<Session[]>([]);
  const [appMetrics, setAppMetrics] = useState<AppMetricsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [timeRange, setTimeRange] = useState<"7d" | "30d" | "90d">("7d");

  const hasJoinedGroup = useRef(false);
  const isTimeRangeMount = useRef(true);

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  // Global admin tenant scope (aggregated-capable): tenant list, selection ("" = all tenants),
  // scope flags, and effectiveTenantId (empty in aggregated mode → skips the SignalR group).
  const scope = useAggregatedAdminScope();
  const { isGlobalAdmin, selectedTenantId, effectiveTenantId, scopeInitialized, scopeKey } = scope;

  useEffect(() => {
    if (!scopeInitialized) return;
    if (isTimeRangeMount.current) {
      isTimeRangeMount.current = false;
    }
    Promise.all([fetchSessions(timeRange), fetchAppMetrics(timeRange)]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, timeRange, scopeKey]);

  useEffect(() => {
    if (!isConnected) return;
    if (!effectiveTenantId) return; // no group in aggregated mode
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

  useEffect(() => {
    const handleNewSession = (data: { session: Session }) => {
      if (data.session) {
        setSessions((prev) => {
          const idx = prev.findIndex(
            (s) => s.sessionId === data.session.sessionId
          );
          if (idx >= 0) {
            const updated = [...prev];
            updated[idx] = data.session;
            return updated;
          }
          return [data.session, ...prev];
        });
      }
    };
    const handleNewEvents = (data: { sessionId: string; sessionUpdate?: Partial<Session>; session?: Session }) => {
      const update = data.sessionUpdate || data.session;
      if (update) {
        setSessions((prev) => {
          const idx = prev.findIndex(
            (s) => s.sessionId === data.sessionId
          );
          if (idx >= 0) {
            const updated = [...prev];
            updated[idx] = { ...prev[idx], ...update };
            return updated;
          }
          return prev;
        });
      }
    };
    on("newSession", handleNewSession);
    on("newevents", handleNewEvents);
    return () => {
      off("newSession", handleNewSession);
      off("newevents", handleNewEvents);
    };
  }, [on, off]);

  const fetchSessions = async (range: "7d" | "30d" | "90d" = timeRange) => {
    try {
      const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
      // Pattern A — progressive eager fetch: render the first batch quickly, then
      // drain remaining pages in the background so fleet stats reflect the full
      // window even when the tenant has thousands of sessions. The previous
      // single-call (default 100) silently truncated busy installations.
      const opts = (continuation?: string) => ({ pageSize: FLEET_PAGE_SIZE, continuation });
      const buildUrl = (continuation?: string) => isGlobalAdmin
        ? api.globalSessions.list(selectedTenantId || undefined, days, opts(continuation))
        : api.sessions.list(tenantId, days, opts(continuation));

      const firstResponse = await authenticatedFetch(buildUrl(), getAccessToken);
      if (!firstResponse.ok) {
        addNotification('error', 'Backend Error', `Failed to load sessions: ${firstResponse.statusText}`, 'fleet-health-sessions-error');
        return;
      }
      const firstData = await firstResponse.json();
      const firstBatch: Session[] = firstData.sessions || [];
      setSessions(firstBatch);
      setLoading(false);

      let nextContinuation = extractContinuation(firstData.nextLink);
      let pages = 1;
      while (nextContinuation && pages < MAX_EAGER_PAGES) {
        const resp = await authenticatedFetch(buildUrl(nextContinuation), getAccessToken);
        if (!resp.ok) {
          console.warn(`[FleetHealth] eager-fetch stopped at page ${pages + 1} (status=${resp.status})`);
          break;
        }
        const pageData = await resp.json();
        const batch: Session[] = pageData.sessions || [];
        if (batch.length > 0) setSessions(prev => prev.concat(batch));
        nextContinuation = extractContinuation(pageData.nextLink);
        pages++;
      }
      if (pages >= MAX_EAGER_PAGES) {
        console.warn(`[FleetHealth] eager-fetch hit MAX_EAGER_PAGES=${MAX_EAGER_PAGES}; remaining sessions not loaded`);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch sessions:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load fleet health data. Please check your connection.', 'fleet-health-sessions-error');
      }
    } finally {
      setLoading(false);
    }
  };

  const fetchAppMetrics = async (range: "7d" | "30d" | "90d" = timeRange) => {
    try {
      const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
      const endpoint = isGlobalAdmin
        ? api.metrics.globalApp(days, selectedTenantId || undefined)
        : api.metrics.app(tenantId, days);
      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (response.ok) {
        const data = await response.json();
        setAppMetrics(data);
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

  // Filter sessions by time range
  const filteredSessions = useMemo(() => {
    const now = new Date();
    const days = timeRange === "7d" ? 7 : timeRange === "30d" ? 30 : 90;
    const cutoff = new Date(now.getTime() - days * 24 * 60 * 60 * 1000);
    return sessions.filter((s) => new Date(s.startedAt) >= cutoff);
  }, [sessions, timeRange]);

  // Stats
  const stats = useMemo(() => {
    const total = filteredSessions.length;
    const succeeded = filteredSessions.filter(
      (s) => s.status === "Succeeded"
    ).length;
    const failed = filteredSessions.filter(
      (s) => s.status === "Failed"
    ).length;
    const inProgress = filteredSessions.filter(
      (s) => s.status === "InProgress"
    ).length;
    const successRate = total > 0 ? (succeeded / total) * 100 : 0;

    const completedSessions = filteredSessions.filter(
      (s) => s.status !== "InProgress" && s.durationSeconds > 0
    );
    const avgDuration =
      completedSessions.length > 0
        ? completedSessions.reduce((sum, s) => sum + s.durationSeconds, 0) /
          completedSessions.length /
          60
        : 0;

    return {
      total,
      succeeded,
      failed,
      inProgress,
      successRate,
      avgDuration: Math.round(avgDuration),
    };
  }, [filteredSessions]);

  // Enrollments by day
  const dailyData = useMemo(() => {
    const days = timeRange === "7d" ? 7 : timeRange === "30d" ? 30 : 90;
    const now = new Date();
    const data: {
      label: string;
      date: string;
      success: number;
      failed: number;
    }[] = [];

    for (let i = days - 1; i >= 0; i--) {
      const d = new Date(now.getTime() - i * 24 * 60 * 60 * 1000);
      const dateStr = d.toISOString().split("T")[0];
      const dayLabel = d.toLocaleDateString(undefined, {
        weekday: "short",
      });

      const daySessions = filteredSessions.filter(
        (s) =>
          new Date(s.startedAt).toISOString().split("T")[0] === dateStr
      );

      data.push({
        label: days <= 7 ? dayLabel : d.toLocaleDateString(undefined, { month: "short", day: "numeric" }),
        date: dateStr,
        success: daySessions.filter((s) => s.status === "Succeeded").length,
        failed: daySessions.filter((s) => s.status === "Failed").length,
      });
    }

    return data;
  }, [filteredSessions, timeRange]);

  // Top failure reasons
  const failureReasons = useMemo(() => {
    const reasons: Record<string, number> = {};
    filteredSessions
      .filter((s) => s.status === "Failed")
      .forEach((s) => {
        const reason = s.failureReason || "Unknown";
        // Simplify reason for grouping
        const simplified =
          reason.length > 50 ? reason.substring(0, 50) + "..." : reason;
        reasons[simplified] = (reasons[simplified] || 0) + 1;
      });
    return Object.entries(reasons)
      .sort(([, a], [, b]) => b - a)
      .slice(0, 5);
  }, [filteredSessions]);

  // Health by device model
  const modelHealth = useMemo(() => {
    const models: Record<
      string,
      { total: number; succeeded: number; model: string }
    > = {};
    filteredSessions.forEach((s) => {
      const key = `${s.manufacturer} ${s.model}`.trim() || "Unknown";
      if (!models[key]) models[key] = { total: 0, succeeded: 0, model: key };
      models[key].total++;
      if (s.status === "Succeeded") models[key].succeeded++;
    });
    return Object.values(models)
      .filter((m) => m.total >= 1)
      .sort((a, b) => b.total - a.total)
      .slice(0, 6);
  }, [filteredSessions]);

  // Slowest models by avg enrollment duration
  const slowestModels = useMemo(() => {
    const models: Record<string, { totalDuration: number; count: number; model: string }> = {};
    filteredSessions
      .filter((s) => s.status === "Succeeded" && s.durationSeconds > 0)
      .forEach((s) => {
        const key = `${s.manufacturer} ${s.model}`.trim() || "Unknown";
        if (!models[key])
          models[key] = { totalDuration: 0, count: 0, model: key };
        models[key].totalDuration += s.durationSeconds;
        models[key].count++;
      });
    return Object.values(models)
      .map((m) => ({
        model: m.model,
        avgMinutes: Math.round(m.totalDuration / m.count / 60),
        count: m.count,
      }))
      .sort((a, b) => b.avgMinutes - a.avgMinutes)
      .slice(0, 5);
  }, [filteredSessions]);

  // Top failing models - models with most failures
  const topFailingModels = useMemo(() => {
    const models: Record<string, { failed: number; total: number; model: string }> = {};
    filteredSessions.forEach((s) => {
      const key = `${s.manufacturer} ${s.model}`.trim() || "Unknown";
      if (!models[key]) models[key] = { failed: 0, total: 0, model: key };
      models[key].total++;
      if (s.status === "Failed") models[key].failed++;
    });
    return Object.values(models)
      .filter((m) => m.failed > 0)
      .map((m) => ({
        model: m.model,
        failed: m.failed,
        total: m.total,
        failureRate: Math.round((m.failed / m.total) * 100),
      }))
      .sort((a, b) => b.failed - a.failed)
      .slice(0, 5);
  }, [filteredSessions]);

  const maxDaily = useMemo(() => {
    return Math.max(
      1,
      ...dailyData.map((d) => d.success + d.failed)
    );
  }, [dailyData]);

  const maxFailureCount = useMemo(() => {
    return failureReasons.length > 0
      ? Math.max(...failureReasons.map(([, c]) => c))
      : 1;
  }, [failureReasons]);

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-600">Loading fleet health data...</div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner show={scope.isGlobalAdmin} subtitle={globalAdminSubtitle(scope)} />
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
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-5 mb-8">
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
            {filteredSessions.length === 0 ? (
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
                  {failureReasons.map(([reason, count], i) => (
                    <div key={reason} className="flex items-center space-x-3">
                      <span className="text-xs text-gray-400 w-4">
                        {i + 1}.
                      </span>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-gray-700 truncate pr-2">
                            {reason}
                          </span>
                          <span className="text-sm font-medium text-gray-900 flex-shrink-0">
                            ({count})
                          </span>
                        </div>
                        <div className="w-full h-2 bg-gray-100 rounded-full overflow-hidden">
                          <div
                            className="h-full bg-red-400 rounded-full"
                            style={{
                              width: `${(count / maxFailureCount) * 100}%`,
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

