"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useParams, useRouter, useSearchParams } from "next/navigation";
import dynamic from "next/dynamic";
import { ProtectedRoute } from "../../../components/ProtectedRoute";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { getErrorCodeEntry, formatErrorCode } from "@/utils/errorCodeMap";
import { useAggregatedAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { chartColors } from "../../../components/charts/chartTheme";

// Lazy-load recharts on the detail page only — keeps the rest of the app's
// initial bundle untouched.
const AppLineChart = dynamic(() => import("../../../components/charts/AppLineChart"), {
  ssr: false,
  loading: () => <div className="h-64 flex items-center justify-center text-gray-400 text-sm">Loading chart…</div>,
});
const AppBarChart = dynamic(() => import("../../../components/charts/AppBarChart"), {
  ssr: false,
  loading: () => <div className="h-60 flex items-center justify-center text-gray-400 text-sm">Loading chart…</div>,
});
const AppStackedBarChart = dynamic(() => import("../../../components/charts/AppStackedBarChart"), {
  ssr: false,
  loading: () => <div className="h-64 flex items-center justify-center text-gray-400 text-sm">Loading chart…</div>,
});

interface TimeSeriesPoint {
  bucketStart: string;
  installs: number;
  succeeded: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
}

interface VersionRow {
  appVersion: string;
  installs: number;
  failed: number;
  failureRate: number;
}

interface PhaseRow {
  phase: string;
  failed: number;
}

interface FailureCodeRow {
  code: string;
  exitCode: number | null;
  count: number;
  sampleMessage: string;
}

interface DeviceModelRow {
  manufacturer: string;
  model: string;
  installs: number;
  failed: number;
  failureRate: number;
  liftVsBaseline: number;
}

interface AnalyticsResponse {
  success: boolean;
  appName: string;
  appType: string;
  windowDays: number;
  bucket: "day" | "week";
  summary: {
    totalInstalls: number;
    succeeded: number;
    failed: number;
    failureRate: number;
    avgDurationSeconds: number;
    p95DurationSeconds: number;
    avgDownloadBytes: number;
    trend: "improving" | "worsening" | "stable";
    trendDelta: number | null;
    flakinessScore: number;
  };
  timeSeries: TimeSeriesPoint[];
  versionBreakdown: VersionRow[];
  installerPhaseBreakdown: PhaseRow[];
  topFailureCodes: FailureCodeRow[];
  detectionLiesCount: number;
  deviceModelBreakdown: DeviceModelRow[];
}

interface SessionRow {
  sessionId: string;
  tenantId: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  appVersion: string;
  status: string;
  installerPhase: string;
  failureCode: string;
  exitCode: number | null;
  attemptNumber: number;
  startedAt: string;
  durationSeconds: number;
}

// ── Affected Sessions: column metadata (for the column picker) ─────────────
interface SessionColumn {
  key: string;
  label: string;
  defaultVisible: boolean;
  /** Only available in Global Admin mode (column hidden for normal users). */
  globalOnly?: boolean;
  /** Right-aligned numeric column. */
  numeric?: boolean;
}

const SESSIONS_COLUMNS: SessionColumn[] = [
  { key: "device", label: "Device", defaultVisible: true },
  { key: "tenant", label: "Tenant", defaultVisible: false, globalOnly: true },
  { key: "model", label: "Model", defaultVisible: true },
  { key: "version", label: "Version", defaultVisible: true },
  { key: "status", label: "Status", defaultVisible: true },
  { key: "attempts", label: "Attempts", defaultVisible: true, numeric: true },
  { key: "failureCode", label: "Failure Code", defaultVisible: true },
  { key: "duration", label: "Duration", defaultVisible: true, numeric: true },
];

const SESSIONS_COLUMNS_STORAGE_KEY = "appHealthSessions_visibleColumns";

function loadInitialSessionColumns(): Set<string> {
  if (typeof window !== "undefined") {
    try {
      const stored = window.localStorage.getItem(SESSIONS_COLUMNS_STORAGE_KEY);
      if (stored) return new Set(JSON.parse(stored));
    } catch {
      /* ignore */
    }
  }
  return new Set(SESSIONS_COLUMNS.filter((c) => c.defaultVisible).map((c) => c.key));
}

interface SessionsResponse {
  success: boolean;
  total: number;
  offset: number;
  limit: number;
  items: SessionRow[];
}

const SESSIONS_PAGE_SIZE = 50;

export default function AppDetailPage() {
  const params = useParams();
  const router = useRouter();
  const searchParams = useSearchParams();

  const rawAppName = (params?.appName as string) ?? "";
  const appName = useMemo(() => {
    try {
      return decodeURIComponent(rawAppName);
    } catch {
      return rawAppName;
    }
  }, [rawAppName]);

  const initialDays = (() => {
    const d = parseInt(searchParams?.get("days") ?? "30", 10);
    return d === 7 || d === 30 || d === 90 ? d : 30;
  })();

  // Initial global-admin scope from URL: ?global=1[&tenantId=...]
  // We mirror this into selectedTenantId; "" means aggregated across all tenants.
  const urlGlobal = searchParams?.get("global") === "1";
  const urlTenantId = searchParams?.get("tenantId") ?? "";

  const [days, setDays] = useState<7 | 30 | 90>(initialDays as 7 | 30 | 90);
  const [analytics, setAnalytics] = useState<AnalyticsResponse | null>(null);
  const [loading, setLoading] = useState(true);

  // Sessions panel state
  const [sessions, setSessions] = useState<SessionsResponse | null>(null);
  const [sessionsLoading, setSessionsLoading] = useState(false);
  const [statusFilter, setStatusFilter] = useState<"all" | "failed" | "succeeded">("failed");
  const [sessionsOffset, setSessionsOffset] = useState(0);

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  // Global admin tenant scope (aggregated-capable), seeded from the URL (?global=1[&tenantId=]):
  // the one-shot init honors the URL scope, else defaults to the GA's own tenant.
  const scope = useAggregatedAdminScope({ urlGlobal, urlTenantId });
  const { isGlobalAdmin, selectedTenantId, tenants, scopeInitialized, scopeKey } = scope;

  // Detail-page endpoint rule: use the /global/ endpoint when the URL flagged global scope,
  // when viewing a tenant other than the user's own, or when the own tenant isn't resolved yet.
  const useGlobalEndpoint = Boolean(isGlobalAdmin && (urlGlobal || selectedTenantId !== tenantId || !tenantId));

  // Build a tid → friendly name lookup once for the Tenant column.
  const tenantLookup = useMemo(() => {
    const map = new Map<string, string>();
    for (const t of tenants) {
      map.set(t.tenantId, t.domainName || t.tenantId);
    }
    return map;
  }, [tenants]);

  // ── Affected Sessions: column picker state ──────────────────────────────
  const [visibleSessionColumns, setVisibleSessionColumns] = useState<Set<string>>(
    loadInitialSessionColumns
  );
  const [showColumnSelector, setShowColumnSelector] = useState(false);
  const columnSelectorRef = useRef<HTMLDivElement>(null);

  // Persist visible columns
  useEffect(() => {
    try {
      window.localStorage.setItem(
        SESSIONS_COLUMNS_STORAGE_KEY,
        JSON.stringify([...visibleSessionColumns])
      );
    } catch {
      /* ignore */
    }
  }, [visibleSessionColumns]);

  // Close picker on outside click
  useEffect(() => {
    if (!showColumnSelector) return;
    const handler = (e: MouseEvent) => {
      if (columnSelectorRef.current && !columnSelectorRef.current.contains(e.target as Node)) {
        setShowColumnSelector(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, [showColumnSelector]);

  const toggleSessionColumn = (key: string) => {
    setVisibleSessionColumns((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const resetSessionColumns = () => {
    setVisibleSessionColumns(
      new Set(SESSIONS_COLUMNS.filter((c) => c.defaultVisible).map((c) => c.key))
    );
  };

  // Columns visible right now: filter out globalOnly when not in GA mode.
  const activeSessionColumns = useMemo(
    () =>
      SESSIONS_COLUMNS.filter((col) => {
        if (col.globalOnly && !isGlobalAdmin) return false;
        return visibleSessionColumns.has(col.key);
      }),
    [visibleSessionColumns, isGlobalAdmin]
  );

  // Columns selectable in the picker (hide globalOnly entries for non-GA users)
  const selectableSessionColumns = useMemo(
    () => SESSIONS_COLUMNS.filter((col) => !col.globalOnly || isGlobalAdmin),
    [isGlobalAdmin]
  );

  const fetchAnalytics = async () => {
    if (!appName) return;
    if (!isGlobalAdmin && !tenantId) return;
    try {
      setLoading(true);
      const url = useGlobalEndpoint
        ? api.apps.globalAnalytics(appName, days, selectedTenantId || undefined)
        : api.apps.analytics(tenantId, appName, days);
      const response = await authenticatedFetch(url, getAccessToken);
      if (response.ok) {
        setAnalytics((await response.json()) as AnalyticsResponse);
      } else {
        addNotification(
          "error",
          "Backend Error",
          `Failed to load app analytics: ${response.statusText}`,
          "app-analytics-error"
        );
      }
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        console.error("Failed to fetch app analytics", err);
        addNotification(
          "error",
          "Backend Not Reachable",
          "Unable to load app analytics.",
          "app-analytics-error"
        );
      }
    } finally {
      setLoading(false);
    }
  };

  const fetchSessions = async (offset: number, status: typeof statusFilter) => {
    if (!appName) return;
    if (!isGlobalAdmin && !tenantId) return;
    try {
      setSessionsLoading(true);
      const url = useGlobalEndpoint
        ? api.apps.globalSessions(appName, days, status, offset, SESSIONS_PAGE_SIZE, selectedTenantId || undefined)
        : api.apps.sessions(tenantId, appName, days, status, offset, SESSIONS_PAGE_SIZE);
      const response = await authenticatedFetch(url, getAccessToken);
      if (response.ok) {
        setSessions((await response.json()) as SessionsResponse);
      }
    } catch (err) {
      if (!(err instanceof TokenExpiredError)) {
        console.error("Failed to fetch sessions", err);
      }
    } finally {
      setSessionsLoading(false);
    }
  };

  // Single fetch effect: re-runs when scope, app, days, or tenant selection change.
  // Gated on scopeInitialized so we don't waste a backend hit fetching the
  // wrong scope before the GA default-to-own-tenant has settled.
  useEffect(() => {
    if (!scopeInitialized) return;
    setSessionsOffset(0);
    Promise.all([fetchAnalytics(), fetchSessions(0, statusFilter)]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, days, scopeKey]);

  function formatDuration(s: number) {
    if (!s) return "—";
    if (s < 60) return `${Math.round(s)}s`;
    if (s < 3600) return `${Math.round(s / 60)}m`;
    return `${(s / 3600).toFixed(1)}h`;
  }

  function formatBucketTick(value: unknown) {
    const d = new Date(String(value));
    if (isNaN(d.getTime())) return String(value);
    return `${d.getUTCMonth() + 1}/${d.getUTCDate()}`;
  }

  const failureCodeBarColor = (row: Record<string, unknown>): string => {
    const c = Number(row.count);
    return c >= 5 ? chartColors.danger : c >= 2 ? chartColors.warning : chartColors.muted;
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <button
              onClick={() => {
                // Preserve global scope when navigating back to the list
                const params = new URLSearchParams();
                if (isGlobalAdmin) {
                  params.set("global", "1");
                  if (selectedTenantId) params.set("tenantId", selectedTenantId);
                }
                const qs = params.toString();
                router.push(`/apps${qs ? `?${qs}` : ""}`);
              }}
              className="text-sm text-blue-600 hover:underline mb-2 inline-flex items-center"
            >
              ← Back to all apps
            </button>
            <div className="flex items-center justify-between">
              <div>
                <h1 className="text-2xl font-normal text-gray-900 inline-flex items-center">
                  {appName}
                  {analytics?.appType && (
                    <span className="ml-3 px-2 py-0.5 text-xs rounded bg-blue-100 text-blue-800">
                      {analytics.appType}
                    </span>
                  )}
                </h1>
                <p className="text-sm text-gray-500 mt-1">
                  {analytics
                    ? `${analytics.windowDays} day window · ${analytics.bucket === "day" ? "daily" : "weekly"} buckets`
                    : ""}
                </p>
              </div>
              <div className="flex items-center gap-3">
                <TenantScopeSelector scope={scope} allowAggregated />
                {([7, 30, 90] as const).map((d) => (
                  <button
                    key={d}
                    onClick={() => setDays(d)}
                    className={`px-4 py-2 text-sm rounded-md transition-colors ${
                      days === d
                        ? "bg-blue-600 text-white"
                        : "bg-gray-100 text-gray-700 hover:bg-gray-200"
                    }`}
                  >
                    {d} Days
                  </button>
                ))}
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8 space-y-6">
          {loading || !analytics ? (
            <div className="text-center text-gray-500 p-8">Loading…</div>
          ) : (
            <>
              {/* Summary cards */}
              <div className="grid grid-cols-2 sm:grid-cols-4 lg:grid-cols-7 gap-3">
                <SummaryCard label="Total" value={analytics.summary.totalInstalls.toString()} />
                <SummaryCard
                  label="Succeeded"
                  value={analytics.summary.succeeded.toString()}
                  color="text-emerald-700"
                />
                <SummaryCard
                  label="Failed"
                  value={analytics.summary.failed.toString()}
                  color="text-red-700"
                />
                <SummaryCard
                  label="Failure Rate"
                  value={`${analytics.summary.failureRate.toFixed(1)}%`}
                  color={
                    analytics.summary.failureRate >= 20
                      ? "text-red-700"
                      : analytics.summary.failureRate >= 5
                      ? "text-amber-700"
                      : "text-gray-900"
                  }
                />
                <SummaryCard
                  label="Avg Duration"
                  value={formatDuration(analytics.summary.avgDurationSeconds)}
                />
                <SummaryCard label="Trend" value={trendText(analytics.summary.trend, analytics.summary.trendDelta)} />
                <SummaryCard
                  label="Flakiness"
                  value={`${(analytics.summary.flakinessScore * 100).toFixed(0)}%`}
                  hint="% of installs with retries"
                />
              </div>

              {/* Detection lies warning */}
              {analytics.detectionLiesCount > 0 && (
                <div className="bg-amber-50 border-l-4 border-amber-400 p-4 rounded">
                  <p className="text-sm text-amber-800">
                    <strong>Heads up:</strong> {analytics.detectionLiesCount} install
                    {analytics.detectionLiesCount === 1 ? " was" : "s were"} reported as Succeeded
                    but the detection rule did not find the app afterwards. Check the detection rule.
                  </p>
                </div>
              )}

              {/* Charts row 1: installs (success vs failure) + duration over time */}
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                <Card title="Installs over time (success vs failure)">
                  {analytics.timeSeries.length > 0 ? (
                    <AppStackedBarChart
                      data={analytics.timeSeries as unknown as Array<Record<string, unknown>>}
                      xKey="bucketStart"
                      stacks={[
                        { dataKey: "succeeded", label: "Succeeded", color: chartColors.success },
                        { dataKey: "failed", label: "Failed", color: chartColors.danger },
                      ]}
                      formatXTick={formatBucketTick}
                    />
                  ) : (
                    <EmptyState />
                  )}
                </Card>
                <Card title="Avg Install Duration over time">
                  {analytics.timeSeries.length > 0 ? (
                    <AppLineChart
                      data={analytics.timeSeries as unknown as Array<Record<string, unknown>>}
                      xKey="bucketStart"
                      series={[
                        { dataKey: "avgDurationSeconds", label: "Avg duration (s)", color: chartColors.primary },
                      ]}
                      yUnit="s"
                      yDomain={[0, "auto"]}
                      formatXTick={formatBucketTick}
                    />
                  ) : (
                    <EmptyState />
                  )}
                </Card>
              </div>

              {/* Charts row 2: version breakdown + phase breakdown.
                  Each card is hidden when empty so the row collapses gracefully:
                  - Version: will appear once new agents emit AppVersion data
                  - Phase: deferred feature, only shows once we have failure-log samples */}
              {(analytics.versionBreakdown.length > 0 || analytics.installerPhaseBreakdown.length > 0) && (
                <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                  {analytics.versionBreakdown.length > 0 && (
                    <Card title="Failure Rate by Version">
                      <AppBarChart
                        data={analytics.versionBreakdown as unknown as Array<Record<string, unknown>>}
                        categoryKey="appVersion"
                        valueKey="failureRate"
                        valueFormatter={(v) => `${v}%`}
                        barColor={(row) => {
                          const r = Number(row.failureRate);
                          return r >= 20 ? chartColors.danger : r >= 5 ? chartColors.warning : chartColors.success;
                        }}
                      />
                    </Card>
                  )}
                  {analytics.installerPhaseBreakdown.length > 0 && (
                    <Card title="Installer Phase (failures only)">
                      <AppBarChart
                        data={analytics.installerPhaseBreakdown as unknown as Array<Record<string, unknown>>}
                        categoryKey="phase"
                        valueKey="failed"
                        horizontal
                        barColor={chartColors.danger}
                      />
                    </Card>
                  )}
                </div>
              )}

              {/* Top failure codes table */}
              <Card title="Top Failure Codes">
                {analytics.topFailureCodes.length > 0 ? (
                  <table className="min-w-full text-sm">
                    <thead className="text-left text-xs text-gray-500 uppercase tracking-wider border-b">
                      <tr>
                        <th className="px-3 py-2">Code</th>
                        <th className="px-3 py-2">Description</th>
                        <th className="px-3 py-2">Exit Code</th>
                        <th className="px-3 py-2 text-right">Count</th>
                        <th className="px-3 py-2">Sample Message</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100">
                      {analytics.topFailureCodes.map((row) => {
                        const entry = getErrorCodeEntry(row.code);
                        return (
                          <tr key={row.code}>
                            <td className="px-3 py-2 font-mono text-xs text-gray-900">
                              {formatErrorCode(row.code)}
                            </td>
                            <td className="px-3 py-2 text-gray-700">
                              {entry ? (
                                <>
                                  {entry.description}
                                  <span
                                    className={`ml-2 px-1.5 py-0.5 rounded text-xs ${
                                      entry.confidence === "high"
                                        ? "bg-emerald-100 text-emerald-800"
                                        : entry.confidence === "medium"
                                        ? "bg-amber-100 text-amber-800"
                                        : "bg-gray-100 text-gray-700"
                                    }`}
                                    title={`Source: ${entry.source}`}
                                  >
                                    {entry.confidence}
                                  </span>
                                </>
                              ) : (
                                <span className="text-gray-400">Unknown code</span>
                              )}
                            </td>
                            <td className="px-3 py-2 font-mono text-xs">
                              {row.exitCode != null ? (
                                (() => {
                                  const ec = getErrorCodeEntry(row.exitCode);
                                  return (
                                    <span title={ec?.description ?? ""}>
                                      {row.exitCode}
                                      {ec ? ` (${ec.description.slice(0, 24)}${ec.description.length > 24 ? "…" : ""})` : ""}
                                    </span>
                                  );
                                })()
                              ) : (
                                <span className="text-gray-400">—</span>
                              )}
                            </td>
                            <td className="px-3 py-2 text-right text-gray-900">{row.count}</td>
                            <td className="px-3 py-2 text-gray-500 text-xs truncate max-w-md" title={row.sampleMessage}>
                              {row.sampleMessage || "—"}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                ) : (
                  <EmptyState text="No failures recorded" />
                )}
              </Card>

              {/* Device model breakdown */}
              <Card title="Device Model Correlation">
                {analytics.deviceModelBreakdown.length > 0 ? (
                  <table className="min-w-full text-sm">
                    <thead className="text-left text-xs text-gray-500 uppercase tracking-wider border-b">
                      <tr>
                        <th className="px-3 py-2">Manufacturer</th>
                        <th className="px-3 py-2">Model</th>
                        <th className="px-3 py-2 text-right">Installs</th>
                        <th className="px-3 py-2 text-right">Failed</th>
                        <th className="px-3 py-2 text-right">Failure Rate</th>
                        <th className="px-3 py-2 text-right">Lift vs baseline</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-gray-100">
                      {analytics.deviceModelBreakdown.map((row, i) => (
                        <tr key={`${row.manufacturer}-${row.model}-${i}`}>
                          <td className="px-3 py-2 text-gray-700">{row.manufacturer}</td>
                          <td className="px-3 py-2 text-gray-900">{row.model}</td>
                          <td className="px-3 py-2 text-right text-gray-700">{row.installs}</td>
                          <td className="px-3 py-2 text-right text-red-700">{row.failed}</td>
                          <td className="px-3 py-2 text-right text-gray-900">
                            {row.failureRate.toFixed(1)}%
                          </td>
                          <td className="px-3 py-2 text-right">
                            <span
                              className={`px-2 py-0.5 rounded text-xs font-medium ${
                                row.liftVsBaseline >= 2
                                  ? "bg-red-100 text-red-800"
                                  : row.liftVsBaseline >= 1.2
                                  ? "bg-amber-100 text-amber-800"
                                  : "bg-emerald-100 text-emerald-800"
                              }`}
                            >
                              {row.liftVsBaseline.toFixed(2)}×
                            </span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                ) : (
                  <EmptyState text="Not enough installs per device model to compute correlation" />
                )}
              </Card>

              {/* Affected sessions panel */}
              <Card title="Affected Sessions">
                <div className="flex items-center gap-2 mb-3">
                  {(["failed", "all", "succeeded"] as const).map((s) => (
                    <button
                      key={s}
                      onClick={() => {
                        setStatusFilter(s);
                        setSessionsOffset(0);
                        fetchSessions(0, s);
                      }}
                      className={`px-3 py-1 text-xs rounded-md ${
                        statusFilter === s
                          ? "bg-blue-600 text-white"
                          : "bg-gray-100 text-gray-700 hover:bg-gray-200"
                      }`}
                    >
                      {s === "failed" ? "Failed only" : s === "succeeded" ? "Succeeded" : "All"}
                    </button>
                  ))}
                  {sessions && (
                    <span className="text-xs text-gray-500 ml-auto">
                      {sessions.total} session{sessions.total === 1 ? "" : "s"}
                    </span>
                  )}
                  {/* Column picker */}
                  <div className="relative" ref={columnSelectorRef}>
                    <button
                      onClick={() => setShowColumnSelector((v) => !v)}
                      className="inline-flex items-center gap-1.5 px-3 py-1 rounded-md text-xs font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 border border-gray-200"
                      title="Configure visible columns"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2" />
                      </svg>
                      Columns
                    </button>
                    {showColumnSelector && (
                      <div className="absolute right-0 top-full mt-1 w-56 bg-white rounded-lg shadow-lg border border-gray-200 z-50 py-2">
                        <div className="px-3 py-1.5 text-xs font-semibold text-gray-400 uppercase tracking-wider flex items-center justify-between">
                          <span>Toggle Columns</span>
                          <button
                            onClick={resetSessionColumns}
                            className="text-blue-500 hover:text-blue-700 normal-case font-medium tracking-normal"
                          >
                            Reset
                          </button>
                        </div>
                        <div className="border-t border-gray-100 mt-1 pt-1">
                          {selectableSessionColumns.map((col) => (
                            <label
                              key={col.key}
                              className="flex items-center gap-2 px-3 py-1.5 hover:bg-gray-50 cursor-pointer text-sm text-gray-700"
                            >
                              <input
                                type="checkbox"
                                checked={visibleSessionColumns.has(col.key)}
                                onChange={() => toggleSessionColumn(col.key)}
                                className="rounded border-gray-300 text-blue-600 focus:ring-blue-500 h-3.5 w-3.5"
                              />
                              <span className="flex-1">{col.label}</span>
                              {col.globalOnly && (
                                <span className="text-[10px] uppercase tracking-wider text-purple-600">GA</span>
                              )}
                            </label>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                </div>
                {sessionsLoading ? (
                  <div className="text-center text-gray-500 p-4">Loading sessions…</div>
                ) : sessions && sessions.items.length > 0 ? (
                  <>
                    <table className="min-w-full text-sm">
                      <thead className="text-left text-xs text-gray-500 uppercase tracking-wider border-b">
                        <tr>
                          {activeSessionColumns.map((col) => (
                            <th
                              key={col.key}
                              className={`px-3 py-2 ${col.numeric ? "text-right" : ""}`}
                            >
                              {col.label}
                            </th>
                          ))}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-100">
                        {sessions.items.map((row) => {
                          const entry = row.failureCode ? getErrorCodeEntry(row.failureCode) : null;
                          return (
                            <tr
                              key={`${row.tenantId}-${row.sessionId}`}
                              onClick={() => router.push(`/sessions/${row.sessionId}`)}
                              className="hover:bg-gray-50 cursor-pointer"
                            >
                              {activeSessionColumns.map((col) => {
                                switch (col.key) {
                                  case "device":
                                    return (
                                      <td key={col.key} className="px-3 py-2 text-gray-900">
                                        {row.deviceName || "—"}
                                      </td>
                                    );
                                  case "tenant": {
                                    const friendly = tenantLookup.get(row.tenantId);
                                    return (
                                      <td
                                        key={col.key}
                                        className="px-3 py-2 text-gray-700 text-xs"
                                        title={row.tenantId}
                                      >
                                        {friendly || `${row.tenantId.substring(0, 8)}…`}
                                      </td>
                                    );
                                  }
                                  case "model":
                                    return (
                                      <td key={col.key} className="px-3 py-2 text-gray-700 text-xs">
                                        {row.manufacturer} {row.model}
                                      </td>
                                    );
                                  case "version":
                                    return (
                                      <td key={col.key} className="px-3 py-2 text-gray-700 font-mono text-xs">
                                        {row.appVersion || "—"}
                                      </td>
                                    );
                                  case "status":
                                    return (
                                      <td key={col.key} className="px-3 py-2">
                                        <span
                                          className={`px-2 py-0.5 rounded text-xs ${
                                            row.status === "Failed"
                                              ? "bg-red-100 text-red-800"
                                              : row.status === "Succeeded"
                                              ? "bg-emerald-100 text-emerald-800"
                                              : "bg-gray-100 text-gray-700"
                                          }`}
                                        >
                                          {row.status}
                                        </span>
                                      </td>
                                    );
                                  case "attempts":
                                    return (
                                      <td key={col.key} className="px-3 py-2 text-right text-gray-700">
                                        {row.attemptNumber || "—"}
                                      </td>
                                    );
                                  case "failureCode":
                                    return (
                                      <td key={col.key} className="px-3 py-2 font-mono text-xs">
                                        {row.failureCode ? (
                                          <span title={entry?.description ?? ""}>
                                            {formatErrorCode(row.failureCode)}
                                          </span>
                                        ) : (
                                          <span className="text-gray-400">—</span>
                                        )}
                                      </td>
                                    );
                                  case "duration":
                                    return (
                                      <td key={col.key} className="px-3 py-2 text-right text-gray-700">
                                        {formatDuration(row.durationSeconds)}
                                      </td>
                                    );
                                  default:
                                    return null;
                                }
                              })}
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                    {/* Pagination */}
                    {sessions.total > SESSIONS_PAGE_SIZE && (
                      <div className="flex items-center justify-between mt-3 text-xs text-gray-600">
                        <span>
                          {sessions.offset + 1}–
                          {Math.min(sessions.offset + sessions.items.length, sessions.total)} of{" "}
                          {sessions.total}
                        </span>
                        <div className="space-x-2">
                          <button
                            disabled={sessions.offset === 0}
                            onClick={() => {
                              const next = Math.max(0, sessionsOffset - SESSIONS_PAGE_SIZE);
                              setSessionsOffset(next);
                              fetchSessions(next, statusFilter);
                            }}
                            className="px-3 py-1 rounded bg-gray-100 hover:bg-gray-200 disabled:opacity-40"
                          >
                            Previous
                          </button>
                          <button
                            disabled={sessions.offset + sessions.items.length >= sessions.total}
                            onClick={() => {
                              const next = sessionsOffset + SESSIONS_PAGE_SIZE;
                              setSessionsOffset(next);
                              fetchSessions(next, statusFilter);
                            }}
                            className="px-3 py-1 rounded bg-gray-100 hover:bg-gray-200 disabled:opacity-40"
                          >
                            Next
                          </button>
                        </div>
                      </div>
                    )}
                  </>
                ) : (
                  <EmptyState text="No sessions match this filter" />
                )}
              </Card>
            </>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}

function SummaryCard({
  label,
  value,
  color,
  hint,
}: {
  label: string;
  value: string;
  color?: string;
  hint?: string;
}) {
  return (
    <div className="bg-white rounded-lg shadow p-3">
      <div className="text-xs text-gray-500 uppercase tracking-wide" title={hint}>
        {label}
      </div>
      <div className={`text-xl font-semibold mt-1 ${color ?? "text-gray-900"}`}>{value}</div>
    </div>
  );
}

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-white rounded-lg shadow p-4">
      <h2 className="text-sm font-semibold text-gray-700 mb-3">{title}</h2>
      {children}
    </div>
  );
}

function EmptyState({ text = "No data" }: { text?: string }) {
  return <div className="h-32 flex items-center justify-center text-gray-400 text-sm">{text}</div>;
}

function trendText(trend: string, delta: number | null) {
  if (delta == null) return "—";
  if (trend === "improving") return `↓ ${Math.abs(delta).toFixed(1)} pp`;
  if (trend === "worsening") return `↑ ${Math.abs(delta).toFixed(1)} pp`;
  return "stable";
}
