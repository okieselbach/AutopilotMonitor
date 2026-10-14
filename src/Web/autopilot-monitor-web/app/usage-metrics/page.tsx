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
import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type { PlatformUsageMetrics } from "@/lib/generated/wire-types.generated";
import {
  AppScriptsBlock,
  DeploymentTypeBreakdown,
  HardwareBlock,
  PerformanceBlock,
  SessionOutcomeCards,
  SessionRecencyCards,
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
          <div aria-busy="true" aria-label="Loading" className="grid grid-cols-1 gap-4 md:grid-cols-4">
            {Array.from({ length: 8 }).map((_, i) => (
              <CardSkeleton key={i} />
            ))}
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

        {/* Session Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-1">Sessions</h2>
          <p className="text-sm text-gray-500 mb-4">
            All counts cover the last {metrics.windowDays ?? 90} days &mdash; older sessions age out of this window and your plan&apos;s data retention. Only Total Enrollments is cumulative since signup.
          </p>
          <div className={`grid grid-cols-1 gap-4 ${metrics.sessions.totalAllTime != null ? 'md:grid-cols-5' : 'md:grid-cols-4'}`}>
            {metrics.sessions.totalAllTime != null && (
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1" title="Cumulative count of all enrollment sessions since this tenant signed up — unaffected by the metrics window or data retention.">Total Enrollments</div>
                <div className="text-3xl font-bold text-gray-900">{metrics.sessions.totalAllTime.toLocaleString()}</div>
                <p className="text-xs text-gray-400 mt-1">since signup</p>
              </div>
            )}
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1" title={`Sessions started within the ${metrics.windowDays ?? 90}-day metrics window — not a cumulative count since signup.`}>Total Sessions</div>
              <div className="text-3xl font-bold text-gray-900">{metrics.sessions.total.toLocaleString()}</div>
              <p className="text-xs text-gray-400 mt-1">last {metrics.windowDays ?? 90} days</p>
            </div>
            <SessionRecencyCards sessions={metrics.sessions} />
          </div>

          <SessionOutcomeCards sessions={metrics.sessions} />
        </div>

        <DeploymentTypeBreakdown deploymentTypes={metrics.deploymentTypes} />

        {metrics.appScripts && <AppScriptsBlock appScripts={metrics.appScripts} />}

        {/* User Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Users</h2>
          {metrics.users.total > 0 || metrics.users.dailyLogins > 0 || metrics.users.active7Days > 0 || metrics.users.active30Days > 0 ? (
            <>
              <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Unique Users (90d)</div>
                  <div className="text-3xl font-bold text-gray-900">{metrics.users.total.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Daily Logins</div>
                  <div className="text-3xl font-bold text-blue-600">{metrics.users.dailyLogins.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Active (Last 7 Days)</div>
                  <div className="text-3xl font-bold text-indigo-600">{metrics.users.active7Days.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Active (Last 30 Days)</div>
                  <div className="text-3xl font-bold text-purple-600">{metrics.users.active30Days.toLocaleString()}</div>
                </div>
              </div>
              {metrics.users.note && (
                <p className="mt-3 text-xs text-gray-500">{metrics.users.note}</p>
              )}
            </>
          ) : (
            <div className="bg-white rounded-lg shadow p-6">
              <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-4 opacity-40">
                <div>
                  <div className="text-sm text-gray-500 mb-1">Unique Users (90d)</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Daily Logins</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Active (7 Days)</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Active (30 Days)</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
              </div>
              <div className="flex items-center space-x-3 pt-3 border-t border-gray-100">
                <svg className="h-5 w-5 text-amber-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <p className="text-sm text-gray-600">{metrics.users.note || 'User metrics will be available when Entra ID authentication tracking is enabled.'}</p>
              </div>
            </div>
          )}
        </div>

        <PerformanceBlock performance={metrics.performance} />

        <HardwareBlock hardware={metrics.hardware} />
        </>
        )}
      </div>
    </div>
  </ProtectedRoute>
  );
}
