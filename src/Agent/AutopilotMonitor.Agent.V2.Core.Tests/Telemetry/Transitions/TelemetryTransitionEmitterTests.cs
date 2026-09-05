using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using Newtonsoft.Json.Linq;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Transitions
{
    /// <summary>
    /// Pure emitter tests for <see cref="TelemetryTransitionEmitter"/> (Codex Finding 1 fix).
    /// Cross-layer round-trip with the backend parser lives in
    /// <c>TelemetryTransitionRoundTripTests</c>.
    /// </summary>
    public sealed class TelemetryTransitionEmitterTests
    {
        private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionTransition Taken(int stepIndex, SessionStage to = SessionStage.EspDeviceSetup)
            => new DecisionTransition(
                stepIndex: stepIndex,
                sessionTraceOrdinal: stepIndex + 500,
                signalOrdinalRef: stepIndex + 1,
                occurredAtUtc: At,
                trigger: "EspExiting",
                fromStage: SessionStage.AwaitingEspPhaseChange,
                toStage: to,
                taken: true,
                deadEndReason: null,
                reducerVersion: "1.0.0");

        [Fact]
        public void Emit_routes_transition_through_transport_with_correct_keys()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            emitter.Emit(Taken(7));

            Assert.Equal(1, transport.EnqueueCount);
            var item = transport.Enqueued[0];
            Assert.Equal(TelemetryItemKind.DecisionTransition, item.Kind);
            Assert.Equal($"{TenantId}_{SessionId}", item.PartitionKey);
            Assert.Equal("0000000007", item.RowKey);                      // D10 padding — matches TableDecisionTransitionRepository.BuildRowKey
            Assert.False(item.RequiresImmediateFlush);
        }

        [Fact]
        public void Emit_serializes_FromStage_and_ToStage_as_string_enum_names()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            emitter.Emit(Taken(1, to: SessionStage.WhiteGloveSealed));

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal("AwaitingEspPhaseChange", (string?)parsed["FromStage"]);
            Assert.Equal("WhiteGloveSealed", (string?)parsed["ToStage"]);
        }

        [Fact]
        public void Emit_emits_PascalCase_fields_the_backend_parser_reads()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            emitter.Emit(Taken(3));

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal(3,                  (int?)parsed["StepIndex"]);
            Assert.Equal(503L,               (long?)parsed["SessionTraceOrdinal"]);
            Assert.Equal(4L,                 (long?)parsed["SignalOrdinalRef"]);
            Assert.Equal("EspExiting",       (string?)parsed["Trigger"]);
            Assert.Equal(true,               (bool?)parsed["Taken"]);
            Assert.Equal("1.0.0",            (string?)parsed["ReducerVersion"]);
            Assert.NotNull(parsed["OccurredAtUtc"]);
        }

        [Fact]
        public void Emit_dead_end_preserves_DeadEndReason_and_Taken_false()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            var blocked = new DecisionTransition(
                stepIndex: 5,
                sessionTraceOrdinal: 505,
                signalOrdinalRef: 6,
                occurredAtUtc: At,
                trigger: "EspExiting",
                fromStage: SessionStage.EspDeviceSetup,
                toStage: SessionStage.EspDeviceSetup,
                taken: false,
                deadEndReason: "hybrid_reboot_gate_blocking",
                reducerVersion: "1.0.0");

            emitter.Emit(blocked);

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            Assert.Equal(false,                          (bool?)parsed["Taken"]);
            Assert.Equal("hybrid_reboot_gate_blocking",  (string?)parsed["DeadEndReason"]);
        }

        [Fact]
        public void Emit_with_classifier_verdict_emits_nested_object_parser_can_read()
        {
            // Backend ParseTransition reads ClassifierVerdict.ClassifierId and ClassifierVerdict.Level
            // (also accepts "HypothesisLevel" as fallback). HypothesisLevel enum must serialize as
            // a string — StringEnumConverter handles it.
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            var verdict = new ClassifierVerdict(
                classifierId: "whiteglove-sealing",
                level: HypothesisLevel.Strong,
                score: 80,
                contributingFactors: new List<string> { "PatternMatched" },
                reason: "test",
                inputHash: "abc123");

            var withVerdict = new DecisionTransition(
                stepIndex: 9,
                sessionTraceOrdinal: 509,
                signalOrdinalRef: 10,
                occurredAtUtc: At,
                trigger: "EspExiting",
                fromStage: SessionStage.EspDeviceSetup,
                toStage: SessionStage.WhiteGloveSealed,
                taken: true,
                deadEndReason: null,
                reducerVersion: "1.0.0",
                classifierVerdict: verdict);

            emitter.Emit(withVerdict);

            var parsed = JObject.Parse(transport.Enqueued[0].PayloadJson);
            var verdictJson = (JObject)parsed["ClassifierVerdict"]!;
            Assert.Equal("whiteglove-sealing", (string?)verdictJson["ClassifierId"]);
            Assert.Equal("Strong",             (string?)verdictJson["Level"]);
        }

        [Fact]
        public void Emit_rejects_null_transition()
        {
            var transport = new FakeTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(transport, SessionId, TenantId);

            Assert.Throws<ArgumentNullException>(() => emitter.Emit(null!));
            Assert.Equal(0, transport.EnqueueCount);
        }

        [Fact]
        public void Ctor_rejects_null_or_empty_identifiers()
        {
            var transport = new FakeTelemetryTransport();
            Assert.Throws<ArgumentException>(() => new TelemetryTransitionEmitter(transport, "",         TenantId));
            Assert.Throws<ArgumentException>(() => new TelemetryTransitionEmitter(transport, SessionId,  ""));
            Assert.Throws<ArgumentNullException>(() => new TelemetryTransitionEmitter(null!, SessionId,  TenantId));
        }
    }
}
