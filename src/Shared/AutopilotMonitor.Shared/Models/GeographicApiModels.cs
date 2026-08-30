using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response containing geographic performance metrics aggregated by location.
    /// </summary>
    public class GeographicMetricsResponse
    {
        public bool Success { get; set; }
        public List<LocationMetrics> Locations { get; set; } = new();
        public GlobalAverages GlobalAverages { get; set; } = new();
        public DateTime ComputedAt { get; set; }
        public int TotalSessions { get; set; }
        public int LocationsWithData { get; set; }
        /// <summary>Whether geo-location collection is enabled for this tenant</summary>
        public bool GeoLocationEnabled { get; set; } = true;
    }

    /// <summary>
    /// Performance metrics for a single geographic location.
    /// </summary>
    public class LocationMetrics
    {
        public string LocationKey { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Loc { get; set; } = string.Empty;

        public int SessionCount { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double SuccessRate { get; set; }

        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }

        /// <summary>Average number of apps installed per session at this location</summary>
        public double AvgAppCount { get; set; }
        /// <summary>Average minutes per app (AvgDurationMinutes / AvgAppCount)</summary>
        public double MinutesPerApp { get; set; }
        /// <summary>Normalized score: 100 = global median, lower is better</summary>
        public double AppLoadScore { get; set; }

        /// <summary>Average download throughput in bytes/sec at this location</summary>
        public double AvgThroughputBytesPerSec { get; set; }
        public long TotalDownloadBytes { get; set; }

        /// <summary>Percentage difference from global avg duration (negative = faster)</summary>
        public double DurationVsGlobalPct { get; set; }
        /// <summary>Percentage difference from global avg throughput (positive = faster)</summary>
        public double ThroughputVsGlobalPct { get; set; }

        /// <summary>
        /// Average agent→backend HTTP round-trip (ms) at this location, weighted per session by
        /// its request count. 0 when no session here carries latency data (pre-feature agents).
        /// A single corrupt session average (e.g. a request spanning a sleep/hibernate) can
        /// dominate this figure — use <see cref="MedianApiLatencyMs"/> as the display statistic.
        /// </summary>
        public double AvgApiLatencyMs { get; set; }
        /// <summary>
        /// Median of the per-session average agent→backend round-trips (ms) at this location —
        /// robust against outlier sessions. 0 when no session here carries latency data.
        /// </summary>
        public double MedianApiLatencyMs { get; set; }
        /// <summary>Sessions at this location that carry API-latency data</summary>
        public int ApiLatencySessionCount { get; set; }
        /// <summary>Percentage difference from global median API latency (positive = slower/farther)</summary>
        public double ApiLatencyVsGlobalPct { get; set; }

        public bool IsOutlier { get; set; }
        /// <summary>"fast", "slow", or null</summary>
        public string? OutlierDirection { get; set; }

        // Delivery Optimization metrics
        /// <summary>Sessions at this location that have DO telemetry data</summary>
        public int DoSessionCount { get; set; }
        /// <summary>Weighted percentage of bytes from peers (0-100), computed from total peer/total DO bytes</summary>
        public double AvgDoPercentPeerCaching { get; set; }
        /// <summary>Total bytes downloaded from all peer sources</summary>
        public long TotalDoBytesFromPeers { get; set; }
        /// <summary>Total bytes downloaded from HTTP/CDN</summary>
        public long TotalDoBytesFromHttp { get; set; }
        /// <summary>Bytes from LAN peers</summary>
        public long TotalDoBytesFromLanPeers { get; set; }
        /// <summary>Bytes from group peers</summary>
        public long TotalDoBytesFromGroupPeers { get; set; }
        /// <summary>Bytes from internet peers</summary>
        public long TotalDoBytesFromInternetPeers { get; set; }
        /// <summary>Bytes from link-local peers (same subnet)</summary>
        public long TotalDoBytesFromLinkLocalPeers { get; set; }
        /// <summary>Bytes served from a Microsoft Connected Cache (MCC) — separate from BytesFromPeers</summary>
        public long TotalDoBytesFromCacheServer { get; set; }
    }

    /// <summary>
    /// Session row returned by the geographic drilldown endpoint. Extends <see cref="SessionSummary"/>
    /// with per-session Delivery Optimization aggregates so the user can troubleshoot DO usage
    /// without leaving the drilldown view.
    /// </summary>
    public class LocationSessionRow : SessionSummary
    {
        /// <summary>True if any app in this session has DO telemetry (DoDownloadMode &gt;= 0).</summary>
        public bool HasDoTelemetry { get; set; }
        /// <summary>Number of apps in this session that have DO telemetry.</summary>
        public int DoAppCount { get; set; }
        /// <summary>Total number of app install summaries recorded for this session.</summary>
        public int TotalAppCount { get; set; }
        /// <summary>Weighted peer-caching percentage for this session (0-100).</summary>
        public double DoPercentPeerCaching { get; set; }
        public long DoBytesFromPeers { get; set; }
        public long DoBytesFromHttp { get; set; }
        public long DoTotalBytesDownloaded { get; set; }
        public long DoBytesFromLanPeers { get; set; }
        public long DoBytesFromGroupPeers { get; set; }
        public long DoBytesFromInternetPeers { get; set; }
        public long DoBytesFromLinkLocalPeers { get; set; }
        public long DoBytesFromCacheServer { get; set; }

        public static LocationSessionRow From(SessionSummary s, DoAggregator.DoAggregate a, int totalAppCount)
        {
            return new LocationSessionRow
            {
                // SessionSummary fields
                SessionId = s.SessionId,
                TenantId = s.TenantId,
                SerialNumber = s.SerialNumber,
                DeviceName = s.DeviceName,
                Manufacturer = s.Manufacturer,
                Model = s.Model,
                StartedAt = s.StartedAt,
                CompletedAt = s.CompletedAt,
                CurrentPhase = s.CurrentPhase,
                CurrentPhaseDetail = s.CurrentPhaseDetail,
                Status = s.Status,
                FailureReason = s.FailureReason,
                FailureSource = s.FailureSource,
                EspSoftFailure = s.EspSoftFailure,
                CompletionSource = s.CompletionSource,
                EventCount = s.EventCount,
                DurationSeconds = s.DurationSeconds,
                EnrollmentType = s.EnrollmentType,
                DiagnosticsBlobName = s.DiagnosticsBlobName,
                LastEventAt = s.LastEventAt,
                IsPreProvisioned = s.IsPreProvisioned,
                ResumedAt = s.ResumedAt,
                StalledAt = s.StalledAt,
                IsHybridJoin = s.IsHybridJoin,
                OsName = s.OsName,
                OsBuild = s.OsBuild,
                OsDisplayVersion = s.OsDisplayVersion,
                OsEdition = s.OsEdition,
                OsLanguage = s.OsLanguage,
                IsUserDriven = s.IsUserDriven,
                AgentVersion = s.AgentVersion,
                ImeAgentVersion = s.ImeAgentVersion,
                GeoCountry = s.GeoCountry,
                GeoRegion = s.GeoRegion,
                GeoCity = s.GeoCity,
                GeoLoc = s.GeoLoc,
                AvgApiLatencyMs = s.AvgApiLatencyMs,
                ApiRequestCount = s.ApiRequestCount,
                PlatformScriptCount = s.PlatformScriptCount,
                RemediationScriptCount = s.RemediationScriptCount,
                PendingActionsJson = s.PendingActionsJson,
                PendingActionsQueuedAt = s.PendingActionsQueuedAt,
                // DO aggregate
                HasDoTelemetry = a.HasTelemetry,
                DoAppCount = a.DoAppCount,
                TotalAppCount = totalAppCount,
                DoPercentPeerCaching = System.Math.Round(a.PercentPeerCaching, 1),
                DoBytesFromPeers = a.BytesFromPeers,
                DoBytesFromHttp = a.BytesFromHttp,
                DoTotalBytesDownloaded = a.TotalBytesDownloaded,
                DoBytesFromLanPeers = a.BytesFromLanPeers,
                DoBytesFromGroupPeers = a.BytesFromGroupPeers,
                DoBytesFromInternetPeers = a.BytesFromInternetPeers,
                DoBytesFromLinkLocalPeers = a.BytesFromLinkLocalPeers,
                DoBytesFromCacheServer = a.BytesFromCacheServer,
            };
        }
    }

    /// <summary>
    /// Full geographic drilldown envelope shared by GetGeographicLocationSessions and
    /// GetGlobalGeographicLocationSessions when the caller passes <c>?full=1</c>.
    /// </summary>
    // Declaration order == wire order.
    public class GeographicLocationSessionsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<LocationSessionRow> Sessions { get; set; } = default!;
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Default (lean) geographic drilldown envelope shared by GetGeographicLocationSessions
    /// and GetGlobalGeographicLocationSessions — per-row payload an order of magnitude
    /// smaller than the full <see cref="LocationSessionRow"/> shape.
    /// </summary>
    // Declaration order == wire order.
    public class GeographicLocationSessionsLeanResponse : IApiResponse
    {
        public bool Success { get; set; }
        public IReadOnlyList<LocationSessionLeanRow> Sessions { get; set; } = default!;
        public int TotalCount { get; set; }
    }

    /// <summary>
    /// Lean projection of <see cref="LocationSessionRow"/> used as the default drilldown row.
    /// Fields chosen to support the typical MCP triage flow (which session, when, where, how
    /// big a deal, was DO active). Built by <c>GetGeographicLocationSessionsFunction.ToLeanRow</c>.
    /// </summary>
    // Declaration order == wire order.
    public class LocationSessionLeanRow
    {
        public string SessionId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string SerialNumber { get; set; } = default!;
        public string DeviceName { get; set; } = default!;
        public string Manufacturer { get; set; } = default!;
        public string Model { get; set; } = default!;
        public DateTime StartedAt { get; set; }

        /// <summary>Absent while the session has not completed.</summary>
        public DateTime? CompletedAt { get; set; }

        public SessionStatus Status { get; set; }
        public string FailureReason { get; set; } = default!;

        /// <summary>Absent while the session carries no authoritative duration.</summary>
        public int? DurationSeconds { get; set; }

        public string EnrollmentType { get; set; } = default!;
        public string GeoCountry { get; set; } = default!;
        public string GeoCity { get; set; } = default!;
        public int TotalAppCount { get; set; }
        public bool HasDoTelemetry { get; set; }

        /// <summary>Weighted peer-caching percentage for this session (0-100, one decimal).</summary>
        public double DoPercentPeerCaching { get; set; }
    }

    /// <summary>
    /// Global average benchmarks for geographic comparison.
    /// </summary>
    public class GlobalAverages
    {
        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double AvgMinutesPerApp { get; set; }
        public double AvgThroughputBytesPerSec { get; set; }
        public double StdDevDurationMinutes { get; set; }
        /// <summary>Global request-weighted average agent→backend API latency (ms); 0 = no data yet</summary>
        public double AvgApiLatencyMs { get; set; }
        /// <summary>Global median of per-session average API latency (ms), robust against outliers; 0 = no data yet</summary>
        public double MedianApiLatencyMs { get; set; }
        /// <summary>Global weighted average peer caching percentage</summary>
        public double AvgDoPercentPeerCaching { get; set; }
        /// <summary>Total peer bytes across all locations with DO data</summary>
        public long TotalDoBytesFromPeers { get; set; }
        /// <summary>Total HTTP bytes across all locations with DO data</summary>
        public long TotalDoBytesFromHttp { get; set; }
    }
}
