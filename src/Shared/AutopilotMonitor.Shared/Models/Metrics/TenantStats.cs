using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Cumulative per-tenant counters that survive session retention cleanup.
    /// Stored in the PlatformStats table (PartitionKey: tenantId, RowKey: "current";
    /// the platform-wide row uses PartitionKey "global" and can never collide with a
    /// tenant GUID). Incremented at session registration; the nightly maintenance
    /// recompute only raises the value to the live session count (floor), never
    /// overwrites it — retention prunes old sessions, so the counter is the sole
    /// source of truth for "since signup".
    /// </summary>
    public class TenantStats
    {
        /// <summary>Total enrollment sessions registered by this tenant since signup</summary>
        public long TotalEnrollments { get; set; }

        /// <summary>When these stats were last updated</summary>
        public DateTime LastUpdated { get; set; }
    }
}
