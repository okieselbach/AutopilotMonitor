using System;

namespace AutopilotMonitor.Shared.Models.Deletion
{
    /// <summary>
    /// State-machine values stored on the Sessions row's <c>DeletionState</c> column.
    /// <list type="bullet">
    ///   <item><c>None</c> — no cascade in flight; writes are allowed.</item>
    ///   <item><c>Preparing</c> — producer has CAS-claimed the row, manifest enumeration in
    ///       progress; writers must reject. Stale rows older than 1h with no progress blob are
    ///       GC'd back to <c>None</c> by the cascade-maintenance function (plan §10).</item>
    ///   <item><c>Queued</c> — manifest + progress blob uploaded, envelope enqueued; worker has
    ///       not yet picked it up. Never auto-cleared (operator inspection required).</item>
    ///   <item><c>Running</c> — worker is executing cascade steps. Never auto-cleared.</item>
    ///   <item><c>Poisoned</c> — worker hit max-dequeue or live verification failed. Requires
    ///       <c>POST /api/admin/sessions/{id}/restore</c> (PR4) to recover.</item>
    /// </list>
    /// <para>
    /// <c>Completed</c> is deliberately NOT a value — the cascade FINAL step removes the Sessions
    /// row, so "completed" is observable via row-absence + <see cref="DeletionProgress.CompletedAt"/>,
    /// not via a stable state value (plan §1 P7 + §16-R2).
    /// </para>
    /// </summary>
    public static class SessionDeletionState
    {
        public const string None      = "None";
        public const string Preparing = "Preparing";
        public const string Queued    = "Queued";
        public const string Running   = "Running";
        public const string Poisoned  = "Poisoned";

        /// <summary>
        /// True when the value is one of the legal lock states (any non-None / non-empty state
        /// that should block writers). Used by <c>SessionDeletionGuard</c>.
        /// </summary>
        public static bool IsLocked(string? state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            return state == Preparing || state == Queued || state == Running || state == Poisoned;
        }

        /// <summary>
        /// Returns true when the given current+next pair is a legal transition. Used by the
        /// producer/worker before issuing a CAS update so a corrupted prior state surfaces fast.
        /// </summary>
        public static bool IsValidTransition(string? from, string to)
        {
            // Treat null/empty as None — older rows pre-PR3 have no DeletionState column.
            var current = string.IsNullOrEmpty(from) ? None : from!;
            switch (current)
            {
                case None:      return to == Preparing;
                case Preparing: return to == Queued || to == None || to == Poisoned;
                case Queued:    return to == Running || to == Poisoned;
                case Running:   return to == Poisoned; // success path removes the row, not a transition
                case Poisoned:  return to == None;     // operator restore re-opens the session
                default:        return false;
            }
        }
    }

    /// <summary>
    /// Short-lived "session was tombstoned" marker written into the <c>SessionTombstones</c> table
    /// by the cascade worker immediately before the FINAL Sessions-row delete. Read by the writer
    /// guard when a missing Sessions row would otherwise look like "fresh registration allowed".
    /// <para>
    /// Lifecycle: Worker → write marker with <see cref="ExpiresAt"/> = <c>TombstonedAt +
    /// <see cref="SessionTombstoneRetention"/></c>. Guard → block writes while marker is present
    /// and not expired. Restore → delete marker on full-restore. Maintenance → prune expired
    /// rows. <see cref="DeletionState.Tombstoned"/> is the sentinel state value the guard
    /// exception carries (never persisted on Sessions rows).
    /// </para>
    /// </summary>
    public sealed class SessionTombstoneRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ManifestId { get; set; } = string.Empty;
        public DateTime TombstonedAt { get; set; }
        public DateTime ExpiresAt { get; set; }

        /// <summary>Column-name constants — kept here so the storage layer + tests share a single source of truth.</summary>
        public static class Columns
        {
            public const string ManifestId   = "ManifestId";
            public const string TombstonedAt = "TombstonedAt";
            public const string ExpiresAt    = "ExpiresAt";
        }

        /// <summary>
        /// Sentinel <c>DeletionState</c> value the guard reports in
        /// <c>SessionDeletionLockedException.CurrentState</c> when a missing Sessions row carries
        /// an active tombstone marker. NOT a member of <see cref="SessionDeletionState"/> because
        /// no Sessions row ever stores this value — the row is gone by the time the marker is
        /// active.
        /// </summary>
        public const string TombstonedStateLabel = "Tombstoned";

        /// <summary>
        /// Default retention window for the tombstone marker. Long enough to catch agent retries
        /// from devices returning online after a vacation; short enough to keep the table small.
        /// </summary>
        public static readonly TimeSpan SessionTombstoneRetention = TimeSpan.FromDays(7);
    }

    /// <summary>
    /// Wire envelope for the <c>session-deletion</c> queue (PR3 producer → PR4 worker). The
    /// worker re-hydrates the manifest blob by (TenantId, SessionId, ManifestId) and verifies
    /// the snapshot SHA-256 against the progress blob before executing any step.
    /// </summary>
    public class SessionDeletionEnvelope
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string ManifestId { get; set; } = string.Empty;

        /// <summary>Free-form trigger reason — e.g. <c>admin_delete</c>, <c>retention_cutoff</c>.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>UTC timestamp when the producer enqueued this envelope; informational.</summary>
        public DateTime EnqueuedAt { get; set; }
    }
}
