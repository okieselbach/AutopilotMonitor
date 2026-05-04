using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan V2-parity PR-B — the reducer is the single state owner, and telemetry events
    /// for the three lifecycle signals (SystemRebootObserved, SessionRecovered→WG bridge,
    /// AdminPreemptionDetected) appear as <see cref="DecisionEffectKind.EmitEventTimelineEntry"/>
    /// effects rather than being synthesised by the V2 host.
    /// </summary>
    public sealed class ReducerTimelineEffectTests
    {
        private static DecisionSignal BuildSignal(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test",
            int ordinal = 1)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        // ---------------------------------------------------------------- SystemRebootObserved

        [Fact]
        public void SystemRebootObservedV1_emits_system_reboot_detected_timeline_entry()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var payload = new Dictionary<string, string>
            {
                ["previousExitType"] = "reboot_kill",
                ["lastBootUtc"] = "2026-04-22T08:00:00.0000000Z",
            };
            var signal = BuildSignal(
                DecisionSignalKind.SystemRebootObserved,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc),
                payload);

            var step = engine.Reduce(state, signal);

            Assert.NotNull(step.NewState.SystemRebootUtc);
            var timelineEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry);
            Assert.NotNull(timelineEffect.Parameters);
            Assert.Equal("system_reboot_detected", timelineEffect.Parameters!["eventType"]);
            Assert.Equal("reboot_kill", timelineEffect.Parameters["previousExitType"]);
            Assert.Equal("2026-04-22T08:00:00.0000000Z", timelineEffect.Parameters["lastBootUtc"]);
        }

        [Fact]
        public void SystemRebootObservedV1_without_payload_still_emits_timeline_entry()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = BuildSignal(
                DecisionSignalKind.SystemRebootObserved,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc));

            var step = engine.Reduce(state, signal);

            var timelineEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry);
            Assert.Equal("system_reboot_detected", timelineEffect.Parameters!["eventType"]);
            Assert.False(timelineEffect.Parameters.ContainsKey("previousExitType"));
        }

        // (PR-B 2026-05-04: WhiteGlovePart1To2 bridge test removed — the SessionRecovered
        // signal kind + bridge handler were deleted with the rest of the V2 WG-Part-2
        // apparatus. The post-reseal-reboot flow is now driven by the orchestrator-side
        // archive-and-reset + a direct `whiteglove_resumed` InformationalEvent emit;
        // EnrollmentOrchestratorPart2EmissionTests covers the new path.)

        // ---------------------------------------------------- AdminPreemptionDetected

        [Fact]
        public void AdminPreemptionDetectedV1_succeeded_transitions_to_completed_and_emits_complete_event()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = BuildSignal(
                DecisionSignalKind.AdminPreemptionDetected,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string> { ["adminOutcome"] = "Succeeded" },
                sourceOrigin: "register_session_response");

            var step = engine.Reduce(state, signal);

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.AdminPreempted, step.NewState.Outcome);

            var effect = Assert.Single(step.Effects);
            Assert.Equal(DecisionEffectKind.EmitEventTimelineEntry, effect.Kind);
            Assert.Equal("enrollment_complete", effect.Parameters!["eventType"]);
            Assert.Equal("Succeeded", effect.Parameters["adminAction"]);
            Assert.Equal("register_session_response", effect.Parameters["source"]);
        }

        [Fact]
        public void AdminPreemptionDetectedV1_failed_transitions_to_failed_and_emits_failed_event()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = BuildSignal(
                DecisionSignalKind.AdminPreemptionDetected,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string> { ["adminOutcome"] = "Failed" });

            var step = engine.Reduce(state, signal);

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.AdminPreempted, step.NewState.Outcome);

            var effect = Assert.Single(step.Effects);
            Assert.Equal("enrollment_failed", effect.Parameters!["eventType"]);
            Assert.Equal("Failed", effect.Parameters["adminAction"]);
        }

        [Fact]
        public void AdminPreemptionDetectedV1_missing_outcome_defaults_to_failed()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = BuildSignal(
                DecisionSignalKind.AdminPreemptionDetected,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc),
                payload: null);

            var step = engine.Reduce(state, signal);

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.AdminPreempted, step.NewState.Outcome);
        }

        [Fact]
        public void AdminPreemptionDetectedV1_clears_active_deadlines()
        {
            var engine = new DecisionEngine();
            var stateWithDeadline = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .AddDeadline(new ActiveDeadline(
                    name: DeadlineNames.ClassifierTick,
                    dueAtUtc: new DateTime(2026, 4, 22, 11, 0, 0, DateTimeKind.Utc),
                    firesSignalKind: DecisionSignalKind.DeadlineFired,
                    firesPayload: new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.Deadline] = DeadlineNames.ClassifierTick,
                    }))
                .Build();

            var signal = BuildSignal(
                DecisionSignalKind.AdminPreemptionDetected,
                new DateTime(2026, 4, 22, 10, 0, 0, DateTimeKind.Utc),
                new Dictionary<string, string> { ["adminOutcome"] = "Succeeded" });

            var step = engine.Reduce(stateWithDeadline, signal);

            Assert.Empty(step.NewState.Deadlines);
        }
    }
}
