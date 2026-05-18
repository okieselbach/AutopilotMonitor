using System;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// Snapshot of a tenant's custom rule row written by the offboarding handler during
    /// Phase 2.D-archive, BEFORE the original row is safe-wiped. Survives forever so a
    /// Global Admin can review (and selectively delete) the tenant's custom rules from
    /// <c>/admin/customs-archive</c> after offboarding completes.
    /// <para>
    /// Storage layout (see PR3.B plan §3.2):
    /// <list type="bullet">
    ///   <item><b>PartitionKey</b>: <c>"{normalizedTenantId}_{historyRowKey}"</c>. One
    ///   partition per offboarding run — Re-Re-Offboarding produces a fresh, immutable
    ///   partition each time so multiple runs co-exist without RowKey collisions.</item>
    ///   <item><b>RowKey</b>: <c>"{originalTable}_{base64url(originalRowKey)}"</c>. The
    ///   table-name prefix disambiguates collisions across the three rules tables; the
    ///   base64url encoding of <see cref="OriginalRowKey"/> avoids the Azure-Tables
    ///   RowKey-forbidden characters (<c>#</c>, <c>?</c>, <c>/</c>, <c>\</c>, control chars).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class TenantOffboardingCustomsArchiveEntry
    {
        /// <summary>"{normalizedTenantId}_{historyRowKey}".</summary>
        public string PartitionKey { get; set; } = string.Empty;

        /// <summary>"{originalTable}_{base64url(originalRowKey)}".</summary>
        public string RowKey { get; set; } = string.Empty;

        public string TenantId { get; set; } = string.Empty;

        /// <summary>One of <c>GatherRules</c> / <c>AnalyzeRules</c> / <c>ImeLogPatterns</c>.</summary>
        public string OriginalTable { get; set; } = string.Empty;

        public string OriginalPartitionKey { get; set; } = string.Empty;

        /// <summary>Original RowKey verbatim — base64url-encoded into <see cref="RowKey"/>'s suffix.</summary>
        public string OriginalRowKey { get; set; } = string.Empty;

        /// <summary>
        /// JSON dump of every property on the source row (excluding system properties).
        /// The full body is small enough to fit in a single Azure Table string property
        /// (64 KB limit; observed rule bodies are well under 4 KB). If a future rule type
        /// ever exceeds that, an EntityJsonBlobUrl overflow pointer can be added — not
        /// part of this PR.
        /// </summary>
        public string EntityJson { get; set; } = string.Empty;

        /// <summary>Cross-reference to the <c>OffboardingHistory</c> row for the run.</summary>
        public string HistoryRowKey { get; set; } = string.Empty;

        public DateTime ArchivedAt { get; set; }

        /// <summary>Constant <c>"TenantOffboardingHandler"</c> in production. Test fakes may override.</summary>
        public string ArchivedBy { get; set; } = "TenantOffboardingHandler";
    }
}
