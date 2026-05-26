using System;

namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Per-job state machine for asynchronous backup + restore work. Persisted as
    /// <see cref="BackupJobStatusEntity"/> in <c>BackupJobs</c> (PartitionKey="BackupJobs",
    /// RowKey={jobId}). The domain DTO carries strongly-typed enums; the Repository
    /// layer maps from/to the string-typed Azure Table columns (Azure Tables has no
    /// EDM enum). Unknown strings on read MUST throw — silent fallback is a regression
    /// risk after enum refactorings.
    /// </summary>
    public sealed class BackupJobStatus
    {
        public string JobId { get; set; } = string.Empty;
        public BackupJobKind Kind { get; set; }
        public BackupJobState State { get; set; }

        /// <summary>UPN for manual triggers; <c>"Timer"</c> never reaches this DTO — Timer skips the queue.</summary>
        public string RequestedBy { get; set; } = string.Empty;

        public DateTime QueuedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>Updated by the renewal-loop and per-phase progress writes; drives the watchdog.</summary>
        public DateTime LastHeartbeatUtc { get; set; }

        /// <summary>
        /// For Kind=Backup: the worker-generated id of the manifest this job produced
        /// (null until the worker has acquired the lease and stamped it). UI uses this
        /// to deep-link from a completed job to its manifest detail page.
        /// </summary>
        public string? BackupId { get; set; }

        /// <summary>For Kind=RestoreTable: the manifest id the operator is restoring FROM.</summary>
        public string? SourceBackupId { get; set; }

        /// <summary>For Kind=RestoreTable: the table being restored.</summary>
        public string? TableName { get; set; }

        /// <summary>For Kind=RestoreTable: <c>upsert-only</c> | <c>replace-all</c>.</summary>
        public string? Strategy { get; set; }

        /// <summary>Free-form JSON for in-flight worker progress (phase, counters). Informational only — recovery does not depend on it.</summary>
        public string? Progress { get; set; }

        public string? Error { get; set; }

        /// <summary>
        /// For Kind=Backup with State=Completed: <c>Success</c> | <c>Partial</c>.
        /// Null for any other (Kind, State) combination.
        /// </summary>
        public BackupOutcome? BackupOutcome { get; set; }
    }

    public enum BackupJobKind
    {
        Backup,
        RestoreTable,
    }

    /// <summary>
    /// Six-state job machine. Terminal states (Completed / Failed / Skipped / BlockedTerminal)
    /// short-circuit the worker's duplicate-detection on reappearance: message is dropped, no
    /// re-run. Failed vs BlockedTerminal vs Skipped is intentional so operator-action follow-up
    /// can be distinguished (logs / re-trigger / nothing-to-do).
    /// </summary>
    public enum BackupJobState
    {
        Queued,
        Running,

        /// <summary>Manifest written (Backup) or restore phase succeeded (RestoreTable).</summary>
        Completed,

        /// <summary>Unexpected abort: storage 5xx / network / bug. After 5 retries → poison-move sets this.</summary>
        Failed,

        /// <summary>Maintenance-lease held by another job — no work performed. Operator retriggers when the parallel job is done.</summary>
        Skipped,

        /// <summary>Deterministic domain error: manifest missing, SHA mismatch, auth-table block, confirmation-token typo. Not retryable.</summary>
        BlockedTerminal,
    }
}
