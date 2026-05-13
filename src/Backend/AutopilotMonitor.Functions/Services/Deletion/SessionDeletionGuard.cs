using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Single chokepoint for the cascade-delete writer-block invariant (Plan §1 P7 / §5 PR3).
    /// Two APIs split for hot-path I/O cost:
    /// <list type="bullet">
    ///   <item><see cref="ThrowIfLocked"/> — piggyback on a Sessions row the caller already
    ///       loaded (e.g. telemetry ingest, mark-success/failed). Caller must include
    ///       <c>DeletionState</c> + <c>PendingDeletionManifestId</c> in its <c>select</c>
    ///       projection. Zero added I/O.</item>
    ///   <item><see cref="EnsureWritableAsync"/> — standalone load for paths with no existing
    ///       Sessions read (e.g. queue handlers, manual rescan). Costs one Get round-trip.</item>
    /// </list>
    /// Both throw <see cref="SessionDeletionLockedException"/> when <c>DeletionState</c> is one
    /// of the lock states (Preparing / Queued / Running / Poisoned). Callers translate to the
    /// appropriate HTTP status / queue-handler outcome per the §5 PR3 wiring table.
    /// <para>
    /// Note: the guard is best-effort (Plan §16 R13). Under contention, a writer can pass the
    /// check at T2, the producer can CAS-Preparing at T3, and the writer's actual write can
    /// land at T4 — leaving a ghost row past the lock. Correctness lives in §1 P4 live
    /// verification + poison + §13 restore-from-poisoned, not here.
    /// </para>
    /// </summary>
    public class SessionDeletionGuard
    {
        private readonly ISessionDeletionInventoryReader _reader;
        private readonly ILogger<SessionDeletionGuard> _logger;

        public SessionDeletionGuard(ISessionDeletionInventoryReader reader, ILogger<SessionDeletionGuard> logger)
        {
            _reader = reader;
            _logger = logger;
        }

        /// <summary>
        /// Piggyback variant. Inspects an already-loaded Sessions row's <c>DeletionState</c>
        /// column. Throws <see cref="SessionDeletionLockedException"/> if the row is in any
        /// of the lock states. No-op if the row is null (caller treats absence per its own
        /// contract).
        /// </summary>
        public void ThrowIfLocked(TableEntity? sessionRow, string callerContext)
        {
            if (sessionRow == null) return;
            var state = sessionRow.GetString("DeletionState");
            if (!SessionDeletionState.IsLocked(state)) return;

            var manifestId = sessionRow.GetString("PendingDeletionManifestId");
            var tenantId = sessionRow.PartitionKey ?? string.Empty;
            var sessionId = sessionRow.RowKey ?? string.Empty;
            _logger.LogInformation(
                "SessionDeletionGuard blocked write: tenant={TenantId} session={SessionId} state={State} manifestId={ManifestId} caller={Caller}",
                tenantId, sessionId, state, manifestId, callerContext);
            throw new SessionDeletionLockedException(tenantId, sessionId, callerContext, state!, manifestId);
        }

        /// <summary>
        /// Standalone variant. Loads the Sessions row via the inventory reader and applies
        /// <see cref="ThrowIfLocked"/>. If the row does not exist, falls back to checking the
        /// <c>SessionTombstones</c> table: a fresh marker means the row was just tombstoned by
        /// the cascade worker and any post-tombstone write would orphan rows past the manifest's
        /// reach (Codex F3). Marker-found → throws <see cref="SessionDeletionLockedException"/>
        /// with <see cref="SessionTombstoneRecord.TombstonedStateLabel"/> as the current-state
        /// label. Genuine 404 (no marker) → silent pass for the fresh-enrollment path.
        /// </summary>
        public async Task EnsureWritableAsync(string tenantId, string sessionId, string callerContext, CancellationToken cancellationToken = default)
        {
            var sessionRow = await _reader.GetSessionRowAsync(tenantId, sessionId, cancellationToken);
            if (sessionRow != null)
            {
                ThrowIfLocked(sessionRow, callerContext);
                return;
            }

            // Sessions row absent → check the tombstone marker. Marker present → still locked.
            // Marker absent → safe to proceed (fresh registration or post-retention slot reuse).
            var tombstone = await _reader.GetActiveSessionTombstoneAsync(tenantId, sessionId, cancellationToken);
            if (tombstone == null) return;

            var manifestId = tombstone.GetString(AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.Columns.ManifestId);
            _logger.LogInformation(
                "SessionDeletionGuard blocked write past tombstone: tenant={TenantId} session={SessionId} manifestId={ManifestId} caller={Caller}",
                tenantId, sessionId, manifestId, callerContext);
            throw new SessionDeletionLockedException(
                tenantId, sessionId, callerContext,
                AutopilotMonitor.Shared.Models.Deletion.SessionTombstoneRecord.TombstonedStateLabel,
                manifestId);
        }
    }
}
