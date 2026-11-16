'use client';

import { Suspense, useEffect, useState, useCallback, useRef } from 'react';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from "@/lib/api";
import { useGlobalAdminScope } from "@/hooks";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { CardSkeleton } from "@/components/skeletons/PageSkeleton";
import { CompactStatPanelSkeleton, StatRowSwitch } from "@/components/CompactStatPanel";
import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type { PlatformUsageMetrics } from "@/lib/generated/wire-types.generated";
import {
  AppScriptsBlock,
  DeploymentTypeBreakdown,
  HardwareBlock,
  PerformanceBlock,
  SessionOutcomeRow,
  UsageSection,
  UsageStatRow,
  UsersBlock,
  sessionRecencyStats,
  type UsageStat,
} from "@/components/UsageMetricsBlocks";
import { fetchJson } from "@/lib/apiClient";
import { useWindowDays } from "@/hooks/useWindowDays";
import { notifyApiError } from "@/contexts/NotificationContext";










/** The backend's default window for these metrics; a URL `?days=` replaces it (no selector). */
const USAGE_DEFAULT_WINDOW_DAYS = 90;

export default function UsageMetricsPage() {
  // useSearchParams (the ?days= window) requires a Suspense boundary under the static export.
  return (
    <Suspense fallback={<div className="min-h-screen bg-gray-50" />}>
      <UsageMetricsContent />
    </Suspense>
  );
}

function UsageMetricsContent() {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
    const [metrics, setMetrics] = useState<PlatformUsageMetrics | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  // Global admin tenant scope (tenant list, selector state, override/effective tenant)
  const scope = useGlobalAdminScope();
  const { isGlobalOverride, effectiveTenantId, selectedTenantId, tenants } = scope;
  const { days, ready: windowReady } = useWindowDays({ defaultDays: USAGE_DEFAULT_WINDOW_DAYS, tenantId: effectiveTenantId });

  // Latest-wins guard: a tenant switch starts a new fetch while an older one may still be in
  // flight; only the most recently started request may write state (raw authenticatedFetch has
  // no such guard, unlike useAuthenticatedFetch).
  const fetchSeqRef = useRef(0);

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!effectiveTenantId || !windowReady) return;
    const seq = ++fetchSeqRef.current;
    const isCurrent = () => fetchSeqRef.current === seq;
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }

      // Global admin viewing another tenant → use global endpoint
      const url = isGlobalOverride
        ? api.metrics.globalUsage(effectiveTenantId, days)
        : api.metrics.usage(effectiveTenantId, days);

      const data = await fetchJson<PlatformUsageMetrics>(url, getAccessToken);
      if (!isCurrent()) return;
      setMetrics(data);
    } catch (err) {
      if (!isCurrent()) return;
      console.error('Error fetching usage metrics:', err);
      notifyApiError(addNotification, 'Backend Error', err, 'usage-metrics-fetch-error', 'Unable to load usage metrics.');
    } finally {
      // A superseded request must not clear the newer request's loading/refreshing state.
      if (isCurrent()) {
        setLoading(false);
        setRefreshing(false);
      }
    }
  }, [effectiveTenantId, isGlobalOverride, windowReady, days, getAccessToken, addNotification]);

  useEffect(() => {
    if (!effectiveTenantId) return;
    const run = async () => {
      await fetchMetrics();
    };
    void run();
  }, [effectiveTenantId, fetchMetrics]);

  const selectedTenantName = tenants.find(t => t.tenantId === selectedTenantId)?.domainName;
  const windowDays = metrics?.windowDays ?? USAGE_DEFAULT_WINDOW_DAYS;

  const formatTimestamp = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  return (
<ProtectedRoute>
    <div className="min-h-screen bg-gray-50">
      <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex flex-wrap items-center justify-between gap-y-3">
            <div>
              <div>
                <h1 className="text-2xl font-normal text-gray-900">Usage Metrics</h1>
                <p className="text-sm text-gray-600 mt-1">
                  {isGlobalOverride && selectedTenantName
                    ? `Tenant: ${selectedTenantName} · `
                    : ''}
                  {metrics ? (
                    <>
                      Computed at {formatTimestamp(metrics.computedAt)} in {metrics.computeDurationMs}ms
                      {metrics.fromCache && (
                        <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                          From Cache
                        </span>
                      )}
                    </>
                  ) : '…'}
                </p>
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
              <TenantScopeSelector scope={scope} />
              <button
                onClick={() => fetchMetrics(true)}
                disabled={refreshing}
                className="px-4 py-2 bg-white border border-gray-200 text-gray-700 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                <svg className={`h-5 w-5 ${refreshing ? 'animate-spin' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
                <span>{refreshing ? 'Refreshing...' : 'Refresh'}</span>
              </button>
              <DocsLink path={DOCS_PATHS.usageMetrics} label="Docs" />
            </div>
          </div>
        </div>
      </header>
      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {/* Skeleton also covers a tenant switch (loading with stale metrics) so
            the previous tenant's numbers never linger under the new scope. */}
        {loading ? (
          <div aria-busy="true" aria-label="Loading">
            <StatRowSwitch
              wide={
                <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
                  {Array.from({ length: 8 }).map((_, i) => (
                    <CardSkeleton key={i} />
                  ))}
                </div>
              }
              compact={
                <div className="space-y-4">
                  <CompactStatPanelSkeleton count={5} />
                  <CompactStatPanelSkeleton count={5} />
                </div>
              }
            />
          </div>
        ) : !metrics ? (
          <div className="bg-white rounded-lg shadow p-12 text-center">
            <p className="text-gray-500">Unable to load usage metrics.</p>
            <button
              onClick={() => fetchMetrics()}
              className="mt-4 px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
            >
              Retry
            </button>
          </div>
        ) : (
        <>

        <UsageSection
          title="Sessions"
          description={
            <>
              All counts cover the last {windowDays} days &mdash; older sessions age out of this window and your plan&apos;s data retention. Only Total Enrollments is cumulative since signup.
            </>
          }
        >
          <UsageStatRow
            stats={[
              ...(metrics.sessions.totalAllTime != null
                ? [
                    {
                      label: "Total Enrollments",
                      title: "Cumulative count of all enrollment sessions since this tenant signed up — unaffected by the metrics window or data retention.",
                      value: metrics.sessions.totalAllTime.toLocaleString(),
                      valueClass: "text-gray-900",
                      note: "since signup",
                      compact: { label: "Enrollments", note: "since signup" },
                    } satisfies UsageStat,
                  ]
                : []),
              {
                label: "Total Sessions",
                title: `Sessions started within the ${windowDays}-day metrics window — not a cumulative count since signup.`,
                value: metrics.sessions.total.toLocaleString(),
                valueClass: "text-gray-900",
                note: `last ${windowDays} days`,
                compact: { label: "Sessions", note: `last ${windowDays} d` },
              },
              ...sessionRecencyStats(metrics.sessions),
            ]}
          />
          <SessionOutcomeRow sessions={metrics.sessions} />
        </UsageSection>

        <DeploymentTypeBreakdown deploymentTypes={metrics.deploymentTypes} />

        {metrics.appScripts && <AppScriptsBlock appScripts={metrics.appScripts} />}

        <UsersBlock users={metrics.users} />

        <PerformanceBlock performance={metrics.performance} />

        <HardwareBlock hardware={metrics.hardware} />
        </>
        )}
      </div>
    </div>
  </ProtectedRoute>
  );
}
