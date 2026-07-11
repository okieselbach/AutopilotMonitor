"use client";

import { useEffect, useState, useRef, useMemo, useCallback } from "react";
import { useRouter } from "next/navigation";
import dynamic from "next/dynamic";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAggregatedAdminScope } from "@/hooks";
import { useFetchProgress } from "@/hooks/useFetchProgress";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { CalculatingCard } from "@/components/CalculatingCard";

// A cross-tenant geo aggregation can take tens of seconds server-side; the default 30s fetch
// timeout would abort it client-side while the server keeps computing.
const GEO_FETCH_TIMEOUT_MS = 180_000;

// Dynamically import the map component (Leaflet requires window/document)
const GeoMap = dynamic(() => import("./GeoMap"), { ssr: false });

interface LocationMetric {
  locationKey: string;
  country: string;
  region: string;
  city: string;
  loc: string;
  sessionCount: number;
  succeeded: number;
  failed: number;
  successRate: number;
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  p95DurationMinutes: number;
  avgAppCount: number;
  minutesPerApp: number;
  appLoadScore: number;
  avgThroughputBytesPerSec: number;
  totalDownloadBytes: number;
  durationVsGlobalPct: number;
  throughputVsGlobalPct: number;
  isOutlier: boolean;
  outlierDirection: string | null;
  // Delivery Optimization
  doSessionCount: number;
  avgDoPercentPeerCaching: number;
  totalDoBytesFromPeers: number;
  totalDoBytesFromHttp: number;
  totalDoBytesFromLanPeers: number;
  totalDoBytesFromGroupPeers: number;
  totalDoBytesFromInternetPeers: number;
}

interface GlobalAverages {
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  avgMinutesPerApp: number;
  avgThroughputBytesPerSec: number;
  stdDevDurationMinutes: number;
  avgDoPercentPeerCaching: number;
  totalDoBytesFromPeers: number;
  totalDoBytesFromHttp: number;
}

interface GeographicMetricsResponse {
  success: boolean;
  locations: LocationMetric[];
  globalAverages: GlobalAverages;
  computedAt: string;
  totalSessions: number;
  locationsWithData: number;
  geoLocationEnabled: boolean;
}

type GroupBy = "city" | "region" | "country";
type SortBy = "sessionCount" | "avgDurationMinutes" | "appLoadScore" | "avgThroughputBytesPerSec" | "avgDoPercentPeerCaching";
type TimeRange = "7d" | "30d" | "90d";

const durationColor = (value: number, globalAvg: number) => {
  if (globalAvg <= 0) return "text-gray-700";
  const ratio = value / globalAvg;
  if (ratio <= 0.8) return "bg-green-100 text-green-800";
  if (ratio <= 1.0) return "bg-green-50 text-green-700";
  if (ratio <= 1.2) return "bg-yellow-50 text-yellow-700";
  if (ratio <= 1.5) return "bg-orange-50 text-orange-700";
  return "bg-red-100 text-red-800";
};

const scoreColor = (score: number) => {
  if (score <= 0) return "text-gray-400";
  if (score < 80) return "text-green-600";
  if (score <= 120) return "text-gray-700";
  return "text-red-600";
};

const formatThroughput = (bytesPerSec: number) => {
  if (bytesPerSec <= 0) return "—";
  if (bytesPerSec >= 1024 * 1024) return `${(bytesPerSec / 1024 / 1024).toFixed(1)} MB/s`;
  if (bytesPerSec >= 1024) return `${(bytesPerSec / 1024).toFixed(0)} KB/s`;
  return `${bytesPerSec.toFixed(0)} B/s`;
};

