using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Azure Tables implementation of <see cref="ISignalRepository"/>. Upserts to the
    /// <c>Signals</c> table via entity-group-transactions (chunked to the 100-op limit).
    /// </summary>
    public sealed class TableSignalRepository : ISignalRepository
    {
        /// <summary>Azure Table Storage limit per entity-group-transaction.</summary>
        internal const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<TableSignalRepository> _logger;

        public TableSignalRepository(TableStorageService storage, ILogger<TableSignalRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public async Task<int> StoreBatchAsync(
            IReadOnlyList<SignalRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (records == null || records.Count == 0) return 0;

            foreach (var r in records)
            {
                SecurityValidator.EnsureValidGuid(r.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(r.SessionId, "SessionId");
            }

            var table = _storage.GetTableClient(Constants.TableNames.Signals);
            var committed = 0;

            foreach (var group in records.GroupBy(r => (r.TenantId, r.SessionId)))
            {
                // Collapse duplicate RowKeys (D19(SessionSignalOrdinal)) before building the
                // transaction — Azure Tables rejects the whole batch with InvalidDuplicateRow
                // otherwise (e.g. an agent replaying an overlapping ordinal). Dedup also yields
                // RowKey ordering, so the previous OrderBy(ordinal) is no longer needed.
                var (deduped, dropped) = TableBatchDedup.ByRowKey(group.Select(ToEntity));
                if (dropped > 0)
                    _logger.LogWarning(
                        "Signals: dropped {Dropped} duplicate-RowKey row(s) (last-wins) for {Tenant}_{Session} — agent likely replayed overlapping SessionSignalOrdinal",
                        dropped, group.Key.TenantId, group.Key.SessionId);

                for (var offset = 0; offset < deduped.Count; offset += TransactionChunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunk = deduped.Skip(offset).Take(TransactionChunkSize).ToList();
                    var actions = chunk
                        .Select(e => new TableTransactionAction(
                            TableTransactionActionType.UpsertReplace, e))
                        .ToList();

                    await table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                    committed += chunk.Count;
                }

                _logger.LogDebug(
                    "Signals: committed {Count} rows for {Tenant}_{Session}",
                    deduped.Count, group.Key.TenantId, group.Key.SessionId);
            }

            return committed;
        }

        /// <summary>
        /// Projects an Azure <see cref="TableEntity"/> back into a <see cref="SignalRecord"/>,
        /// reassembling chunked <c>PayloadJson</c> if present. Internal for mapping tests.
        /// </summary>
        internal static SignalRecord FromEntity(TableEntity entity)
        {
            return new SignalRecord
            {
                TenantId             = entity.GetString("TenantId") ?? string.Empty,
                SessionId            = entity.GetString("SessionId") ?? string.Empty,
                SessionSignalOrdinal = entity.GetInt64("SessionSignalOrdinal") ?? 0,
                SessionTraceOrdinal  = entity.GetInt64("SessionTraceOrdinal") ?? 0,
                Kind                 = entity.GetString("Kind") ?? string.Empty,
                KindSchemaVersion    = entity.GetInt32("KindSchemaVersion") ?? 0,
                OccurredAtUtc        = entity.GetDateTime("OccurredAtUtc") ?? default,
                SourceOrigin         = entity.GetString("SourceOrigin") ?? string.Empty,
                PayloadJson          = TableStorageChunking.ReassembleProperty(entity, "PayloadJson") ?? string.Empty,
            };
        }

        /// <summary>
        /// Projects a <see cref="SignalRecord"/> onto its Azure <see cref="TableEntity"/> shape.
        /// Keys: PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{SessionSignalOrdinal:D19}</c>
        /// (19 digits covers long.MaxValue for lexicographic ordering).
        /// </summary>
        internal static TableEntity ToEntity(SignalRecord r)
        {
            var pk = BuildPartitionKey(r.TenantId, r.SessionId);
            var rk = BuildRowKey(r.SessionSignalOrdinal);

            var entity = new TableEntity(pk, rk)
            {
                ["TenantId"] = r.TenantId,
                ["SessionId"] = r.SessionId,
                ["SessionSignalOrdinal"] = r.SessionSignalOrdinal,
                ["SessionTraceOrdinal"] = r.SessionTraceOrdinal,
                ["Kind"] = r.Kind ?? string.Empty,
                ["KindSchemaVersion"] = r.KindSchemaVersion,
                ["OccurredAtUtc"] = r.OccurredAtUtc,
                ["SourceOrigin"] = r.SourceOrigin ?? string.Empty,
            };

            // Guard the 32 K-char per-property limit — DecisionSignal Evidence+Payload can cross it.
            foreach (var kv in TableStorageChunking.ChunkProperty("PayloadJson", r.PayloadJson ?? string.Empty))
            {
                entity[kv.Key] = kv.Value;
            }

            return entity;
        }

        internal static string BuildPartitionKey(string tenantId, string sessionId)
            => $"{tenantId}_{sessionId}";

        internal static string BuildRowKey(long sessionSignalOrdinal)
            => sessionSignalOrdinal.ToString("D19");
    }
}
