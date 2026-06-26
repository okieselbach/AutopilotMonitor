'use client';

import { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useGlobalAdminScope } from "@/hooks";
import { GlobalAdminBanner } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";

interface SessionMetrics {
  total: number;
  today: number;
  last7Days: number;
  last30Days: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  successRate: number;
}

interface TenantMetrics {
  total: number;
  active7Days: number;
  active30Days: number;
}

interface UserMetrics {
  total: number;
  dailyLogins: number;
  active7Days: number;
  active30Days: number;
  note: string;
}

interface PerformanceMetrics {
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  p95DurationMinutes: number;
  p99DurationMinutes: number;
}

interface HardwareCount {
  name: string;
  count: number;
  percentage: number;
}

interface HardwareMetrics {
  topManufacturers: HardwareCount[];
  topModels: HardwareCount[];
}

interface DeploymentTypeMetrics {
  userDriven: number;
  whiteGlove: number;
  userDrivenPercentage: number;
  whiteGlovePercentage: number;
}

interface AppScriptMetrics {
  avgAppsPerSession: number;
  totalUniqueApps: number;
  avgPlatformScriptsPerSession: number;
  avgRemediationScriptsPerSession: number;
  totalPlatformScripts: number;
  totalRemediationScripts: number;
}

interface PlatformUsageMetrics {
  sessions: SessionMetrics;
  tenants: TenantMetrics;
  users: UserMetrics;
  performance: PerformanceMetrics;
  hardware: HardwareMetrics;
  deploymentTypes: DeploymentTypeMetrics;
  appScripts?: AppScriptMetrics;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
}

