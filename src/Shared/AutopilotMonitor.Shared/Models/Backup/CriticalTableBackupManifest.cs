using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Manifest written as the LAST blob in a backup run. Existence of
    /// <c>{backupId}/manifest.json</c> is the durability anchor: orphan NDJSON
    /// blobs under a prefix without a manifest are treated as <c>Incomplete</c>
    /// by the listing API and never offered for restore.
    /// <para>
    /// Schema-version is intentionally bumpable but starts at 1; restore code
    /// rejects unknown EDM types and unknown outcome strings with a clear error.
    /// </para>
    /// </summary>
    public sealed class CriticalTableBackupManifest
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Format <c>yyyyMMddTHHmmssZ_{guid8}</c>.</summary>
        public string BackupId { get; set; } = string.Empty;

        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }

        /// <summary>UPN of the operator who manually triggered the job, or <c>"Timer"</c> for the daily cron.</summary>
        public string TriggeredBy { get; set; } = "Timer";

        /// <summary>Manifest-level outcome. NOT a JobState — see plan §JobStatus.</summary>
        public BackupOutcome Outcome { get; set; }

        public List<CriticalTableBackupTableEntry> Tables { get; set; } = new();
    }

    public sealed class CriticalTableBackupTableEntry
    {
        public string TableName { get; set; } = string.Empty;
        public TableBackupStatus Status { get; set; }
        public long RowCount { get; set; }
        public long ByteSize { get; set; }
        public string Sha256Hex { get; set; } = string.Empty;

        /// <summary>Canonical: <c>{backupId}/{TableName}.ndjson</c>.</summary>
        public string BlobName { get; set; } = string.Empty;

        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Manifest-level outcome. Job-level state (Failed for fatal errors) is tracked
    /// separately on <c>BackupJobStatus</c>; a manifest only exists when the run
    /// reached the "all tables attempted, manifest write succeeded" milestone.
    /// </summary>
    public enum BackupOutcome
    {
        /// <summary>All tables backed up successfully.</summary>
        Success,

        /// <summary>At least one table Failed or Skipped, but the manifest was still written
        /// for the remaining tables. Operationally valuable — 14 of 15 tables are still restorable.</summary>
        Partial,
    }

    /// <summary>Per-table outcome inside a manifest.</summary>
    public enum TableBackupStatus
    {
        /// <summary>NDJSON blob written, SHA matched, RowCount &gt; 0.</summary>
        Ok,

        /// <summary>Table had 0 rows — empty NDJSON blob (and SHA-of-empty-string) is still committed for symmetry.</summary>
        Empty,

        /// <summary>Per-run budget exceeded before this table was attempted (or finished). Operator can retry next run.</summary>
        Skipped,

        /// <summary>An unexpected exception aborted this table's dump. Other tables continued.</summary>
        Failed,
    }
}
