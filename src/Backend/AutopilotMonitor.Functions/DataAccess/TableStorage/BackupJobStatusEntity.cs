using System;
using Azure;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Azure Table Storage row for the <c>BackupJobs</c> table. Kind / State / BackupOutcome
    /// are persisted as strings because Azure Tables has no EDM enum type; the
    /// <see cref="BackupJobsRepository"/> maps to and from the strongly-typed
    /// <c>BackupJobStatus</c> DTO in <c>AutopilotMonitor.Shared.Models.Backup</c>.
    /// Unknown strings on read throw so enum refactorings cannot silently shift
    /// the persisted state.
    /// <para>
    /// Lives in the Functions project (not Shared) because <c>ITableEntity</c> is an
    /// Azure-SDK dependency we deliberately keep out of the Shared layer.
    /// </para>
    /// <para>
    /// Single-partition design (<c>PartitionKey="BackupJobs"</c>): job IDs are guids,
    /// the table never grows large enough to need partitioning, and a single PK lets
    /// the watchdog enumerate the whole table cheaply.
    /// </para>
    /// </summary>
    public sealed class BackupJobStatusEntity : ITableEntity
    {
        /// <summary>Always <c>"BackupJobs"</c>.</summary>
        public string PartitionKey { get; set; } = "BackupJobs";

        /// <summary>Job id, Guid-N format.</summary>
        public string RowKey { get; set; } = string.Empty;

        /// <summary>Filled by the SDK on read; ignored on write.</summary>
        public DateTimeOffset? Timestamp { get; set; }

        /// <summary>Filled by the SDK on read; used by callers for CAS updates.</summary>
        public ETag ETag { get; set; }

        /// <summary>"Backup" or "RestoreTable".</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>"Queued" | "Running" | "Completed" | "Failed" | "Skipped" | "BlockedTerminal".</summary>
        public string State { get; set; } = string.Empty;

        public string RequestedBy { get; set; } = string.Empty;

        public DateTime QueuedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>Watchdog stale-detection. Refreshed every renewal-tick and per-phase progress write.</summary>
        public DateTime LastHeartbeatUtc { get; set; }

        public string? BackupId { get; set; }
        public string? SourceBackupId { get; set; }
        public string? TableName { get; set; }
        public string? Strategy { get; set; }
        public string? Progress { get; set; }
        public string? Error { get; set; }

        /// <summary>"Success" or "Partial" (only when Kind=Backup AND State=Completed). Null otherwise.</summary>
        public string? BackupOutcome { get; set; }
    }
}
