"use client";

import { useAuth } from '@/contexts/AuthContext';
import { useNotifications } from '@/contexts/NotificationContext';
import { useSignalR } from '@/contexts/SignalRContext';
import { useTenant } from '@/contexts/TenantContext';
import { useState, useEffect, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { api } from '@/lib/api';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface HealthCheck {
  name: string;
  description: string;
  status: string;
  message: string;
  details?: Record<string, any>;
}

interface HealthCheckResult {
  service: string;
  timestamp: string;
  overallStatus: string;
  checks: HealthCheck[];
  version?: string;
  commitHash?: string;
  buildUtc?: string;
}

export default function HealthCheckPage() {
  const { user, getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { connectionState, isConnected, joinedGroups, joinGroup } = useSignalR();
  const { tenantId } = useTenant();
  const [healthResult, setHealthResult] = useState<HealthCheckResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [hasRun, setHasRun] = useState(false);
  const [groupJoinTest, setGroupJoinTest] = useState<'idle' | 'testing' | 'success' | 'failed'>('idle');
  // MCP-server check is fetched on its own track: the MCP Container App scales to zero and a
  // probe may need to wake it (multi-second cold start), so we never block the rest of the
  // dashboard on it. The card renders a "checking" state immediately and updates in place.
  const [mcpCheck, setMcpCheck] = useState<HealthCheck | null>(null);
  const [mcpLoading, setMcpLoading] = useState(false);

  const performHealthCheck = useCallback(async () => {
    setLoading(true);
    try {
      const response = await authenticatedFetch(api.health.detailed(), getAccessToken);

      if (!response.ok) {
        if (response.status === 403) {
          addNotification('error', 'Access Denied', 'You do not have permission to access health checks', 'health-check-forbidden');
        } else {
          addNotification('error', 'Health Check Failed', `Status: ${response.status}`, 'health-check-failed');
        }
        return;
      }

      const data = await response.json();
      setHealthResult(data);
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Health check error:', error);
        addNotification('error', 'Health Check Error', error instanceof Error ? error.message : 'Unknown error', 'health-check-error');
      }
    } finally {
      setLoading(false);
      setHasRun(true);
    }
  }, [getAccessToken, addNotification]);

  // Separate, non-blocking probe for the MCP server. Runs concurrently with — and
  // independently of — performHealthCheck so a cold-starting MCP container never holds
  // up the other cards. Resolves to healthy / warning / unhealthy; the card shows a
  // "checking" state until then.
  const performMcpCheck = useCallback(async () => {
    setMcpLoading(true);
    try {
      const response = await authenticatedFetch(api.health.mcp(), getAccessToken);
      if (!response.ok) {
        setMcpCheck({
          name: 'MCP Server',
          description: 'AI query interface availability',
          status: response.status === 403 ? 'unknown' : 'unhealthy',
          message: response.status === 403
            ? 'Not permitted to view the MCP server status'
            : `MCP status check failed (HTTP ${response.status})`,
        });
        return;
      }
      const data = await response.json();
      if (data?.check) setMcpCheck(data.check as HealthCheck);
    } catch (error) {
      // Network/token errors: surface on the card itself, never as a blocking page error.
      setMcpCheck({
        name: 'MCP Server',
        description: 'AI query interface availability',
        status: 'warning',
        message: error instanceof Error ? error.message : 'MCP status check could not complete',
      });
    } finally {
      setMcpLoading(false);
    }
  }, [getAccessToken]);

  // Auto-run health checks on page load. The two probes are fired independently so the
  // fast backend checks render immediately while the MCP probe (possible cold start) catches up.
  useEffect(() => {
    if (user && !hasRun) {
      performHealthCheck();
      performMcpCheck();
    }
  }, [user, hasRun, performHealthCheck, performMcpCheck]);

  const testGroupJoin = useCallback(async () => {
    if (!tenantId || !isConnected) return;
    setGroupJoinTest('testing');
    try {
      await joinGroup(`tenant-${tenantId}`);
      // joinGroup updates joinedGroups state on success/failure
      // Small delay to let state propagate
      setTimeout(() => setGroupJoinTest('success'), 300);
    } catch {
      setGroupJoinTest('failed');
    }
  }, [tenantId, isConnected, joinGroup]);

  const getConnectionStateLabel = (state: signalR.HubConnectionState) => {
    switch (state) {
      case signalR.HubConnectionState.Connected: return 'Connected';
      case signalR.HubConnectionState.Connecting: return 'Connecting';
      case signalR.HubConnectionState.Reconnecting: return 'Reconnecting';
      case signalR.HubConnectionState.Disconnecting: return 'Disconnecting';
      case signalR.HubConnectionState.Disconnected: return 'Disconnected';
      default: return 'Unknown';
    }
  };

  const getConnectionStatus = (): 'healthy' | 'unhealthy' | 'warning' => {
    if (connectionState === signalR.HubConnectionState.Connected) return 'healthy';
    if (connectionState === signalR.HubConnectionState.Reconnecting || connectionState === signalR.HubConnectionState.Connecting) return 'warning';
    return 'unhealthy';
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'healthy': return { bg: 'bg-green-50 dark:bg-green-900/20', border: 'border-green-200 dark:border-green-800', text: 'text-green-700 dark:text-green-400', accent: 'border-green-500', badge: 'bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-400' };
      case 'unhealthy': return { bg: 'bg-red-50 dark:bg-red-900/20', border: 'border-red-200 dark:border-red-800', text: 'text-red-700 dark:text-red-400', accent: 'border-red-500', badge: 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-400' };
      case 'warning': return { bg: 'bg-yellow-50 dark:bg-yellow-900/20', border: 'border-yellow-200 dark:border-yellow-800', text: 'text-yellow-700 dark:text-yellow-400', accent: 'border-yellow-500', badge: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-400' };
      case 'checking': return { bg: 'bg-blue-50 dark:bg-blue-900/20', border: 'border-blue-200 dark:border-blue-800', text: 'text-blue-700 dark:text-blue-400', accent: 'border-blue-500', badge: 'bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-400' };
      default: return { bg: 'bg-gray-50 dark:bg-gray-800', border: 'border-gray-200 dark:border-gray-700', text: 'text-gray-700 dark:text-gray-300', accent: 'border-gray-500', badge: 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300' };
    }
  };

  const connStatus = getConnectionStatus();
  // The MCP card only contributes to the banner/counts once it has resolved to a rated
  // status. While it is still probing (null/loading) or not viewable (unknown), it stays
  // neutral so a slow cold start never flips the overall banner to "warning".
  const mcpRated = mcpCheck && mcpCheck.status !== 'unknown' ? mcpCheck.status : null;
  const totalChecks = healthResult ? healthResult.checks.length + 1 + (mcpRated ? 1 : 0) : 0;
  const healthyChecks = healthResult
    ? healthResult.checks.filter(c => c.status === 'healthy').length
      + (connStatus === 'healthy' ? 1 : 0)
      + (mcpRated === 'healthy' ? 1 : 0)
    : 0;
  const combinedOverallStatus = healthResult
    ? (connStatus === 'unhealthy' || healthResult.overallStatus === 'unhealthy' || mcpRated === 'unhealthy')
      ? 'unhealthy'
      : (connStatus === 'warning' || healthResult.overallStatus === 'warning' || mcpRated === 'warning')
        ? 'warning'
        : 'healthy'
    : null;
  const overallColors = combinedOverallStatus ? getStatusColor(combinedOverallStatus) : null;

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      {/* Header */}
      <header className="bg-white shadow dark:bg-gray-800 dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8 flex items-center justify-between">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">System Health</h1>
          <button
            onClick={() => { performHealthCheck(); performMcpCheck(); }}
            disabled={loading}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium flex items-center gap-2"
          >
            {loading ? (
              <>
                <svg className="animate-spin h-4 w-4 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                </svg>
                Checking...
              </>
            ) : (
              <>
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
                Re-check
              </>
            )}
          </button>
        </div>
      </header>

      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {/* Overall Status Bar */}
        {healthResult && overallColors && (
          <div className={`rounded-lg border mb-6 p-4 flex items-center justify-between ${overallColors.bg} ${overallColors.border}`}>
            <div className="flex items-center space-x-3">
              <div className={`w-3 h-3 rounded-full ${combinedOverallStatus === 'healthy' ? 'bg-green-500' : combinedOverallStatus === 'unhealthy' ? 'bg-red-500' : 'bg-yellow-500'}`}></div>
              <span className={`text-sm font-medium ${overallColors.text}`}>
                {combinedOverallStatus === 'healthy' ? 'All systems operational' : combinedOverallStatus === 'unhealthy' ? 'System issues detected' : 'Warnings detected'}
              </span>
              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${overallColors.badge}`}>
                {healthyChecks}/{totalChecks} healthy
              </span>
            </div>
            <span className="text-xs text-gray-500 dark:text-gray-400">
              Last checked: {new Date(healthResult.timestamp).toLocaleString()}
            </span>
          </div>
        )}

        {/* Loading State */}
        {loading && !healthResult && (
          <div className="p-8 flex flex-col items-center justify-center">
            <svg className="animate-spin h-8 w-8 text-blue-500 mb-3" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
            </svg>
            <p className="text-sm text-gray-500 dark:text-gray-400">Running health checks...</p>
          </div>
        )}

        {/* Backend Build (Global Admin only) */}
        {user?.isGlobalAdmin && healthResult?.version && (
          <div className="mb-6">
            <h2 className="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-3">Backend Build</h2>
            <div className="bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 border-indigo-500">
              <div className="p-4 grid grid-cols-1 sm:grid-cols-3 gap-4 text-sm">
                <div>
                  <dt className="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase">Version</dt>
                  <dd className="mt-1 font-mono text-gray-900 dark:text-gray-100">{healthResult.version}</dd>
                </div>
                <div>
                  <dt className="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase">Commit</dt>
                  <dd className="mt-1 font-mono text-gray-900 dark:text-gray-100">
                    {healthResult.commitHash ? (
                      <a
                        href={`https://github.com/okieselbach/Autopilot-Monitor/commit/${healthResult.commitHash}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-indigo-600 dark:text-indigo-400 hover:underline"
                      >
                        {healthResult.commitHash}
                      </a>
                    ) : (
                      <span className="text-gray-400">unknown</span>
                    )}
                  </dd>
                </div>
                <div>
                  <dt className="text-xs font-semibold text-gray-500 dark:text-gray-400 uppercase">Built</dt>
                  <dd className="mt-1 text-gray-900 dark:text-gray-100">
                    {healthResult.buildUtc ? new Date(healthResult.buildUtc).toLocaleString() : '—'}
                  </dd>
                </div>
              </div>
            </div>
          </div>
        )}

        {/* Real-Time Connection Status */}
        <div className="mb-6">
          <h2 className="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-3">Real-Time Connection</h2>
          {(() => {
            const tenantGroup = `tenant-${tenantId}`;
            const hasTenantGroup = joinedGroups.includes(tenantGroup);
            const colors = getStatusColor(connStatus);
            return (
              <div className={`bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 ${colors.accent}`}>
                <div className="p-6">
                  <div className="flex items-start justify-between mb-3">
                    <div className="flex items-center space-x-3">
                      <div className={`w-8 h-8 rounded-full ${colors.bg} flex items-center justify-center`}>
                        {connStatus === 'healthy' ? (
                          <svg className="w-5 h-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                          </svg>
                        ) : connStatus === 'unhealthy' ? (
                          <svg className="w-5 h-5 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        ) : (
                          <svg className="w-5 h-5 text-yellow-600 animate-spin" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                          </svg>
                        )}
                      </div>
                      <div>
                        <h3 className="text-lg font-semibold text-gray-900 dark:text-white">Live Updates</h3>
                        <p className="text-xs text-gray-500 dark:text-gray-400">Real-time event hub</p>
                      </div>
                    </div>
                    <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.badge}`}>
                      {getConnectionStateLabel(connectionState)}
                    </span>
                  </div>
                  <p className={`text-sm ${colors.text}`}>
                    {connStatus === 'healthy'
                      ? 'Connected to real-time event hub'
                      : connStatus === 'warning'
                      ? 'Attempting to establish connection...'
                      : 'Not connected — real-time updates unavailable'}
                  </p>
                  <div className="mt-4 bg-gray-50 dark:bg-gray-700/50 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
                    <h4 className="text-xs font-semibold text-gray-500 uppercase mb-2">Details</h4>
                    <dl className="space-y-1">
                      <div className="flex justify-between text-xs">
                        <dt className="font-medium text-gray-600 dark:text-gray-400">State</dt>
                        <dd className="text-gray-900 dark:text-gray-100 font-mono">{getConnectionStateLabel(connectionState)}</dd>
                      </div>
                      <div className="flex justify-between text-xs">
                        <dt className="font-medium text-gray-600 dark:text-gray-400">Tenant Group</dt>
                        <dd className={`font-mono ${hasTenantGroup ? 'text-green-700 dark:text-green-400' : 'text-gray-400'}`}>
                          {hasTenantGroup ? 'Joined' : 'Not joined'}
                        </dd>
                      </div>
                    </dl>
                    {isConnected && !hasTenantGroup && (
                      <button
                        onClick={testGroupJoin}
                        disabled={groupJoinTest === 'testing'}
                        className="mt-3 px-3 py-1.5 bg-blue-600 text-white rounded text-xs font-medium hover:bg-blue-700 disabled:opacity-50 transition-colors"
                      >
                        {groupJoinTest === 'testing' ? 'Testing...' : groupJoinTest === 'failed' ? 'Retry Join Test' : 'Test Group Join'}
                      </button>
                    )}
                    {groupJoinTest === 'failed' && (
                      <p className="mt-2 text-xs text-red-600">
                        Group join failed — check your connection and try again
                      </p>
                    )}
                  </div>
                </div>
              </div>
            );
          })()}
        </div>

        {/* Individual Check Cards */}
        {healthResult && (
          <div>
          <h2 className="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-3">Backend Services</h2>
          <div className="grid gap-5 sm:grid-cols-2">
            {healthResult.checks.map((check, index) => {
              const colors = getStatusColor(check.status);
              return (
                <div key={index} className={`bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 ${colors.accent}`}>
                  <div className="p-6">
                    <div className="flex items-start justify-between mb-3">
                      <div className="flex items-center space-x-3">
                        <div className={`w-8 h-8 rounded-full ${colors.bg} flex items-center justify-center`}>
                          {check.status === 'healthy' ? (
                            <svg className="w-5 h-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                            </svg>
                          ) : check.status === 'unhealthy' ? (
                            <svg className="w-5 h-5 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                            </svg>
                          ) : (
                            <svg className="w-5 h-5 text-yellow-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                          )}
                        </div>
                        <div>
                          <h3 className="text-lg font-semibold text-gray-900 dark:text-white">{check.name}</h3>
                          <p className="text-xs text-gray-500 dark:text-gray-400">{check.description}</p>
                        </div>
                      </div>
                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.badge}`}>
                        {check.status}
                      </span>
                    </div>

                    <p className={`text-sm ${colors.text}`}>
                      {check.message}
                    </p>

                    {check.details && Object.keys(check.details).length > 0 && (
                      <div className="mt-4 bg-gray-50 dark:bg-gray-700/50 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
                        <h4 className="text-xs font-semibold text-gray-500 uppercase mb-2">Details</h4>
                        <dl className="space-y-1">
                          {Object.entries(check.details).map(([key, value]) => {
                            const isResourceId = key === 'Resource' && typeof value === 'string' && value.startsWith('/subscriptions/');
                            return (
                              <div key={key} className="flex justify-between text-xs items-center gap-3">
                                <dt className="font-medium text-gray-600 dark:text-gray-400 shrink-0">{key}</dt>
                                <dd className="text-gray-900 dark:text-gray-100 font-mono text-right break-all">
                                  {isResourceId ? (
                                    <span
                                      title={String(value)}
                                      className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-400 cursor-help"
                                    >
                                      Set
                                    </span>
                                  ) : Array.isArray(value) ? value.join(', ') : typeof value === 'object' ? JSON.stringify(value) : String(value)}
                                </dd>
                              </div>
                            );
                          })}
                        </dl>
                      </div>
                    )}
                  </div>
                </div>
              );
            })}

            {/* MCP Server — fetched on its own track; renders a "checking" state while the
                probe (possibly waking the scaled-to-zero container) is in flight, then
                updates in place without blocking any of the cards above. */}
            {(() => {
              const display = mcpLoading
                ? { name: 'MCP Server', description: 'AI query interface availability', status: 'checking', message: 'Probing MCP server — waking it from idle if needed…', details: undefined as Record<string, any> | undefined }
                : (mcpCheck ?? { name: 'MCP Server', description: 'AI query interface availability', status: 'unknown', message: 'Not checked yet', details: undefined as Record<string, any> | undefined });
              const colors = getStatusColor(display.status);
              return (
                <div className={`bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 ${colors.accent}`}>
                  <div className="p-6">
                    <div className="flex items-start justify-between mb-3">
                      <div className="flex items-center space-x-3">
                        <div className={`w-8 h-8 rounded-full ${colors.bg} flex items-center justify-center`}>
                          {display.status === 'checking' ? (
                            <svg className="w-5 h-5 text-blue-600 animate-spin" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                            </svg>
                          ) : display.status === 'healthy' ? (
                            <svg className="w-5 h-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                            </svg>
                          ) : display.status === 'unhealthy' ? (
                            <svg className="w-5 h-5 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                            </svg>
                          ) : (
                            <svg className="w-5 h-5 text-yellow-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                          )}
                        </div>
                        <div>
                          <h3 className="text-lg font-semibold text-gray-900 dark:text-white">{display.name}</h3>
                          <p className="text-xs text-gray-500 dark:text-gray-400">{display.description}</p>
                        </div>
                      </div>
                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.badge}`}>
                        {display.status === 'checking' ? 'checking…' : display.status}
                      </span>
                    </div>

                    <p className={`text-sm ${colors.text}`}>
                      {display.message}
                    </p>

                    {display.details && Object.keys(display.details).length > 0 && (
                      <div className="mt-4 bg-gray-50 dark:bg-gray-700/50 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
                        <h4 className="text-xs font-semibold text-gray-500 uppercase mb-2">Details</h4>
                        <dl className="space-y-1">
                          {Object.entries(display.details).map(([key, value]) => (
                            <div key={key} className="flex justify-between text-xs items-center gap-3">
                              <dt className="font-medium text-gray-600 dark:text-gray-400 shrink-0">{key}</dt>
                              <dd className="text-gray-900 dark:text-gray-100 font-mono text-right break-all">
                                {Array.isArray(value) ? value.join(', ') : typeof value === 'object' ? JSON.stringify(value) : String(value)}
                              </dd>
                            </div>
                          ))}
                        </dl>
                      </div>
                    )}
                  </div>
                </div>
              );
            })()}
          </div>
          </div>
        )}
      </div>
    </div>
  );
}
