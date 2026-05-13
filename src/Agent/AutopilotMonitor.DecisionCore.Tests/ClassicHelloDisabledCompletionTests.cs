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
    ///         AccountSetup has been reached, synthesize <c>HelloOutcome="Skipped"</c> on
    ///         DesktopArrived and route through Finalizing immediately.</item>
    ///   <item>Reducer-path via EspExiting → HelloSafety 300s deadline → synthetic
    ///         <c>HelloOutcome="Timeout"</c> fact (independent of the Hello policy).</item>
    /// </list>
    /// The dual-path design ensures completion even if one half regresses (missing
    /// HelloPolicyDetected adapter or missing EspExiting adapter).
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
        public void HelloDisabled_FastPath_cancels_armed_HelloSafety_deadline()
        {
            // When EspExiting arms HelloSafety (300s) and Desktop arrives before the deadline,
            // the fast-path must cancel the deadline so it cannot fire post-Completion. The
            // reducer's deadline collection on the resulting state must no longer contain
            // HelloSafety, only FinalizingGrace.
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
        }

        // ====================================================================== test helpers

        /// <summary>
        /// Reaches the typical post-AccountSetup state for a Classic-v1 + Hello-disabled
        /// enrollment: SessionStarted, ESP DeviceSetup → AccountSetup, HelloPolicyDetected
        /// (helloEnabled=false). No EspExiting yet.
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
