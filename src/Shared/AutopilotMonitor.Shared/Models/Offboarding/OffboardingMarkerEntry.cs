using System;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// Lifecycle anchor + audit row for a tenant currently (or recently) being offboarded.
    /// Lives in <c>OffboardingAudit</c> table under
    /// <c>PartitionKey = OffboardingPartitionKeys.Marker</c> with <c>RowKey = normalizedTenantId</c>.
    /// <para>
    /// In the PR3-revised architecture this row is NO LONGER read in the agent or web
    /// auth hotpath. The active gate is <c>TenantConfiguration.Disabled=true</c> + the
    /// 6-minute cache-drain barrier on the worker envelope. The marker exists to:
    /// </para>
    /// <list type="bullet">
    ///   <item>Provide an idempotency anchor for the <c>TenantOffboardFunction</c>
    ///   admin endpoint (re-clicks resolve to the existing marker and resume).</item>
    ///   <item>Track the offboarding lifecycle status (<see cref="Status"/>) across
    ///   Phase 1 → Phase 2 → Completed/Failed for ops visibility.</item>
    ///   <item>Anchor the post-completion cleanup sweeps run by
    ///   <c>OffboardingMarkerCleanupFunction</c> (Expectations blob delete +
    ///   defense-in-depth TenantConfiguration sweep if Phase 2.F-final failed).</item>
    /// </list>
    /// <para>
    /// Marker survives the Completed transition: Phase 2.G sets <see cref="Status"/> =
    /// <c>"Completed"</c> + <see cref="CompletedAt"/>. <c>OffboardingMarkerCleanupFunction</c>
    /// removes Completed markers after 15min (post-cleanup-sweep settle buffer). Failed
    /// markers are never auto-deleted — operator action required.
    /// </para>
    /// </summary>
    public sealed class OffboardingMarkerEntry
    {
        /// <summary>Always <c>OffboardingPartitionKeys.Marker</c>.</summary>
        public string PartitionKey { get; set; } = string.Empty;

        /// <summary>Normalized (lowercase GUID) tenant id.</summary>
        public string RowKey { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        /// <summary>Cross-reference to the matching <c>OffboardingHistory</c> row's RowKey.</summary>
        public string OffboardingHistoryRowKey { get; set; } = string.Empty;

        public DateTime InitiatedAt { get; set; }

        /// <summary>UPN of the admin (or "System.*") that triggered the offboarding.</summary>
        public string InitiatedBy { get; set; } = string.Empty;

        /// <summary>
        /// Lifecycle: <c>Initiated</c> → <c>InProgress</c> → <c>Completed</c> | <c>Failed</c>.
        /// Marker remains active in all four states; only the cleanup function removes
        /// Completed markers (and never Failed).
        /// </summary>
        public string Status { get; set; } = "Initiated";

        /// <summary>UTC timestamp set by Phase 2.G; gates 15-min cleanup window.</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>UTC timestamp set after max-dequeue → Failed; marker stays active until operator action.</summary>
        public DateTime? FailedAt { get; set; }

        /// <summary>Phase that failed: <c>drain</c>, <c>wipe</c>, <c>blobs</c>, <c>tenantconfig</c>,
        /// <c>killswitch</c>, <c>poisoned</c>, <c>cas_exhausted</c>, <c>drain_timeout</c>,
        /// <c>expectations_missing</c>, <c>expectations_corrupt</c>, <c>enumeration_incomplete</c>,
        /// <c>expectations_size_mismatch</c>, <c>alreadyinflight_no_manifest</c>.</summary>
        public string? FailedPhase { get; set; }
    }
}
