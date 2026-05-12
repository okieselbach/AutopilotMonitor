using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Deletion;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Result of <see cref="TableStorageService.DeleteByExactKeysInBatchesAsync"/> — accurate
    /// progress accounting for the cascade worker. <see cref="Attempted"/> is the input key
    /// count, <see cref="DeletedNow"/> is the number of rows that the helper actually removed
    /// in this call, <see cref="AlreadyMissing"/> is the number that returned 404 (idempotent
    /// re-run of a partially-completed cascade).
    /// </summary>
    public class DeletionBatchResult
    {
        public int Attempted { get; }
        public int DeletedNow { get; }
        public int AlreadyMissing { get; }

        public DeletionBatchResult(int attempted, int deletedNow, int alreadyMissing)
        {
            Attempted = attempted;
            DeletedNow = deletedNow;
            AlreadyMissing = alreadyMissing;
        }

        public static readonly DeletionBatchResult Empty = new DeletionBatchResult(0, 0, 0);
    }

    /// <summary>
    /// Cascade-deletion read surface. Implements <see cref="ISessionDeletionInventoryReader"/>
    /// against the existing <c>_tableServiceClient</c>. Pure delegation; fail-loud on every
    /// non-404 storage error per memory <c>feedback_storage_helpers_fail_soft</c>.
    /// </summary>
    public partial class TableStorageService : ISessionDeletionInventoryReader
    {
        public async Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.Sessions);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<TableEntity?> GetSessionsIndexRowAsync(string tenantId, string indexRowKey, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionsIndex);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, indexRowKey, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async IAsyncEnumerable<TableEntity> QueryAsync(
            string tableName,
            string filter,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                yield return entity;
            }
        }

        public async Task<TableEntity?> GetEntityOrNullAsync(string tableName, string partitionKey, string rowKey, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(tableName);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: cancellationToken);
                return response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        // ============================================================ Delete helpers (PR2) ====

        // Azure Tables batch transactions cap at 100 actions per submission, all sharing a PartitionKey.
        private const int BatchActionLimit = 100;

        /// <summary>
        /// Idempotent batched deletion by exact <c>(PK, RK)</c> keys. Groups by PartitionKey
        /// (batch transactions are partition-scoped), chunks each group into <see cref="BatchActionLimit"/>-row
        /// transactions, and falls back to per-row <see cref="TableClient.DeleteEntityAsync(string, string, ETag, CancellationToken)"/>
        /// with 404-ignore when the whole-batch rollback hides which row was already missing.
        /// Returns <see cref="DeletionBatchResult"/> for accurate progress accounting on
        /// re-runs of partially-completed cascades.
        /// </summary>
        public async Task<DeletionBatchResult> DeleteByExactKeysInBatchesAsync(
            string tableName,
            IReadOnlyList<(string Pk, string Rk)> keys,
            CancellationToken cancellationToken = default)
        {
            if (keys == null) throw new ArgumentNullException(nameof(keys));
            var attempted = keys.Count;
            if (attempted == 0) return DeletionBatchResult.Empty;

            var tableClient = _tableServiceClient.GetTableClient(tableName);
            var deletedNow = 0;
            var alreadyMissing = 0;

            foreach (var group in keys.GroupBy(k => k.Pk, StringComparer.Ordinal))
            {
                var rowsInGroup = group.ToList();
                for (var i = 0; i < rowsInGroup.Count; i += BatchActionLimit)
                {
                    var chunk = rowsInGroup.Skip(i).Take(BatchActionLimit).ToList();
                    var actions = chunk
                        .Select(k => new TableTransactionAction(
                            TableTransactionActionType.Delete,
                            new TableEntity(k.Pk, k.Rk) { ETag = ETag.All }))
                        .ToList();

                    try
                    {
                        await tableClient.SubmitTransactionAsync(actions, cancellationToken);
                        deletedNow += chunk.Count;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Azure rolls back the entire transaction when a single Delete returns 404.
                        // Fall back to per-row delete with 404-ignore so the helper stays idempotent.
                        foreach (var (pk, rk) in chunk)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                await tableClient.DeleteEntityAsync(pk, rk, ETag.All, cancellationToken);
                                deletedNow++;
                            }
                            catch (RequestFailedException rfe) when (rfe.Status == 404)
                            {
                                alreadyMissing++;
                            }
                        }
                    }
                }
            }

            return new DeletionBatchResult(attempted, deletedNow, alreadyMissing);
        }

        // ---------------- DISCRIMINATOR_PK_RK_SUFFIX / DISCRIMINATOR_PK_RK_EXACT --------------

        public Task<DeletionBatchResult> DeleteEventTypeIndexEntriesAsync(
            string tenantId, string sessionId, IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            return DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.EventTypeIndex, keys, ct);
        }

        public Task<DeletionBatchResult> DeleteCveIndexEntriesAsync(
            string tenantId, string sessionId, IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            return DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.CveIndex, keys, ct);
        }

        // ---------------- DISCRIMINATOR_PK_PROP -----------------------------------------------

        public Task<DeletionBatchResult> DeleteSessionsByTerminalAsync(
            IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
            => DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.SessionsByTerminal, keys, ct);

        public Task<DeletionBatchResult> DeleteSessionsByStageAsync(
            IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
            => DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.SessionsByStage, keys, ct);

        public Task<DeletionBatchResult> DeleteDeadEndsByReasonAsync(
            IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
            => DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.DeadEndsByReason, keys, ct);

        public Task<DeletionBatchResult> DeleteClassifierVerdictsByIdLevelAsync(
            IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
            => DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.ClassifierVerdictsByIdLevel, keys, ct);

        public Task<DeletionBatchResult> DeleteSignalsByKindAsync(
            IReadOnlyList<(string Pk, string Rk)> keys, CancellationToken ct = default)
            => DeleteByExactKeysInBatchesAsync(Shared.Constants.TableNames.SignalsByKind, keys, ct);

        // ---------------- PK_RK_EXACT (single-row) --------------------------------------------

        public async Task DeleteDeviceSnapshotAsync(string tenantId, string sessionId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.DeviceSnapshot);
            try
            {
                await tableClient.DeleteEntityAsync(tenantId, sessionId, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Idempotent — already gone.
            }
        }

        // ---------------- PK_BY_SESSION (enumerate-then-delete) -------------------------------

        public Task<DeletionBatchResult> DeleteSessionSignalsAsync(string tenantId, string sessionId, CancellationToken ct = default)
            => DeletePkBySessionAsync(Shared.Constants.TableNames.Signals, tenantId, sessionId, ct);

        public Task<DeletionBatchResult> DeleteSessionDecisionTransitionsAsync(string tenantId, string sessionId, CancellationToken ct = default)
            => DeletePkBySessionAsync(Shared.Constants.TableNames.DecisionTransitions, tenantId, sessionId, ct);

        /// <summary>
        /// Enumerates every row in <paramref name="tableName"/> with
        /// <c>PartitionKey == {tenantId}_{sessionId}</c> and deletes them via
        /// <see cref="DeleteByExactKeysInBatchesAsync"/>. Used for PK_BY_SESSION-class cascade
        /// targets when the caller does not yet have a manifest in hand (e.g. legacy direct-delete
        /// paths or test fixtures); the §1 P6 cascade worker passes manifest keys to
        /// <see cref="DeleteByExactKeysInBatchesAsync"/> directly.
        /// </summary>
        private async Task<DeletionBatchResult> DeletePkBySessionAsync(
            string tableName, string tenantId, string sessionId, CancellationToken ct)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(tableName);
            var partitionKey = $"{tenantId}_{sessionId}";
            var safePk = ODataSanitizer.EscapeValue(partitionKey);

            var keys = new List<(string Pk, string Rk)>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq '{safePk}'",
                select: new[] { "PartitionKey", "RowKey" },
                cancellationToken: ct))
            {
                keys.Add((entity.PartitionKey, entity.RowKey));
            }

            return await DeleteByExactKeysInBatchesAsync(tableName, keys, ct);
        }
    }
}
