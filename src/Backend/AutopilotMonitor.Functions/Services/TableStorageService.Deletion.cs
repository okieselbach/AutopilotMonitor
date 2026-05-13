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
        public virtual async Task<TableEntity?> GetSessionRowAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
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

        // ============================================================ Tombstone-marker helpers (Codex F3) ====

        /// <summary>
        /// Writes (or replaces) the tombstone marker for <c>(tenantId, sessionId)</c>. Called by
        /// the cascade worker right before the FINAL Sessions-row delete in <c>ExecuteTombstoneAsync</c>;
        /// signed up as <c>Upsert(Replace)</c> so worker re-runs after a transient crash are
        /// idempotent (each replays with the same composite row content).
        /// </summary>
        public virtual async Task RecordSessionTombstoneAsync(
            string tenantId, string sessionId, string manifestId,
            TimeSpan retention, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(manifestId)) throw new ArgumentException("manifestId is required", nameof(manifestId));

            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionTombstones);
            var now = DateTime.UtcNow;
            var entity = new TableEntity(tenantId, sessionId)
            {
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ManifestId] = manifestId,
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.TombstonedAt] = now,
                [AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ExpiresAt] = now + retention,
            };
            await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
        }

        /// <summary>
        /// Reads the tombstone marker. Returns null on 404 OR when the marker's <c>ExpiresAt</c>
        /// has already passed — both indistinguishable from "no marker present" to writers, so
        /// fresh-enrollment paths can proceed once the retention window has lapsed. Maintenance
        /// physically prunes the rows; this filter is the in-flight safety net.
        /// </summary>
        public virtual async Task<TableEntity?> GetActiveSessionTombstoneAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionTombstones);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken);
                var entity = response.Value;
                var expiresAt = entity.GetDateTime(AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ExpiresAt);
                if (expiresAt is null || expiresAt.Value <= DateTime.UtcNow)
                {
                    return null;
                }
                return entity;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes the tombstone marker. Called by <see cref="Services.Deletion.SessionRestoreService"/>
        /// after a successful Full-Restore re-inserts the Sessions row — the new row is "fresh"
        /// and the marker would otherwise block its own writers. 404-tolerant (idempotent).
        /// </summary>
        public virtual async Task DeleteSessionTombstoneAsync(string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionTombstones);
            try
            {
                await tableClient.DeleteEntityAsync(tenantId, sessionId, ETag.All, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone — idempotent.
            }
        }

        /// <summary>
        /// Enumerates tombstone rows whose <c>ExpiresAt</c> column is at or before <paramref name="now"/>.
        /// Maintenance pruning consumer (<c>SessionDeletionMaintenanceFunction</c>) iterates this
        /// and per-row deletes. Cross-partition scan, acceptable for the 12h cadence and the
        /// small absolute count expected (one row per recently-deleted session, retention 7d).
        /// </summary>
        public virtual async IAsyncEnumerable<TableEntity> EnumerateExpiredSessionTombstonesAsync(
            DateTime now,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.SessionTombstones);
            // Server-side filter: avoid pulling the whole table when ramping up usage.
            var filter = $"{AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ExpiresAt} le datetime'{now:o}'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entity;
            }
        }

        /// <summary>
        /// Cascade-maintenance probe (PR6). Scans the Sessions table for rows whose
        /// <c>DeletionState</c> matches <paramref name="state"/>; used by
        /// <c>SessionDeletionMaintenanceFunction</c> for the stale-<c>Preparing</c> GC and the
        /// stranded-<c>Queued</c> alert. Returns the minimal projection needed by both callers
        /// (PK, RK, DeletionState, PendingDeletionManifestId, Timestamp).
        /// <para>
        /// Cross-partition scan — acceptable for a 12h maintenance cadence and the small absolute
        /// row count expected to be in non-<c>None</c> states at any moment (typically &lt; 10 per
        /// run during steady state). The yield contract lets the caller bound peak memory on
        /// pathological tenants.
        /// </para>
        /// </summary>
        public virtual async IAsyncEnumerable<TableEntity> GetSessionsByDeletionStateAsync(
            string state,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(state)) throw new ArgumentException("state is required", nameof(state));

            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.Sessions);
            var filter = $"DeletionState eq '{state}'";
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(
                filter,
                select: new[] { "PartitionKey", "RowKey", "DeletionState", "PendingDeletionManifestId", "Timestamp" },
                cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entity;
            }
        }

        /// <summary>
        /// Conditional <c>Preparing → None</c> revert used by the stale-Preparing GC (PR6).
        /// Reads the row, verifies it is still in <c>Preparing</c> with the expected manifest id,
        /// then writes back the cleared state under the captured ETag so a racing producer that
        /// just transitioned the row to <c>Queued</c> wins (412 → returns false). On success
        /// the manifest id is also cleared so the slot can be re-acquired on the next admin click.
        /// </summary>
        public virtual async Task<bool> RevertStalePreparingToNoneAsync(
            string tenantId, string sessionId, string expectedManifestId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("tenantId is required", nameof(tenantId));
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId is required", nameof(sessionId));
            if (string.IsNullOrEmpty(expectedManifestId)) throw new ArgumentException("expectedManifestId is required", nameof(expectedManifestId));

            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.Sessions);
            TableEntity entity;
            ETag etag;
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken);
                entity = response.Value;
                etag = response.Value.ETag;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }

            var currentState = entity.GetString("DeletionState");
            var currentManifestId = entity.GetString("PendingDeletionManifestId");

            if (!string.Equals(currentState, AutopilotMonitor.Shared.Models.Deletion.SessionDeletionState.Preparing, StringComparison.Ordinal)) return false;
            if (!string.Equals(currentManifestId, expectedManifestId, StringComparison.Ordinal)) return false;

            entity["DeletionState"] = AutopilotMonitor.Shared.Models.Deletion.SessionDeletionState.None;
            entity["PendingDeletionManifestId"] = null;

            try
            {
                await tableClient.UpdateEntityAsync(entity, etag, TableUpdateMode.Replace, cancellationToken);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Concurrent producer beat us to it — Preparing → Queued already happened. Caller treats as no-op.
                return false;
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
        public virtual async Task<DeletionBatchResult> DeleteByExactKeysInBatchesAsync(
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

        // ============================================================ Sessions DeletionState CAS (PR3) ====

        /// <summary>
        /// Outcome of a <see cref="CasSetSessionDeletionStateAsync"/> call. Distinguishes the
        /// happy path from the lost-CAS / wrong-state / session-missing branches the producer
        /// surfaces to its caller.
        /// </summary>
        public enum SessionDeletionStateCasOutcome
        {
            /// <summary>CAS succeeded; <see cref="SessionDeletionStateCasResult.CurrentState"/> is the new state.</summary>
            Updated,
            /// <summary>Sessions row not found at preflight time. No write performed.</summary>
            SessionMissing,
            /// <summary>Existing state did not match <c>fromState</c>. The current state is reported back so
            ///     the caller can decide: resume (state already past <c>fromState</c>), reject (different cascade),
            ///     or 409 (Poisoned).</summary>
            WrongState,
            /// <summary>ETag conflict on Update — concurrent writer raced us. Caller should retry.</summary>
            EtagConflict,
        }

        public class SessionDeletionStateCasResult
        {
            public SessionDeletionStateCasOutcome Outcome { get; set; }
            public string CurrentState { get; set; } = string.Empty;
            public string? CurrentManifestId { get; set; }
        }

        /// <summary>
        /// CAS-update <c>Sessions.DeletionState</c> from <paramref name="fromState"/> to
        /// <paramref name="toState"/>, optionally stamping <paramref name="newManifestId"/>
        /// when the transition is <c>None → Preparing</c>. ETag-CAS so a concurrent writer
        /// can't silently clobber. Returns the structured outcome for the producer's
        /// idempotent-resume / 409-conflict / poisoned-recovery branches.
        /// </summary>
        public virtual async Task<SessionDeletionStateCasResult> CasSetSessionDeletionStateAsync(
            string tenantId, string sessionId,
            string fromState, string toState,
            string? newManifestId,
            CancellationToken cancellationToken = default)
        {
            var tableClient = _tableServiceClient.GetTableClient(Shared.Constants.TableNames.Sessions);

            TableEntity existing;
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId, cancellationToken: cancellationToken);
                existing = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return new SessionDeletionStateCasResult { Outcome = SessionDeletionStateCasOutcome.SessionMissing };
            }

            var currentState = existing.GetString("DeletionState") ?? string.Empty;
            // Empty/null → treat as None for legacy rows that pre-date the column.
            if (string.IsNullOrEmpty(currentState)) currentState = Shared.Models.Deletion.SessionDeletionState.None;
            var currentManifestId = existing.GetString("PendingDeletionManifestId");

            if (currentState != fromState)
            {
                return new SessionDeletionStateCasResult
                {
                    Outcome = SessionDeletionStateCasOutcome.WrongState,
                    CurrentState = currentState,
                    CurrentManifestId = currentManifestId,
                };
            }

            existing["DeletionState"] = toState;
            // Manifest id is stamped on entry to Preparing and CLEARED when going back to None
            // (poisoned-recovery restore path); preserved in-flight transitions otherwise.
            if (toState == Shared.Models.Deletion.SessionDeletionState.None)
            {
                existing["PendingDeletionManifestId"] = null;
            }
            else if (!string.IsNullOrEmpty(newManifestId))
            {
                existing["PendingDeletionManifestId"] = newManifestId;
            }
            // else: keep existing PendingDeletionManifestId (Preparing → Queued → Running progression).

            try
            {
                await tableClient.UpdateEntityAsync(existing, existing.ETag, TableUpdateMode.Merge, cancellationToken);
                return new SessionDeletionStateCasResult
                {
                    Outcome = SessionDeletionStateCasOutcome.Updated,
                    CurrentState = toState,
                    CurrentManifestId = !string.IsNullOrEmpty(newManifestId) ? newManifestId : currentManifestId,
                };
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                return new SessionDeletionStateCasResult
                {
                    Outcome = SessionDeletionStateCasOutcome.EtagConflict,
                    CurrentState = currentState,
                    CurrentManifestId = currentManifestId,
                };
            }
        }

        // ============================================================ PR2 helpers continued ====

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
