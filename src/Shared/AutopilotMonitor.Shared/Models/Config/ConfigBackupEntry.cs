using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One pre-write snapshot of a single-row config entity (TenantConfiguration or
    /// AdminConfiguration), taken immediately before the row is overwritten so the
    /// change can be reverted. Stored in <c>Constants.TableNames.ConfigurationBackups</c>.
    /// <para>
    /// PartitionKey = normalized tenantId (tenant config) or AdminConfiguration's
    /// "GlobalConfig" partition (admin config). RowKey = reverse-ticks + short guid,
    /// so a partition scan returns newest-first. The RowKey doubles as the public
    /// backupId in API responses.
    /// </para>
    /// <para>
    /// <see cref="EntityJson"/> holds the raw stored row (every table property except
    /// odata.etag/Timestamp) — full fidelity for restore, independent of model
    /// refactors. It contains secrets in clear text and must NEVER be returned by
    /// list endpoints; <see cref="DiffJson"/> is the masked, advisory summary that is
    /// safe to surface.
    /// </para>
    /// </summary>
    public class ConfigBackupEntry
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;

        /// <summary>Normalized tenantId, or "GlobalConfig" for admin-config snapshots.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>Raw stored row as JSON (property name → value), excluding odata.etag/Timestamp.</summary>
        public string EntityJson { get; set; } = string.Empty;

        /// <summary>Identity about to overwrite the row (UpdatedBy of the incoming save, or "system").</summary>
        public string ChangedBy { get; set; } = "system";

        /// <summary>
        /// Write path that triggered the snapshot: portal-put | plan | auth | app-homing |
        /// offboard | admin-config | mcp-patch | mcp-revert | revert | unknown.
        /// </summary>
        public string Source { get; set; } = "unknown";

        /// <summary>Optional caller-provided intent (MCP tools require one).</summary>
        public string? Reason { get; set; }

        /// <summary>Masked "old → new" summary of the incoming change (ConfigDiffHelper output). Advisory only.</summary>
        public string? DiffJson { get; set; }

        public DateTime BackupTakenAt { get; set; }
    }
}
