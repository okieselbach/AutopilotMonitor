using System;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// O(1) point-lookup pointer used by the re-onboarding hook to find the latest history
    /// row for a given tenant without scanning the <c>OffboardingHistory</c> partition.
    /// Azure Tables cannot serve <c>RowKey endswith '_{tenantId}'</c> efficiently
    /// server-side (full partition scan + client-side filter), so we maintain a sidecar
    /// row keyed by tenantId.
    /// <para>
    /// Lives in <c>OffboardingAudit</c> under
    /// <c>PartitionKey = OffboardingPartitionKeys.ByTenant</c> with
    /// <c>RowKey = normalizedTenantId</c>. Upserted with two-step writes (history-first,
    /// pointer-second) and updated as the worker progresses through Status transitions.
    /// </para>
    /// </summary>
    public sealed class OffboardingByTenantPointer
    {
        /// <summary>Always <c>OffboardingPartitionKeys.ByTenant</c>.</summary>
        public string PartitionKey { get; set; } = string.Empty;

        /// <summary>Normalized (lowercase GUID) tenant id.</summary>
        public string RowKey { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        /// <summary>"{yyyyMMddHHmmssfff}_{tenantId}" of the current/latest history row.</summary>
        public string LatestHistoryRowKey { get; set; } = string.Empty;

        /// <summary>Mirrors the matching history row's Status:
        /// <c>Initiated | InProgress | Completed | Failed | Reonboarded</c>.</summary>
        public string LatestStatus { get; set; } = "Initiated";

        /// <summary>UTC of the most recent pointer update; monotonically increasing.</summary>
        public DateTime LatestUpdatedAt { get; set; }

        /// <summary>How many distinct offboarding rounds this tenant has been through.
        /// Incremented on each new Initiate. Useful for "Tenant ging und kam zurück"-Reporting.</summary>
        public int OffboardCount { get; set; } = 1;
    }
}
