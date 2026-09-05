#nullable enable
using System;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Einheitliche Transport-Einheit für Events, Signals und DecisionTransitions. Plan §2.7a.
    /// <para>
    /// <b>Storage-Schlüssel</b> (<see cref="PartitionKey"/> / <see cref="RowKey"/>) sind typspezifisch
    /// (Signals = <c>D10(SessionSignalOrdinal)</c>, Transitions = <c>D6(StepIndex)</c>, Events =
    /// bestehendes Schema) und gehen <b>nur</b> in die Azure-Table-Ablage.
    /// </para>
    /// <para>
    /// <b>Transport-Cursor</b> (<see cref="TelemetryItemId"/>) ist dagegen <b>monoton über alle
    /// Item-Typen innerhalb einer Session</b> und die alleinige Größe für Retry, Back-Pressure
    /// und Crash-Recovery.
    /// </para>
    /// </summary>
    public sealed class TelemetryItem
    {
        public TelemetryItem(
            TelemetryItemKind kind,
            string partitionKey,
            string rowKey,
            long telemetryItemId,
            long? sessionTraceOrdinal,
            string payloadJson,
            bool requiresImmediateFlush,
            DateTime enqueuedAtUtc,
            int retryCount = 0)
        {
            if (string.IsNullOrEmpty(partitionKey))
            {
                throw new ArgumentException("PartitionKey is mandatory.", nameof(partitionKey));
            }

            if (string.IsNullOrEmpty(rowKey))
            {
                throw new ArgumentException("RowKey is mandatory.", nameof(rowKey));
            }

            if (telemetryItemId < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(telemetryItemId),
                    "TelemetryItemId must be non-negative.");
            }

            if (payloadJson == null)
            {
                throw new ArgumentNullException(nameof(payloadJson));
            }

            if (retryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retryCount), "RetryCount must be non-negative.");
            }

            Kind = kind;
            PartitionKey = partitionKey;
            RowKey = rowKey;
            TelemetryItemId = telemetryItemId;
            SessionTraceOrdinal = sessionTraceOrdinal;
            PayloadJson = payloadJson;
            RequiresImmediateFlush = requiresImmediateFlush;
            EnqueuedAtUtc = enqueuedAtUtc;
            RetryCount = retryCount;
        }

        public TelemetryItemKind Kind { get; }

        /// <summary>Azure-Table-PartitionKey, typisch <c>{tenantId}_{sessionId}</c>.</summary>
        public string PartitionKey { get; }

        public string RowKey { get; }

        /// <summary>Transport-Cursor — monoton pro Session über ALLE Item-Typen.</summary>
        public long TelemetryItemId { get; }

        /// <summary>
        /// Transport-envelope view of <see cref="TelemetryItemId"/> — present when the
        /// item belongs to a session, null for agent-global items.
        /// <para>
        /// Codex follow-up #3 semantic clarification: this is the <b>transport-cursor</b>
        /// counter (spool-item ordering), <b>not</b> the reducer-step correlation counter
        /// that lives on <c>DecisionSignal.SessionTraceOrdinal</c> /
        /// <c>DecisionTransition.SessionTraceOrdinal</c>. Both counters are monotonic per
        /// session but come from independent sources (spool vs.
        /// <c>SessionTraceOrdinalProvider</c>). They share a name historically; the
        /// backend's <c>TelemetryPayloadParser</c> prefers the inner payload value on
        /// ingest for Signals and Transitions, so the reducer-correlation value wins
        /// in downstream storage. This envelope value is only used for transport-cursor
        /// bookkeeping.
        /// </para>
        /// </summary>
        public long? SessionTraceOrdinal { get; }

        /// <summary>Bereits fertig serialisiertes JSON der Payload (Event / DecisionSignal / DecisionTransition).</summary>
        public string PayloadJson { get; }

        /// <summary>Hinweis an den Orchestrator, diesen Item sofort zu drainen (Terminal-Flush). Nicht Teil der Atomaritäts-Garantie.</summary>
        public bool RequiresImmediateFlush { get; }

        public DateTime EnqueuedAtUtc { get; }

        /// <summary>Anzahl der bisher fehlgeschlagenen Upload-Versuche. Wird vom Orchestrator incrementiert.</summary>
        public int RetryCount { get; }

        /// <summary>
        /// The wire shape of this item. <see cref="TelemetryItemDto"/> (Shared) is the ONE class both
        /// ends serialise; this mapping is the only place the agent's validated model meets it.
        /// <c>TelemetryWireContractTests</c> proves every DTO property is populated here and
        /// freezes the resulting body under <c>tests/fixtures/telemetry-wire/</c> for the backend.
        /// </summary>
        public TelemetryItemDto ToWire() =>
            new TelemetryItemDto
            {
                Kind = Kind.ToString(),
                PartitionKey = PartitionKey,
                RowKey = RowKey,
                TelemetryItemId = TelemetryItemId,
                SessionTraceOrdinal = SessionTraceOrdinal,
                PayloadJson = PayloadJson,
                RequiresImmediateFlush = RequiresImmediateFlush,
                EnqueuedAtUtc = EnqueuedAtUtc,
                RetryCount = RetryCount,
            };

        /// <summary>Copy-with-incremented <see cref="RetryCount"/>. Immutable-Erhalt (§2.3 L.3).</summary>
        public TelemetryItem WithRetryIncremented() =>
            new TelemetryItem(
                Kind,
                PartitionKey,
                RowKey,
                TelemetryItemId,
                SessionTraceOrdinal,
                PayloadJson,
                RequiresImmediateFlush,
                EnqueuedAtUtc,
                RetryCount + 1);
    }
}
