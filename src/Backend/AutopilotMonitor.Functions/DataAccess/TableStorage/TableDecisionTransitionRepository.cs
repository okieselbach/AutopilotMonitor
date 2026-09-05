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
    /// Azure Tables implementation of <see cref="IDecisionTransitionRepository"/>. Upserts to
    /// the <c>DecisionTransitions</c> table via entity-group-transactions (chunked to the 100-op
    /// limit).
    /// </summary>
    public sealed class TableDecisionTransitionRepository : IDecisionTransitionRepository
    {
        /// <summary>Azure Table Storage limit per entity-group-transaction.</summary>
        internal const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<TableDecisionTransitionRepository> _logger;

        public TableDecisionTransitionRepository(
            TableStorageService storage,
            ILogger<TableDecisionTransitionRepository> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public async Task<int> StoreBatchAsync(
            IReadOnlyList<DecisionTransitionRecord> records,
            CancellationToken cancellationToken = default)
        {
            if (records == null || records.Count == 0) return 0;

            foreach (var r in records)
            {
                SecurityValidator.EnsureValidGuid(r.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(r.SessionId, "SessionId");
            }

            var table = _storage.GetTableClient(Constants.TableNames.DecisionTransitions);
            var committed = 0;

            foreach (var group in records.GroupBy(r => (r.TenantId, r.SessionId)))
            {
                // Collapse duplicate RowKeys (D10(StepIndex)) before building the transaction —
                // Azure Tables rejects the whole batch with InvalidDuplicateRow otherwise. Dedup
                // also yields RowKey ordering, so the previous OrderBy(StepIndex) is redundant.
                var (deduped, dropped) = TableBatchDedup.ByRowKey(group.Select(ToEntity));
                if (dropped > 0)
                    _logger.LogWarning(
                        "DecisionTransitions: dropped {Dropped} duplicate-RowKey row(s) (last-wins) for {Tenant}_{Session} — agent likely replayed overlapping StepIndex",
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
                    "DecisionTransitions: committed {Count} rows for {Tenant}_{Session}",
                    deduped.Count, group.Key.TenantId, group.Key.SessionId);
            }

            return committed;
        }

        /// <summary>
        /// Projects an Azure <see cref="TableEntity"/> back into a <see cref="DecisionTransitionRecord"/>,
        /// reassembling chunked <c>PayloadJson</c> if present. Internal for mapping tests.
        /// </summary>
        internal static DecisionTransitionRecord FromEntity(TableEntity entity)
        {
            return new DecisionTransitionRecord
            {
                TenantId                  = entity.GetString("TenantId") ?? string.Empty,
                SessionId                 = entity.GetString("SessionId") ?? string.Empty,
                StepIndex                 = entity.GetInt32("StepIndex") ?? 0,
                SessionTraceOrdinal       = entity.GetInt64("SessionTraceOrdinal") ?? 0,
                SignalOrdinalRef          = entity.GetInt64("SignalOrdinalRef") ?? 0,
                OccurredAtUtc             = entity.GetDateTime("OccurredAtUtc") ?? default,
                Trigger                   = entity.GetString("Trigger") ?? string.Empty,
                FromStage                 = entity.GetString("FromStage") ?? string.Empty,
                ToStage                   = entity.GetString("ToStage") ?? string.Empty,
                Taken                     = entity.GetBoolean("Taken") ?? false,
                DeadEndReason             = entity.GetString("DeadEndReason"),
                ReducerVersion            = entity.GetString("ReducerVersion") ?? string.Empty,
                IsTerminal                = entity.GetBoolean("IsTerminal") ?? false,
                ClassifierVerdictId       = entity.GetString("ClassifierVerdictId"),
                ClassifierHypothesisLevel = entity.GetString("ClassifierHypothesisLevel"),
                PayloadJson               = TableStorageChunking.ReassembleProperty(entity, "PayloadJson") ?? string.Empty,
            };
        }

        /// <summary>
        /// Projects a <see cref="DecisionTransitionRecord"/> onto its Azure <see cref="TableEntity"/>
        /// shape. Keys: PK = <c>{TenantId}_{SessionId}</c>, RK = <c>{StepIndex:D10}</c>.
        /// </summary>
        internal static TableEntity ToEntity(DecisionTransitionRecord r)
        {
            var pk = BuildPartitionKey(r.TenantId, r.SessionId);
            var rk = BuildRowKey(r.StepIndex);

            var entity = new TableEntity(pk, rk)
            {
                ["TenantId"] = r.TenantId,
                ["SessionId"] = r.SessionId,
                ["StepIndex"] = r.StepIndex,
                ["SessionTraceOrdinal"] = r.SessionTraceOrdinal,
                ["SignalOrdinalRef"] = r.SignalOrdinalRef,
                ["OccurredAtUtc"] = r.OccurredAtUtc,
                ["Trigger"] = r.Trigger ?? string.Empty,
                ["FromStage"] = r.FromStage ?? string.Empty,
                ["ToStage"] = r.ToStage ?? string.Empty,
                ["Taken"] = r.Taken,
                ["DeadEndReason"] = r.DeadEndReason,
                ["ReducerVersion"] = r.ReducerVersion ?? string.Empty,
                ["IsTerminal"] = r.IsTerminal,
                ["ClassifierVerdictId"] = r.ClassifierVerdictId,
                ["ClassifierHypothesisLevel"] = r.ClassifierHypothesisLevel,
            };

            foreach (var kv in TableStorageChunking.ChunkProperty("PayloadJson", r.PayloadJson ?? string.Empty))
            {
                entity[kv.Key] = kv.Value;
            }

            return entity;
        }

        internal static string BuildPartitionKey(string tenantId, string sessionId)
            => $"{tenantId}_{sessionId}";

        internal static string BuildRowKey(int stepIndex)
            => stepIndex.ToString("D10");
    }
}
