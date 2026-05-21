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

        // ===================================================================
        // Session 8b8d611d regression coverage (2026-05-20):
        //
        // Bug: HandleHelloResolvedV1 / HandleDesktopArrivedV1 cancelled HelloSafety in the
        //      reducer state but did NOT emit a CancelDeadline EFFECT — the live
        //      DeadlineScheduler timer kept ticking and later fired DeadlineFired:HelloSafety
        //      AFTER the session had already reached Completed via the FinalizingGrace path.
        //      The dispatcher then re-entered HandleHelloSafetyDeadlineFired, which routes
        //      through TransitionToFinalizing → second phase_transition + second
        //      enrollment_complete on the wire.
        //
        // Fix: a) Emit CancelDeadline(HelloSafety) effect from both signal handlers when the
        //         deadline is actually armed in state.Deadlines.
        //      b) Defense-in-depth dispatch guard: any signal arriving with state.Stage in
        //         {Completed, Failed} is short-circuited to a bookkept dead-end before
        //         reaching the handlers.
        // ===================================================================

        /// <summary>
        /// Build a state where the deferred-promote path of
        /// <c>HandleAccountSetupProvisioningCompleteV1</c> has just armed HelloSafety. Mirrors
        /// the 8b8d611d production sequence: ESP intermediate exit → AccountSetup entered →
        /// AccountSetup provisioning completed (deferred-promote → AwaitingHello, HelloSafety
        /// armed for +300 s).
        /// </summary>
        private static DecisionState InitialAwaitingHello_WithHelloSafetyArmed(DecisionEngine engine, out DateTime accountSetupProvisioningCompleteUtc)
        {
            var state = DecisionState.CreateInitial("sess-hs", "tenant-hs", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // EspExiting fires before AccountSetup provisioning completes — guard blocks the
            // promote, but EspFinalExitUtc is recorded so the next AccountSetupProvisioningComplete
            // takes the deferred-promote path.
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspExiting, T0.AddMinutes(2), null)).NewState;
            Assert.NotNull(state.EspFinalExitUtc);

            accountSetupProvisioningCompleteUtc = T0.AddMinutes(3);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.AccountSetupProvisioningComplete,
                accountSetupProvisioningCompleteUtc, null)).NewState;

            // Sanity: deferred-promote landed us in AwaitingHello with HelloSafety armed.
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            return state;
        }

        [Fact]
        public void HelloResolved_when_HelloSafety_armed_emits_CancelDeadline_effect_to_scheduler()
        {
            // Session 8b8d611d primary fix #1. AwaitingDesktop branch: Hello resolves before
            // Desktop arrives. The reducer cancels HelloSafety in state — we now require the
            // matching CancelDeadline effect so the live DeadlineScheduler timer is disposed.
            var engine = new DecisionEngine();
            var state = InitialAwaitingHello_WithHelloSafetyArmed(engine, out _);

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            Assert.Equal(SessionStage.AwaitingDesktop, step.NewState.Stage);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            var cancelEffect = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.CancelDeadline
                     && e.CancelDeadlineName == DeadlineNames.HelloSafety);
            Assert.NotNull(cancelEffect);
        }

        [Fact]
        public void HelloResolved_when_HelloSafety_armed_and_Desktop_already_arrived_cancels_then_arms_Finalizing()
        {
            // Both-prereqs path: HelloSafety must be cancelled BEFORE FinalizingGrace is armed
            // so the scheduler observes the cancel before any subsequent state read could see
            // both timers active. TransitionToFinalizing's extraLeadingEffects parameter orders
            // the cancel before ScheduleDeadline + phase_transition.
            var engine = new DecisionEngine();
            var state = InitialAwaitingHello_WithHelloSafetyArmed(engine, out _);
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DesktopArrived, T0.AddMinutes(4), null)).NewState;
            Assert.NotNull(state.DesktopArrivedUtc);
            // Desktop without HelloResolved keeps us in AwaitingHello with HelloSafety still armed.
            Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.HelloResolved, T0.AddMinutes(5),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            // Effect ordering: cancel HelloSafety → schedule FinalizingGrace → emit phase_transition.
            Assert.Equal(3, step.Effects.Count);
            Assert.Equal(DecisionEffectKind.CancelDeadline, step.Effects[0].Kind);
            Assert.Equal(DeadlineNames.HelloSafety, step.Effects[0].CancelDeadlineName);
            Assert.Equal(DecisionEffectKind.ScheduleDeadline, step.Effects[1].Kind);
            Assert.Equal(DeadlineNames.FinalizingGrace, step.Effects[1].Deadline!.Name);
            Assert.Equal(DecisionEffectKind.EmitEventTimelineEntry, step.Effects[2].Kind);
        }

        [Fact]
        public void HelloSafetyDeadline_firing_after_Completed_is_dead_end_with_no_duplicate_enrollment_complete()
        {
            // Session 8b8d611d defense-in-depth. Simulate the production race: HelloSafety
            // armed by the deferred-promote path, HelloResolved + DesktopArrived drive the
            // session through Finalizing → Completed, THEN the stale HelloSafety timer fires
            // its synthetic DeadlineFired signal. Without the dispatch guard the signal would
            // hit HandleHelloSafetyDeadlineFired → TransitionToFinalizing → duplicate
            // enrollment_complete. With the guard it produces a bookkept dead-end and
            // emits no effects.
            var engine = new DecisionEngine();
            var state = InitialAwaitingHello_WithHelloSafetyArmed(engine, out _);
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            // Drive Finalizing → Completed via the FinalizingGrace deadline (+5s).
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DeadlineFired,
                T0.AddMinutes(5).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace })).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            // Stale HelloSafety timer fires AFTER Completed.
            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DeadlineFired,
                T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety }));

            // State must NOT regress out of Completed.
            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);

            // No effects — in particular, no second ScheduleDeadline(FinalizingGrace) and no
            // second enrollment_complete emission.
            Assert.Empty(step.Effects);

            // Recorded as a dead-end with a descriptive reason.
            Assert.False(step.Transition.Taken);
            Assert.StartsWith("signal_after_terminal:", step.Transition.DeadEndReason!);
        }

        [Fact]
        public void Dispatch_guard_short_circuits_arbitrary_signals_after_Completed()
        {
            // Defense-in-depth coverage beyond the HelloSafety-specific scenario: any signal
            // arriving in Completed must be bookkept-only.
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DeadlineFired,
                T0.AddMinutes(5).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace })).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            // A late ImeUserSessionCompleted (replayed CMTrace tail after Completed) must not
            // mutate state or emit effects.
            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.ImeUserSessionCompleted,
                T0.AddMinutes(7),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-COMPLETE" }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Empty(step.Effects);
            Assert.False(step.Transition.Taken);
            Assert.Equal("signal_after_terminal:Completed", step.Transition.DeadEndReason);
        }

        [Fact]
        public void Dispatch_guard_allows_InformationalEvent_after_Completed_so_terminal_handler_events_reach_wire()
        {
            // Session 875178a3 regression coverage (2026-05-21):
            //
            // Bug: the post-terminal dispatch guard blanket-rejected EVERY signal kind once
            //      Stage reached Completed/Failed — including DecisionSignalKind.InformationalEvent,
            //      which is the single-rail pass-through used by EnrollmentTerminationHandler
            //      to emit agent_shutting_down / app_tracking_summary / shutdown-delta
            //      software_inventory_analysis / diagnostics_* / enrollment_summary_shown /
            //      reboot_triggered / whiteglove_part1_complete. Result: every V2 session on
            //      agent builds >= 2.0.835 ended at enrollment_complete with no terminal
            //      lifecycle events at all (confirmed 0/11 affected vs 9/9 working on <=2.0.820
            //      across one tenant sample).
            //
            // Fix: exempt InformationalEvent from the guard. The signal kind is pure
            //      pass-through (no state mutation, no deadline arming) so it cannot regress
            //      the duplicate-enrollment_complete protection that motivated the original
            //      guard. enrollment_complete on the FinalizingGrace path is emitted directly
            //      from the reducer step, not via InformationalEvent.
            var engine = new DecisionEngine();
            var state = InitialAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null)).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DeadlineFired,
                T0.AddMinutes(5).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace })).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            // EnrollmentTerminationHandler.EmitAgentShuttingDown post()s an InformationalEvent
            // with these payload keys (eventType + source are the only mandatory ones for
            // HandleInformationalEventV1; the rest are forwarded verbatim by EventTimelineEmitter).
            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.InformationalEvent,
                T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EventType] = "agent_shutting_down",
                    [SignalPayloadKeys.Source] = "EnrollmentTerminationHandler",
                    [SignalPayloadKeys.Message] = "Agent shutting down (reason=decision_terminal, outcome=Succeeded).",
                }));

            // State must NOT regress out of Completed — the informational pass-through does
            // not mutate Stage/Outcome/facts.
            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);

            // The EmitEventTimelineEntry effect MUST be present — that is the signal-on-wire
            // mechanism. Before the fix this assertion failed because the guard collapsed the
            // step to Array.Empty<DecisionEffect>().
            var emitEffect = Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue(SignalPayloadKeys.EventType, out var et)
                     && et == "agent_shutting_down");
            Assert.Equal("EnrollmentTerminationHandler", emitEffect.Parameters![SignalPayloadKeys.Source]);

            // Recorded as a taken transition (not a dead end) — the timeline shows the
            // step happened, not "signal_after_terminal".
            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
        }

        [Fact]
        public void Dispatch_guard_allows_InformationalEvent_after_Failed_for_failure_path_termination_events()
        {
            // Sibling coverage for the Failed terminal stage. The termination handler runs the
            // same emit pipeline on the failure path (agent_shutting_down with outcome=Failed,
            // diagnostics_collecting/_upload when DiagnosticsUploadMode=Always|OnFailure, etc.)
            // so the guard must let InformationalEvent through after Failed too.
            var engine = new DecisionEngine();

            // Drive a session to Failed via the EspTerminalFailure path (Edge handler).
            var state = DecisionState.CreateInitial("sess-fail", "tenant-fail", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(2), null)).NewState;
            Assert.Equal(SessionStage.Failed, state.Stage);

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.InformationalEvent,
                T0.AddMinutes(3),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EventType] = "diagnostics_collecting",
                    [SignalPayloadKeys.Source] = "EnrollmentTerminationHandler",
                    [SignalPayloadKeys.Message] = "Collecting diagnostics package.",
                }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            Assert.Single(
                step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue(SignalPayloadKeys.EventType, out var et)
                     && et == "diagnostics_collecting");
            Assert.True(step.Transition.Taken);
        }
    }
}
