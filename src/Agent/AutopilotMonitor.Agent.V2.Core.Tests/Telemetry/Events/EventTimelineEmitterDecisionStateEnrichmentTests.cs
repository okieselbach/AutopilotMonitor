using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using SharedConstants = AutopilotMonitor.Shared.Constants;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    /// <summary>
    /// Tests for the decisionState-snapshot enrichment in
    /// <see cref="EventTimelineEmitter.Emit"/> (Plan §A — Edge-Triggered State
    /// Snapshots, 2026-05-03). The enrichment runs after <c>ResolveData</c> so it
    /// works for both event paths (parameter-rebuilt Data and typedPayload-merged
    /// Data) and never clobbers existing event-specific fields.
    /// </summary>
    public sealed class EventTimelineEmitterDecisionStateEnrichmentTests
    {
        private static readonly DateTime At = new DateTime(2026, 5, 1, 13, 45, 32, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeTelemetryTransport Transport { get; } = new FakeTelemetryTransport();
            public EventSequenceCounter Counter { get; }
            public TelemetryEventEmitter Inner { get; }
            public EventTimelineEmitter Sut { get; }

            public Rig()
            {
                Counter = new EventSequenceCounter(new EventSequencePersistence(Tmp.File("seq.json")));
                Inner = new TelemetryEventEmitter(Transport, Counter, "S1", "T1");
                Sut = new EventTimelineEmitter(Inner);
            }

            public JObject EmittedData() =>
                (JObject)JObject.Parse(Transport.Enqueued[0].PayloadJson)["Data"]!;

            public void Dispose() => Tmp.Dispose();
        }

        private static DecisionState State() => DecisionState.CreateInitial("S1", "T1");

        // ============================================================================
        // Full-coverage: every anchor event-type gets decisionState enrichment
        // ============================================================================

        public static IEnumerable<object[]> AllAnchorEventTypes => new[]
        {
            new object[] { SharedConstants.EventTypes.AgentStarted },
            new object[] { SharedConstants.EventTypes.EspPhaseChanged },
            new object[] { SharedConstants.EventTypes.NetworkStateChange },
            new object[] { SharedConstants.EventTypes.DesktopArrived },
            new object[] { SharedConstants.EventTypes.AadPlaceholderUserDetected },
            new object[] { SharedConstants.EventTypes.AadUserJoinedObserved },
            new object[] { SharedConstants.EventTypes.HybridLoginPending },
            new object[] { SharedConstants.EventTypes.AgentShuttingDown },
            new object[] { SharedConstants.EventTypes.SystemRebootDetected },
            new object[] { SharedConstants.EventTypes.PerformanceCollectorStopped },
            new object[] { SharedConstants.EventTypes.AgentMetricsCollectorStopped },
            new object[] { SharedConstants.EventTypes.PriorRunDiedWithState },
        };

        [Theory]
        [MemberData(nameof(AllAnchorEventTypes))]
        public void Anchor_event_gets_decisionState_added_to_Data(string anchorEventType)
        {
            using var r = new Rig();
            var parameters = new Dictionary<string, string> { ["eventType"] = anchorEventType };

            r.Sut.Emit(parameters, State(), At);

            var data = r.EmittedData();
            Assert.NotNull(data["decisionState"]);
            // schemaVersion proves we wrote the actual builder output, not just any key
            Assert.Equal(
                DecisionStateSnapshotBuilder.SchemaVersion,
                (string?)data["decisionState"]!["schemaVersion"]);
        }

        // ============================================================================
        // Parameter-based path — existing Data fields preserved
        // ============================================================================

        [Fact]
        public void Anchor_with_parameter_data_preserves_event_specific_fields()
        {
            using var r = new Rig();
            // esp_phase_changed historically carries reason + custom keys via parameters
            // (not typedPayload). Verify those parameters survive into the wire Data
            // alongside the new decisionState.
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.EspPhaseChanged,
                ["reason"] = "IME-ESP-PHASE",
                ["espPhase"] = "AccountSetup",
            };

            r.Sut.Emit(parameters, State(), At);

            var data = r.EmittedData();
            // Original parameter fields stay intact.
            Assert.Equal("IME-ESP-PHASE", (string?)data["reason"]);
            Assert.Equal("AccountSetup", (string?)data["espPhase"]);
            // Plus enrichment.
            Assert.NotNull(data["decisionState"]);
        }

        // ============================================================================
        // typedPayload-based path — existing Data fields preserved
        // ============================================================================

        [Fact]
        public void Anchor_with_typedPayload_dict_preserves_existing_fields()
        {
            using var r = new Rig();
            // network_state_change is the canonical typedPayload-bearing anchor — it
            // flows via InformationalEventPost.Emit(EnrollmentEvent) which routes
            // EnrollmentEvent.Data through typedPayload. Verify the original payload
            // survives the enrichment.
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.NetworkStateChange,
            };
            var typedPayload = new Dictionary<string, object>
            {
                ["from"] = "WiFi 'GBM_Wireless'",
                ["to"] = "WiFi 'GBM_Guests'",
                ["transition"] = "wifi_change",
            };

            r.Sut.Emit(parameters, State(), At, typedPayload);

            var data = r.EmittedData();
            Assert.Equal("WiFi 'GBM_Wireless'", (string?)data["from"]);
            Assert.Equal("WiFi 'GBM_Guests'", (string?)data["to"]);
            Assert.Equal("wifi_change", (string?)data["transition"]);
            Assert.NotNull(data["decisionState"]);
        }

        // ============================================================================
        // Non-anchor — no enrichment
        // ============================================================================

        [Theory]
        [InlineData("enrollment_complete")]    // already carries DecisionAuditTrail
        [InlineData("enrollment_failed")]      // already carries DecisionAuditTrail
        [InlineData("app_install_completed")]  // hochfrequent, intentionally excluded
        [InlineData("performance_snapshot")]   // hochfrequent, intentionally excluded
        [InlineData("download_progress")]      // hochfrequent, intentionally excluded
        public void Non_anchor_event_does_NOT_get_decisionState(string nonAnchorEventType)
        {
            using var r = new Rig();
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = nonAnchorEventType,
                ["reason"] = "test",
            };

            r.Sut.Emit(parameters, State(), At);

            var data = r.EmittedData();
            Assert.Null(data["decisionState"]);
        }

        // ============================================================================
        // Idempotence — pre-existing decisionState in typedPayload is not clobbered
        // ============================================================================

        [Fact]
        public void Anchor_with_decisionState_already_in_typedPayload_is_NOT_clobbered()
        {
            using var r = new Rig();
            // Defensive contract: if for some reason an upstream caller already put a
            // "decisionState" key in the typedPayload, the emitter must not overwrite
            // it. This shouldn't occur in production (only the emitter writes that
            // key) but documents the merge invariant.
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.AgentStarted,
            };
            var prebuilt = new Dictionary<string, object?>
            {
                ["sentinel"] = "prebuilt-snapshot",
            };
            var typedPayload = new Dictionary<string, object>
            {
                ["decisionState"] = prebuilt,
                ["original"] = "value",
            };

            r.Sut.Emit(parameters, State(), At, typedPayload);

            var data = r.EmittedData();
            // The prebuilt sentinel survives — emitter respected the existing key.
            Assert.Equal("prebuilt-snapshot", (string?)data["decisionState"]!["sentinel"]);
            // Original sibling field also survives.
            Assert.Equal("value", (string?)data["original"]);
        }

        // ============================================================================
        // decisionState shape sanity — reads through to the actual builder output
        // ============================================================================

        [Fact]
        public void Anchor_decisionState_carries_top_level_allowlist_keys()
        {
            using var r = new Rig();
            var parameters = new Dictionary<string, string>
            {
                ["eventType"] = SharedConstants.EventTypes.DesktopArrived,
            };

            r.Sut.Emit(parameters, State(), At);

            var data = r.EmittedData();
            var snap = (JObject)data["decisionState"]!;
            // Spot-check the top-level allowlist contract — full coverage lives in
            // DecisionStateSnapshotBuilderTests.
            Assert.NotNull(snap["schemaVersion"]);
            Assert.NotNull(snap["stepIndex"]);
            Assert.NotNull(snap["lastAppliedSignalOrdinal"]);
            Assert.NotNull(snap["stage"]);
            Assert.NotNull(snap["facts"]);
            Assert.NotNull(snap["scenario"]);
            Assert.NotNull(snap["activeDeadlines"]);
            // Outcome is null on a fresh state — must be present as JSON null,
            // not missing (consumer differentiates "no outcome" from "missing field").
            Assert.True(snap.ContainsKey("outcome"));
            Assert.Equal(JTokenType.Null, snap["outcome"]!.Type);
        }
    }
}
