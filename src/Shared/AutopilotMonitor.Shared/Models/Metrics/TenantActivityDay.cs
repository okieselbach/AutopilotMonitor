using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// A UTC day on which a tenant registered sessions, read from the daily UsageMetrics rows
    /// (PartitionKey: day, RowKey: tenantId). The daily job writes a row only for days with
    /// sessions and never for today.
    /// </summary>
    public class TenantActivityDay
    {
        /// <summary>The UTC day (midnight, DateTimeKind.Utc).</summary>
        public DateTime Day { get; set; }

        public string TenantId { get; set; } = string.Empty;

        /// <summary>Sessions the tenant started that day.</summary>
        public int SessionsTotal { get; set; }
    }
}
