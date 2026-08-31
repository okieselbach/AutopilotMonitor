using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>
    /// Response of <c>GET metrics/app</c> and <c>GET global/metrics/app</c>: per-app install
    /// health over the requested window (slowest apps by average FINAL-attempt duration, top
    /// failing apps) plus the fleet Delivery Optimization rollup.
    /// </summary>
    public class AppMetricsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public int TotalApps { get; set; }
        public int TotalInstalls { get; set; }
        public int TotalSkipped { get; set; }
        public int TotalUnmeasured { get; set; }
        /// <summary>Rows excluded from per-app groups because their name-keyed row merged distinct appIds.</summary>
        public int TotalCollisionExcluded { get; set; }
        public IReadOnlyList<AppMetricsAppGroup> SlowestApps { get; set; } = default!;
        public IReadOnlyList<AppMetricsAppGroup> TopFailingApps { get; set; } = default!;
        public AppMetricsDeliveryOptimization DeliveryOptimization { get; set; } = default!;
    }

    /// <summary>One app's aggregate across its install rows in the window.</summary>
    public class AppMetricsAppGroup
    {
        public string AppName { get; set; } = string.Empty;
        public int TotalInstalls { get; set; }
        public int Succeeded { get; set; }
        public int Skipped { get; set; }
        public int Unmeasured { get; set; }
        public int Failed { get; set; }
        /// <summary>Failed / (failed + succeeded) as a percentage; skips never count as attempts.</summary>
        public double FailureRate { get; set; }
        /// <summary>Average measured FINAL-attempt duration (whole seconds); 0 with no measured installs.</summary>
        public double AvgDurationSeconds { get; set; }
        public int MaxDurationSeconds { get; set; }
        public int MeasuredInstalls { get; set; }
        public long AvgDownloadBytes { get; set; }
        public long DoTotalBytesDownloaded { get; set; }
        public long DoBytesFromPeers { get; set; }
        public long DoBytesFromCacheServer { get; set; }
        public long DoBytesFromHttp { get; set; }
        public double PeerOffloadPercent { get; set; }
        /// <summary>Top 3 failure codes by count, descending.</summary>
        public IReadOnlyList<AppFailureCodeCount> TopFailureCodes { get; set; } = default!;
    }

    /// <summary>One failure code with its occurrence count.</summary>
    public class AppFailureCodeCount
    {
        public string Code { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    /// <summary>Fleet-wide Delivery Optimization rollup across every install row in the window.</summary>
    public class AppMetricsDeliveryOptimization
    {
        public long TotalBytesDownloaded { get; set; }
        public long FromPeers { get; set; }
        public long FromCacheServer { get; set; }
        public long FromHttp { get; set; }
        /// <summary>Share of bytes not pulled from the CDN (peers + Microsoft Connected Cache), 0-100 one decimal.</summary>
        public double PeerOffloadPercent { get; set; }
    }
}
