#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Platform usage metrics response
    /// </summary>
    public class PlatformUsageMetrics : IApiResponse
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
        /// Sessions in the window by enrollment type, join type and Autopilot deployment mode
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
        /// <summary>
        /// Cumulative enrollment sessions since the tenant signed up (retention-independent
        /// per-tenant counter, see <see cref="TenantStats"/>). Null on the platform-wide
        /// (all-tenants) metrics, which carry the cumulative figure in PlatformStats instead.
        /// </summary>
        public long? TotalAllTime { get; set; }
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
        /// and FleetHealthStats. See tasks/enrollment-status-reclassification.md §5.
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

    /// <summary>
    /// One manufacturer or model in the window. The lists rank by sessions: a device that was
    /// reset and enrolled again adds one session per enrollment but stays one device.
    /// </summary>
    public class HardwareCount
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Enrollment sessions in the window.</summary>
        public int Count { get; set; }

        /// <summary>
        /// Distinct devices behind those sessions, told apart by serial number per tenant. A session
        /// without a usable serial number counts as its own device, so this never exceeds <see cref="Count"/>.
        /// </summary>
        public int DeviceCount { get; set; }

        /// <summary>Share of all sessions in the window.</summary>
        public double Percentage { get; set; }
    }

    /// <summary>
    /// Sessions in the window in three groups, each summing to <see cref="SessionMetrics.Total"/>:
    /// enrollment type (<see cref="AutopilotV1"/> + <see cref="DevicePreparation"/>), join type
    /// (<see cref="EntraJoin"/> + <see cref="HybridJoin"/>) and Autopilot deployment mode
    /// (<see cref="UserDriven"/> + <see cref="SelfDeploying"/> + <see cref="WhiteGlove"/>, summing to
    /// <see cref="AutopilotV1"/>). Every percentage is the share of all sessions in the window.
    /// </summary>
    public class DeploymentTypeMetrics
    {
        /// <summary>Windows Autopilot sessions (classic, enrollment type v1).</summary>
        public int AutopilotV1 { get; set; }
        public double AutopilotV1Percentage { get; set; }

        /// <summary>Windows Autopilot device preparation sessions (enrollment type v2).</summary>
        public int DevicePreparation { get; set; }
        public double DevicePreparationPercentage { get; set; }

        /// <summary>Sessions whose Autopilot profile does not ask for a hybrid join.</summary>
        public int EntraJoin { get; set; }
        public double EntraJoinPercentage { get; set; }

        /// <summary>Sessions whose Autopilot profile asks for a hybrid join.</summary>
        public int HybridJoin { get; set; }
        public double HybridJoinPercentage { get; set; }

        /// <summary>Autopilot v1 sessions that are neither pre-provisioned nor self-deploying.</summary>
        public int UserDriven { get; set; }
        public double UserDrivenPercentage { get; set; }

        /// <summary>
        /// Autopilot v1 sessions with a self-deploying profile that are not pre-provisioned. Sessions
        /// whose agent predates the profile flag count as user-driven.
        /// </summary>
        public int SelfDeploying { get; set; }
        public double SelfDeployingPercentage { get; set; }

        /// <summary>Pre-provisioned (White Glove) sessions.</summary>
        public int WhiteGlove { get; set; }
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
