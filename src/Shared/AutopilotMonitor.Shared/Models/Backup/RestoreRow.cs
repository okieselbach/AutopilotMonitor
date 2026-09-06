using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Operating mode for <c>POST /api/global/backups/{backupId}/restore-row</c>.
    /// Preview is a read-only diff; Commit is the conditional write under maintenance
    /// lease + ETag-CAS.
    /// </summary>
    public enum RestoreRowMode
    {
        Preview,
        Commit,
    }

    /// <summary>
    /// Body of <c>POST /api/global/backups/{backupId}/restore-row</c>.
    /// PartitionKey + RowKey are carried in the body (never the URL) because Azure
    /// Tables permits <c>/</c>, <c>+</c>, <c>%</c> in PK/RK; URL-encoding them on the
    /// route would interact poorly with the Functions router.
    /// </summary>
    public sealed class RestoreRowRequest : IApiRequest
    {
        public string TableName { get; set; } = string.Empty;
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;

        public RestoreRowMode Mode { get; set; } = RestoreRowMode.Preview;

        /// <summary>
        /// SHA-256 (hex, lowercase) of the raw NDJSON line bytes echoed from the
        /// matching Preview response. Required on Commit. The server re-computes the
        /// hash on the fresh NDJSON line and rejects with 409 on mismatch (tamper /
        /// blob churn after the preview).
        /// </summary>
        public string? IfSha256 { get; set; }

        /// <summary>
        /// Live-row ETag echoed from the matching Preview response.
        /// <para>
        /// <c>null</c> means "Preview saw no live row" → Commit uses
        /// <c>AddEntity</c>. A non-null value means "Preview saw a live row with this
        /// ETag" → Commit uses <c>UpdateEntity(ifMatch=etag, Replace)</c>. Mismatch
        /// (412) or existence change → 409 <c>CurrentRowChanged</c>.
        /// </para>
        /// </summary>
        public string? IfCurrentETag { get; set; }
    }

    /// <summary>
    /// Response body of <c>mode=preview</c>. Contains the backup-row dump, the live
    /// row (or null), a per-property diff, the row-hash to echo on commit, and the
    /// live ETag (or null) to echo on commit.
    /// </summary>
    [WireContract]
    public sealed class RestoreRowPreviewResponse
    {
        public string BackupId { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;

        /// <summary>
        /// EDM-tagged property snapshot from the backup NDJSON line. Keys are
        /// case-faithful original Azure Table column names.
        /// </summary>
        public Dictionary<string, RestoreRowPropertySnapshot> BackupProperties { get; set; }
            = new Dictionary<string, RestoreRowPropertySnapshot>();

        /// <summary>
        /// EDM-tagged property snapshot of the live row, or <c>null</c> if the live
        /// row does not currently exist.
        /// </summary>
        public Dictionary<string, RestoreRowPropertySnapshot>? CurrentProperties { get; set; }

        public List<RestoreRowPropertyDiff> Diff { get; set; } = new List<RestoreRowPropertyDiff>();

        /// <summary>SHA-256 (hex, lowercase) of the raw NDJSON line bytes. Echo this on Commit.</summary>
        public string RowSha256 { get; set; } = string.Empty;

        /// <summary>Live-row ETag at preview time, or <c>null</c> if the live row did not exist.</summary>
        public string? CurrentETag { get; set; }

        /// <summary>
        /// True for tables whose <c>IsEnabled</c> column carries security semantics
        /// (<see cref="Constants.CriticalBackupTables.AuthTablesFullRestoreForbidden"/>).
        /// UI must render the warning banner.
        /// </summary>
        public bool IsAuthTable { get; set; }
    }

    /// <summary>
    /// Successful response body of <c>mode=commit</c>. Echoes the write outcome.
    /// </summary>
    [WireContract]
    public sealed class RestoreRowCommitResponse
    {
        public string BackupId { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;

        /// <summary><c>Inserted</c> when the live row was absent, <c>Replaced</c> when it existed.</summary>
        public RestoreRowCommitOutcome Outcome { get; set; }
    }

    public enum RestoreRowCommitOutcome
    {
        Inserted,
        Replaced,
    }

    /// <summary>
    /// Single EDM-tagged property value carried over the wire. Matches the shape of
    /// <see cref="AutopilotMonitor.Shared.Models.Deletion.DeletionPropValue"/> but is
    /// a separate DTO so we can serialize <c>JsonElement</c> snapshots from both
    /// backup and live rows without leaking a Deletion-namespaced type.
    /// </summary>
    public sealed class RestoreRowPropertySnapshot
    {
        public string EdmType { get; set; } = string.Empty;
        public System.Text.Json.JsonElement Value { get; set; }
    }

    /// <summary>Per-property diff entry — exactly one property name with a change kind.</summary>
    public sealed class RestoreRowPropertyDiff
    {
        public string Name { get; set; } = string.Empty;
        public RestoreRowDiffKind Kind { get; set; }

        public RestoreRowPropertySnapshot? Backup { get; set; }
        public RestoreRowPropertySnapshot? Current { get; set; }
    }

    public enum RestoreRowDiffKind
    {
        /// <summary>Property exists only in the backup (live row is missing it; restore will add it).</summary>
        Added,

        /// <summary>Property exists only on the live row; restore will REMOVE it (Replace semantics).</summary>
        Removed,

        /// <summary>Property exists on both sides with different values; restore will overwrite.</summary>
        Changed,

        /// <summary>Property exists on both sides and values match.</summary>
        Unchanged,
    }
}
