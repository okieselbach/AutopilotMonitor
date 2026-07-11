using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Daily snapshot of usage metrics
    /// Stored in Azure Table Storage for historical analysis and trending
    /// PartitionKey = Date (YYYY-MM-DD format)
    /// RowKey = TenantId (or "global" for cross-tenant aggregates)
    /// </summary>
    public class UsageMetricsSnapshot
    {
        /// <summary>
        /// Date of the snapshot (YYYY-MM-DD) - PartitionKey
        /// </summary>
        public string Date { get; set; } = string.Empty;

        /// <summary>
        /// Tenant ID or "global" for cross-tenant - RowKey
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// When this snapshot was computed
        /// </summary>
        public DateTime ComputedAt { get; set; }

        /// <summary>
        /// How long it took to compute (milliseconds)
        /// </summary>
        public int ComputeDurationMs { get; set; }

        // ===== SESSION METRICS =====

        /// <summary>
        /// Total sessions for this day
        /// </summary>
        public int SessionsTotal { get; set; }

        /// <summary>
        /// Sessions that succeeded
        /// </summary>
        public int SessionsSucceeded { get; set; }

        /// <summary>
        /// Sessions that failed
        /// </summary>
        public int SessionsFailed { get; set; }

        /// <summary>
        /// Sessions still in progress (at end of day)
        /// </summary>
        public int SessionsInProgress { get; set; }

        /// <summary>
        /// Terminal, non-failure sessions (timeout reclassification): classified Incomplete rather
        /// than Failed. Tracked separately and excluded from <see cref="SessionsSuccessRate"/>
        /// (denominator = Succeeded + Failed). Historical rows written before this field existed
        /// deserialize to 0. See tasks/enrollment-status-reclassification.md §5.
        /// </summary>
        public int SessionsIncomplete { get; set; }

        /// <summary>
        /// Success rate percentage
        /// </summary>
        public double SessionsSuccessRate { get; set; }

        // ===== PERFORMANCE METRICS =====

        /// <summary>
        /// Average session duration in minutes
        /// </summary>
        public double AvgDurationMinutes { get; set; }

        /// <summary>
        /// Median session duration in minutes
        /// </summary>
        public double MedianDurationMinutes { get; set; }

        /// <summary>
        /// P95 session duration in minutes
        /// </summary>
        public double P95DurationMinutes { get; set; }

        /// <summary>
        /// P99 session duration in minutes
        /// </summary>
        public double P99DurationMinutes { get; set; }

        // ===== TENANT METRICS (only for "global" row) =====

        /// <summary>
        /// Number of unique tenants active on this day (only for global)
        /// </summary>
        public int UniqueTenants { get; set; }

        // ===== USER METRICS (future - requires Entra ID) =====

        /// <summary>
        /// Number of unique users active on this day
        /// </summary>
        public int UniqueUsers { get; set; }

        /// <summary>
        /// Total login events on this day
        /// </summary>
        public int LoginCount { get; set; }

        // ===== DEPLOYMENT TYPE METRICS =====

        /// <summary>
        /// Number of user-driven deployment sessions
        /// </summary>
        public int UserDrivenSessions { get; set; }

        /// <summary>
        /// Number of white glove (pre-provisioned) deployment sessions
        /// </summary>
        public int WhiteGloveSessions { get; set; }

        // ===== HARDWARE METRICS (Top 5 only for storage efficiency) =====

        /// <summary>
        /// Top 5 manufacturers as JSON: [{"name":"Dell","count":100,"percentage":50.0},...]
        /// </summary>
        public string TopManufacturers { get; set; } = "[]";

        /// <summary>
        /// Top 5 models as JSON: [{"name":"Latitude 7420","count":50,"percentage":25.0},...]
        /// </summary>
        public string TopModels { get; set; } = "[]";

        // ===== APP & SCRIPT METRICS =====

        public double AvgAppsPerSession { get; set; }
        public int TotalUniqueApps { get; set; }
        public double AvgPlatformScriptsPerSession { get; set; }
        public double AvgRemediationScriptsPerSession { get; set; }
        public int TotalPlatformScripts { get; set; }
        public int TotalRemediationScripts { get; set; }
    }
}
