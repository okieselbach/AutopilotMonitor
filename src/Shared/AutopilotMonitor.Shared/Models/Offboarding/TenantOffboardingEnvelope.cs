using System;

namespace AutopilotMonitor.Shared.Models.Offboarding
{
    /// <summary>
    /// Queue message envelope carried on <c>tenant-offboarding</c> (and its poison sibling).
    /// Producer (<c>TenantOffboardFunction</c>) writes one envelope per tenant after the
    /// History/Pointer/Marker rows are committed. Worker (<c>TenantOffboardingWorker</c>)
    /// dequeues, runs <c>TenantOffboardingHandler.HandleAsync</c>, and either acks on
    /// success or re-enqueues itself with <see cref="DrainPollCount"/> incremented when the
    /// drain predicate is still in flight.
    /// </summary>
    public sealed class TenantOffboardingEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        public string TenantId { get; set; } = string.Empty;

        /// <summary>Always <c>OffboardingPartitionKeys.History</c>; kept for self-documentation.</summary>
        public string HistoryPartitionKey { get; set; } = string.Empty;

        /// <summary>"{yyyyMMddHHmmssfff}_{tenantId}" pointing at the matching history row.</summary>
        public string HistoryRowKey { get; set; } = string.Empty;

        public string InitiatedBy { get; set; } = string.Empty;
        public DateTime InitiatedAt { get; set; }
        public DateTime EnqueuedAt { get; set; }

        /// <summary>
        /// Drain self-poll counter. Handler re-enqueues with this incremented when the
        /// cascade-progress drain predicate has not yet settled. Capped at 60 (~2h) before
        /// failing the offboarding with <c>FailedPhase="drain_timeout"</c>.
        /// </summary>
        public int DrainPollCount { get; set; }
    }
}
