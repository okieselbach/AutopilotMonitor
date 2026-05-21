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
    /// Plan §2.7 / Hello-disabled deadlock fix (project_v2_classic_hello_disabled_deadlock):
    /// the Classic-v1 + Hello-policy-disabled enrollment must complete via one of two
    /// dual-path mechanisms:
    /// <list type="number">
    ///   <item>Fast-path in <c>HandleDesktopArrivedV1</c>: when HelloPolicyEnabled==false AND
    ///         the strong post-AccountSetup gate (<c>ShouldTransitionToAwaitingHello</c>) holds —
    ///         i.e. either <c>AccountSetupProvisioningSucceededUtc</c> is set OR
    ///         <c>ScenarioObservations.SkipUserEsp</c> is observed as <c>true</c> — synthesise
    ///         <c>HelloOutcome="Skipped"</c> on DesktopArrived and route through Finalizing
    ///         immediately.</item>
    ///   <item>Reducer-path via EspExiting → HelloSafety 300s deadline → synthetic
    ///         <c>HelloOutcome="Timeout"</c> fact (independent of the Hello policy).</item>
    /// </list>
    /// The dual-path design ensures completion even if one half regresses (missing
    /// HelloPolicyDetected adapter or missing EspExiting adapter).
    /// <para>
    /// Session 08c99638 fix (2026-05-21): the fast-path guard was tightened from
    /// <c>AccountSetupEnteredUtc != null</c> to the strong gate above, restoring parity with
    /// <c>HandleEspExitingV1</c>. Without the tightening, a late <c>desktop_arrived</c> would
    /// drive <c>enrollment_complete</c> on a session whose AccountSetup category never reached
    /// <c>categorySucceeded=true</c> (apps stuck in_progress, 0/N subcategories complete).
    /// </para>
    /// </summary>
    public sealed class ClassicHelloDisabledCompletionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 5, 12, 17, 0, 0, DateTimeKind.Utc);

        // -------------------------------------------------------- Fix 3 — fast-path happy path

        [Fact]
        public void HelloDisabled_FastPath_completes_on_DesktopArrived_after_AccountSetup()
        {
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);

            // Sanity — Hello policy disabled fact recorded, AccountSetup entered, no HelloResolved.
            Assert.False(state.HelloPolicyEnabled!.Value);
            Assert.NotNull(state.AccountSetupEnteredUtc);
            Assert.Null(state.HelloResolvedUtc);
            Assert.Null(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null));

            // Fast-path → Finalizing in one step, synthetic HelloOutcome="Skipped" recorded.
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome); // EnrollmentComplete is deferred to FinalizingGrace fire
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Equal(T0.AddMinutes(10), step.NewState.HelloResolvedUtc!.Value);

            // Effects: phase_transition(FinalizingSetup) declaration + FinalizingGrace deadline schedule.
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition"
                     && e.Parameters.TryGetValue("phase", out var ph) && ph == nameof(EnrollmentPhase.FinalizingSetup));
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline
                     && e.Deadline?.Name == DeadlineNames.FinalizingGrace);

            // FinalizingGrace fires → terminal enrollment_complete with HelloDisabledFastPath trigger.
            var afterGrace = engine.Reduce(
                step.NewState,
                MakeSignal(11, DecisionSignalKind.DeadlineFired, T0.AddMinutes(10).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));

            Assert.Equal(SessionStage.Completed, afterGrace.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, afterGrace.NewState.Outcome);
            Assert.Contains(afterGrace.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
        }

        // ---------------------------------------------------------------- Fix 3 — guard matrix

        [Fact]
        public void HelloDisabled_FastPath_skipped_when_policy_not_detected()
        {
            // HelloPolicyEnabled == null (policy reader hasn't fired yet) → fast-path MUST NOT
            // trigger; reducer parks waiting for Hello/HelloSafety as before.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // NO HelloPolicyDetected signal.

            var step = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc); // fast-path didn't synthesize
            Assert.Null(step.NewState.HelloOutcome);
            Assert.NotNull(step.NewState.DesktopArrivedUtc); // but desktop fact IS recorded
            Assert.DoesNotContain(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
        }

        [Fact]
        public void HelloDisabled_FastPath_skipped_before_AccountSetup()
        {
            // HelloPolicyEnabled==false AND DeviceSetup-only — premature DesktopArrived (e.g.
            // an early-explorer false positive) must NOT complete the session.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            // AccountSetup NOT entered.

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddMinutes(3), null));

            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
        }

        [Fact]
        public void HelloDisabled_FastPath_skipped_when_policy_enabled()
        {
            // HelloPolicyEnabled == true → fast-path MUST NOT trigger; the real Hello wizard
            // is expected to complete and HandleHelloResolvedV1 will route through Finalizing.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" })).NewState;

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DesktopArrived, T0.AddMinutes(3), null));

            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
        }

        // -------------------------------------------------------- Fix 1 — reducer-path via HelloSafety

        [Fact]
        public void HelloDisabled_via_EspExiting_HelloSafety_then_Desktop_completes()
        {
            // Belt-and-suspenders verification: if the Hello-disabled fast-path were ever
            // removed/regressed, the EspExiting → HelloSafety reducer path must still complete
            // the session within 300s of ESP exit.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);

            // ESP exits → HelloSafety deadline armed (300s from ESP-exit time).
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(5), null)).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            var helloSafety = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(T0.AddMinutes(5).AddSeconds(300), helloSafety.DueAtUtc);

            // HelloSafety fires → synthetic HelloOutcome="Timeout" + Stage AwaitingDesktop.
            state = engine.Reduce(
                state,
                MakeSignal(11, DecisionSignalKind.DeadlineFired, T0.AddMinutes(10),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety })).NewState;
            Assert.Equal(SessionStage.AwaitingDesktop, state.Stage);
            Assert.Equal("Timeout", state.HelloOutcome!.Value);
            Assert.NotNull(state.HelloResolvedUtc);

            // Desktop arrives → Finalizing (the fast-path guard is false because HelloResolved
            // is already set, so the existing "both prerequisites resolved" branch fires).
            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.DesktopArrived, T0.AddMinutes(11), null));
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            // HelloOutcome stays "Timeout" — fast-path didn't overwrite to "Skipped" because
            // HelloResolvedUtc was already populated (helloAlreadyResolved short-circuit).
            Assert.Equal("Timeout", step.NewState.HelloOutcome!.Value);
        }

        // -------------------------------------------------------- Fix 3 — HelloSafety cancel

        [Fact]
        public void HelloDisabled_FastPath_cancels_armed_HelloSafety_deadline_in_state_and_effects()
        {
            // When EspExiting arms HelloSafety (300s) and Desktop arrives before the deadline,
            // the fast-path must cancel the deadline both in reducer state AND as a
            // scheduler-visible CancelDeadline effect. Without the effect, the external scheduler
            // (DefaultComponentFactory) would still fire the timer post-Completion and the
            // synthesised HelloSafety DeadlineFired signal would dead-end the reducer.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);

            // ESP exits → HelloSafety armed.
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(5), null)).NewState;
            Assert.Contains(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            // Desktop arrives within the HelloSafety window → fast-path fires.
            var step = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6), null));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);

            // Effect side: CancelDeadline(HelloSafety) must be emitted so the external scheduler
            // drops its timer. ScheduleDeadline(FinalizingGrace) + phase_transition stay as usual.
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.CancelDeadline
                     && e.CancelDeadlineName == DeadlineNames.HelloSafety);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline
                     && e.Deadline?.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
        }

        [Fact]
        public void HelloDisabled_FastPath_without_armed_HelloSafety_does_not_emit_redundant_CancelDeadline_effect()
        {
            // When HelloSafety is NOT armed (no EspExiting yet, fast-path is the sole completion
            // path), the fast-path must NOT emit a no-op CancelDeadline effect — otherwise the
            // audit trail grows pointless entries and the external scheduler logs cancellations
            // for timers it never armed.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            Assert.DoesNotContain(state.Deadlines, d => d.Name == DeadlineNames.HelloSafety);

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5), null));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);
        }

        // -------------------------------------------- Fix 3 — session 08c99638 strong-gate parity

        [Fact]
        public void HelloDisabled_FastPath_skipped_when_AccountSetup_entered_but_provisioning_never_succeeded()
        {
            // Session 08c99638 (2026-05-21) repro: Classic-v1 + Hello-disabled.
            // ESP "exits" via Shell-Core event 62407 (errorCode=0) while AccountSetupCategory.Status
            // is still categorySucceeded=in_progress (apps never finished, 0/N subcategories).
            // AccountSetupProvisioningComplete is therefore NEVER raised → AccountSetupProvisioningSucceededUtc
            // stays null. The pre-fix weak gate (AccountSetupEnteredUtc != null) let a late
            // DesktopArrived drive enrollment_complete; the strong gate (parity with
            // ShouldTransitionToAwaitingHello) MUST keep the session parked.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-08c99638", "tenant-08c99638", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(10),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // NO AccountSetupProvisioningComplete — AccountSetup never reached categorySucceeded=true.
            // EspExiting is observed (Shell-Core 62407, errorCode=0), recording EspFinalExitUtc as
            // an observability fact but not promoting to AwaitingHello (strong gate rejects).
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspExiting, T0.AddMinutes(25), null)).NewState;

            // Preconditions for the fast-path: Hello disabled + AccountSetup entered + Desktop not yet seen.
            Assert.False(state.HelloPolicyEnabled!.Value);
            Assert.NotNull(state.AccountSetupEnteredUtc);
            Assert.NotNull(state.EspFinalExitUtc);
            Assert.Null(state.AccountSetupProvisioningSucceededUtc);
            Assert.Null(state.HelloResolvedUtc);
            Assert.NotEqual(SessionStage.AwaitingHello, state.Stage);

            // Desktop arrives 35 min after EspExiting (mirroring 08c99638's 35-min gap).
            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(60), null));

            // Strong gate rejects: no Finalizing, no synthetic HelloOutcome, no FinalizingGrace deadline.
            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.NotNull(step.NewState.DesktopArrivedUtc); // observability fact IS recorded
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.DoesNotContain(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "phase_transition");
            Assert.DoesNotContain(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline
                     && e.Deadline?.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void HelloDisabled_FastPath_fires_on_SkipUserEsp_flow_even_without_AccountSetupProvisioningComplete()
        {
            // Mirror of the strong-gate's second arm: SkipUserEsp=true (no User-ESP page on this
            // flow) is sufficient to satisfy the gate. AccountSetupProvisioningSucceededUtc need
            // not be set — there's no AccountSetup phase to wait for.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-skipuser", "tenant-skipuser", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspConfigDetected, T0.AddSeconds(5),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "true",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // NO AccountSetupProvisioningComplete — but SkipUserEsp=true unlocks the gate.

            Assert.True(state.ScenarioObservations.SkipUserEsp?.Value);
            Assert.Null(state.AccountSetupProvisioningSucceededUtc);

            var step = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null));

            // Fast-path fires via the SkipUserEsp arm of the strong gate.
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        // ====================================================================== test helpers

        /// <summary>
        /// Reaches the typical post-AccountSetup state for a Classic-v1 + Hello-disabled
        /// enrollment: SessionStarted, ESP DeviceSetup → AccountSetup, HelloPolicyDetected
        /// (helloEnabled=false), and AccountSetupProvisioningComplete (session 330f73f3 fix —
        /// the strong post-AccountSetup gate that <see cref="DecisionEngine"/>.<c>ShouldTransitionToAwaitingHello</c>
        /// requires before a subsequent EspExiting promotes to AwaitingHello). No EspExiting yet.
        /// </summary>
        private static DecisionState ProgressToAccountSetupWithHelloDisabled(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("sess-hello-disabled", "tenant-hello-disabled", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.AccountSetupProvisioningComplete,
                T0.AddMinutes(4).AddSeconds(30), null)).NewState;
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
    }
}
