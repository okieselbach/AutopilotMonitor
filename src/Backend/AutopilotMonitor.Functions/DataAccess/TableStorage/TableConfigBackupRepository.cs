using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage backend for <see cref="IConfigBackupRepository"/>.
    /// Fail-loud — callers decide whether a snapshot failure is soft (legacy save hook)
    /// or hard (transactional patch/revert, which must not write without a backup).
    /// </summary>
    public sealed class TableConfigBackupRepository : IConfigBackupRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableConfigBackupRepository> _logger;

        public TableConfigBackupRepository(
            TableStorageService storage,
            ILogger<TableConfigBackupRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.ConfigurationBackups);
            _logger = logger;
        }

        public Task UpsertAsync(ConfigBackupEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.PartitionKey)) throw new ArgumentException("PartitionKey required", nameof(entry));
            if (string.IsNullOrEmpty(entry.RowKey)) throw new ArgumentException("RowKey required", nameof(entry));
            return _tableClient.UpsertEntityAsync(Store(entry), TableUpdateMode.Replace, ct);
        }

        public async Task<List<ConfigBackupEntry>> ListByPartitionAsync(
            string partitionKey, int max = 25, CancellationToken ct = default)
        {
            if (max < 1) throw new ArgumentOutOfRangeException(nameof(max));

            var results = new List<ConfigBackupEntry>();
            var filter = $"PartitionKey eq '{Escape(partitionKey)}'";
            await foreach (var e in _tableClient.QueryAsync<TableEntity>(filter, cancellationToken: ct))
            {
                // Reverse-ticks RowKey → Azure's RowKey-ascending order IS newest-first.
                results.Add(Map(e));
                if (results.Count >= max) break;
            }
            return results;
        }

        public async Task<ConfigBackupEntry?> TryGetAsync(
            string partitionKey, string backupId, CancellationToken ct = default)
        {
            try
            {
                var response = await _tableClient.GetEntityAsync<TableEntity>(partitionKey, backupId, cancellationToken: ct);
                return Map(response.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<int> PruneAsync(string partitionKey, int keep, CancellationToken ct = default)
        {
            if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep));

            var filter = $"PartitionKey eq '{Escape(partitionKey)}'";
            var seen = 0;
            var deleted = 0;
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
                filter, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
            {
                seen++;
                if (seen <= keep) continue;

                try
                {
                    await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, ETag.All, ct);
                    deleted++;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Concurrent prune already removed it — idempotent.
                }
            }
            return deleted;
        }

        /// <summary>
        /// Reverse-ticks + short guid: newest sorts first, concurrent writers within the
        /// same tick cannot collide. The value is the public backupId.
        /// </summary>
        public static string BuildRowKey(DateTime utcNow)
            => $"{RowKeyCodec.InvertedTicks(utcNow)}_{Guid.NewGuid():N}"[..28];

        private static string Escape(string s) => s.Replace("'", "''");

        private static TableEntity Store(ConfigBackupEntry e) => new(e.PartitionKey, e.RowKey)
        {
            ["TenantId"] = e.TenantId,
            ["EntityJson"] = e.EntityJson,
            ["ChangedBy"] = e.ChangedBy,
            ["Source"] = e.Source,
            ["Reason"] = e.Reason,
            ["DiffJson"] = e.DiffJson,
            ["BackupTakenAt"] = e.BackupTakenAt,
        };

        private static ConfigBackupEntry Map(TableEntity e) => new()
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            EntityJson = e.GetString("EntityJson") ?? string.Empty,
            ChangedBy = e.GetString("ChangedBy") ?? "system",
            Source = e.GetString("Source") ?? "unknown",
            Reason = e.GetString("Reason"),
            DiffJson = e.GetString("DiffJson"),
            BackupTakenAt = e.GetDateTime("BackupTakenAt") ?? default,
        };
    }
}
