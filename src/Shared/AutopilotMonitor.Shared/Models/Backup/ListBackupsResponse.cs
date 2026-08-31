using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Backup
{
    /// <summary>
    /// Response of <c>GET global/backups</c>: every backupId in the critical-table-backups
    /// container, newest first. Serialized with <see cref="BackupManifestJson.SerializerOptions"/>
    /// (the backup surface's own options), not the ApiJsonOptions pipeline.
    /// </summary>
    public class ListBackupsResponse : IApiResponse
    {
        public IReadOnlyList<string> BackupIds { get; set; } = default!;
    }
}
