'use client';

import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import { useAuth } from '../../../../contexts/AuthContext';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface SessionMetrics {
  total: number;
  today: number;
  last7Days: number;
  last30Days: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  incomplete: number;
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

interface PlatformStats {
  totalEnrollments: number;
  totalUsers: number;
  totalTenants: number;
  totalSignedUpTenants: number;
  uniqueDeviceModels: number;
  totalEventsProcessed: number;
  successfulEnrollments: number;
  issuesDetected: number;
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
  platformStats?: PlatformStats;
  windowDays: number;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
}

export function SectionPlatformUsage() {
  const router = useRouter();

  const { getAccessToken } = useAuth();
  const [metrics, setMetrics] = useState<PlatformUsageMetrics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const fetchMetrics = async (showRefreshing = false) => {
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }
      setError(null);

      // Platform-wide metrics - cross-tenant (Global Admin only)
      const response = await authenticatedFetch(api.metrics.globalUsage(), getAccessToken);

      if (!response.ok) {
        throw new Error(`Failed to fetch platform usage metrics: ${response.statusText}`);
      }

      const data = await response.json();
      setMetrics(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error('Session expired:', err.message);
      } else {
        console.error('Error fetching platform usage metrics:', err);
      }
      setError(err instanceof Error ? err.message : 'Failed to fetch platform usage metrics');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  };

  useEffect(() => {
    // Fetch metrics on mount
    // Authorization is handled by admin layout ProtectedRoute
    fetchMetrics();
  }, []);

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
          <p className="mt-4 text-gray-600">Loading platform usage metrics...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full">
          <div className="text-center">
            <svg className="h-12 w-12 text-red-500 mx-auto" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <h2 className="mt-4 text-xl font-semibold text-gray-900">Error Loading Platform Usage Metrics</h2>
            <p className="mt-2 text-gray-600">{error}</p>
            <div className="mt-6 space-y-3">
              <button
                onClick={() => fetchMetrics()}
                className="w-full px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors"
              >
                Retry
              </button>
              <button
                onClick={() => router.push('/dashboard')}
                className="w-full px-4 py-2 bg-gray-200 text-gray-700 rounded-lg hover:bg-gray-300 transition-colors"
              >
                Back to Home
              </button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (!metrics) {
    return null;
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between">
            <div>
              <div>
                <h1 className="text-2xl font-normal text-gray-900">Platform Usage Metrics</h1>
                <p className="text-sm text-gray-600 mt-1">
                  Cross-tenant metrics • Rolling {metrics.windowDays ?? 90}-day window • Computed at {formatTimestamp(metrics.computedAt)} in {metrics.computeDurationMs}ms
                  {metrics.fromCache && (
                    <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                      From Cache
                    </span>
                  )}
                </p>
              </div>
            </div>
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
      </header>
      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">

        {/* Session Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-1">Sessions (Last {metrics.windowDays ?? 90} Days)</h2>
          <p className="text-xs text-gray-500 mb-4">
            Rolling {metrics.windowDays ?? 90}-day window. For all-time totals see Platform Statistics below.
          </p>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1" title={`Sessions started in the last ${metrics.windowDays ?? 90} days. All-time total is under Platform Statistics.`}>Sessions in Window</div>
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

          <div className="grid grid-cols-1 md:grid-cols-5 gap-4 mt-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Succeeded</div>
              <div className="text-3xl font-bold text-green-600">{metrics.sessions.succeeded.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Failed</div>
              <div className="text-3xl font-bold text-red-600">{metrics.sessions.failed.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1" title="Terminal, non-failure: timed out with no completion or failure signal. Excluded from the success rate.">Incomplete</div>
              <div className="text-3xl font-bold text-slate-500">{metrics.sessions.incomplete.toLocaleString()}</div>
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

        {/* Tenant & User Statistics */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mb-6">
          {/* Tenants */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Tenants</h2>
            <div className="space-y-4">
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Total Tenants</div>
                <div className="text-3xl font-bold text-gray-900">{metrics.tenants.total.toLocaleString()}</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Active (Last 7 Days)</div>
                <div className="text-3xl font-bold text-blue-600">{metrics.tenants.active7Days.toLocaleString()}</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Active (Last 30 Days)</div>
                <div className="text-3xl font-bold text-indigo-600">{metrics.tenants.active30Days.toLocaleString()}</div>
              </div>
            </div>
          </div>

          {/* Users */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Users</h2>
            {metrics.users.total > 0 || metrics.users.dailyLogins > 0 || metrics.users.active7Days > 0 || metrics.users.active30Days > 0 ? (
              <div className="space-y-4">
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
                {metrics.users.note && (
                  <p className="text-xs text-gray-500">{metrics.users.note}</p>
                )}
              </div>
            ) : (
              <div className="bg-white rounded-lg shadow p-6">
                <div className="space-y-4 mb-4 opacity-40">
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-gray-500">Unique Users (90d)</span>
                    <span className="text-xl font-bold text-gray-400">--</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-gray-500">Daily Logins</span>
                    <span className="text-xl font-bold text-gray-400">--</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-gray-500">Active (7 Days)</span>
                    <span className="text-xl font-bold text-gray-400">--</span>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-sm text-gray-500">Active (30 Days)</span>
                    <span className="text-xl font-bold text-gray-400">--</span>
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

        {/* Platform Statistics (Cumulative Since Release) */}
        {metrics.platformStats && (
          <div className="mb-6">
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Platform Statistics (Since Release)</h2>
            {/* Tenants & Users */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Signed Up Tenants</div>
                <div className="text-3xl font-bold text-cyan-600">{(metrics.platformStats.totalSignedUpTenants ?? 0).toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">all registered tenants</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Active Tenants</div>
                <div className="text-3xl font-bold text-teal-600">{metrics.platformStats.totalTenants.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">with at least one session</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Users Seen</div>
                <div className="text-3xl font-bold text-indigo-600">{metrics.platformStats.totalUsers.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">cumulative unique users</div>
              </div>
            </div>
            {/* Enrollments */}
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1" title="One enrollment = one session. This is the all-time cumulative count, so it is higher than the windowed Sessions figure above.">Total Enrollments</div>
                <div className="text-3xl font-bold text-blue-600">{metrics.platformStats.totalEnrollments.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">all sessions ever (cumulative)</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Successful Enrollments</div>
                <div className="text-3xl font-bold text-green-600">{metrics.platformStats.successfulEnrollments.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">cumulative, all-time</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Issues Detected</div>
                <div className="text-3xl font-bold text-red-600">{metrics.platformStats.issuesDetected.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">by analyze rules</div>
              </div>
            </div>
            {/* Events & Models */}
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Total Events Processed</div>
                <div className="text-3xl font-bold text-purple-600">{metrics.platformStats.totalEventsProcessed.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">all ingested events</div>
              </div>
              <div className="bg-white rounded-lg shadow p-6">
                <div className="text-sm text-gray-500 mb-1">Unique Device Models</div>
                <div className="text-3xl font-bold text-amber-600">{metrics.platformStats.uniqueDeviceModels.toLocaleString()}</div>
                <div className="text-xs text-gray-400 mt-1">distinct manufacturer + model combinations</div>
              </div>
            </div>
          </div>
        )}

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
  );
}
