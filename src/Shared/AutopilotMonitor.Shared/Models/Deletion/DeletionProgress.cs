using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// Mutable progress companion to <see cref="DeletionManifest"/>. Tracks which steps have
    /// already executed and whether the live verification pass has run. Plan §3 (Round-2 R9):
    /// schema is intentionally minimal — observability data goes to AuditLogs, not here.
    /// Stored as the sibling <c>{manifestId}.progress.json</c> blob and CAS'd on every write.
    /// </summary>
    public class DeletionProgress
    {
        /// <summary>SHA-256 of the immutable snapshot blob; mismatch = corruption, refuse to proceed.</summary>
        public string SnapshotSha256 { get; set; } = string.Empty;

        /// <summary>Step.Order values that have completed; the worker iterates the remaining ones.</summary>
        public HashSet<int> CompletedSteps { get; set; } = new HashSet<int>();

        /// <summary>True once the live verification pass succeeded; gates the FINAL tombstone step.</summary>
        public bool VerificationDone { get; set; }

        /// <summary>UTC timestamp once the FINAL tombstone step has completed; null while in flight.</summary>
        public DateTime? CompletedAt { get; set; }
    }
}