const formatBytes = (bytes: number) => {
  if (bytes <= 0) return "—";
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
  if (bytes >= 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
  return `${(bytes / 1024).toFixed(0)} KB`;
};

export default function GeographicPerformancePage() {
  const router = useRouter();

  const [geoMetrics, setGeoMetrics] = useState<GeographicMetricsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [timeRange, setTimeRange] = useState<TimeRange>("30d");
  const [groupBy, setGroupBy] = useState<GroupBy>("city");
  const [sortBy, setSortBy] = useState<SortBy>("sessionCount");
  const [sortDesc, setSortDesc] = useState(true);
  const [selectedLocation, setSelectedLocation] = useState<string | null>(null);

  const isTimeRangeMount = useRef(true);

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  // Global admin tenant scope (aggregated-capable): tenant list, selection ("" = all tenants),
  // and scope flags. Default selection is the GA's own tenant.
  const scope = useAggregatedAdminScope();
  const { isGlobalAdmin, routeGlobal, selectedTenantId, isAggregatedGlobalView, scopeInitialized, scopeKey } = scope;

  const progress = useFetchProgress("geoPerf.lastFetchMs");
  const { begin: progressBegin, finish: progressFinish } = progress;

  const fetchGeoMetrics = useCallback(async (range: TimeRange = timeRange, group: GroupBy = groupBy) => {
    let succeeded = false;
    try {
      progressBegin();
      const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
      const endpoint = routeGlobal
        ? api.metrics.globalGeographic(days, group, selectedTenantId || undefined)
        : api.metrics.geographic(tenantId, days, group);
      const response = await authenticatedFetch(endpoint, getAccessToken, {
        signal: AbortSignal.timeout(GEO_FETCH_TIMEOUT_MS),
      });
      if (response.ok) {
        const data = await response.json();
        setGeoMetrics(data);
        succeeded = true;
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        console.error("Session expired:", error.message);
      } else {
        console.error("Failed to fetch geographic metrics:", error);
      }
    } finally {
      progressFinish(succeeded);
      setLoading(false);
    }
  }, [routeGlobal, selectedTenantId, tenantId, getAccessToken, timeRange, groupBy, progressBegin, progressFinish]);

  useEffect(() => {
    if (!scopeInitialized) return;
    if (isTimeRangeMount.current) {
      isTimeRangeMount.current = false;
    } else {
      setLoading(true);
    }
    fetchGeoMetrics(timeRange, groupBy);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, timeRange, groupBy, scopeKey]);

  const sortedLocations = useMemo(() => {
    if (!geoMetrics?.locations) return [];
    const sorted = [...geoMetrics.locations].sort((a, b) => {
      const aVal = a[sortBy];
      const bVal = b[sortBy];
      return sortDesc ? (bVal as number) - (aVal as number) : (aVal as number) - (bVal as number);
    });
    return sorted;
  }, [geoMetrics, sortBy, sortDesc]);

  const stats = useMemo(() => {
    if (!geoMetrics) return null;
    const outliers = geoMetrics.locations.filter((l) => l.isOutlier);
    const withDuration = geoMetrics.locations.filter((l) => l.avgDurationMinutes > 0);
    const fastest = withDuration.length > 0
      ? withDuration.reduce((a, b) => (a.avgDurationMinutes < b.avgDurationMinutes ? a : b))
      : null;
    const slowest = withDuration.length > 0
      ? withDuration.reduce((a, b) => (a.avgDurationMinutes > b.avgDurationMinutes ? a : b))
      : null;
    return { outliers: outliers.length, fastest, slowest };
  }, [geoMetrics]);

  const handleSort = (col: SortBy) => {
    if (sortBy === col) {
      setSortDesc(!sortDesc);
    } else {
      setSortBy(col);
      setSortDesc(true);
    }
  };

  const SortIcon = ({ col }: { col: SortBy }) => {
    if (sortBy !== col) return <span className="text-gray-300 ml-1">&#8597;</span>;
    return <span className="text-blue-600 ml-1">{sortDesc ? "▼" : "▲"}</span>;
  };

  if (loading) {
    return (
      <CalculatingCard
        title="Calculating geographic metrics…"
        subtitle="Aggregating sessions, download throughput and Delivery Optimization data by location."
        elapsedMs={progress.elapsedMs}
        estimateMs={progress.estimateMs}
      />
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner
          show={scope.isGlobalAdmin}
          delegated={scope.isDelegatedScope}
          subtitle={globalAdminSubtitle(scope, "aggregating data across all tenants")}
        />
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <div className="flex items-center space-x-3">
                  <svg className="w-8 h-8 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <h1 className="text-2xl font-normal text-gray-900">Geographic Performance</h1>
                </div>
              </div>
              <div className="flex items-center space-x-4">
                <TenantScopeSelector scope={scope} allowAggregated />
                {/* Time Range Toggle */}
                <div className="flex bg-gray-100 rounded-lg p-1">
                  {(["7d", "30d", "90d"] as TimeRange[]).map((range) => (
                    <button
                      key={range}
                      onClick={() => setTimeRange(range)}
                      className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
                        timeRange === range
                          ? "bg-white text-gray-900 shadow-sm"
                          : "text-gray-500 hover:text-gray-700"
                      }`}
                    >
                      {range === "7d" ? "7 Days" : range === "30d" ? "30 Days" : "90 Days"}
                    </button>
                  ))}
                </div>
                {/* Group By Toggle */}
                <div className="flex bg-gray-100 rounded-lg p-1">
                  {(["city", "region", "country"] as GroupBy[]).map((g) => (
                    <button
                      key={g}
                      onClick={() => setGroupBy(g)}
                      className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors capitalize ${
                        groupBy === g
                          ? "bg-white text-gray-900 shadow-sm"
                          : "text-gray-500 hover:text-gray-700"
                      }`}
                    >
                      {g}
                    </button>
                  ))}
                </div>
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {/* Geo Disabled Warning */}
          {geoMetrics && !geoMetrics.geoLocationEnabled && !isAggregatedGlobalView && (
            <div className="mb-4 bg-amber-50 border border-amber-200 rounded-lg p-4 flex items-start space-x-3">
              <svg className="w-5 h-5 text-amber-500 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
              <div>
                <div className="text-sm font-medium text-amber-800">Geo-Location collection is disabled</div>
                <div className="text-sm text-amber-700 mt-0.5">
                  New sessions will not collect location data. Enable geo-location in{" "}
                  <a href="/settings" className="underline hover:text-amber-900">Configuration</a>{" "}
                  to start collecting geographic data for future enrollments.
                </div>
              </div>
            </div>
          )}

          {/* Coverage Info */}
          {geoMetrics && (
            <div className="mb-4 text-sm text-gray-500">
              {geoMetrics.locationsWithData} of {geoMetrics.totalSessions} sessions have location data
              {geoMetrics.locationsWithData < geoMetrics.totalSessions && (
                <span className="ml-1 text-gray-400">(older sessions may lack geo data)</span>
              )}
            </div>
          )}

          {/* Summary Cards */}
          {geoMetrics && stats && (
            <div className="grid grid-cols-1 md:grid-cols-5 gap-4 mb-6">
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Locations Tracked</div>
                <div className="text-2xl font-bold text-gray-900">{geoMetrics.locations.length}</div>
                <div className="text-xs text-gray-400">{geoMetrics.locationsWithData} sessions with geo</div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Outliers Detected</div>
                <div className={`text-2xl font-bold ${stats.outliers > 0 ? "text-red-600" : "text-green-600"}`}>
                  {stats.outliers}
                </div>
                <div className="text-xs text-gray-400">&gt;2 std deviations from mean</div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Fastest Location</div>
                <div className="text-lg font-bold text-green-600 truncate" title={stats.fastest?.locationKey}>
                  {stats.fastest?.locationKey || "—"}
                </div>
                <div className="text-xs text-gray-400">
                  {stats.fastest ? `${Math.round(stats.fastest.avgDurationMinutes)} min avg` : "No data"}
                </div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Slowest Location</div>
                <div className="text-lg font-bold text-red-600 truncate" title={stats.slowest?.locationKey}>
                  {stats.slowest?.locationKey || "—"}
                </div>
                <div className="text-xs text-gray-400">
                  {stats.slowest ? `${Math.round(stats.slowest.avgDurationMinutes)} min avg` : "No data"}
                </div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">DO Peer Efficiency</div>
                <div className={`text-2xl font-bold ${
                  geoMetrics.globalAverages.avgDoPercentPeerCaching > 30 ? "text-green-600" :
                  geoMetrics.globalAverages.avgDoPercentPeerCaching > 0 ? "text-yellow-600" : "text-gray-400"
                }`}>
                  {geoMetrics.globalAverages.avgDoPercentPeerCaching > 0
                    ? `${geoMetrics.globalAverages.avgDoPercentPeerCaching}%`
                    : "—"}
                </div>
                <div className="text-xs text-gray-400">
                  {geoMetrics.globalAverages.totalDoBytesFromPeers > 0
                    ? `${formatBytes(geoMetrics.globalAverages.totalDoBytesFromPeers)} from peers / ${formatBytes(geoMetrics.globalAverages.totalDoBytesFromPeers + geoMetrics.globalAverages.totalDoBytesFromHttp)} total`
                    : "No DO data yet"}
                </div>
              </div>
            </div>
          )}

          {/* Map */}
          {geoMetrics && geoMetrics.locations.length > 0 && (
            <div className="bg-white rounded-lg shadow mb-6">
              <div className="px-4 py-3 border-b border-gray-200">
                <h2 className="text-lg font-semibold text-gray-900">Performance Map</h2>
              </div>
              <div className="p-4" style={{ height: "450px" }}>
                <GeoMap
                  locations={geoMetrics.locations}
                  globalAvgDuration={geoMetrics.globalAverages.avgDurationMinutes}
                  selectedLocation={selectedLocation}
                  onLocationSelect={setSelectedLocation}
                />
              </div>
            </div>
          )}

          {/* Global Averages Banner */}
          {geoMetrics && (
            <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 mb-6">
              <div className="text-sm font-medium text-blue-800 mb-2">Global Averages (Benchmark)</div>
              <div className="grid grid-cols-2 md:grid-cols-5 gap-4 text-sm">
                <div>
                  <span className="text-blue-600 font-medium">Avg Duration:</span>{" "}
                  <span className="text-blue-900">{geoMetrics.globalAverages.avgDurationMinutes} min</span>
                </div>
                <div>
                  <span className="text-blue-600 font-medium">Median:</span>{" "}
                  <span className="text-blue-900">{geoMetrics.globalAverages.medianDurationMinutes} min</span>
                </div>
                <div>
                  <span className="text-blue-600 font-medium">Avg Min/App:</span>{" "}
                  <span className="text-blue-900">{geoMetrics.globalAverages.avgMinutesPerApp} min</span>
                </div>
                <div>
                  <span className="text-blue-600 font-medium">Avg Throughput:</span>{" "}
                  <span className="text-blue-900">{formatThroughput(geoMetrics.globalAverages.avgThroughputBytesPerSec)}</span>
                </div>
                <div>
                  <span className="text-blue-600 font-medium">DO P2P:</span>{" "}
                  <span className="text-blue-900">
                    {geoMetrics.globalAverages.avgDoPercentPeerCaching > 0
                      ? `${geoMetrics.globalAverages.avgDoPercentPeerCaching}% peers`
                      : "No data"}
                  </span>
                </div>
              </div>
            </div>
          )}

          {/* Heatmap Table */}
          {geoMetrics && (
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="px-4 py-3 border-b border-gray-200">
                <h2 className="text-lg font-semibold text-gray-900">Location Performance</h2>
              </div>
              {sortedLocations.length === 0 ? (
                <div className="p-8 text-center text-gray-500">
                  No geographic data available for the selected time range.
                </div>
              ) : (
                <div className="overflow-x-auto">
                  <table className="min-w-full divide-y divide-gray-200">
                    <thead className="bg-gray-50">
                      <tr>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Location
                        </th>
                        <th
                          className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:text-gray-700"
                          onClick={() => handleSort("sessionCount")}
                        >
                          Sessions <SortIcon col="sessionCount" />
                        </th>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Success
                        </th>
                        <th
                          className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:text-gray-700"
                          onClick={() => handleSort("avgDurationMinutes")}
                        >
                          Avg Duration <SortIcon col="avgDurationMinutes" />
                        </th>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          P95
                        </th>
                        <th
                          className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:text-gray-700"
                          onClick={() => handleSort("appLoadScore")}
                        >
                          App-Load-Score <SortIcon col="appLoadScore" />
                        </th>
                        <th
                          className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:text-gray-700"
                          onClick={() => handleSort("avgThroughputBytesPerSec")}
                        >
                          Throughput <SortIcon col="avgThroughputBytesPerSec" />
                        </th>
                        <th
                          className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:text-gray-700"
                          onClick={() => handleSort("avgDoPercentPeerCaching")}
                        >
                          P2P % <SortIcon col="avgDoPercentPeerCaching" />
                        </th>
                        <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          vs Global
                        </th>
                      </tr>
                    </thead>
                    <tbody className="bg-white divide-y divide-gray-200">
                      {sortedLocations.map((loc) => (
                        <tr
                          key={loc.locationKey}
                          id={`loc-${loc.locationKey.replace(/[^a-zA-Z0-9]/g, "-")}`}
                          className={`hover:bg-gray-50 transition-colors cursor-pointer ${
                            loc.isOutlier ? "border-l-4 border-l-red-400" : ""
                          } ${selectedLocation === loc.locationKey ? "bg-blue-50" : ""}`}
                          onClick={() => {
                            const days = timeRange === "7d" ? 7 : timeRange === "30d" ? 30 : 90;
                            // Carry the selected tenant into the drill-in so a cross-tenant caller (GA override
                            // or delegated/MSP) scopes the session list to it. Empty = GA all-tenants aggregate.
                            const tenantParam = selectedTenantId ? `&tenantId=${selectedTenantId}` : "";
                            router.push(`/geographic-performance/sessions?locationKey=${encodeURIComponent(loc.locationKey)}&days=${days}&groupBy=${groupBy}${tenantParam}`);
                          }}
                          onMouseEnter={() => setSelectedLocation(loc.locationKey)}
                        >
                          <td className="px-4 py-3 text-sm text-gray-900">
                            <div className="flex items-center">
                              <span className="font-medium">{loc.locationKey}</span>
                              {loc.isOutlier && (
                                <span
                                  className={`ml-2 inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                    loc.outlierDirection === "slow"
                                      ? "bg-red-100 text-red-800"
                                      : "bg-green-100 text-green-800"
                                  }`}
                                >
                                  {loc.outlierDirection === "slow" ? "Slow" : "Fast"}
                                </span>
                              )}
                            </div>
                            {loc.avgAppCount > 0 && (
                              <div className="text-xs text-gray-400 mt-0.5">
                                ~{loc.avgAppCount} apps/session &middot; {formatBytes(loc.totalDownloadBytes)} total
                              </div>
                            )}
                          </td>
                          <td className="px-4 py-3 text-sm text-gray-700 font-medium">{loc.sessionCount}</td>
                          <td className="px-4 py-3 text-sm">
                            <span
                              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                loc.successRate >= 90
                                  ? "bg-green-100 text-green-800"
                                  : loc.successRate >= 70
                                  ? "bg-yellow-100 text-yellow-800"
                                  : "bg-red-100 text-red-800"
                              }`}
                            >
                              {loc.successRate}%
                            </span>
                          </td>
                          <td className="px-4 py-3 text-sm">
                            <span
                              className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${durationColor(
                                loc.avgDurationMinutes,
                                geoMetrics.globalAverages.avgDurationMinutes
                              )}`}
                            >
                              {Math.round(loc.avgDurationMinutes)} min
                            </span>
                          </td>
                          <td className="px-4 py-3 text-sm text-gray-500">
                            {loc.p95DurationMinutes > 0 ? `${Math.round(loc.p95DurationMinutes)} min` : "—"}
                          </td>
                          <td className="px-4 py-3 text-sm">
                            <span className={`font-medium ${scoreColor(loc.appLoadScore)}`}>
                              {loc.appLoadScore > 0 ? Math.round(loc.appLoadScore) : "—"}
                            </span>
                            {loc.minutesPerApp > 0 && (
                              <span className="text-xs text-gray-400 ml-1">
                                ({loc.minutesPerApp.toFixed(1)} min/app)
                              </span>
                            )}
                          </td>
                          <td className="px-4 py-3 text-sm text-gray-700">
                            {formatThroughput(loc.avgThroughputBytesPerSec)}
                          </td>
                          <td className="px-4 py-3 text-sm">
                            {loc.doSessionCount > 0 ? (
                              <div title={
                                loc.totalDoBytesFromPeers > 0
                                  ? `LAN: ${formatBytes(loc.totalDoBytesFromLanPeers)} | Group: ${formatBytes(loc.totalDoBytesFromGroupPeers)} | Internet: ${formatBytes(loc.totalDoBytesFromInternetPeers)}`
                                  : `${loc.doSessionCount} session(s) with DO data`
                              }>
                                <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                  loc.avgDoPercentPeerCaching >= 50 ? "bg-green-100 text-green-800" :
                                  loc.avgDoPercentPeerCaching >= 10 ? "bg-yellow-100 text-yellow-800" :
                                  "bg-gray-100 text-gray-600"
                                }`}>
                                  {loc.avgDoPercentPeerCaching > 0 ? `${loc.avgDoPercentPeerCaching}%` : "0%"}
                                </span>
                                <span className="text-xs text-gray-400 ml-1">
                                  ({loc.doSessionCount})
                                </span>
                              </div>
                            ) : (
                              <span className="text-gray-300">—</span>
                            )}
                          </td>
                          <td className="px-4 py-3 text-sm">
                            <div className="flex items-center justify-between">
                              {loc.durationVsGlobalPct !== 0 ? (
                                <span
                                  className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                                    loc.durationVsGlobalPct > 0
                                      ? "bg-red-50 text-red-700"
                                      : "bg-green-50 text-green-700"
                                  }`}
                                >
                                  {loc.durationVsGlobalPct > 0 ? "+" : ""}
                                  {loc.durationVsGlobalPct.toFixed(0)}%
                                </span>
                              ) : (
                                <span className="text-gray-400">—</span>
                              )}
                              <svg className="w-4 h-4 text-gray-400 ml-2 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                              </svg>
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}

          {/* No data state */}
          {!geoMetrics && !loading && (
            <div className="bg-white rounded-lg shadow p-8 text-center text-gray-500">
              Failed to load geographic metrics. Please try again later.
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}
