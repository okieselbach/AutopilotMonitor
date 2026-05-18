using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// Per-tenant snapshot of the cascade enqueue outcome captured at the first handler pickup.
    /// Drives the drain predicate: every entry is matched against either a progress blob
    /// (Enqueued/AlreadyInFlight), a no-op (SessionNotFound), a fail-closed condition
    /// (KillSwitchActive/Poisoned), or a bounded retry loop (CasExhausted).
    /// <para>
    /// Stored as a single blob at
    /// <c>{BlobContainers.OffboardingState}/{tenantId}/{historyRowKey}.expectations.json</c>.
    /// The container is deliberately separate from <c>deletion-manifests</c> because the latter
    /// is wiped in Phase 2.E; storing this blob there would make crash-recovery between 2.E
    /// and 2.G impossible.
    /// </para>
    /// </summary>
    public sealed class OffboardingExpectations
    {
        /// <summary>Bump on breaking-schema changes. Drain-probe fail-closed if mismatch.</summary>
        public int SchemaVersion { get; set; } = 1;

        public string TenantId { get; set; } = string.Empty;

        /// <summary>Matching history row's RowKey, so a blob can be paired back to its run.</summary>
        public string HistoryRowKey { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// True only after the session enumerator iterated to completion without exception AND
        /// the upload is being written. Default false; drain-probe treats <c>false</c> as
        /// "this blob was uploaded with incomplete data" → fail-closed with
        /// <c>FailedPhase="enumeration_incomplete"</c>. Critical disambiguation: lets the
        /// probe tell apart "0 sessions, happy path" from "0 entries because enumerator
        /// crashed before yielding any".
        /// </summary>
        public bool EnumerationCompleted { get; set; }

        /// <summary>
        /// Number of sessions the enumerator yielded BEFORE per-session enqueue ran. Must equal
        /// <see cref="Expectations"/>.Count on upload; mismatch → fail-closed.
        /// </summary>
        public int EnumeratedSessionCount { get; set; }

        public List<OffboardingExpectation> Expectations { get; set; } = new();
    }

    /// <summary>One per session that the enumerator returned.</summary>
    public sealed class OffboardingExpectation
    {
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// Manifest id returned by <c>SessionDeletionProducer.EnqueueAsync</c>. Null for outcomes
        /// that did not produce a cascade (SessionNotFound, KillSwitchActive, CasExhausted) or
        /// when the producer returned <c>AlreadyInFlight</c> against a Preparing-without-snapshot
        /// state. The drain probe fails closed on Enqueued/AlreadyInFlight with a null/empty
        /// manifest id.
        /// </summary>
        public string? ManifestId { get; set; }

        /// <summary>
        /// String form of <c>SessionDeletionProducer.EnqueueOutcome</c>:
        /// <c>Enqueued | AlreadyInFlight | SessionNotFound | KillSwitchActive | Poisoned | CasExhausted</c>.
        /// Stored as string so the shared assembly doesn't need to reference the
        /// Functions-only enum and so future enum additions don't break already-written blobs.
        /// </summary>
        public string Outcome { get; set; } = string.Empty;

        /// <summary>
        /// CasExhausted retry counter. Drain-probe re-enqueues this session and increments;
        /// after 3 retries the offboarding fails closed with <c>FailedPhase="cas_exhausted"</c>.
        /// </summary>
        public int RetryCount { get; set; }
    }
}
