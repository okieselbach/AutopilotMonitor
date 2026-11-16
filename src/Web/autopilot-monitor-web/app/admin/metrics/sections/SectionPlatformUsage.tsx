'use client';

import { Suspense, useCallback, useEffect, useState } from 'react';
import { api } from '@/lib/api';
import { useAuth } from '../../../../contexts/AuthContext';
import { apiErrorText, fetchJson } from "@/lib/apiClient";
import { useFetchProgress } from "@/hooks/useFetchProgress";
import { CalculatingDialog } from "@/components/CalculatingCard";
import { useWindowDays } from "@/hooks/useWindowDays";
import { windowProgressKey } from "@/lib/timeWindow";
import type { PlatformUsageMetrics, PlatformStats } from "@/lib/generated/wire-types.generated";
import {
  AppScriptsBlock,
  DeploymentTypeBreakdown,
  HardwareBlock,
  NestedUsageHeadings,
  PerformanceBlock,
  SessionOutcomeRow,
  UsageSection,
  UsageStatRow,
  UsersBlock,
  sessionRecencyStats,
} from "@/components/UsageMetricsBlocks";

// A fresh cross-tenant compute takes tens of seconds server-side; the default 30s fetch timeout
// aborted it client-side (HTTP 499 on the backend) while the server kept computing. Give this one
// call generous headroom — the backend caches the result for 15 minutes afterwards.
const FETCH_TIMEOUT_MS = 180_000;

/** The backend's default window for these metrics; a URL `?days=` replaces it (no selector). */
const USAGE_DEFAULT_WINDOW_DAYS = 90;

export function SectionPlatformUsage() {
  // useSearchParams (the ?days= window) requires a Suspense boundary under the static export.
  return (
    <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
      <PlatformUsageContent />
    </Suspense>
  );
}

function PlatformUsageContent() {
  const { getAccessToken } = useAuth();
  const [metrics, setMetrics] = useState<PlatformUsageMetrics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  // Platform roles may request up to 365 days; the section has no tenant of its own.
  const { days, ready: windowReady } = useWindowDays({ defaultDays: USAGE_DEFAULT_WINDOW_DAYS, tenantId: undefined });
  // Progress estimate records only fresh computes — a 15-min-cache hit finishing in ~100ms
  // would make the next cold load look stuck at the bar's cap.
  const progress = useFetchProgress(windowProgressKey('platformUsage.lastFreshFetchMs', days));
  const { begin: progressBegin, finish: progressFinish } = progress;

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!windowReady) return;
    let recordEstimate = false;
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }
      setError(null);
      progressBegin();

      // Platform-wide metrics - cross-tenant (Global Admin only)
      const data = await fetchJson<PlatformUsageMetrics>(api.metrics.globalUsage(undefined, days), getAccessToken, {
        signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
      });
      setMetrics(data);
      recordEstimate = !data.fromCache;
    } catch (err) {
      console.error('Error fetching platform usage metrics:', err);
      if (err instanceof DOMException && (err.name === 'TimeoutError' || err.name === 'AbortError')) {
        setError('The calculation is taking unusually long. It continues on the server and the result is cached once finished — please retry in a moment.');
      } else {
        setError(apiErrorText(err, 'Failed to fetch platform usage metrics'));
      }
    } finally {
      progressFinish(recordEstimate);
      setLoading(false);
      setRefreshing(false);
    }
  }, [windowReady, days, getAccessToken, progressBegin, progressFinish]);

  useEffect(() => {
    // Fetch metrics on mount
    // Authorization is handled by admin layout ProtectedRoute
    const run = async () => {
      await fetchMetrics();
    };
    void run();
  }, [fetchMetrics]);

  const formatTimestamp = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  const windowDays = metrics?.windowDays ?? USAGE_DEFAULT_WINDOW_DAYS;

  // The metrics layout's header is the page header; a section only adds its title row (like the other sections).
  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Platform Usage</h2>
          <p className="text-sm text-gray-500">
            {metrics ? (
              <>
                Cross-tenant · rolling {windowDays}-day window · computed at {formatTimestamp(metrics.computedAt)} in {metrics.computeDurationMs}ms
                {metrics.fromCache && (
                  <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                    From Cache
                  </span>
                )}
              </>
            ) : (
              'Platform usage statistics across all tenants'
            )}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <button
            type="button"
            // Without figures on screen a retry is a full load with the progress dialog.
            onClick={() => void fetchMetrics(metrics != null)}
            disabled={loading || refreshing}
            className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-50"
          >
            {refreshing ? `Refreshing… ${Math.floor(progress.elapsedMs / 1000)}s` : 'Refresh'}
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
            title="Calculating platform metrics…"
            subtitle="Aggregating sessions, app installs and user activity across all tenants. A fresh calculation can take a minute — the result is then cached for 15 minutes."
            elapsedMs={progress.elapsedMs}
            estimateMs={progress.estimateMs}
          />
        </div>
      ) : (
        metrics && (
          <NestedUsageHeadings>
            <div>
              <UsageSection
                title={`Sessions (Last ${windowDays} Days)`}
                description={`Rolling ${windowDays}-day window. For all-time totals see Platform Statistics below.`}
              >
                <UsageStatRow
                  stats={[
                    {
                      label: "Sessions in Window",
                      title: `Sessions started in the last ${windowDays} days. All-time total is under Platform Statistics.`,
                      value: metrics.sessions.total.toLocaleString(),
                      valueClass: "text-gray-900",
                      compact: { label: "In window" },
                    },
                    ...sessionRecencyStats(metrics.sessions),
                  ]}
                />
                <SessionOutcomeRow sessions={metrics.sessions} />
              </UsageSection>

              <DeploymentTypeBreakdown deploymentTypes={metrics.deploymentTypes} />

              {metrics.appScripts && <AppScriptsBlock appScripts={metrics.appScripts} />}

              <UsageSection title="Tenants">
                <UsageStatRow
                  stats={[
                    {
                      label: "Total Tenants",
                      value: metrics.tenants.total.toLocaleString(),
                      valueClass: "text-gray-900",
                      compact: { label: "Tenants" },
                    },
                    {
                      label: "Active (Last 7 Days)",
                      value: metrics.tenants.active7Days.toLocaleString(),
                      valueClass: "text-blue-600",
                      compact: { label: "Active · 7 d" },
                    },
                    {
                      label: "Active (Last 30 Days)",
                      value: metrics.tenants.active30Days.toLocaleString(),
                      valueClass: "text-indigo-600",
                      compact: { label: "Active · 30 d" },
                    },
                  ]}
                />
              </UsageSection>

              <UsersBlock users={metrics.users} />

              <PerformanceBlock performance={metrics.performance} />

              {metrics.platformStats && <PlatformStatistics stats={metrics.platformStats} />}

              <HardwareBlock hardware={metrics.hardware} />
            </div>
          </NestedUsageHeadings>
        )
      )}
    </div>
  );
}

