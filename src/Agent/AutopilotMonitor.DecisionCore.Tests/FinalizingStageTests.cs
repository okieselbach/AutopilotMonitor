using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §5 Fix 6 — both-prerequisites-resolved routes through the non-terminal
    /// <see cref="SessionStage.Finalizing"/> stage with a FinalizingGrace deadline, emits a
    /// <c>phase_transition(FinalizingSetup)</c> declaration effect, and reaches
    /// <see cref="SessionStage.Completed"/> only when the deadline fires.
    /// </summary>
    public sealed class FinalizingStageTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionState InitialAwaitingDesktop(DecisionEngine engine)
        {
            // Build a state where ESP has reached AccountSetup, EspExiting arrived, Hello has
            // already resolved — the reducer parks in AwaitingDesktop waiting for the desktop.
            // Pass T0 as the agent-boot anchor so the EffectiveDeadlineBase guard doesn't
            // floor the deterministic T0-based deadline math at the test runner's wall clock.
            var state = DecisionState.CreateInitial("sess-fin", "tenant-fin", T0);

            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspExiting, T0.AddMinutes(3), null)).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;

            // Hello has resolved, Desktop hasn't — reducer should be in AwaitingDesktop.
            Assert.Equal(SessionStage.AwaitingDesktop, state.Stage);
            Assert.NotNull(state.HelloResolvedUtc);
            Assert.Null(state.DesktopArrivedUtc);
            return state;
        }

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"test-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }

        [Fact]
        public void DesktopArrived_when_Hello_already_resolved_transitions_to_Finalizing_not_Completed()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome); // NOT EnrollmentComplete yet — deferred until deadline fires
            Assert.NotNull(step.NewState.DesktopArrivedUtc);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void DesktopArrived_when_Hello_resolved_emits_phase_transition_FinalizingSetup_effect()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            var phaseTransition = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
            Assert.Equal(nameof(EnrollmentPhase.FinalizingSetup), phaseTransition.Parameters!["phase"]);
        }

        [Fact]
        public void DesktopArrived_when_Hello_resolved_schedules_FinalizingGrace_deadline_effect()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            var signalTime = T0.AddMinutes(5);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, signalTime, null));

            var scheduleEffect = Assert.Single(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.NotNull(scheduleEffect.Deadline);
            Assert.Equal(DeadlineNames.FinalizingGrace, scheduleEffect.Deadline!.Name);
            Assert.Equal(signalTime.AddSeconds(5), scheduleEffect.Deadline.DueAtUtc);
        }

        [Fact]
        public void FinalizingGraceDeadline_fire_transitions_Finalizing_to_Completed_with_enrollment_complete()
        {
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(
                state,
                MakeSignal(6, DecisionSignalKind.DeadlineFired, T0.AddMinutes(5).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            Assert.Empty(step.NewState.Deadlines);

            var terminalEffect = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
            Assert.NotNull(terminalEffect);
        }

        [Fact]
        public void FinalizingGraceDeadline_enrollment_complete_carries_v1_audit_trail_in_typed_payload()
        {
            // 882fef64 debrief follow-up: V2 had regressed by emitting enrollment_complete with
            // empty Data — V1's audit trail (signalsSeen, signalTimestamps, completionSource,
            // helloOutcome) had only been written to the local final-status.json. The reducer
            // now restores parity by attaching a structured TypedPayload that the
            // EventTimelineEmitter passes verbatim to EnrollmentEvent.Data.
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;

            var step = engine.Reduce(
                state,
                MakeSignal(6, DecisionSignalKind.DeadlineFired, T0.AddMinutes(5).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));

            var terminalEffect = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");

            var payload = Assert.IsType<Dictionary<string, object>>(terminalEffect.TypedPayload);
            // Field names come from the unified DecisionAuditTrailBuilder (decisionSource /
            // trigger replace the legacy completionSource / completionTrigger; the rest of the
            // shape is a strict superset of what enrollment_complete used to publish).
            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal($"DeadlineFired:{DeadlineNames.FinalizingGrace}", payload["trigger"]);
            Assert.Equal(nameof(SessionStage.Completed), payload["sessionStage"]);

            var signalsSeen = Assert.IsType<List<string>>(payload["signalsSeen"]);
            Assert.Contains("hello_resolved", signalsSeen);
            Assert.Contains("desktop_arrived", signalsSeen);

            // Schema-Drift Sync (2026-05-04): signalTimestamps is now Dictionary<string, string>
            // (all values are ISO-8601 strings — the engine no longer carries raw DateTime objects
            // through this slot). Shared with FinalStatusBuilder via DecisionStateSignalCensus.
            var timestamps = Assert.IsType<Dictionary<string, string>>(payload["signalTimestamps"]);
            // Hello resolved at T0+4min, Desktop arrived at T0+5min — both ISO-8601 round-trip strings.
            Assert.Equal(T0.AddMinutes(4).ToString("o", System.Globalization.CultureInfo.InvariantCulture), timestamps["helloResolved"]);
            Assert.Equal(T0.AddMinutes(5).ToString("o", System.Globalization.CultureInfo.InvariantCulture), timestamps["desktopArrived"]);

            // HelloOutcome payload was supplied as "Success" by the InitialAwaitingDesktop fixture.
            Assert.Equal("Success", payload["helloOutcome"]);

            // Evidence map carries ordinal + utc per fact for the Inspector's signal-jump.
            var evidence = Assert.IsType<Dictionary<string, object>>(payload["signalEvidence"]);
            var helloEvidence = Assert.IsType<Dictionary<string, object>>(evidence["helloResolved"]);
            Assert.True(helloEvidence.ContainsKey("ordinal"));
            Assert.True(helloEvidence.ContainsKey("utc"));
        }

        [Fact]
        public void HelloResolved_when_Desktop_already_arrived_also_routes_through_Finalizing()
        {
            // Mirror scenario: Desktop arrives first (before Hello resolves), reducer stays in
            // current stage; when HelloResolved arrives, reducer parks in Finalizing.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-fin-rev", "tenant-fin-rev");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DesktopArrived, T0.AddMinutes(2), null)).NewState;
            Assert.NotNull(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.HelloResolved, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
        }

        [Fact]
        public void Finalizing_stage_is_not_marked_terminal_by_SessionStageExtensions()
        {
            // Guard against someone accidentally adding Finalizing to IsTerminal — that would
            // defeat the whole point of the grace window.
            Assert.False(SessionStage.Finalizing.IsTerminal());
            Assert.True(SessionStage.Completed.IsTerminal());
        }
    }
}
