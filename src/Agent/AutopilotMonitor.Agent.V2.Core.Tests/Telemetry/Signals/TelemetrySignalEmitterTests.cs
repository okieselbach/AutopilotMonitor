using System;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Signals;
using Newtonsoft.Json.Linq;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Signals
{
    /// <summary>
    /// Pure emitter tests for <see cref="TelemetrySignalEmitter"/> (Codex Finding 1 fix).
    /// Verifies the agent-side wire format and transport projection; the cross-layer
    /// round-trip with the backend parser lives in <c>TelemetrySignalRoundTripTests</c>.
    /// </summary>
    public sealed class TelemetrySignalEmitterTests
    {
        private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal Signal(long ordinal, DecisionSignalKind kind = DecisionSignalKind.EspPhaseChanged)
            => new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal + 100,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: At,
                sourceOrigin: "EspAndHelloTrackerAdapter",
                evidence: new Evidence(EvidenceKind.Raw, $"raw-{ordinal}", $"evidence-{ordinal}"));

        [Fact]
        public void Emit_routes_signal_through_transport_with_correct_keys()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(transport, SessionId, TenantId);

            emitter.Emit(Signal(42));

            Assert.Equal(1, transport.EnqueueCount);
            var item = transport.Enqueued[0];
            Assert.Equal(TelemetryItemKind.Signal, item.Kind);
            Assert.Equal($"{TenantId}_{SessionId}", item.PartitionKey);
            Assert.Equal("0000000000000000042", item.RowKey);            // D19 padding — matches TableSignalRepository.BuildRowKey
            Assert.False(item.RequiresImmediateFlush);
        }

        [Fact]
        public void Emit_serializes_Kind_as_string_enum_name_for_backend_parser_compatibility()
        {
            // Backend TelemetryPayloadParser.ParseSignal reads `(string?)root["Kind"]`. Default
            // Newtonsoft serializes enums as ints, which would silently fail the cast. The emitter
            // must configure StringEnumConverter.
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(transport, SessionId, TenantId);

            emitter.Emit(Signal(1, DecisionSignalKind.EspTerminalFailure));

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal("EspTerminalFailure", (string?)parsed["Kind"]);
        }

        [Fact]
        public void Emit_emits_PascalCase_fields_the_backend_parser_reads()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(transport, SessionId, TenantId);

            emitter.Emit(Signal(7));

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal(7L,                              (long?)parsed["SessionSignalOrdinal"]);
            Assert.Equal(107L,                            (long?)parsed["SessionTraceOrdinal"]);
            Assert.Equal("EspPhaseChanged",               (string?)parsed["Kind"]);
            Assert.Equal(1,                               (int?)parsed["KindSchemaVersion"]);
            Assert.Equal("EspAndHelloTrackerAdapter",     (string?)parsed["SourceOrigin"]);
            Assert.NotNull(parsed["OccurredAtUtc"]);
        }

        [Fact]
        public void Emit_RowKey_padding_survives_long_MaxValue()
        {
            // 19 digits covers long.MaxValue. Guard the invariant — lex ordering matches numeric
            // ordinal order only with full padding.
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(transport, SessionId, TenantId);

            emitter.Emit(Signal(long.MaxValue));

            Assert.Equal("9223372036854775807", transport.Enqueued[0].RowKey);
        }

        [Fact]
        public void Emit_rejects_null_signal()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetrySignalEmitter(transport, SessionId, TenantId);

            Assert.Throws<ArgumentNullException>(() => emitter.Emit(null!));
            Assert.Equal(0, transport.EnqueueCount);
        }

        [Fact]
        public void Ctor_rejects_null_or_empty_identifiers()
        {
            var transport = new FakeTelemetryTransport();
            Assert.Throws<ArgumentException>(() => new TelemetrySignalEmitter(transport, "",         TenantId));
            Assert.Throws<ArgumentException>(() => new TelemetrySignalEmitter(transport, SessionId,  ""));
            Assert.Throws<ArgumentNullException>(() => new TelemetrySignalEmitter(null!, SessionId,  TenantId));
        }
    }
}
