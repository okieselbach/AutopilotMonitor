using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>Outcome of <see cref="SessionDeletionGuard.GetLivenessAsync"/>.</summary>
    public enum SessionLiveness
    {
        /// <summary>Sessions row present and not in a cascade lock state.</summary>
        Live,
        /// <summary>No Sessions row: never registered, or deleted.</summary>
        Missing,
        /// <summary>Sessions row present but owned by a cascade (Preparing / Queued / Running / Poisoned).</summary>
        Locked,
    }

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
        public Task EnsureWritableAsync(string tenantId, string sessionId, string callerContext, CancellationToken cancellationToken = default)
            => EnsureWritableAndGetRowAsync(tenantId, sessionId, callerContext, cancellationToken);

        /// <summary>
        /// Same contract as <see cref="EnsureWritableAsync"/>, but returns the full Sessions row
        /// the guard had to load anyway (null when the row does not exist and no tombstone marker
        /// is active). Lets hot-path callers reuse the read — e.g. telemetry ingest feeds the
        /// row's <c>Status</c> into the stall-heal check instead of issuing a second point-read.
        /// </summary>
        public async Task<TableEntity?> EnsureWritableAndGetRowAsync(string tenantId, string sessionId, string callerContext, CancellationToken cancellationToken = default)
        {
            var sessionRow = await _reader.GetSessionRowAsync(tenantId, sessionId, cancellationToken);
            return await GuardRowAsync(sessionRow, tenantId, sessionId, callerContext, cancellationToken);
        }

        /// <summary>Columns the guard reads off the row itself; a projected read adds them to the caller's list.</summary>
        public static readonly string[] LockColumns = { "DeletionState", "PendingDeletionManifestId" };

        /// <summary>
        /// For writers that DERIVE rows for a session (queue handlers, the manual rescan). Unlike
        /// the registration/ingest contract, a missing Sessions row is a stop here, not a pass:
        /// a derived row for a session the backend does not know can never be reached by a
        /// cascade and would only feed the orphan sweep. Read errors propagate (queue retry).
        /// </summary>
        public async Task<SessionLiveness> GetLivenessAsync(string tenantId, string sessionId, string callerContext, CancellationToken cancellationToken = default)
        {
            var sessionRow = await _reader.GetSessionRowAsync(tenantId, sessionId, LockColumns, cancellationToken);
            if (sessionRow == null)
            {
                _logger.LogWarning(
                    "SessionDeletionGuard: no Sessions row — derived write skipped: tenant={TenantId} session={SessionId} caller={Caller}",
                    tenantId, sessionId, callerContext);
                return SessionLiveness.Missing;
            }

            var state = sessionRow.GetString("DeletionState");
            if (SessionDeletionState.IsLocked(state))
            {
                _logger.LogInformation(
                    "SessionDeletionGuard: session locked — derived write skipped: tenant={TenantId} session={SessionId} state={State} caller={Caller}",
                    tenantId, sessionId, state, callerContext);
                return SessionLiveness.Locked;
            }

            return SessionLiveness.Live;
        }

        /// <summary>
        /// <see cref="EnsureWritableAndGetRowAsync(string, string, string, CancellationToken)"/> with the
        /// row projected to <paramref name="select"/> plus <see cref="LockColumns"/> — the agent hot
        /// path's form (see <c>SessionRowProjections.GuardRow</c>). The returned row carries only
        /// those columns.
        /// </summary>
        public async Task<TableEntity?> EnsureWritableAndGetRowAsync(string tenantId, string sessionId, string callerContext, IReadOnlyCollection<string> select, CancellationToken cancellationToken = default)
        {
            var sessionRow = await _reader.GetSessionRowAsync(tenantId, sessionId, select.Concat(LockColumns).Distinct(), cancellationToken);
            return await GuardRowAsync(sessionRow, tenantId, sessionId, callerContext, cancellationToken);
        }

        private async Task<TableEntity?> GuardRowAsync(TableEntity? sessionRow, string tenantId, string sessionId, string callerContext, CancellationToken cancellationToken)
        {
            if (sessionRow != null)
            {
                ThrowIfLocked(sessionRow, callerContext);
                return sessionRow;
            }

            // Sessions row absent → check the tombstone marker. Marker present → still locked.
            // Marker absent → safe to proceed (fresh registration or post-retention slot reuse).
            var tombstone = await _reader.GetActiveSessionTombstoneAsync(tenantId, sessionId, cancellationToken);
            if (tombstone == null) return null;

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
