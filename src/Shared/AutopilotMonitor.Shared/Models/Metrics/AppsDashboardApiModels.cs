using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order. App Dashboard endpoints (apps/list, apps/{app}/analytics,
    // apps/{app}/sessions and their global variants) — lifted from the Functions-local
    // anonymous builders in AppsAnalyticsHelper so the manifest exports them.

    /// <summary>
    /// Response of <c>GET apps/list</c> and <c>GET global/apps/list</c>. Legacy mode (no
    /// pageSize) returns the full array with the paging keys absent; opt-in pagination adds
    /// count/offset/pageSize and a nextLink while more pages remain.
    /// </summary>
    public class AppsListResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int TotalApps { get; set; }
        public int TotalInstalls { get; set; }
        /// <summary>Rows excluded because their name-keyed row merged distinct appIds.</summary>
        public int CollisionExcluded { get; set; }
        public int WindowDays { get; set; }
        /// <summary>Rows on this page; absent in legacy full-array mode.</summary>
        public int? Count { get; set; }
        /// <summary>Absent in legacy full-array mode.</summary>
        public int? Offset { get; set; }
        /// <summary>Absent in legacy full-array mode.</summary>
        public int? PageSize { get; set; }
        public IReadOnlyList<AppsListItem> Apps { get; set; } = default!;
        /// <summary>Next-page link; absent in legacy mode and on the last page.</summary>
        public string? NextLink { get; set; }
    }

    /// <summary>One app row of the apps list (failed desc, then failure rate, then name).</summary>
    public class AppsListItem
    {
        public string AppName { get; set; } = string.Empty;
        public string AppType { get; set; } = string.Empty;
        public int TotalInstalls { get; set; }
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
        public int Unmeasured { get; set; }
        public int Failed { get; set; }
        public double FailureRate { get; set; }
        public double AvgDurationSeconds { get; set; }
        public int MaxDurationSeconds { get; set; }
        public long AvgDownloadBytes { get; set; }
        /// <summary>"improving" | "worsening" | "stable".</summary>
        public string Trend { get; set; } = string.Empty;
        /// <summary>Failure-rate delta between window halves; absent when either half has under 5 finished installs.</summary>
        public double? TrendDelta { get; set; }
        public DateTime LastSeenAt { get; set; }
    }

    /// <summary>Response of <c>GET apps/{appName}/analytics</c> and its global variant.</summary>
    public class AppAnalyticsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string AppName { get; set; } = string.Empty;
        public string AppType { get; set; } = string.Empty;
        public int WindowDays { get; set; }
        public int CollisionExcluded { get; set; }
        /// <summary>"day" (windows up to 30 days) or "week".</summary>
        public string Bucket { get; set; } = string.Empty;
        public AppAnalyticsSummary Summary { get; set; } = default!;
        public IReadOnlyList<AppAnalyticsTimeBucket> TimeSeries { get; set; } = default!;
        public IReadOnlyList<AppVersionBreakdownItem> VersionBreakdown { get; set; } = default!;
        public IReadOnlyList<AppInstallerPhaseCount> InstallerPhaseBreakdown { get; set; } = default!;
        public IReadOnlyList<AppAnalyticsFailureCode> TopFailureCodes { get; set; } = default!;
        /// <summary>Succeeded installs whose detection re-check reported NotDetected.</summary>
        public int DetectionLiesCount { get; set; }
        public IReadOnlyList<AppDeviceModelBreakdownItem> DeviceModelBreakdown { get; set; } = default!;
        /// <summary>Active duration-regression episodes for this app (tracker rows).</summary>
        public IReadOnlyList<AppVersionRegressionAlert> VersionRegressions { get; set; } = default!;
    }

    /// <summary>Headline aggregate of one app's analytics window.</summary>
    public class AppAnalyticsSummary
    {
        public int TotalInstalls { get; set; }
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
        public int Unmeasured { get; set; }
        public int Failed { get; set; }
        public double FailureRate { get; set; }
        public double AvgDurationSeconds { get; set; }
        public int P95DurationSeconds { get; set; }
        public long AvgDownloadBytes { get; set; }
        /// <summary>"improving" | "worsening" | "stable".</summary>
        public string Trend { get; set; } = string.Empty;
        /// <summary>Absent when either window half has under 5 finished installs.</summary>
        public double? TrendDelta { get; set; }
        /// <summary>Share of installs with AttemptNumber &gt; 1 (0-1, three decimals).</summary>
        public double FlakinessScore { get; set; }
    }

    /// <summary>One day/week bucket of the analytics time series.</summary>
    public class AppAnalyticsTimeBucket
    {
        public DateTime BucketStart { get; set; }
        public int Installs { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double FailureRate { get; set; }
        public double AvgDurationSeconds { get; set; }
    }

    /// <summary>Per-app-version aggregate (installs descending).</summary>
    public class AppVersionBreakdownItem
    {
        public string AppVersion { get; set; } = string.Empty;
        public int Installs { get; set; }
        public int Failed { get; set; }
        public double FailureRate { get; set; }
        public int MeasuredInstalls { get; set; }
        public int MedianDurationSeconds { get; set; }
        public int P95DurationSeconds { get; set; }
    }

    /// <summary>Failed installs per installer phase (descending).</summary>
    public class AppInstallerPhaseCount
    {
        public string Phase { get; set; } = string.Empty;
        public int Failed { get; set; }
    }

    /// <summary>One of the top 5 failure codes of the analytics window.</summary>
    public class AppAnalyticsFailureCode
    {
        public string Code { get; set; } = string.Empty;
        /// <summary>First observed exit code; absent when none of the rows carried one.</summary>
        public int? ExitCode { get; set; }
        public int Count { get; set; }
        public string SampleMessage { get; set; } = string.Empty;
    }

    /// <summary>Per device-model failure aggregate (lift vs the app's baseline rate, descending).</summary>
    public class AppDeviceModelBreakdownItem
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int Installs { get; set; }
        public int Failed { get; set; }
        public double FailureRate { get; set; }
        public double LiftVsBaseline { get; set; }
    }

    /// <summary>Response of <c>GET apps/{appName}/sessions</c> and its global variant (offset-paged).</summary>
    public class AppSessionsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int Total { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public IReadOnlyList<AppSessionItem> Items { get; set; } = default!;
    }

    /// <summary>One install row of the app-sessions drilldown (failed first, then in-progress, newest first).</summary>
    public class AppSessionItem
    {
        public string SessionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string InstallerPhase { get; set; } = string.Empty;
        public string FailureCode { get; set; } = string.Empty;
        /// <summary>Absent when the install carried no exit code.</summary>
        public int? ExitCode { get; set; }
        public int AttemptNumber { get; set; }
        public DateTime StartedAt { get; set; }
        public int DurationSeconds { get; set; }
        /// <summary>2+ = the IME processed this app in multiple passes (device-ESP evaluation + real install).</summary>
        public int InstallPassCount { get; set; }
    }
}
