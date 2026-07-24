'use client';

import { useEffect, useState, useCallback } from 'react';
import { useTenant } from '../../contexts/TenantContext';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import TruncatedLabel from '@/components/TruncatedLabel';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import dynamic from "next/dynamic";
import { SlaGauge } from "@/components/charts/SlaGauge";
import { chartColors } from "@/components/charts/chartTheme";

// recharts (~300 kB) only renders below the fold here. Lazy-load it so it
// stays out of the /sla route chunk until the trend chart is actually shown.
const AppLineChart = dynamic(() => import("@/components/charts/AppLineChart"), {
  ssr: false,
  loading: () => (
    <div className="flex h-64 items-center justify-center text-sm text-gray-400">
      Loading chart…
    </div>
  ),
});
import { trackEvent } from "@/lib/appInsights";
import { useGlobalAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import Link from "next/link";

interface SlaSnapshot {
  week: string;
  totalCompleted: number;
  succeeded: number;
  failed: number;
  successRate: number;
  avgDurationMinutes: number;
  p95DurationMinutes: number;
  durationViolationCount: number;
  successRateMet: boolean;
  durationTargetMet: boolean;
}

interface SlaWeeklyTrend {
  week: string;
  successRate: number;
  p95DurationMinutes: number;
  appInstallSuccessRate: number;
  totalCompleted: number;
  successRateMet: boolean;
  durationTargetMet: boolean;
  appInstallTargetMet: boolean;
}

interface SlaViolatorSession {
  sessionId: string;
  tenantId: string;
  deviceName: string;
  serialNumber: string;
  startedAt: string;
  completedAt: string | null;
  durationSeconds: number | null;
  status: number;
  failureReason: string | null;
  violationType: string;
}

interface TopFailingApp {
  appName: string;
  failCount: number;
  totalCount: number;
  successRate: number;
}

interface AppInstallSlaSnapshot {
  totalInstalls: number;
  succeeded: number;
  failed: number;
  successRate: number;
  targetMet: boolean;
  topFailingApps: TopFailingApp[];
}

interface SlaMetricsResponse {
  targetSuccessRate: number | null;
  targetMaxDurationMinutes: number | null;
  targetAppInstallSuccessRate: number | null;
  currentWeek: SlaSnapshot;
  weeklyTrend: SlaWeeklyTrend[];
  violators: SlaViolatorSession[];
  appInstallSla: AppInstallSlaSnapshot | null;
  computedAt: string;
  fromCache: boolean;
  computeDurationMs: number;
}

function formatDuration(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

const statusLabels: Record<number, string> = {
  0: "InProgress", 1: "Pending", 2: "Stalled", 3: "Succeeded", 4: "Failed", 5: "Unknown",
  6: "AwaitingUser", 7: "Incomplete", // appended 2026-07-08 — ordinals must match SessionStatus
};

export default function SlaPage() {
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const [metrics, setMetrics] = useState<SlaMetricsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [months, setMonths] = useState(3);
  const [initialLoad, setInitialLoad] = useState(true);

  // Global admin tenant scope (tenant list, selector state, override/effective tenant)
  const scope = useGlobalAdminScope();
  const { isGlobalOverride, effectiveTenantId, selectedTenantId, tenants } = scope;

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!effectiveTenantId) return;
    try {
      if (showRefreshing) setRefreshing(true);
      else setLoading(true);

      const useFresh = initialLoad;
      if (initialLoad) setInitialLoad(false);

      const url = isGlobalOverride
        ? api.metrics.globalSla(effectiveTenantId, months, useFresh)
        : api.metrics.sla(effectiveTenantId, months, useFresh);

      const response = await authenticatedFetch(url, getAccessToken);
      if (!response.ok) {
        addNotification('error', 'Error', `Failed to load SLA metrics: ${response.statusText}`, 'sla-fetch-error');
        return;
      }
      setMetrics(await response.json());
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Error loading SLA metrics:', err);
        addNotification('error', 'Error', 'Failed to load SLA metrics', 'sla-fetch-error');
      }
      trackEvent('sla_load_failed', {
        months,
        error: err instanceof Error ? err.message : 'unknown',
      });
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [effectiveTenantId, isGlobalOverride, months, getAccessToken, addNotification]);

  useEffect(() => { fetchMetrics(); }, [fetchMetrics]);

  useEffect(() => {
    if (tenantId) trackEvent('sla_page_viewed', { months });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId]);

  const selectedTenantName = tenants.find(t => t.tenantId === selectedTenantId)?.domainName;

  const hasTargets = metrics &&
    (metrics.targetSuccessRate != null || metrics.targetMaxDurationMinutes != null ||
     metrics.targetAppInstallSuccessRate != null);

  if (loading && !metrics) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 dark:from-gray-900 dark:to-gray-800 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto" />
          <p className="mt-4 text-gray-600 dark:text-gray-400">Loading SLA metrics...</p>
        </div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
        <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
        {/* Header */}
        <header className="bg-white dark:bg-gray-800 shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <h1 className="text-2xl font-normal text-gray-900 dark:text-white">SLA Compliance</h1>
                <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
                  {isGlobalOverride && selectedTenantName
                    ? `Tenant: ${selectedTenantName} · `
                    : ''}
                  Monitor enrollment performance against your SLA targets
                  {metrics && (
                    <>
                      {" · "}Computed at {new Date(metrics.computedAt).toLocaleString()} in {metrics.computeDurationMs}ms
                      {metrics.fromCache && (
                        <span className="ml-2 px-2 py-0.5 bg-blue-100 dark:bg-blue-900/40 text-blue-700 dark:text-blue-400 text-xs rounded">
                          From Cache
                        </span>
                      )}
                    </>
                  )}
                </p>
              </div>
              <div className="flex items-center gap-3">
                <TenantScopeSelector scope={scope} themed />
                <select
                  value={months}
                  onChange={(e) => {
                    const next = Number(e.target.value);
                    trackEvent('sla_months_changed', { months: next });
                    setMonths(next);
                  }}
                  className="text-sm border border-gray-300 dark:border-gray-600 rounded-md px-2 py-1.5 bg-white dark:bg-gray-700 text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
                >
                  <option value={1}>Last month</option>
                  <option value={3}>Last 3 months</option>
                  <option value={6}>Last 6 months</option>
                </select>
                <button
                  onClick={() => {
                    trackEvent('sla_refresh_clicked', { months });
                    fetchMetrics(true);
                  }}
                  disabled={refreshing}
                  className="px-4 py-2 bg-white dark:bg-gray-700 border border-gray-200 dark:border-gray-600 text-gray-700 dark:text-gray-200 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                >
                  <svg className={`h-5 w-5 ${refreshing ? 'animate-spin' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <span>{refreshing ? 'Refreshing...' : 'Refresh'}</span>
                </button>
              </div>
            </div>
          </div>
        </header>

        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">

          {/* No targets state */}
          {!hasTargets && (
            <div className="bg-white dark:bg-gray-800 shadow rounded-lg p-8 text-center">
              <svg className="h-16 w-16 text-gray-300 dark:text-gray-600 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
              </svg>
              <h2 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">No SLA Targets Configured</h2>
              <p className="text-gray-500 dark:text-gray-400 mb-4">
                Set up SLA targets to track enrollment success rate and duration compliance.
              </p>
              <Link
                href="/settings/tenant/sla-targets"
                className="inline-flex items-center px-4 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-500 transition-colors"
              >
                Configure SLA Targets
              </Link>
            </div>
          )}

          {metrics && hasTargets && (
            <>
              {/* Overall status banner */}
              <OverallStatusBanner metrics={metrics} />

              {/* SLA Gauges */}
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-5 mb-8">
                {metrics.targetSuccessRate != null && (
                  <SlaGauge
                    value={metrics.currentWeek.successRate}
                    target={metrics.targetSuccessRate}
                    label="Enrollment Success Rate"
                    unit="%"
                  />
                )}
                {metrics.targetMaxDurationMinutes != null && (
                  <SlaGauge
                    value={metrics.currentWeek.p95DurationMinutes}
                    target={metrics.targetMaxDurationMinutes}
                    label="P95 Enrollment Duration"
                    unit="min"
                    invert
                  />
                )}
                {metrics.targetAppInstallSuccessRate != null && metrics.appInstallSla && (
                  <SlaGauge
                    value={metrics.appInstallSla.successRate}
                    target={metrics.targetAppInstallSuccessRate}
                    label="App Install Success Rate"
                    unit="%"
                  />
                )}
              </div>

              {/* Current week stats */}
              <div className="mb-3 text-sm text-gray-500 dark:text-gray-400">This week ({metrics.currentWeek.week})</div>
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-5 mb-8">
                <StatCard label="Total Sessions" value={metrics.currentWeek.totalCompleted} color="blue" />
                <StatCard label="Succeeded" value={metrics.currentWeek.succeeded} color="green" />
                <StatCard label="Failed" value={metrics.currentWeek.failed} color="red" />
                <StatCard label="Duration Violations" value={metrics.currentWeek.durationViolationCount} color="yellow" />
              </div>

              {/* Weekly trend */}
              {metrics.weeklyTrend.length > 1 && (
                <div className="bg-white dark:bg-gray-800 shadow rounded-lg p-6 mb-8">
                  <h2 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Weekly Trend</h2>
                  <AppLineChart
                    data={[...metrics.weeklyTrend].reverse() as unknown as Array<Record<string, unknown>>}
                    xKey="week"
                    series={[
                      { dataKey: "successRate", label: "Success Rate (%)", color: chartColors.primary },
                      ...(metrics.targetAppInstallSuccessRate != null
                        ? [{ dataKey: "appInstallSuccessRate", label: "App Install Rate (%)", color: chartColors.success }]
                        : []),
                    ]}
                    height={300}
                  />
                </div>
              )}

              {/* Two-column layout: Failing Apps + Summary */}
              {metrics.appInstallSla && metrics.appInstallSla.topFailingApps.length > 0 && (
                <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-8">
                  <div className="bg-white dark:bg-gray-800 shadow rounded-lg p-6">
                    <h2 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">Top Failing Apps</h2>
                    <div className="space-y-3">
                      {metrics.appInstallSla.topFailingApps.map((app, i) => (
                        <div key={app.appName} className="flex items-center space-x-3">
                          <span className="text-xs text-gray-400 w-4">{i + 1}</span>
                          <div className="flex-1 min-w-0">
                            <div className="flex items-center justify-between mb-1">
                              <TruncatedLabel text={app.appName} className="text-sm text-gray-700 dark:text-gray-300 pr-2" />
                              <span className="text-sm font-medium text-red-600 dark:text-red-400 flex-shrink-0">{app.failCount} failed</span>
                            </div>
                            <div className="w-full h-2 bg-gray-100 dark:bg-gray-700 rounded-full overflow-hidden">
                              <div
                                className="h-full bg-red-400 rounded-full"
                                style={{ width: `${100 - app.successRate}%` }}
                              />
                            </div>
                            <span className="text-xs text-gray-400">{app.successRate.toFixed(1)}% success ({app.totalCount} total)</span>
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                  <div className="bg-white dark:bg-gray-800 shadow rounded-lg p-6">
                    <h2 className="text-lg font-semibold text-gray-900 dark:text-white mb-4">App Install Summary</h2>
                    <div className="space-y-4">
                      <div className="flex justify-between items-center">
                        <span className="text-sm text-gray-500 dark:text-gray-400">Total Installs</span>
                        <span className="text-lg font-semibold text-gray-900 dark:text-white">{metrics.appInstallSla.totalInstalls}</span>
                      </div>
                      <div className="flex justify-between items-center">
                        <span className="text-sm text-gray-500 dark:text-gray-400">Succeeded</span>
                        <span className="text-lg font-semibold text-green-700 dark:text-green-400">{metrics.appInstallSla.succeeded}</span>
                      </div>
                      <div className="flex justify-between items-center">
                        <span className="text-sm text-gray-500 dark:text-gray-400">Failed</span>
                        <span className="text-lg font-semibold text-red-700 dark:text-red-400">{metrics.appInstallSla.failed}</span>
                      </div>
                      <div className="flex justify-between items-center">
                        <span className="text-sm text-gray-500 dark:text-gray-400">Success Rate</span>
                        <span className={`text-lg font-semibold ${metrics.appInstallSla.targetMet ? "text-green-700 dark:text-green-400" : "text-red-700 dark:text-red-400"}`}>
                          {metrics.appInstallSla.successRate.toFixed(1)}%
                        </span>
                      </div>
                    </div>
                  </div>
                </div>
              )}

              {/* Violators table */}
              {metrics.violators.length > 0 && (
                <div className="bg-white dark:bg-gray-800 shadow rounded-lg overflow-hidden mb-8">
                  <div className="px-6 py-4 border-b border-gray-200 dark:border-gray-700">
                    <h2 className="text-lg font-semibold text-gray-900 dark:text-white">
                      SLA Violators
                      <span className="ml-2 text-sm font-normal text-gray-500 dark:text-gray-400">({metrics.violators.length})</span>
                    </h2>
                  </div>
                  <div className="overflow-x-auto">
                    <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                      <thead className="bg-gray-50 dark:bg-gray-750">
                        <tr>
                          {["Device", "Serial", "Started", "Duration", "Status", "Violation", "Failure Reason"].map((h) => (
                            <th key={h} className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                              {h}
                            </th>
                          ))}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
                        {metrics.violators.map((v) => (
                          <tr key={v.sessionId} className="hover:bg-gray-50 dark:hover:bg-gray-750">
                            <td className="px-6 py-4 whitespace-nowrap">
                              <Link
                                href={`/sessions/${v.sessionId}`}
                                onClick={() => trackEvent('sla_violator_opened', {
                                  sessionId: v.sessionId,
                                  tenantId: v.tenantId,
                                  violationType: v.violationType,
                                })}
                                className="text-indigo-600 dark:text-indigo-400 hover:underline text-sm"
                              >
                                {v.deviceName || "Unknown"}
                              </Link>
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400">{v.serialNumber || "-"}</td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400">{new Date(v.startedAt).toLocaleDateString()}</td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-700 dark:text-gray-300">{v.durationSeconds ? formatDuration(v.durationSeconds) : "-"}</td>
                            <td className="px-6 py-4 whitespace-nowrap">
                              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                                v.status === 4 ? "bg-red-100 dark:bg-red-900/40 text-red-700 dark:text-red-400" : "bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300"
                              }`}>
                                {statusLabels[v.status] ?? "Unknown"}
                              </span>
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap">
                              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${
                                v.violationType === "Both" ? "bg-red-100 dark:bg-red-900/40 text-red-700 dark:text-red-400" :
                                v.violationType === "Failed" ? "bg-red-100 dark:bg-red-900/40 text-red-700 dark:text-red-400" :
                                "bg-yellow-100 dark:bg-yellow-900/40 text-yellow-700 dark:text-yellow-400"
                              }`}>
                                {v.violationType}
                              </span>
                            </td>
                            <td className="px-6 py-4 max-w-xs">
                              <TruncatedLabel text={v.failureReason || "-"} className="block text-sm text-gray-500 dark:text-gray-400" />
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {metrics.violators.length === 0 && (
                <div className="bg-white dark:bg-gray-800 shadow rounded-lg p-6 mb-8 text-center">
                  <svg className="h-12 w-12 text-green-400 mx-auto mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <p className="text-gray-500 dark:text-gray-400 text-sm">No SLA violations in the selected period</p>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </ProtectedRoute>
  );
}

/* ---------- Overall status banner ---------- */

function OverallStatusBanner({ metrics }: { metrics: SlaMetricsResponse }) {
  const checks: { label: string; met: boolean }[] = [];
  if (metrics.targetSuccessRate != null)
    checks.push({ label: "Success Rate", met: metrics.currentWeek.successRateMet });
  if (metrics.targetMaxDurationMinutes != null)
    checks.push({ label: "Duration", met: metrics.currentWeek.durationTargetMet });
  if (metrics.targetAppInstallSuccessRate != null && metrics.appInstallSla)
    checks.push({ label: "App Installs", met: metrics.appInstallSla.targetMet });

  const allMet = checks.every(c => c.met);
  const metCount = checks.filter(c => c.met).length;

  return (
    <div className={`rounded-lg shadow p-4 mb-8 flex items-center justify-between ${
      allMet
        ? "bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800"
        : "bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800"
    }`}>
      <div className="flex items-center space-x-3">
        {allMet ? (
          <svg className="h-8 w-8 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
        ) : (
          <svg className="h-8 w-8 text-red-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
        )}
        <div>
          <span className={`text-lg font-semibold ${allMet ? "text-green-800 dark:text-green-300" : "text-red-800 dark:text-red-300"}`}>
            {allMet ? "All SLA Targets Met" : "SLA Targets Breached"}
          </span>
          <span className="text-sm text-gray-500 dark:text-gray-400 ml-2">
            {metCount}/{checks.length} targets on track
          </span>
        </div>
      </div>
      <div className="flex items-center space-x-4">
        {checks.map((c) => (
          <div key={c.label} className="flex items-center space-x-1.5">
            {c.met ? (
              <svg className="h-4 w-4 text-green-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
              </svg>
            ) : (
              <svg className="h-4 w-4 text-red-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M6 18L18 6M6 6l12 12" />
              </svg>
            )}
            <span className={`text-sm ${c.met ? "text-green-700 dark:text-green-400" : "text-red-700 dark:text-red-400"}`}>
              {c.label}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}

/* ---------- Stat card (FleetStatCard pattern) ---------- */

const colorMap = {
  blue:   { border: "border-blue-500",   bg: "bg-blue-50 dark:bg-blue-900/20",   text: "text-blue-700 dark:text-blue-400" },
  green:  { border: "border-green-500",  bg: "bg-green-50 dark:bg-green-900/20",  text: "text-green-700 dark:text-green-400" },
  red:    { border: "border-red-500",    bg: "bg-red-50 dark:bg-red-900/20",    text: "text-red-700 dark:text-red-400" },
  yellow: { border: "border-yellow-500", bg: "bg-yellow-50 dark:bg-yellow-900/20", text: "text-yellow-700 dark:text-yellow-400" },
};

function StatCard({ label, value, color }: { label: string; value: number; color: keyof typeof colorMap }) {
  const c = colorMap[color];
  return (
    <div className={`bg-white dark:bg-gray-800 overflow-hidden shadow rounded-lg border-l-4 ${c.border}`}>
      <div className={`p-5 ${c.bg}`}>
        <div className="text-sm font-medium text-gray-500 dark:text-gray-400">{label}</div>
        <div className={`text-3xl font-bold ${c.text} mt-1`}>{value.toLocaleString()}</div>
      </div>
    </div>
  );
}