/** Cumulative since release: tenants and users, enrollments, events and models. */
function PlatformStatistics({ stats }: { stats: PlatformStats }) {
  return (
    <UsageSection title="Platform Statistics (Since Release)">
      <UsageStatRow
        className="mb-4"
        stats={[
          {
            label: "Signed Up Tenants",
            value: (stats.totalSignedUpTenants ?? 0).toLocaleString(),
            valueClass: "text-cyan-600",
            note: "all registered tenants",
            compact: { label: "Signed-up tenants" },
          },
          {
            label: "Active Tenants",
            value: stats.totalTenants.toLocaleString(),
            valueClass: "text-teal-600",
            note: "with at least one session",
            compact: { label: "Active tenants", note: "≥ 1 session" },
          },
          {
            label: "Users Seen",
            value: stats.totalUsers.toLocaleString(),
            valueClass: "text-indigo-600",
            note: "cumulative unique users",
            compact: { label: "Users seen" },
          },
        ]}
      />
      <UsageStatRow
        className="mb-4"
        stats={[
          {
            label: "Total Enrollments",
            title: "One enrollment = one session. This is the all-time cumulative count, so it is higher than the windowed Sessions figure above.",
            value: stats.totalEnrollments.toLocaleString(),
            valueClass: "text-blue-600",
            note: "all sessions ever (cumulative)",
            compact: { label: "Enrollments" },
          },
          {
            label: "Successful Enrollments",
            value: stats.successfulEnrollments.toLocaleString(),
            valueClass: "text-green-600",
            note: "cumulative, all-time",
            compact: { label: "Successful", tone: "green" },
          },
          {
            label: "Issues Detected",
            value: stats.issuesDetected.toLocaleString(),
            valueClass: "text-red-600",
            note: "by analyze rules",
            compact: { label: "Issues detected", tone: "red" },
          },
        ]}
      />
      <UsageStatRow
        stats={[
          {
            label: "Total Events Processed",
            value: stats.totalEventsProcessed.toLocaleString(),
            valueClass: "text-purple-600",
            note: "all ingested events",
            compact: { label: "Events processed" },
          },
          {
            label: "Unique Device Models",
            value: stats.uniqueDeviceModels.toLocaleString(),
            valueClass: "text-amber-600",
            note: "distinct manufacturer + model combinations",
            compact: { label: "Device models" },
          },
        ]}
      />
    </UsageSection>
  );
}
