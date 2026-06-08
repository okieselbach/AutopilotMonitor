using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// Azure Tables implementation of <see cref="IIndexTableRepository"/> (Plan §2.8, §M5.d).
    /// One class covers all 5 index tables — each table's projection lives in a dedicated
    /// <c>ToEntity</c> overload, while the shared batched-transaction write path
    /// (group-by-PK, 100-op chunk) mirrors <see cref="TableSignalRepository"/>.
    /// </summary>
    public sealed class TableIndexRepository : IIndexTableRepository
    {
        /// <summary>Azure Table Storage limit per entity-group-transaction.</summary>
        internal const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<TableIndexRepository> _logger;

        public TableIndexRepository(TableStorageService storage, ILogger<TableIndexRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        // ============================================================ public Store methods

        public Task<int> StoreSessionsByTerminalAsync(
            IReadOnlyList<SessionsByTerminalRecord> records,
            CancellationToken cancellationToken = default)
            => StoreAsync(Constants.TableNames.SessionsByTerminal, records, ToEntity, cancellationToken);

        public Task<int> StoreSessionsByStageAsync(
            IReadOnlyList<SessionsByStageRecord> records,
            CancellationToken cancellationToken = default)
            => StoreAsync(Constants.TableNames.SessionsByStage, records, ToEntity, cancellationToken);

        public Task<int> StoreDeadEndsByReasonAsync(
            IReadOnlyList<DeadEndsByReasonRecord> records,
            CancellationToken cancellationToken = default)
            => StoreAsync(Constants.TableNames.DeadEndsByReason, records, ToEntity, cancellationToken);

        public Task<int> StoreClassifierVerdictsByIdLevelAsync(
            IReadOnlyList<ClassifierVerdictsByIdLevelRecord> records,
            CancellationToken cancellationToken = default)
            => StoreAsync(Constants.TableNames.ClassifierVerdictsByIdLevel, records, ToEntity, cancellationToken);

        public Task<int> StoreSignalsByKindAsync(
            IReadOnlyList<SignalsByKindRecord> records,
            CancellationToken cancellationToken = default)
            => StoreAsync(Constants.TableNames.SignalsByKind, records, ToEntity, cancellationToken);

        // ============================================================ shared write path

        private async Task<int> StoreAsync<T>(
            string tableName,
            IReadOnlyList<T> records,
            Func<T, TableEntity> toEntity,
            CancellationToken cancellationToken)
        {
            if (records is null || records.Count == 0) return 0;

            var entities = new List<TableEntity>(records.Count);
            foreach (var r in records)
            {
                var entity = toEntity(r);
                SecurityValidator.EnsureValidGuid(entity.GetString("TenantId"), "TenantId");
                SecurityValidator.EnsureValidGuid(entity.GetString("SessionId"), "SessionId");
                entities.Add(entity);
            }

            var table = _storage.GetTableClient(tableName);
            var committed = 0;

            foreach (var group in entities.GroupBy(e => e.PartitionKey))
            {
                // Collapse duplicate RowKeys before building the transaction — Azure Tables
                // rejects the whole batch with InvalidDuplicateRow otherwise. Dedup also yields
                // RowKey ordering, replacing the previous explicit OrderBy.
                var (deduped, dropped) = TableBatchDedup.ByRowKey(group);
                if (dropped > 0)
                    _logger.LogWarning(
                        "{Table}: dropped {Dropped} duplicate-RowKey index row(s) (last-wins) for partition {Pk}",
                        tableName, dropped, group.Key);

                for (var offset = 0; offset < deduped.Count; offset += TransactionChunkSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunk = deduped.Skip(offset).Take(TransactionChunkSize).ToList();
                    var actions = chunk
                        .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                        .ToList();

                    await table.SubmitTransactionAsync(actions, cancellationToken).ConfigureAwait(false);
                    committed += chunk.Count;
                }
            }

            _logger.LogDebug("{Table}: committed {Count} index rows", tableName, committed);
            return committed;
        }

        // ============================================================ per-record entity mapping

        internal static TableEntity ToEntity(SessionsByTerminalRecord r) => new(
            IndexRowKeys.BuildSessionsByTerminalPk(r.TenantId, r.TerminalStage),
            IndexRowKeys.BuildSessionsByTerminalRk(r.OccurredAtUtc, r.SessionId))
        {
            ["TenantId"]      = r.TenantId,
            ["SessionId"]     = r.SessionId,
            ["TerminalStage"] = r.TerminalStage ?? string.Empty,
            ["OccurredAtUtc"] = r.OccurredAtUtc,
            ["StepIndex"]     = r.StepIndex,
        };

        internal static TableEntity ToEntity(SessionsByStageRecord r) => new(
            IndexRowKeys.BuildSessionsByStagePk(r.TenantId, r.Stage),
            IndexRowKeys.BuildSessionsByStageRk(r.LastUpdatedUtc, r.SessionId))
        {
            ["TenantId"]       = r.TenantId,
            ["SessionId"]      = r.SessionId,
            ["Stage"]          = r.Stage ?? string.Empty,
            ["LastUpdatedUtc"] = r.LastUpdatedUtc,
            ["StepIndex"]      = r.StepIndex,
        };

        internal static TableEntity ToEntity(DeadEndsByReasonRecord r) => new(
            IndexRowKeys.BuildDeadEndsByReasonPk(r.TenantId, r.DeadEndReason),
            IndexRowKeys.BuildDeadEndsByReasonRk(r.OccurredAtUtc, r.SessionId, r.StepIndex))
        {
            ["TenantId"]         = r.TenantId,
            ["SessionId"]        = r.SessionId,
            ["DeadEndReason"]    = r.DeadEndReason ?? string.Empty,
            ["StepIndex"]        = r.StepIndex,
            ["FromStage"]        = r.FromStage ?? string.Empty,
            ["AttemptedToStage"] = r.AttemptedToStage ?? string.Empty,
            ["OccurredAtUtc"]    = r.OccurredAtUtc,
        };

        internal static TableEntity ToEntity(ClassifierVerdictsByIdLevelRecord r) => new(
            IndexRowKeys.BuildClassifierVerdictsByIdLevelPk(r.TenantId, r.ClassifierId, r.HypothesisLevel),
            IndexRowKeys.BuildClassifierVerdictsByIdLevelRk(r.OccurredAtUtc, r.SessionId, r.StepIndex))
        {
            ["TenantId"]        = r.TenantId,
            ["SessionId"]       = r.SessionId,
            ["ClassifierId"]    = r.ClassifierId ?? string.Empty,
            ["HypothesisLevel"] = r.HypothesisLevel ?? string.Empty,
            ["StepIndex"]       = r.StepIndex,
            ["OccurredAtUtc"]   = r.OccurredAtUtc,
        };

        internal static TableEntity ToEntity(SignalsByKindRecord r) => new(
            IndexRowKeys.BuildSignalsByKindPk(r.TenantId, r.SignalKind),
            IndexRowKeys.BuildSignalsByKindRk(r.OccurredAtUtc, r.SessionId, r.SessionSignalOrdinal))
        {
            ["TenantId"]             = r.TenantId,
            ["SessionId"]            = r.SessionId,
            ["SignalKind"]           = r.SignalKind ?? string.Empty,
            ["SessionSignalOrdinal"] = r.SessionSignalOrdinal,
            ["OccurredAtUtc"]        = r.OccurredAtUtc,
            ["SourceOrigin"]         = r.SourceOrigin ?? string.Empty,
        };
    }
}
