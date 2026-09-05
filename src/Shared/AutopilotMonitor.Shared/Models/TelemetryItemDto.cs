using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The wire shape of one telemetry item sent by the V2 agent to
    /// <c>POST /api/agent/telemetry</c> (Plan §2.7a / §M5). This class IS the contract on both
    /// ends: the agent maps its ctor-validated, immutable <c>TelemetryItem</c> through
    /// <c>TelemetryItem.ToWire()</c> and serialises a list of these; the ingest deserialises the
    /// same class. Two tests pin it — the agent's <c>TelemetryWireContractTests</c> (round trip
    /// plus the frozen body under <c>tests/fixtures/telemetry-wire/</c>) and the backend's
    /// <c>TelemetryWireFixtureTests</c> (every frozen body must dispatch with zero rejections).
    /// <para>
    /// <b>Serialisation:</b> Newtonsoft.Json defaults on both sides — PascalCase member names,
    /// ISO-8601 UTC dates, <see cref="Kind"/> as the <see cref="TelemetryItemKind"/> name. A
    /// value the ingest cannot route or parse is answered with 422 + a poison body naming the
    /// RowKeys (<see cref="TelemetryItemsRejectedResponse"/>), never dropped silently.
    /// </para>
    /// </summary>
    [WireContract]
    public sealed class TelemetryItemDto
    {
        /// <summary>
        /// <see cref="TelemetryItemKind"/> name — routes to the destination table. Kept as a
        /// string on the wire so an unknown value fails ONE item (poison), not the whole batch.
        /// </summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Azure-Table PartitionKey, typically <c>{tenantId}_{sessionId}</c>.</summary>
        public string PartitionKey { get; set; } = string.Empty;

        public string RowKey { get; set; } = string.Empty;

        /// <summary>Transport-cursor — monotonic per session across all item kinds.</summary>
        public long TelemetryItemId { get; set; }

        /// <summary>Null for agent-global items (no session).</summary>
        public long? SessionTraceOrdinal { get; set; }

        /// <summary>Already-serialised JSON of the wrapped payload (EnrollmentEvent / DecisionSignal / DecisionTransition).</summary>
        public string PayloadJson { get; set; } = string.Empty;

        public bool RequiresImmediateFlush { get; set; }

        public DateTime EnqueuedAtUtc { get; set; }

        public int RetryCount { get; set; }
    }
}
