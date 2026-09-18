using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Cumulative per-tenant counters that survive session retention cleanup.
    /// Stored in the PlatformStats table (PartitionKey: tenantId, RowKey: "current";
    /// the platform-wide row uses PartitionKey "global" and can never collide with a
    /// tenant GUID). Incremented on the hot path (registration, first Succeeded transition,
    /// ingest batch); the maintenance recompute only raises a value to the live figure
    /// (floor), never overwrites it — retention prunes old sessions, so these counters are
    /// the sole source of truth for "since signup" and the source the platform-wide
    /// counters are rolled up from.
    /// </summary>
    public class TenantStats
    {
        /// <summary>Total enrollment sessions registered by this tenant since signup</summary>
        public long TotalEnrollments { get; set; }

        /// <summary>Sessions of this tenant that reached Succeeded since signup</summary>
        public long SuccessfulEnrollments { get; set; }

        /// <summary>Events stored for this tenant since signup</summary>
        public long TotalEventsProcessed { get; set; }

        /// <summary>When these stats were last updated</summary>
        public DateTime LastUpdated { get; set; }
    }
}
