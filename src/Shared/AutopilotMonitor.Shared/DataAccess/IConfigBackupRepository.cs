using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Pre-write snapshots of the two single-row config entities (see
    /// <see cref="ConfigBackupEntry"/>). Written by the config repository's save hook
    /// (fail-soft) and by the transactional patch/revert flow (fail-closed), pruned to
    /// the newest few per partition. Reads back newest-first via the reverse-ticks RowKey.
    /// </summary>
    public interface IConfigBackupRepository
    {
        Task UpsertAsync(ConfigBackupEntry entry, CancellationToken ct = default);

        /// <summary>Newest-first snapshots for one partition (tenantId or "GlobalConfig").</summary>
        Task<List<ConfigBackupEntry>> ListByPartitionAsync(string partitionKey, int max = 25, CancellationToken ct = default);

        /// <summary>Point lookup by backupId (= RowKey); null when absent.</summary>
        Task<ConfigBackupEntry?> TryGetAsync(string partitionKey, string backupId, CancellationToken ct = default);

        /// <summary>Deletes every snapshot beyond the newest <paramref name="keep"/>; returns rows removed.</summary>
        Task<int> PruneAsync(string partitionKey, int keep, CancellationToken ct = default);
    }
}