export default function UsageMetricsPage() {
  const router = useRouter();

  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const [metrics, setMetrics] = useState<PlatformUsageMetrics | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  // Global admin tenant scope (tenant list, selector state, override/effective tenant)
  const scope = useGlobalAdminScope();
  const { isGlobalOverride, effectiveTenantId, selectedTenantId, tenants } = scope;

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!effectiveTenantId) return;
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }

      // Global admin viewing another tenant → use global endpoint
      const url = isGlobalOverride
        ? api.metrics.globalUsage(effectiveTenantId)
        : api.metrics.usage(effectiveTenantId);

      const response = await authenticatedFetch(url, getAccessToken);

      if (!response.ok) {
        addNotification('error', 'Backend Error', `Failed to load usage metrics: ${response.statusText}`, 'usage-metrics-fetch-error');
        return;
      }

      const data = await response.json();
      setMetrics(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Error fetching usage metrics:', err);
        addNotification('error', 'Backend Not Reachable', 'Unable to load usage metrics. Please check your connection.', 'usage-metrics-fetch-error');
      }
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [effectiveTenantId, isGlobalOverride, getAccessToken, addNotification]);

  useEffect(() => {
    if (!effectiveTenantId) return;
    fetchMetrics();
  }, [effectiveTenantId]);

  const selectedTenantName = tenants.find(t => t.tenantId === selectedTenantId)?.domainName;

  const formatDuration = (minutes: number) => {
    if (minutes < 60) {
      return `${minutes.toFixed(1)}m`;
    }
    const hours = Math.floor(minutes / 60);
    const mins = Math.round(minutes % 60);
    return `${hours}h ${mins}m`;
  };

  const formatTimestamp = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading usage metrics...</p>
        </div>
      </div>
    );
  }

  if (!metrics) {
    return null;
  }

  return (
<ProtectedRoute>
    <div className="min-h-screen bg-gray-50">
      <GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} />
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between">
            <div>
              <div>
                <h1 className="text-2xl font-normal text-gray-900">Usage Metrics</h1>
                <p className="text-sm text-gray-600 mt-1">
                  {isGlobalOverride && selectedTenantName
                    ? `Tenant: ${selectedTenantName} · `
                    : ''}
                  Computed at {formatTimestamp(metrics.computedAt)} in {metrics.computeDurationMs}ms
                  {metrics.fromCache && (
                    <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                      From Cache
                    </span>
                  )}
                </p>
              </div>
            </div>
            <div className="flex items-center gap-3">
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
            </div>
          </div>
        </div>
      </header>
      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">

        {/* Session Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Sessions</h2>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Total Sessions</div>
              <div className="text-3xl font-bold text-gray-900">{metrics.sessions.total.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Today</div>
              <div className="text-3xl font-bold text-blue-600">{metrics.sessions.today.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Last 7 Days</div>
              <div className="text-3xl font-bold text-indigo-600">{metrics.sessions.last7Days.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Last 30 Days</div>
              <div className="text-3xl font-bold text-purple-600">{metrics.sessions.last30Days.toLocaleString()}</div>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mt-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Succeeded</div>
              <div className="text-3xl font-bold text-green-600">{metrics.sessions.succeeded.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Failed</div>
              <div className="text-3xl font-bold text-red-600">{metrics.sessions.failed.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">In Progress</div>
              <div className="text-3xl font-bold text-yellow-600">{metrics.sessions.inProgress.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Success Rate</div>
              <div className="text-3xl font-bold text-gray-900">{metrics.sessions.successRate}%</div>
            </div>
          </div>
        </div>

        {/* Deployment Types */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Deployment Types</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="flex items-center justify-between mb-3">
                <div>
                  <div className="text-sm text-gray-500 mb-1">User Driven</div>
                  <div className="text-3xl font-bold text-blue-600">{metrics.deploymentTypes.userDriven.toLocaleString()}</div>
                </div>
                <div className="text-right">
                  <span className="text-2xl font-semibold text-blue-500">{metrics.deploymentTypes.userDrivenPercentage}%</span>
                </div>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2.5">
                <div
                  className="bg-blue-600 h-2.5 rounded-full transition-all duration-300"
                  style={{ width: `${metrics.deploymentTypes.userDrivenPercentage}%` }}
                ></div>
              </div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="flex items-center justify-between mb-3">
                <div>
                  <div className="text-sm text-gray-500 mb-1">Pre-Provisioned</div>
                  <div className="text-3xl font-bold text-purple-600">{metrics.deploymentTypes.whiteGlove.toLocaleString()}</div>
                </div>
                <div className="text-right">
                  <span className="text-2xl font-semibold text-purple-500">{metrics.deploymentTypes.whiteGlovePercentage}%</span>
                </div>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2.5">
                <div
                  className="bg-purple-600 h-2.5 rounded-full transition-all duration-300"
                  style={{ width: `${metrics.deploymentTypes.whiteGlovePercentage}%` }}
                ></div>
              </div>
            </div>
          </div>
        </div>

        {/* Apps & Scripts */}
        {metrics.appScripts && (
          <div className="mb-6">
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Apps & Scripts</h2>
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Avg Apps / Session</div>
                <div className="text-3xl font-bold text-blue-600">{metrics.appScripts.avgAppsPerSession}</div>
                <p className="text-xs text-gray-400 mt-1">{metrics.appScripts.totalUniqueApps.toLocaleString()} unique apps total</p>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Avg Platform Scripts / Session</div>
                <div className="text-3xl font-bold text-teal-600">{metrics.appScripts.avgPlatformScriptsPerSession}</div>
                <p className="text-xs text-gray-400 mt-1">{metrics.appScripts.totalPlatformScripts.toLocaleString()} executions total</p>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Avg Remediation Scripts / Session</div>
                <div className="text-3xl font-bold text-amber-600">{metrics.appScripts.avgRemediationScriptsPerSession}</div>
                <p className="text-xs text-gray-400 mt-1">{metrics.appScripts.totalRemediationScripts.toLocaleString()} executions total</p>
              </div>
            </div>
          </div>
        )}

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

        {/* Performance Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Performance</h2>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Average Duration</div>
              <div className="text-3xl font-bold text-gray-900">{formatDuration(metrics.performance.avgDurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Median Duration</div>
              <div className="text-3xl font-bold text-blue-600">{formatDuration(metrics.performance.medianDurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">P95 Duration</div>
              <div className="text-3xl font-bold text-orange-600">{formatDuration(metrics.performance.p95DurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">P99 Duration</div>
              <div className="text-3xl font-bold text-red-600">{formatDuration(metrics.performance.p99DurationMinutes)}</div>
            </div>
          </div>
        </div>

        {/* Hardware Statistics */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          {/* Top Manufacturers */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Top Manufacturers</h2>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="divide-y divide-gray-200">
                {metrics.hardware.topManufacturers.length > 0 ? (
                  metrics.hardware.topManufacturers.map((item, index) => (
                    <div key={index} className="p-4 hover:bg-gray-50 transition-colors">
                      <div className="flex items-center justify-between mb-2">
                        <span className="font-medium text-gray-900">{item.name || 'Unknown'}</span>
                        <div className="text-right">
                          <span className="text-sm font-semibold text-gray-900">{item.count.toLocaleString()}</span>
                          <span className="text-xs text-gray-500 ml-2">({item.percentage}%)</span>
                        </div>
                      </div>
                      <div className="w-full bg-gray-200 rounded-full h-2">
                        <div
                          className="bg-blue-600 h-2 rounded-full transition-all duration-300"
                          style={{ width: `${item.percentage}%` }}
                        ></div>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="p-8 text-center text-gray-500">No data available</div>
                )}
              </div>
            </div>
          </div>

          {/* Top Models */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Top Models</h2>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="divide-y divide-gray-200">
                {metrics.hardware.topModels.length > 0 ? (
                  metrics.hardware.topModels.map((item, index) => (
                    <div key={index} className="p-4 hover:bg-gray-50 transition-colors">
                      <div className="flex items-center justify-between mb-2">
                        <span className="font-medium text-gray-900">{item.name || 'Unknown'}</span>
                        <div className="text-right">
                          <span className="text-sm font-semibold text-gray-900">{item.count.toLocaleString()}</span>
                          <span className="text-xs text-gray-500 ml-2">({item.percentage}%)</span>
                        </div>
                      </div>
                      <div className="w-full bg-gray-200 rounded-full h-2">
                        <div
                          className="bg-indigo-600 h-2 rounded-full transition-all duration-300"
                          style={{ width: `${item.percentage}%` }}
                        ></div>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="p-8 text-center text-gray-500">No data available</div>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </ProtectedRoute>
  );
}
