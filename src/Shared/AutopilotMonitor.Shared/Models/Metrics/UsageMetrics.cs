#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Platform usage metrics response
    /// </summary>
    public class PlatformUsageMetrics
    {
        /// <summary>
        /// Session metrics
        /// </summary>
        public SessionMetrics Sessions { get; set; } = new();

        /// <summary>
        /// Tenant metrics
        /// </summary>
        public TenantMetrics Tenants { get; set; } = new();

        /// <summary>
        /// User metrics (requires Entra ID authentication)
        /// </summary>
        public UserMetrics Users { get; set; } = new();

        /// <summary>
        /// Performance metrics
        /// </summary>
        public PerformanceMetrics Performance { get; set; } = new();

        /// <summary>
        /// Hardware metrics
        /// </summary>
        public HardwareMetrics Hardware { get; set; } = new();

        /// <summary>
        /// Deployment type metrics (User Driven vs White Glove)
        /// </summary>
        public DeploymentTypeMetrics DeploymentTypes { get; set; } = new();

        /// <summary>
        /// App and script count metrics
        /// </summary>
        public AppScriptMetrics AppScripts { get; set; } = new();

        /// <summary>
        /// Platform statistics (cumulative since release)
        /// </summary>
        public PlatformStats? PlatformStats { get; set; }

        /// <summary>
        /// When these metrics were computed
        /// </summary>
        public DateTime ComputedAt { get; set; }

        /// <summary>
        /// How long it took to compute (milliseconds)
        /// </summary>
        public int ComputeDurationMs { get; set; }

        /// <summary>
        /// Whether result is from cache
        /// </summary>
        public bool FromCache { get; set; }

        /// <summary>
        /// Time window (in days) the metrics were computed over.
        /// </summary>
        public int WindowDays { get; set; }
    }

    public class SessionMetrics
    {
        public int Total { get; set; }
        public int Today { get; set; }
        public int Last7Days { get; set; }
        public int Last30Days { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int InProgress { get; set; }
        /// <summary>
        /// Terminal, non-failure sessions (timeout reclassification): the sweep classified them as
        /// Incomplete instead of Failed. Surfaced as its own count and deliberately excluded from
        /// <see cref="SuccessRate"/> (denominator = Succeeded + Failed only), mirroring SessionStats
        /// and FleetHealthStats. See docs/design/enrollment-status-reclassification.md §5.
        /// </summary>
        public int Incomplete { get; set; }
        public double SuccessRate { get; set; }
    }

    public class TenantMetrics
    {
        public int Total { get; set; }
        public int Active7Days { get; set; }
        public int Active30Days { get; set; }
    }

    public class UserMetrics
    {
        /// <summary>
        /// Total unique users (available after Entra ID integration)
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Daily logins across all users
        /// </summary>
        public int DailyLogins { get; set; }

        /// <summary>
        /// Active users in last 7 days
        /// </summary>
        public int Active7Days { get; set; }

        /// <summary>
        /// Active users in last 30 days
        /// </summary>
        public int Active30Days { get; set; }

        /// <summary>
        /// Note about availability
        /// </summary>
        public string Note { get; set; } = "";
    }

    public class PerformanceMetrics
    {
        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }
        public double P99DurationMinutes { get; set; }

        /// <summary>Number of sessions contributing to the duration distribution (after the &gt;0 filter).</summary>
        public int SampleCount { get; set; }

        /// <summary>
        /// Number of sessions whose raw duration exceeded the clamp ceiling and were capped before
        /// aggregation. A non-zero value flags stuck/non-terminal sessions skewing the window — the
        /// percentiles above are computed on the clamped values, not the runaway wall-clock duration.
        /// </summary>
        public int ClampedSessionCount { get; set; }
    }

    public class HardwareMetrics
    {
        public List<HardwareCount> TopManufacturers { get; set; } = new();
        public List<HardwareCount> TopModels { get; set; } = new();
    }

    public class HardwareCount
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class DeploymentTypeMetrics
    {
        public int UserDriven { get; set; }
        public int WhiteGlove { get; set; }
        public double UserDrivenPercentage { get; set; }
        public double WhiteGlovePercentage { get; set; }
    }

    public class AppScriptMetrics
    {
        public double AvgAppsPerSession { get; set; }
        public int TotalUniqueApps { get; set; }
        public double AvgPlatformScriptsPerSession { get; set; }
        public double AvgRemediationScriptsPerSession { get; set; }
        public int TotalPlatformScripts { get; set; }
        public int TotalRemediationScripts { get; set; }
    }
}
