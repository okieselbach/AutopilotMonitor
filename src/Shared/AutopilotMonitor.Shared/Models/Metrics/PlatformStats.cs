using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Pre-computed platform-wide statistics for the public landing page.
    /// Stored as a single row (PartitionKey: "global", RowKey: "current").
    /// Incremented during registration/ingest/login; the daily maintenance recompute treats
    /// every cumulative counter as a monotonic "since release" high-water-mark (raise-only —
    /// the scanned tables are retention-pruned, so a raw recompute would regress the figures).
    /// Only TotalSignedUpTenants is current-state (its source table is not retention-pruned).
    /// See MaintenanceService.BuildMonotonicPlatformStats.
    /// </summary>
    public class PlatformStats
    {
        /// <summary>Total enrollment sessions monitored since launch</summary>
        public long TotalEnrollments { get; set; }

        /// <summary>Total unique users who logged in</summary>
        public long TotalUsers { get; set; }

        /// <summary>Total unique tenants using the platform (have at least one session)</summary>
        public long TotalTenants { get; set; }

        /// <summary>Total tenants signed up (have a tenant configuration entry)</summary>
        public long TotalSignedUpTenants { get; set; }

        /// <summary>Total unique device models seen (manufacturer + model)</summary>
        public long UniqueDeviceModels { get; set; }

        /// <summary>Total events processed across all tenants</summary>
        public long TotalEventsProcessed { get; set; }

        /// <summary>Total successful enrollments</summary>
        public long SuccessfulEnrollments { get; set; }

        /// <summary>Total analysis issues detected</summary>
        public long IssuesDetected { get; set; }

        /// <summary>When these stats were last fully recomputed</summary>
        public DateTime LastFullCompute { get; set; }

        /// <summary>When these stats were last updated (including incremental)</summary>
        public DateTime LastUpdated { get; set; }
    }
}
