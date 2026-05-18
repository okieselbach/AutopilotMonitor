using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage backend for <see cref="ITenantCustomsArchiveRepository"/>.
    /// Writes during Phase 2.D-archive, reads + deletes from the Global Admin UI.
    /// Fail-loud — exceptions propagate to the caller (worker or HTTP handler).
    /// </summary>
    public sealed class TableTenantCustomsArchiveRepository : ITenantCustomsArchiveRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableTenantCustomsArchiveRepository> _logger;

        public TableTenantCustomsArchiveRepository(
            TableStorageService storage,
            ILogger<TableTenantCustomsArchiveRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.TenantOffboardingCustomsArchive);
            _logger = logger;
        }

        public Task UpsertAsync(TenantOffboardingCustomsArchiveEntry entry, CancellationToken ct = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrEmpty(entry.PartitionKey)) throw new ArgumentException("PartitionKey required", nameof(entry));
            if (string.IsNullOrEmpty(entry.RowKey)) throw new ArgumentException("RowKey required", nameof(entry));
            return _tableClient.UpsertEntityAsync(Store(entry), TableUpdateMode.Replace, ct);
        }

        public async Task<int> CountByRunAndTableAsync(
            string normalizedTenantId, string historyRowKey, string originalTable, CancellationToken ct = default)
        {
            var pk = BuildPartitionKey(normalizedTenantId, historyRowKey);
            var rkPrefix = originalTable + "_";
            // Range filter: rowKey ge 'GatherRules_' and rowKey lt 'GatherRules_~'.
            // '~' (0x7E) is the largest visible ASCII char that base64url uses; works as
            // exclusive upper bound because base64url alphabet is [A-Za-z0-9_-] (no '~').
            var filter = $"PartitionKey eq '{Escape(pk)}' and RowKey ge '{Escape(rkPrefix)}' and RowKey lt '{Escape(rkPrefix + "~")}'";

            var count = 0;
            await foreach (var _ in _tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
            {
                count++;
            }
            return count;
        }

        public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByRunAsync(
            string normalizedTenantId, string historyRowKey, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var pk = BuildPartitionKey(normalizedTenantId, historyRowKey);
            var filter = $"PartitionKey eq '{Escape(pk)}'";
            await foreach (var e in _tableClient.QueryAsync<TableEntity>(filter, cancellationToken: ct))
            {
                yield return Map(e);
            }
        }

        public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByTenantAsync(
            string normalizedTenantId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            // Per-tenant prefix scan: PartitionKey ge '{tid}_' and PartitionKey lt '{tid}_~'.
            // History row keys begin with a numeric timestamp (yyyyMMddHHmmssfff) which is < '~',
            // so the exclusive upper bound is correct.
            var prefix = normalizedTenantId + "_";
            var filter = $"PartitionKey ge '{Escape(prefix)}' and PartitionKey lt '{Escape(prefix + "~")}'";
            await foreach (var e in _tableClient.QueryAsync<TableEntity>(filter, cancellationToken: ct))
            {
                yield return Map(e);
            }
        }

        public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in _tableClient.QueryAsync<TableEntity>(cancellationToken: ct))
            {
                yield return Map(e);
            }
        }

        public async Task<TenantOffboardingCustomsArchiveEntry?> TryGetEntryAsync(
            string partitionKey, string rowKey, CancellationToken ct = default)
        {
            try
            {
                var response = await _tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
                return Map(response.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task DeleteEntryAsync(string partitionKey, string rowKey, CancellationToken ct = default)
        {
            try
            {
                await _tableClient.DeleteEntityAsync(partitionKey, rowKey, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Idempotent — already gone.
            }
        }

        public async Task<int> DeleteRunAsync(
            string normalizedTenantId, string historyRowKey, CancellationToken ct = default)
        {
            var pk = BuildPartitionKey(normalizedTenantId, historyRowKey);
            var filter = $"PartitionKey eq '{Escape(pk)}'";

            var deleted = 0;
            var batch = new List<TableTransactionAction>();
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter, select: new[] { "PartitionKey", "RowKey" }, cancellationToken: ct))
            {
                batch.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
                deleted++;

                if (batch.Count >= 100)
                {
                    await _tableClient.SubmitTransactionAsync(batch, ct);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await _tableClient.SubmitTransactionAsync(batch, ct);
            }
            return deleted;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        internal static string BuildPartitionKey(string normalizedTenantId, string historyRowKey)
            => $"{normalizedTenantId}_{historyRowKey}";

        /// <summary>
        /// Builds the archive RowKey for one original row. base64url-encodes the original
        /// RowKey so it survives Azure-Tables' RowKey-forbidden character rules (#, ?,
        /// /, \, control chars). Decoder is <see cref="DecodeOriginalRowKey"/>.
        /// </summary>
        public static string BuildRowKey(string originalTable, string originalRowKey)
            => $"{originalTable}_{Base64UrlEncode(originalRowKey)}";

        /// <summary>Inverse of <see cref="BuildRowKey"/> — returns the original RowKey.</summary>
        public static string DecodeOriginalRowKey(string archiveRowKey, string originalTable)
        {
            var prefix = originalTable + "_";
            if (!archiveRowKey.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException($"Archive RowKey '{archiveRowKey}' does not start with expected table prefix '{prefix}'", nameof(archiveRowKey));
            return Base64UrlDecode(archiveRowKey.Substring(prefix.Length));
        }

        private static string Base64UrlEncode(string s)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }

        private static string Base64UrlDecode(string s)
        {
            var padded = s.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var bytes = Convert.FromBase64String(padded);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static string Escape(string s) => s.Replace("'", "''");

        private static TableEntity Store(TenantOffboardingCustomsArchiveEntry e) => new(e.PartitionKey, e.RowKey)
        {
            ["TenantId"] = e.TenantId,
            ["OriginalTable"] = e.OriginalTable,
            ["OriginalPartitionKey"] = e.OriginalPartitionKey,
            ["OriginalRowKey"] = e.OriginalRowKey,
            ["EntityJson"] = e.EntityJson,
            ["HistoryRowKey"] = e.HistoryRowKey,
            ["ArchivedAt"] = e.ArchivedAt,
            ["ArchivedBy"] = e.ArchivedBy,
        };

        private static TenantOffboardingCustomsArchiveEntry Map(TableEntity e) => new()
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            OriginalTable = e.GetString("OriginalTable") ?? string.Empty,
            OriginalPartitionKey = e.GetString("OriginalPartitionKey") ?? string.Empty,
            OriginalRowKey = e.GetString("OriginalRowKey") ?? string.Empty,
            EntityJson = e.GetString("EntityJson") ?? string.Empty,
            HistoryRowKey = e.GetString("HistoryRowKey") ?? string.Empty,
            ArchivedAt = e.GetDateTime("ArchivedAt") ?? default,
            ArchivedBy = e.GetString("ArchivedBy") ?? "TenantOffboardingHandler",
        };
    }
}
