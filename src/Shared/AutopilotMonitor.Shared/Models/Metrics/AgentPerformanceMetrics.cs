using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Agent performance / efficiency wire DTOs, moved verbatim from the Functions-local
    // service files (PlatformMetricsService.cs / AgentEfficiencyMetricsService.cs) so the
    // manifest exports them: property names and DECLARATION ORDER are the wire contract
    // and must not change in a move-only refactor.

    /// <summary>Response of <c>GET global/metrics/platform</c> (GetGlobalPlatformMetrics).</summary>
    public class PlatformAgentMetricsResponse : IApiResponse
    {
        public List<SessionAgentMetric> Sessions { get; set; } = new();
        public DeliveryLatencyMetrics? DeliveryLatency { get; set; }
        public CrashRateMetrics? CrashRate { get; set; }
        public DateTime ComputedAt { get; set; }
        public int ComputeDurationMs { get; set; }
        public bool FromCache { get; set; }
        public int WindowDays { get; set; }
        public int SessionLimit { get; set; }
        /// <summary>
        /// Sessions the scan actually covered in the window (before the has-snapshots filter
        /// that shapes <see cref="Sessions"/>). Callers must compare THIS against
        /// <see cref="SessionLimit"/> to decide whether the window was truncated —
        /// <c>Sessions.Count</c> understates truncation on fleets where many sessions
        /// emit no agent_metrics_snapshot.
        /// </summary>
        public int SessionsScanned { get; set; }
    }

    public class SessionAgentMetric
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? StartedAt { get; set; }
        public string? Status { get; set; }
        public string? AgentVersion { get; set; }
        public int SnapshotCount { get; set; }
        public double TotalBytesUp { get; set; }
        public double TotalBytesDown { get; set; }
        public double TotalRequests { get; set; }
        public double AvgCpu { get; set; }
        public double MaxCpu { get; set; }
        public double AvgWorkingSet { get; set; }
        public double MaxWorkingSet { get; set; }
        public double AvgPrivateBytes { get; set; }
        public double AvgLatency { get; set; }
        public double AvgSpoolDepth { get; set; }
        public double MaxSpoolDepth { get; set; }
        public double PeakSpoolDepth { get; set; }
        public double MaxSpoolFileBytes { get; set; }
        public double TotalEventsEmitted { get; set; }
        public bool SpoolPressureDetected { get; set; }
    }

    public class DeliveryLatencyMetrics
    {
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double P99Ms { get; set; }
        public double AvgMs { get; set; }
        public int SampleCount { get; set; }
        public double ClockSkewPercent { get; set; }
    }

    public class CrashRateMetrics
    {
        public int TotalStarts { get; set; }
        public int CleanExits { get; set; }
        public int ExceptionCrashes { get; set; }
        public int HardKills { get; set; }
        public int RebootKills { get; set; }
        public int FirstRuns { get; set; }
        public double CrashRatePercent { get; set; }
        public List<CrashExceptionSummary> TopExceptions { get; set; } = new();
    }

    public class CrashExceptionSummary
    {
        public string ExceptionType { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>Response of <c>GET global/metrics/agent-efficiency</c> (GetGlobalAgentEfficiency).</summary>
    public class AgentEfficiencyMetricsResponse : IApiResponse
    {
        public int WindowDays { get; set; }
        public int SessionLimit { get; set; }
        /// <summary>Echo of the requested tenant filter; null = cross-tenant aggregate.</summary>
        public string? TenantId { get; set; }
        /// <summary>Sessions the scan covered — compare against <see cref="SessionLimit"/> for truncation.</summary>
        public int SessionsScanned { get; set; }
        public int SessionsWithSnapshots { get; set; }
        public List<AgentVersionEfficiency> ByVersion { get; set; } = new();
        public AgentVersionEfficiency? Overall { get; set; }
        public DateTime ComputedAt { get; set; }
        public int ComputeDurationMs { get; set; }
        public bool FromCache { get; set; }
    }

    public class AgentVersionEfficiency
    {
        /// <summary>Null on the cross-version "overall" bucket (omitted on the wire).</summary>
        public string? AgentVersion { get; set; }
        public int SessionsScanned { get; set; }
        public int SessionsWithSnapshots { get; set; }
        public int SpoolPressureSessions { get; set; }
        public PercentileStats? AvgCpuPercent { get; set; }
        public PercentileStats? MaxCpuPercent { get; set; }
        public PercentileStats? MaxWorkingSetMb { get; set; }
        public PercentileStats? MaxPrivateBytesMb { get; set; }
        public PercentileStats? MaxThreadCount { get; set; }
        public PercentileStats? MaxHandleCount { get; set; }
        public PercentileStats? MaxSpoolDepth { get; set; }
        public PercentileStats? MaxSpoolFileBytes { get; set; }
        public PercentileStats? ApiLatencyMs { get; set; }
        public PercentileStats? ApiRequestCount { get; set; }
        public CrashRateMetrics? CrashRate { get; set; }
        public List<EfficiencyOffender>? TopOffenders { get; set; }
    }

    /// <summary>Distribution of a per-session statistic across a version bucket.</summary>
    public class PercentileStats
    {
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double Max { get; set; }
        public double Avg { get; set; }
        public int SampleCount { get; set; }
    }

    public class EfficiencyOffender
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string Dimension { get; set; } = string.Empty;
        public double Value { get; set; }
    }
}
