'use client';

import { useEffect, useState, useMemo, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { api } from '@/lib/api';
import TruncatedLabel from '@/components/TruncatedLabel';
import { useAuth } from '../../../../contexts/AuthContext';
import { useNotifications } from '../../../../contexts/NotificationContext';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

// ── Types ──────────────────────────────────────────────────────────────────────

interface SessionAgentMetricDTO {
  sessionId: string;
  tenantId: string;
  deviceName?: string;
  manufacturer?: string;
  model?: string;
  startedAt?: string;
  status?: string;
  agentVersion?: string;
  snapshotCount: number;
  totalBytesUp: number;
  totalBytesDown: number;
  totalRequests: number;
  avgCpu: number;
  maxCpu: number;
  avgWorkingSet: number;
  maxWorkingSet: number;
  avgPrivateBytes: number;
  avgLatency: number;
  avgSpoolDepth: number;
  maxSpoolDepth: number;
  peakSpoolDepth: number;
  maxSpoolFileBytes: number;
  totalEventsEmitted: number;
  spoolPressureDetected: boolean;
}

interface DeliveryLatencyMetricsDTO {
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
  avgMs: number;
  sampleCount: number;
  clockSkewPercent: number;
}

interface CrashExceptionSummaryDTO {
  exceptionType: string;
  count: number;
}

interface CrashRateMetricsDTO {
  totalStarts: number;
  cleanExits: number;
  exceptionCrashes: number;
  hardKills: number;
  rebootKills: number;
  firstRuns: number;
  crashRatePercent: number;
  topExceptions: CrashExceptionSummaryDTO[];
}

interface PlatformMetricsResponse {
  sessions: SessionAgentMetricDTO[];
  deliveryLatency?: DeliveryLatencyMetricsDTO;
  crashRate?: CrashRateMetricsDTO;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
}

interface SessionAgentMetrics {
  session: { sessionId: string; tenantId: string; deviceName?: string; manufacturer?: string; model?: string; startedAt?: string; status?: string; agentVersion?: string };
  snapshotCount: number;
  totalBytesUp: number;
  totalBytesDown: number;
  totalRequests: number;
  avgCpu: number;
  maxCpu: number;
  avgWorkingSet: number;
  maxWorkingSet: number;
  avgPrivateBytes: number;
  avgLatency: number;
  avgSpoolDepth: number;
  maxSpoolDepth: number;
  peakSpoolDepth: number;
  maxSpoolFileBytes: number;
  totalEventsEmitted: number;
  spoolPressureDetected: boolean;
}

// ── Helpers ────────────────────────────────────────────────────────────────────

function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  const val = bytes / Math.pow(1024, i);
  return `${val.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

function formatLatency(ms: number): string {
  if (ms < 1000) return `${ms.toFixed(0)} ms`;
  if (ms < 60000) return `${(ms / 1000).toFixed(1)} s`;
  return `${(ms / 60000).toFixed(1)} min`;
}

function formatVersion(version: string): string {
  // "1.0.189.0+95a2ea7abc..." → "1.0.189+95a2ea7"
  const plusIdx = version.indexOf('+');
  let base = plusIdx >= 0 ? version.slice(0, plusIdx) : version;
  const hash = plusIdx >= 0 ? version.slice(plusIdx + 1, plusIdx + 8) : '';
  // Drop 4th segment (e.g. ".0") if present: "1.0.189.0" → "1.0.189"
  const segments = base.split('.');
  if (segments.length > 3) base = segments.slice(0, 3).join('.');
  return hash ? `${base}+${hash}` : base;
}

function avg(values: number[]): number {
  if (values.length === 0) return 0;
  return values.reduce((a, b) => a + b, 0) / values.length;
}

function max(values: number[]): number {
  if (values.length === 0) return 0;
  return Math.max(...values);
}

function pN(values: number[], percentile: number): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.ceil((percentile / 100) * sorted.length) - 1;
  return sorted[Math.max(0, idx)];
}

// ── Component ──────────────────────────────────────────────────────────────────

export function SectionAgentMetrics() {
  const router = useRouter();

  const { getAccessToken, user } = useAuth();
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(true);
  const [sessionMetrics, setSessionMetrics] = useState<SessionAgentMetrics[]>([]);
  // sampleSize drives the backend `limit` query, windowDays drives the backend
  // `days` query. Each picker change forces a fresh fetch (previous version
  // sliced client-side over an unbounded backend fetch and timed out on busy
  // installs). Defaults stay snappy: 20 sessions over 30 days.
  const [sampleSize, setSampleSize] = useState(20);
  const [windowDays, setWindowDays] = useState(30);
  const [error, setError] = useState<string | null>(null);
  const [versionFilter, setVersionFilter] = useState<string>('all');
  const [cacheInfo, setCacheInfo] = useState<{ fromCache: boolean; computeDurationMs: number; computedAt: string } | null>(null);
  const [deliveryLatency, setDeliveryLatency] = useState<DeliveryLatencyMetricsDTO | null>(null);
  const [crashRate, setCrashRate] = useState<CrashRateMetricsDTO | null>(null);

  // ── Fetch pre-computed metrics from backend (with 5-min per-(days,limit) cache) ────────────

  const fetchMetrics = useCallback(async (limit: number, days: number) => {
    setLoading(true);
    setError(null);

    try {
      const res = await authenticatedFetch(api.metrics.platform({ limit, days }), getAccessToken);

      if (!res.ok) {
        if (res.status === 403) {
          setError('Access denied. Global Admin privileges required.');
        } else {
          setError(`Failed to fetch platform metrics: ${res.status}`);
        }
        return;
      }

      const data: PlatformMetricsResponse = await res.json();
      setCacheInfo({ fromCache: data.fromCache, computeDurationMs: data.computeDurationMs, computedAt: data.computedAt });
      setDeliveryLatency(data.deliveryLatency || null);
      setCrashRate(data.crashRate || null);

      const mapped: SessionAgentMetrics[] = (data.sessions || []).map((s) => ({
        session: {
          sessionId: s.sessionId,
          tenantId: s.tenantId,
          deviceName: s.deviceName,
          manufacturer: s.manufacturer,
          model: s.model,
          startedAt: s.startedAt,
          status: s.status,
          agentVersion: s.agentVersion,
        },
        snapshotCount: s.snapshotCount,
        totalBytesUp: s.totalBytesUp,
        totalBytesDown: s.totalBytesDown,
        totalRequests: s.totalRequests,
        avgCpu: s.avgCpu,
        maxCpu: s.maxCpu,
        avgWorkingSet: s.avgWorkingSet,
        maxWorkingSet: s.maxWorkingSet,
        avgPrivateBytes: s.avgPrivateBytes,
        avgLatency: s.avgLatency,
        avgSpoolDepth: s.avgSpoolDepth,
        maxSpoolDepth: s.maxSpoolDepth,
        peakSpoolDepth: s.peakSpoolDepth ?? 0,
        maxSpoolFileBytes: s.maxSpoolFileBytes ?? 0,
        totalEventsEmitted: s.totalEventsEmitted ?? 0,
        spoolPressureDetected: s.spoolPressureDetected ?? false,
      }));

      setSessionMetrics(mapped);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Platform metrics fetch error:', err);
        setError(err instanceof Error ? err.message : 'Unknown error');
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, addNotification]);

  // Refetch whenever the user picks a new sample size or window (both drive
  // backend params, not a client-side slice). fetchMetrics is intentionally
  // excluded from deps: getAccessToken's identity churns on every MSAL
  // accounts refresh which would otherwise re-fire the effect after each
  // request and ping-pong the loading spinner.
  useEffect(() => {
    fetchMetrics(sampleSize, windowDays);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sampleSize, windowDays]);

  // ── Resolve agent version per session (pre-resolved by backend) ──

  const resolveVersion = useCallback((sm: SessionAgentMetrics): string => {
    return sm.session.agentVersion || 'unknown';
  }, []);

  // ── Available versions for filter dropdown ─────────────────────────────────

  const availableVersions = useMemo(() => {
    const versions = new Set<string>();
    for (const sm of sessionMetrics) {
      versions.add(resolveVersion(sm));
    }
    return Array.from(versions).sort();
  }, [sessionMetrics, resolveVersion]);

  // ── Filtered metrics by version (sample-size cap is enforced server-side) ──

  const filteredMetrics = useMemo(() => {
    // Backend already returns the newest `limit` sessions in startedAt-desc order;
    // re-sorting client-side is just defensive against repository-order drift.
    const sorted = [...sessionMetrics].sort(
      (a, b) => new Date(b.session.startedAt || 0).getTime() - new Date(a.session.startedAt || 0).getTime()
    );
    if (versionFilter === 'all') return sorted;
    return sorted.filter((sm) => resolveVersion(sm) === versionFilter);
  }, [sessionMetrics, versionFilter, resolveVersion]);

  // ── Aggregated stats across filtered sessions ──────────────────────────────

  const globalStats = useMemo(() => {
    if (filteredMetrics.length === 0) return null;

    const allCpuAvgs = filteredMetrics.map((s) => s.avgCpu);
    const allCpuMaxes = filteredMetrics.map((s) => s.maxCpu);
    const allWsAvgs = filteredMetrics.map((s) => s.avgWorkingSet);
    const allWsMaxes = filteredMetrics.map((s) => s.maxWorkingSet);
    const allPbAvgs = filteredMetrics.map((s) => s.avgPrivateBytes);
    const allLatAvgs = filteredMetrics.filter((s) => s.avgLatency > 0).map((s) => s.avgLatency);
    const allBytesUp = filteredMetrics.map((s) => s.totalBytesUp);
    const allBytesDown = filteredMetrics.map((s) => s.totalBytesDown);
    const allRequests = filteredMetrics.map((s) => s.totalRequests);
    const totalSnapshots = filteredMetrics.reduce((sum, s) => sum + s.snapshotCount, 0);

    return {
      sessionsAnalyzed: filteredMetrics.length,
      totalSnapshots,
      cpu: {
        avg: avg(allCpuAvgs),
        max: max(allCpuMaxes),
        p95: pN(allCpuMaxes, 95),
      },
      memory: {
        avgWs: avg(allWsAvgs),
        maxWs: max(allWsMaxes),
        p95Ws: pN(allWsMaxes, 95),
        avgPb: avg(allPbAvgs),
      },
      network: {
        avgBytesUpPerSession: avg(allBytesUp),
        avgBytesDownPerSession: avg(allBytesDown),
        maxBytesUp: max(allBytesUp),
        avgRequestsPerSession: avg(allRequests),
        avgLatency: avg(allLatAvgs),
        p95Latency: pN(allLatAvgs, 95),
      },
    };
  }, [filteredMetrics]);

  // ── Agent version distribution (always from all sessions, not filtered) ────

  const versionDistribution = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const sm of sessionMetrics) {
      const v = resolveVersion(sm);
      counts[v] = (counts[v] || 0) + 1;
    }
    return Object.entries(counts)
      .sort((a, b) => b[1] - a[1])
      .map(([version, count]) => ({ version, count, pct: (count / sessionMetrics.length) * 100 }));
  }, [sessionMetrics, resolveVersion]);

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between">
            <div>
              <h1 className="text-2xl font-normal text-gray-900">Agent Metrics</h1>
              {cacheInfo && (
                <p className="text-xs text-gray-400 mt-1">
                  {cacheInfo.fromCache ? 'From cache' : `Computed in ${cacheInfo.computeDurationMs}ms`}
                  {' · '}
                  {new Date(cacheInfo.computedAt).toLocaleTimeString()}
                </p>
              )}
            </div>
            <div className="flex items-center gap-3">
              <label className="text-sm text-gray-500">Window:</label>
              <select
                value={windowDays}
                onChange={(e) => setWindowDays(Number(e.target.value))}
                className="text-sm border border-gray-300 rounded-md px-2 py-1"
              >
                <option value={7}>7 days</option>
                <option value={14}>14 days</option>
                <option value={30}>30 days</option>
                <option value={90}>90 days</option>
                <option value={180}>180 days</option>
                <option value={365}>365 days</option>
              </select>
              <label className="text-sm text-gray-500">Sessions to analyze:</label>
              <select
                value={sampleSize}
                onChange={(e) => setSampleSize(Number(e.target.value))}
                className="text-sm border border-gray-300 rounded-md px-2 py-1"
              >
                <option value={20}>20</option>
                <option value={50}>50</option>
                <option value={100}>100</option>
                <option value={200}>200</option>
                <option value={500}>500</option>
                <option value={1000}>1000</option>
                <option value={2000}>2000</option>
              </select>
              {availableVersions.length > 1 && (
                <>
                  <label className="text-sm text-gray-500">Agent version:</label>
                  <select
                    value={versionFilter}
                    onChange={(e) => setVersionFilter(e.target.value)}
                    className="text-sm border border-gray-300 rounded-md px-2 py-1"
                  >
                    <option value="all">All versions</option>
                    {availableVersions.map((v) => (
                      <option key={v} value={v}>{formatVersion(v)}</option>
                    ))}
                  </select>
                </>
              )}
              <button
                onClick={() => fetchMetrics(sampleSize, windowDays)}
                disabled={loading}
                className="px-3 py-1.5 text-sm bg-purple-600 text-white rounded-md hover:bg-purple-700 disabled:opacity-50 transition-colors"
              >
                {loading ? 'Loading...' : 'Refresh'}
              </button>
            </div>
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {error && (
          <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
            {error}
          </div>
        )}

        {loading && (
          <div className="flex items-center justify-center py-20">
            <div className="flex items-center gap-3 text-gray-500">
              <svg className="animate-spin h-5 w-5" viewBox="0 0 24 24" fill="none">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
              <span>Fetching agent metrics...</span>
            </div>
          </div>
        )}

        {!loading && !error && globalStats && (
          <>
            {/* ── Overview Stats ──────────────────────────────────────── */}
            <div className="grid grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
              <StatCard
                label="Sessions Analyzed"
                value={globalStats.sessionsAnalyzed.toString()}
                detail={`${globalStats.totalSnapshots} snapshots total`}
                color="purple"
              />
              <StatCard
                label="Avg Agent CPU"
                value={`${globalStats.cpu.avg.toFixed(2)}%`}
                detail={`p95 peak: ${globalStats.cpu.p95.toFixed(2)}%, max: ${globalStats.cpu.max.toFixed(2)}%`}
                color={globalStats.cpu.avg < 2 ? 'green' : globalStats.cpu.avg < 5 ? 'yellow' : 'red'}
              />
              <StatCard
                label="Avg Working Set"
                value={`${globalStats.memory.avgWs.toFixed(1)} MB`}
                detail={`p95 peak: ${globalStats.memory.p95Ws.toFixed(1)} MB, max: ${globalStats.memory.maxWs.toFixed(1)} MB`}
                color={globalStats.memory.avgWs < 30 ? 'green' : globalStats.memory.avgWs < 60 ? 'yellow' : 'red'}
              />
              <StatCard
                label="Avg Network / Session"
                value={formatBytes(globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession)}
                detail={`${formatBytes(globalStats.network.avgBytesUpPerSession)} up, ${formatBytes(globalStats.network.avgBytesDownPerSession)} down`}
                color="blue"
              />
            </div>

            {/* ── Agent Footprint Assessment ─────────────────────────── */}
            <div className="bg-white shadow rounded-lg p-6 mb-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">Agent Footprint Assessment</h2>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                {/* CPU */}
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">CPU Impact</span>
                    <FootprintBadge value={globalStats.cpu.avg} thresholds={[1, 3, 5]} unit="%" />
                  </div>
                  <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all ${globalStats.cpu.avg < 1 ? 'bg-green-500' : globalStats.cpu.avg < 3 ? 'bg-green-400' : globalStats.cpu.avg < 5 ? 'bg-yellow-400' : 'bg-red-500'}`}
                      style={{ width: `${Math.min(globalStats.cpu.avg * 10, 100)}%` }}
                    />
                  </div>
                  <p className="text-xs text-gray-500 mt-1">
                    Avg: {globalStats.cpu.avg.toFixed(2)}% | Peak p95: {globalStats.cpu.p95.toFixed(2)}%
                  </p>
                </div>

                {/* Memory */}
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Memory Footprint</span>
                    <FootprintBadge value={globalStats.memory.avgWs} thresholds={[20, 40, 80]} unit=" MB" />
                  </div>
                  <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all ${globalStats.memory.avgWs < 20 ? 'bg-green-500' : globalStats.memory.avgWs < 40 ? 'bg-green-400' : globalStats.memory.avgWs < 80 ? 'bg-yellow-400' : 'bg-red-500'}`}
                      style={{ width: `${Math.min((globalStats.memory.avgWs / 100) * 100, 100)}%` }}
                    />
                  </div>
                  <p className="text-xs text-gray-500 mt-1">
                    Working Set avg: {globalStats.memory.avgWs.toFixed(1)} MB | Private Bytes avg: {globalStats.memory.avgPb.toFixed(1)} MB
                  </p>
                </div>

                {/* Network */}
                <div>
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm font-medium text-gray-700">Network Usage</span>
                    <FootprintBadge
                      value={(globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession) / 1024}
                      thresholds={[100, 500, 2048]}
                      unit=" KB"
                      formatFn={(v) => formatBytes(v * 1024)}
                    />
                  </div>
                  <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className="h-full bg-blue-500 rounded-full transition-all"
                      style={{ width: `${Math.min(((globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession) / (1024 * 1024)) * 100, 100)}%` }}
                    />
                  </div>
                  <p className="text-xs text-gray-500 mt-1">
                    Avg {globalStats.network.avgRequestsPerSession.toFixed(0)} requests/session | Latency avg: {globalStats.network.avgLatency.toFixed(0)} ms, p95: {globalStats.network.p95Latency.toFixed(0)} ms
                  </p>
                </div>
              </div>
            </div>

            {/* ── Detailed Breakdown ─────────────────────────────────── */}
            <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6">
              {/* Per-Session CPU Distribution */}
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-sm font-semibold text-gray-900 mb-4">CPU % per Session (avg)</h3>
                <div className="space-y-2">
                  {filteredMetrics
                    .sort((a, b) => b.avgCpu - a.avgCpu)
                    .slice(0, 10)
                    .map((sm) => {
                      const barWidth = globalStats.cpu.max > 0 ? (sm.avgCpu / globalStats.cpu.max) * 100 : 0;
                      return (
                        <div key={sm.session.sessionId} className="flex items-center gap-3">
                          <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                            {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                          </span>
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div
                              className={`h-full rounded-full ${sm.avgCpu < 1 ? 'bg-green-400' : sm.avgCpu < 3 ? 'bg-green-500' : sm.avgCpu < 5 ? 'bg-yellow-400' : 'bg-red-500'}`}
                              style={{ width: `${Math.max(barWidth, 2)}%` }}
                            />
                          </div>
                          <span className="text-xs font-mono text-gray-600 w-14 text-right">
                            {sm.avgCpu.toFixed(2)}%
                          </span>
                        </div>
                      );
                    })}
                </div>
              </div>

              {/* Per-Session Memory Distribution */}
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-sm font-semibold text-gray-900 mb-4">Working Set per Session (avg MB)</h3>
                <div className="space-y-2">
                  {filteredMetrics
                    .sort((a, b) => b.avgWorkingSet - a.avgWorkingSet)
                    .slice(0, 10)
                    .map((sm) => {
                      const barWidth = globalStats.memory.maxWs > 0 ? (sm.avgWorkingSet / globalStats.memory.maxWs) * 100 : 0;
                      return (
                        <div key={sm.session.sessionId} className="flex items-center gap-3">
                          <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                            {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                          </span>
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div
                              className={`h-full rounded-full ${sm.avgWorkingSet < 20 ? 'bg-green-400' : sm.avgWorkingSet < 40 ? 'bg-yellow-400' : 'bg-red-500'}`}
                              style={{ width: `${Math.max(barWidth, 2)}%` }}
                            />
                          </div>
                          <span className="text-xs font-mono text-gray-600 w-16 text-right">
                            {sm.avgWorkingSet.toFixed(1)} MB
                          </span>
                        </div>
                      );
                    })}
                </div>
              </div>

              {/* Per-Session Network Usage */}
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-sm font-semibold text-gray-900 mb-4">Network per Session (total bytes)</h3>
                <div className="space-y-2">
                  {filteredMetrics
                    .sort((a, b) => (b.totalBytesUp + b.totalBytesDown) - (a.totalBytesUp + a.totalBytesDown))
                    .slice(0, 10)
                    .map((sm) => {
                      const total = sm.totalBytesUp + sm.totalBytesDown;
                      const maxTotal = globalStats.network.maxBytesUp + max(filteredMetrics.map(s => s.totalBytesDown));
                      const barWidth = maxTotal > 0 ? (total / maxTotal) * 100 : 0;
                      return (
                        <div key={sm.session.sessionId} className="flex items-center gap-3">
                          <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                            {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                          </span>
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div className="h-full bg-blue-500 rounded-full" style={{ width: `${Math.max(barWidth, 2)}%` }} />
                          </div>
                          <span className="text-xs font-mono text-gray-600 w-16 text-right">
                            {formatBytes(total)}
                          </span>
                        </div>
                      );
                    })}
                </div>
              </div>

              {/* Agent Version Distribution */}
              <div className="bg-white shadow rounded-lg p-6">
                <div className="flex items-center justify-between mb-4">
                  <h3 className="text-sm font-semibold text-gray-900">Agent Version Distribution</h3>
                  {versionFilter !== 'all' && (
                    <button
                      onClick={() => setVersionFilter('all')}
                      className="text-xs text-purple-600 hover:text-purple-800 transition-colors"
                    >
                      Clear filter
                    </button>
                  )}
                </div>
                {versionDistribution.length === 0 ? (
                  <p className="text-sm text-gray-500">No version data available</p>
                ) : (
                  <div className="space-y-2">
                    {versionDistribution.map((v) => (
                      <button
                        key={v.version}
                        onClick={() => setVersionFilter(versionFilter === v.version ? 'all' : v.version)}
                        className={`flex items-center gap-3 w-full text-left rounded-md px-1 py-0.5 transition-colors ${versionFilter === v.version ? 'bg-purple-50 ring-1 ring-purple-300' : 'hover:bg-gray-50'}`}
                      >
                        <span className="text-xs font-mono text-gray-600 w-32 truncate" title={v.version}>
                          {formatVersion(v.version)}
                        </span>
                        <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                          <div className="h-full bg-purple-500 rounded-full" style={{ width: `${v.pct}%` }} />
                        </div>
                        <span className="text-xs text-gray-500 w-16 text-right">
                          {v.count} ({v.pct.toFixed(0)}%)
                        </span>
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </div>

            {/* ── Platform Health Metrics ───────────────────────────── */}
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-lg font-semibold text-gray-900 mb-4">Platform Health</h2>
              <div className="grid grid-cols-1 md:grid-cols-3 gap-6">

                {/* Event Delivery Latency */}
                <div>
                  <h3 className="text-sm font-medium text-gray-700 mb-3">Event Delivery Latency</h3>
                  {deliveryLatency && deliveryLatency.sampleCount > 0 ? (
                    <div className="space-y-2">
                      <div className="flex justify-between text-sm">
                        <span className="text-gray-500">p50</span>
                        <span className="font-mono text-gray-900">{formatLatency(deliveryLatency.p50Ms)}</span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-gray-500">p95</span>
                        <span className={`font-mono ${deliveryLatency.p95Ms > 30000 ? 'text-yellow-600' : 'text-gray-900'}`}>
                          {formatLatency(deliveryLatency.p95Ms)}
                        </span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-gray-500">p99</span>
                        <span className={`font-mono ${deliveryLatency.p99Ms > 60000 ? 'text-red-600' : 'text-gray-900'}`}>
                          {formatLatency(deliveryLatency.p99Ms)}
                        </span>
                      </div>
                      <div className="flex justify-between text-sm border-t border-gray-100 pt-2">
                        <span className="text-gray-500">avg</span>
                        <span className="font-mono text-gray-900">{formatLatency(deliveryLatency.avgMs)}</span>
                      </div>
                      <p className="text-xs text-gray-400 mt-1">
                        {deliveryLatency.sampleCount.toLocaleString()} events sampled
                        {deliveryLatency.clockSkewPercent > 0 && (
                          <span className="text-yellow-600 ml-1">
                            ({deliveryLatency.clockSkewPercent.toFixed(1)}% clock skew)
                          </span>
                        )}
                      </p>
                    </div>
                  ) : (
                    <p className="text-sm text-gray-400">No latency data yet</p>
                  )}
                </div>

                {/* Crash Rate */}
                <div>
                  <h3 className="text-sm font-medium text-gray-700 mb-3">Agent Exit Statistics</h3>
                  {crashRate && crashRate.totalStarts > 0 ? (
                    <div className="space-y-2">
                      <div className="flex items-baseline gap-2">
                        <span className={`text-2xl font-bold ${crashRate.crashRatePercent === 0 ? 'text-green-600' : crashRate.crashRatePercent < 5 ? 'text-yellow-600' : 'text-red-600'}`}>
                          {crashRate.crashRatePercent.toFixed(1)}%
                        </span>
                        <span className="text-xs text-gray-500">exception crash rate</span>
                      </div>
                      <div className="space-y-1 text-sm">
                        <div className="flex justify-between">
                          <span className="text-gray-500">Clean exits</span>
                          <span className="font-mono text-green-600">{crashRate.cleanExits}</span>
                        </div>
                        <div className="flex justify-between">
                          <span className="text-gray-500">Exception crashes</span>
                          <span className={`font-mono ${crashRate.exceptionCrashes > 0 ? 'text-red-600' : 'text-gray-400'}`}>{crashRate.exceptionCrashes}</span>
                        </div>
                        <div className="flex justify-between border-t border-gray-100 pt-1 mt-1">
                          <span className="text-gray-400">Hard kills (expected)</span>
                          <span className={`font-mono ${crashRate.hardKills > 0 ? 'text-gray-500' : 'text-gray-400'}`}>{crashRate.hardKills}</span>
                        </div>
                        <div className="flex justify-between">
                          <span className="text-gray-400">Reboot kills (expected)</span>
                          <span className={`font-mono ${(crashRate.rebootKills ?? 0) > 0 ? 'text-gray-500' : 'text-gray-400'}`}>{crashRate.rebootKills ?? 0}</span>
                        </div>
                        <div className="flex justify-between">
                          <span className="text-gray-400">First runs</span>
                          <span className="font-mono text-gray-400">{crashRate.firstRuns}</span>
                        </div>
                      </div>
                      {crashRate.topExceptions.length > 0 && (
                        <div className="border-t border-gray-100 pt-2 mt-2">
                          <p className="text-xs text-gray-500 mb-1">Top exceptions:</p>
                          {crashRate.topExceptions.map((e) => (
                            <div key={e.exceptionType} className="flex justify-between text-xs">
                              <TruncatedLabel text={e.exceptionType} className="text-red-600 font-mono mr-2" />
                              <span className="text-gray-500">{e.count}x</span>
                            </div>
                          ))}
                        </div>
                      )}
                      <p className="text-xs text-gray-400 mt-1">
                        {crashRate.totalStarts} total starts across {globalStats.sessionsAnalyzed} sessions
                      </p>
                    </div>
                  ) : (
                    <p className="text-sm text-gray-400">No crash data yet (requires agent with clean-exit marker)</p>
                  )}
                </div>

                {/* Spool Queue Depth */}
                <div>
                  <h3 className="text-sm font-medium text-gray-700 mb-3">Spool Queue Depth</h3>
                  {(() => {
                    const spoolAvgs = filteredMetrics.map(s => s.avgSpoolDepth);
                    const spoolMaxes = filteredMetrics.map(s => s.maxSpoolDepth);
                    const peakValues = filteredMetrics.map(s => s.peakSpoolDepth);
                    const fileSizeMaxes = filteredMetrics.map(s => s.maxSpoolFileBytes);
                    const totalEventsValues = filteredMetrics.map(s => s.totalEventsEmitted);
                    const pressureCount = filteredMetrics.filter(s => s.spoolPressureDetected).length;
                    const hasData = spoolAvgs.some(v => v > 0) || spoolMaxes.some(v => v > 0)
                      || peakValues.some(v => v > 0) || totalEventsValues.some(v => v > 0);
                    const overallAvg = avg(spoolAvgs);
                    // Sampled max (max-of-snapshots) vs. true intra-tick peak (V2 only).
                    // V2 sessions: peak >= sampled. V1 sessions: peak = 0 → falls back to sampled.
                    const truePeak = Math.max(max(spoolMaxes), max(peakValues));
                    const maxFileBytes = max(fileSizeMaxes);
                    const avgEvents = avg(totalEventsValues);
                    const maxEvents = max(totalEventsValues);
                    const pressurePct = filteredMetrics.length > 0
                      ? (pressureCount / filteredMetrics.length) * 100
                      : 0;
                    return hasData ? (
                      <div className="space-y-2">
                        <div className="flex items-baseline gap-2">
                          <span className={`text-2xl font-bold ${overallAvg < 5 ? 'text-green-600' : overallAvg < 20 ? 'text-yellow-600' : 'text-red-600'}`}>
                            {overallAvg.toFixed(1)}
                          </span>
                          <span className="text-xs text-gray-500">avg depth</span>
                        </div>
                        <div className="space-y-1 text-sm">
                          <div className="flex justify-between">
                            <span className="text-gray-500">Avg across sessions</span>
                            <span className="font-mono text-gray-900">{overallAvg.toFixed(1)}</span>
                          </div>
                          <div className="flex justify-between" title="Highest pending count seen at any tick (V2 reports true intra-tick peak; V1 = 60s sample max)">
                            <span className="text-gray-500">Peak (worst seen)</span>
                            <span className={`font-mono ${truePeak > 50 ? 'text-red-600' : 'text-gray-900'}`}>{truePeak.toFixed(0)}</span>
                          </div>
                          <div className="flex justify-between">
                            <span className="text-gray-500">p95 max</span>
                            <span className="font-mono text-gray-900">{pN(spoolMaxes, 95).toFixed(0)}</span>
                          </div>
                          <div className="flex justify-between border-t border-gray-100 pt-1 mt-1" title="Largest on-disk spool file across sessions (V2 only)">
                            <span className="text-gray-500">Max file size</span>
                            <span className={`font-mono ${maxFileBytes > 5 * 1024 * 1024 ? 'text-red-600' : 'text-gray-900'}`}>
                              {maxFileBytes > 0 ? formatBytes(maxFileBytes) : '—'}
                            </span>
                          </div>
                          <div className="flex justify-between" title="Total events emitted by the agent during the session (V2 only)">
                            <span className="text-gray-500">Events emitted (avg / max)</span>
                            <span className="font-mono text-gray-900">
                              {avgEvents > 0 ? `${avgEvents.toFixed(0)} / ${maxEvents.toFixed(0)}` : '—'}
                            </span>
                          </div>
                          <div className="flex justify-between" title="One-shot pressure event (pending > 2000 OR file > 5 MB) — V2 only">
                            <span className="text-gray-500">Pressure detected</span>
                            <span className={`font-mono ${pressureCount > 0 ? 'text-red-600' : 'text-gray-400'}`}>
                              {pressureCount} ({pressurePct.toFixed(0)}%)
                            </span>
                          </div>
                        </div>
                        <p className="text-xs text-gray-400 mt-1">
                          Events queued in spool before upload (0 = real-time delivery)
                        </p>
                      </div>
                    ) : (
                      <p className="text-sm text-gray-400">No spool data yet (requires updated agent)</p>
                    );
                  })()}
                </div>
              </div>
            </div>
          </>
        )}

        {!loading && !error && filteredMetrics.length === 0 && (
          <div className="text-center py-20 text-gray-500">
            <svg className="mx-auto h-12 w-12 text-gray-300 mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 013 19.875v-6.75zM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V8.625zM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V4.125z" />
            </svg>
            <p className="text-lg font-medium">No agent metrics data yet</p>
            <p className="text-sm mt-1">
              Deploy the agent with <code className="text-xs bg-gray-100 px-1.5 py-0.5 rounded">AgentSelfMetricsCollector</code> enabled.
              <br />
              Metrics will appear here after the first enrollment sessions complete.
            </p>
          </div>
        )}
      </main>
    </div>
  );
}

// ── Sub-components ─────────────────────────────────────────────────────────────

function StatCard({ label, value, detail, color }: { label: string; value: string; detail: string; color: string }) {
  const borderColor: Record<string, string> = {
    green: 'border-green-500',
    yellow: 'border-yellow-500',
    red: 'border-red-500',
    blue: 'border-blue-500',
    purple: 'border-purple-500',
  };
  const textColor: Record<string, string> = {
    green: 'text-green-700',
    yellow: 'text-yellow-700',
    red: 'text-red-700',
    blue: 'text-blue-700',
    purple: 'text-purple-700',
  };

  return (
    <div className={`bg-white shadow rounded-lg p-4 border-l-4 ${borderColor[color] || 'border-gray-300'}`}>
      <p className="text-xs text-gray-500 uppercase tracking-wide">{label}</p>
      <p className={`text-2xl font-bold mt-1 ${textColor[color] || 'text-gray-900'}`}>{value}</p>
      <p className="text-xs text-gray-400 mt-1">{detail}</p>
    </div>
  );
}

function FootprintBadge({
  value,
  thresholds,
  unit,
  formatFn,
}: {
  value: number;
  thresholds: [number, number, number];
  unit: string;
  formatFn?: (v: number) => string;
}) {
  let label: string;
  let className: string;

  if (value < thresholds[0]) {
    label = 'Minimal';
    className = 'bg-green-100 text-green-800';
  } else if (value < thresholds[1]) {
    label = 'Light';
    className = 'bg-green-50 text-green-700';
  } else if (value < thresholds[2]) {
    label = 'Moderate';
    className = 'bg-yellow-100 text-yellow-800';
  } else {
    label = 'Heavy';
    className = 'bg-red-100 text-red-800';
  }

  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${className}`}>
      {label}
    </span>
  );
}
