using System;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// Chronological audit-trail row for one tenant-offboarding attempt. Multiple rows per
    /// tenant when the tenant offboarded, re-onboarded, and offboarded again. Lives in
    /// <c>OffboardingAudit</c> under <c>PartitionKey = OffboardingPartitionKeys.History</c>
    /// with <c>RowKey = "{yyyyMMddHHmmssfff}_{normalizedTenantId}"</c> so rows sort
    /// newest-last lexicographically within the partition.
    /// </summary>
    public sealed class OffboardingHistoryEntry
    {
        /// <summary>Always <c>OffboardingPartitionKeys.History</c>.</summary>
        public string PartitionKey { get; set; } = string.Empty;

        /// <summary>"{yyyyMMddHHmmssfff}_{normalizedTenantId}". Unique per offboarding attempt.</summary>
        public string RowKey { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;
        public string DomainName { get; set; } = string.Empty;
        public string InitiatedBy { get; set; } = string.Empty;

        public DateTime OffboardedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        /// <summary><c>Initiated</c> | <c>InProgress</c> | <c>Completed</c> | <c>Failed</c>.</summary>
        public string Status { get; set; } = "Initiated";

        /// <summary>
        /// Cache-Drain-Barrier (plan v2 §2). The worker MUST NOT begin destructive Phase 2
        /// before this UTC timestamp. <c>TenantOffboardFunction</c> sets it to
        /// <c>OffboardedAt + DrainBarrier</c> (default 6 min) and enqueues the envelope
        /// with the matching <c>visibilityDelay</c>, so the message is invisible to the
        /// worker until then. All warm function-host instances will have hit their 5-min
        /// <c>TenantConfigurationService</c> cache TTL by the time it expires, so the
        /// existing <c>Disabled=true</c> gate carries the 403 instead of the marker.
        /// <para>
        /// Also rendered by the Web UI as the "data deletion starts in mm ss" countdown
        /// during the drain barrier state, and used by the resume-path to compute the
        /// remaining delay when an admin re-clicks mid-barrier.
        /// </para>
        /// </summary>
        public DateTime? EarliestProcessingAt { get; set; }

        // ── Diagnostics ────────────────────────────────────────────────────────

        /// <summary>JSON: <c>{ tableName: deletedRowCount }</c> across SafeWipe + Cascade phases.</summary>
        public string? DeletedRowCountsJson { get; set; }

        public int? TotalRowsDeleted { get; set; }
        public int? DeletedBlobCount { get; set; }
        public int? CascadeSessionsEnqueued { get; set; }
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }

        /// <summary>
        /// Rev-9-F1 phase marker. Set once by the handler immediately after the drain predicate
        /// returns true, BEFORE Phase 2.D begins wiping tables. Re-pickup after crash skips the
        /// drain predicate when this is non-null because Phase 2.E may have wiped the cascade
        /// progress blobs that the drain predicate reads from; the post-drain phases (2.D-G)
        /// are all idempotent so a clean resume requires no progress blobs.
        /// </summary>
        public DateTime? DrainCompletedAt { get; set; }

        /// <summary>
        /// Stamped by the handler the first time it enters <c>EnsureExpectationsBlobAsync</c>,
        /// BEFORE the session enumerator starts iterating. On re-pickup after an enumerator
        /// crash this field is non-null while the Expectations blob is still missing — the
        /// handler then fail-closes with <c>FailedPhase="expectations_missing"</c> instead of
        /// re-enumerating against a tenant whose underlying Sessions table may already be
        /// mid-mutation. Plan §7.4 step 3 (Rev-5-F3 + Rev-7-F2 crash-after-enumerate-start).
        /// </summary>
        public DateTime? EnumerationStartedAt { get; set; }

        /// <summary>
        /// Stamped after the session enumerator loop finishes cleanly, BEFORE the Expectations
        /// blob upload. Disambiguates two failure modes that both leave the blob missing with
        /// <see cref="EnumerationStartedAt"/> set:
        /// <list type="bullet">
        ///   <item><b>Mid-enumeration crash</b>: ECBU is null → fail-closed
        ///         (<c>FailedPhase="expectations_missing"</c>); the tenant may be mid-mutation
        ///         and re-enumerating is unsafe.</item>
        ///   <item><b>Upload failure</b>: ECBU is non-null → retry the enumerate+upload pair
        ///         on the next pickup. The session-deletion producer is idempotent
        ///         (<c>AlreadyInFlight</c> outcome with the existing ManifestId), so re-running
        ///         the loop produces the same cascade-state set.</item>
        /// </list>
        /// Plan §7.4 step 3 + Review-Fix (second-pass) Finding 2.
        /// </summary>
        public DateTime? EnumerationCompletedBeforeUpload { get; set; }

        // ── Archived Customs (PR3.B §3) ────────────────────────────────────────
        //
        // Counts of rows that were snapshotted into TenantOffboardingCustomsArchive
        // during Phase 2.D-archive, immediately before the originals were safe-wiped.
        // Recomputed from the archive table by post-archive query so a crash between
        // archive-insert and counter-write does not leave a stale 0.

        public int? CustomGatherRulesArchived { get; set; }
        public int? CustomAnalyzeRulesArchived { get; set; }
        public int? ImeLogPatternOverridesArchived { get; set; }

        // ── Re-onboarding tracking (legacy, kept for audit history continuity) ─

        public DateTime? ReonboardedAt { get; set; }
        public string? ReonboardedBy { get; set; }
        public int? CustomsAutoWipedOnReonboard { get; set; }
    }
}
