using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Session 772fe502 fix (2026-07-13) — <see cref="DecisionSignalKind.HelloWizardStarted"/>:
    /// a flip-flopping user-scoped WHfB CSP read "disabled" once, the arm-C completion
    /// synthesized <c>HelloOutcome="Skipped"</c> and the session completed while the Hello
    /// wizard (Shell-Core 62404, started 230 ms earlier) was still on screen.
    /// <list type="bullet">
    ///   <item>PREVENTION — once the wizard-start fact is recorded, the policy-disabled
    ///         stand-in no longer satisfies the Hello completion gate at any of the five
    ///         Skipped-synthesis sites; sessions take their pessimistic AwaitingHello path.</item>
    ///   <item>CURE — an already-synthesized "Skipped" resolution is retracted: cancel
    ///         FinalizingGrace, back to AwaitingHello, arm HelloSafety. Only the exact-case
    ///         engine-synthesized <c>"Skipped"</c> is ever retracted — tracker-posted outcomes
    ///         (lowercase vocabulary) and the synthetic <c>"Timeout"</c> are not.</item>
    /// </list>
    /// </summary>
    public sealed class ClassicHelloWizardStartedTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 13, 6, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------- fact recording (no-op arm)

        [Fact]
        public void RecordsFact_setOnce_withoutStageChange()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            var stageBefore = state.Stage;

            var step = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(1), null));

            Assert.Equal(stageBefore, step.NewState.Stage);
            Assert.NotNull(step.NewState.HelloWizardStartedUtc);
            Assert.Equal(T0.AddMinutes(1), step.NewState.HelloWizardStartedUtc!.Value);
            Assert.Equal(1, step.NewState.HelloWizardStartedUtc.SourceSignalOrdinal);
            Assert.Empty(step.Effects);

            // Set-once: a duplicate signal keeps the first observation.
            var dup = engine.Reduce(step.NewState, MakeSignal(2, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(2), null));
            Assert.Equal(T0.AddMinutes(1), dup.NewState.HelloWizardStartedUtc!.Value);
            Assert.Equal(1, dup.NewState.HelloWizardStartedUtc.SourceSignalOrdinal);
        }

        [Fact]
        public void DeadEnds_postTerminal()
        {
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DeadlineFired, T0.AddMinutes(10).AddSeconds(5),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace })).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(11), null));

            // Terminal dispatch guard dead-ends the signal — no un-skip after Completed.
            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Equal("Skipped", step.NewState.HelloOutcome!.Value);
            Assert.Null(step.NewState.HelloWizardStartedUtc);
            Assert.False(step.Transition.Taken);
        }

        // -------------------------------------------------------------------------- cure (un-skip)

        [Fact]
        public void UnSkips_whenFinalizingWithSyntheticSkipped()
        {
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            // Fast-path completes on DesktopArrived: synthetic Skipped + Finalizing + grace armed.
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);
            Assert.Equal("Skipped", state.HelloOutcome!.Value);
            Assert.Contains(state.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);

            // Session 772fe502: the wizard demonstrably starts 230 ms later.
            var wizardAt = T0.AddMinutes(10).AddMilliseconds(230);
            var step = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.HelloWizardStarted, wizardAt, null));

            // Synthetic resolution retracted, back to AwaitingHello.
            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.NotNull(step.NewState.HelloWizardStartedUtc);

            // FinalizingGrace canceled (state + scheduler effect), HelloSafety armed at signal+300s.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            var helloSafety = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(wizardAt.AddSeconds(300), helloSafety.DueAtUtc);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.CancelDeadline
                     && e.CancelDeadlineName == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.ScheduleDeadline
                     && e.Deadline?.Name == DeadlineNames.HelloSafety);

            // completion_waiting re-lists hello_resolution (the wizard veto re-opens the gate).
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "completion_waiting"
                     && e.Parameters.TryGetValue("missingPrerequisites", out var mp) && mp.Contains("hello_resolution")
                     && e.Parameters.TryGetValue("trigger", out var tr) && tr == "HelloWizardStarted:UnSkip");
        }

        [Fact]
        public void UnSkip_thenRealHelloResolved_completesThroughFinalizing()
        {
            var engine = new DecisionEngine();
            var state = ProgressToUnSkipped(engine);

            // The user finishes the wizard: real tracker-posted resolution.
            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.HelloResolved, T0.AddMinutes(13),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "completed" }));
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("completed", step.NewState.HelloOutcome!.Value);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.CancelDeadline
                     && e.CancelDeadlineName == DeadlineNames.HelloSafety);

            var afterGrace = engine.Reduce(step.NewState,
                MakeSignal(13, DecisionSignalKind.DeadlineFired, T0.AddMinutes(13).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));
            Assert.Equal(SessionStage.Completed, afterGrace.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, afterGrace.NewState.Outcome);
            Assert.Equal("completed", afterGrace.NewState.HelloOutcome!.Value);
        }

        [Fact]
        public void UnSkip_thenHelloSafetyFires_completesWithTimeout()
        {
            var engine = new DecisionEngine();
            var state = ProgressToUnSkipped(engine);

            // No terminal Hello event ever arrives — the re-armed HelloSafety bounds the wait.
            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.DeadlineFired, T0.AddMinutes(16),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety }));

            // Desktop already arrived → synthetic Timeout routes through Finalizing.
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.HelloOutcome!.Value);

            var afterGrace = engine.Reduce(step.NewState,
                MakeSignal(13, DecisionSignalKind.DeadlineFired, T0.AddMinutes(16).AddSeconds(5),
                    new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace }));
            Assert.Equal(SessionStage.Completed, afterGrace.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, afterGrace.NewState.Outcome);
        }

        [Fact]
        public void DoesNotUnSkip_whenHelloResolutionIsTrackerPosted()
        {
            // The tracker's own lowercase "skipped" (HfB 6045-skip) is a REAL resolution — the
            // exact-case discriminator must leave it untouched even when the wizard-start
            // signal arrives afterwards.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.HelloResolved, T0.AddMinutes(9),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "skipped" })).NewState;
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);
            Assert.Equal("skipped", state.HelloOutcome!.Value);

            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(10).AddSeconds(1), null));

            // Fact-record-only: no retraction, grace untouched, stage stays Finalizing.
            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("skipped", step.NewState.HelloOutcome!.Value);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.NotNull(step.NewState.HelloWizardStartedUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void DoesNotUnSkip_whenHelloTimeoutSynthetic()
        {
            // The HelloSafety "Timeout" synthesis is a deliberate bound, not a policy-disabled
            // shortcut — a late wizard start must not retract it.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.EspExiting, T0.AddMinutes(5), null)).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DeadlineFired, T0.AddMinutes(10),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.HelloSafety })).NewState;
            Assert.Equal(SessionStage.AwaitingDesktop, state.Stage);
            Assert.Equal("Timeout", state.HelloOutcome!.Value);

            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(11), null));

            Assert.Equal(SessionStage.AwaitingDesktop, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.HelloOutcome!.Value);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.NotNull(step.NewState.HelloWizardStartedUtc);
        }

        [Fact]
        public void UnSkips_whenGateDeferredSyntheticSkipped()
        {
            // D2: a synthetic Skipped can also sit parked behind a closed RealmJoin completion
            // gate (stage stays pre-Finalizing, no FinalizingGrace armed). The gate-release
            // path completes on HelloResolvedUtc != null — the cure must retract there too, or
            // the RealmJoin release reproduces the mid-wizard completion through another door.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(9),
                new Dictionary<string, string> { ["deploymentPhase"] = "100" })).NewState;

            // Fast-path attempt on DesktopArrived defers on the closed RealmJoin gate — the
            // synthetic Skipped facts persist while the stage stays put.
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            Assert.NotEqual(SessionStage.Finalizing, state.Stage);
            Assert.Equal("Skipped", state.HelloOutcome!.Value);
            Assert.DoesNotContain(state.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);

            // Wizard starts → cure retracts the synthetic skip even without a grace to cancel.
            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(10).AddSeconds(1), null));
            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);

            // RealmJoin resolves → the release path must bookkeep (Hello unresolved), not complete.
            var afterRelease = engine.Reduce(step.NewState, MakeSignal(13, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(12),
                new Dictionary<string, string> { ["deploymentPhase"] = "110" }));
            Assert.NotEqual(SessionStage.Finalizing, afterRelease.NewState.Stage);
            Assert.NotEqual(SessionStage.Completed, afterRelease.NewState.Stage);

            // The real Hello resolution then completes normally.
            var resolved = engine.Reduce(afterRelease.NewState, MakeSignal(14, DecisionSignalKind.HelloResolved, T0.AddMinutes(14),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "completed" }));
            Assert.Equal(SessionStage.Finalizing, resolved.NewState.Stage);
            Assert.Equal("completed", resolved.NewState.HelloOutcome!.Value);
        }

        [Fact]
        public void UnSkip_HelloSafety_floorsAtAgentBoot_forHistoricalSignals()
        {
            // Replay-safety: a wizard-start replayed from a historical log entry must not arm a
            // past-due HelloSafety that fires immediately at boot.
            var engine = new DecisionEngine();
            var boot = T0.AddHours(2);
            var state = DecisionState.CreateInitial("s", "t", boot);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(4), null)).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(10).AddSeconds(1), null));

            var helloSafety = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.Equal(boot.AddSeconds(300), helloSafety.DueAtUtc);
        }

        // ------------------------------------------------------------------ prevention (per site)

        [Fact]
        public void Prevention_DesktopArrived_fastPath_blocked_afterWizardStart()
        {
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(9), null)).NewState;

            var step = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null));

            // No synthesis, no Finalizing — desktop-first parked, hello_resolution listed missing.
            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.NotNull(step.NewState.DesktopArrivedUtc);
            Assert.Contains(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "completion_waiting"
                     && e.Parameters.TryGetValue("missingPrerequisites", out var mp) && mp.Contains("hello_resolution"));
        }

        [Fact]
        public void Prevention_EspExiting_deferredCompletion_promotesInsteadOfSkipping()
        {
            // Site 1 (EspExiting:DeferredCompletion): desktop already in, exit arrives last.
            var engine = new DecisionEngine();
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(9), null)).NewState;
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            Assert.Null(state.HelloResolvedUtc);

            var step = engine.Reduce(state, MakeSignal(12, DecisionSignalKind.EspExiting, T0.AddMinutes(11), null));

            // Pessimistic fallthrough: AwaitingHello + HelloSafety instead of synthetic Skipped.
            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void Prevention_armC_promotesInsteadOfCompleting()
        {
            // Site 3 (ImeUserSessionCompleted arm-C — the exact site that completed session
            // 772fe502 mid-wizard). Guard-blocked exit + desktop + wizard, then IME last.
            var engine = new DecisionEngine();
            var state = ProgressWithoutStrongGateToWizardSeen(engine);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(15), null));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void Prevention_accountSetupComplete_deferredCompletion_promotesInsteadOfSkipping()
        {
            // Site 5 (AccountSetupProvisioningComplete:DeferredCompletion): the strong gate
            // arrives last, after EspExiting + DesktopArrived + wizard.
            var engine = new DecisionEngine();
            var state = ProgressWithoutStrongGateToWizardSeen(engine);

            var step = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(15), null));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Null(step.NewState.HelloOutcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
        }

        [Fact]
        public void Prevention_advisoryCompletion_promotesInsteadOfFailing()
        {
            // Site 4 (AdvisoryCompletion deadline). Without the wizard-aware promote branch,
            // prevention alone would FAIL this session: the completion conjunction is vetoed
            // by the wizard, and the handler's fallthrough is re-arm-or-fail.
            var engine = new DecisionEngine();
            var state = ProgressWithoutStrongGateToWizardSeen(engine);
            // IME user session completes → arm-C promotes (prevention) to AwaitingHello; the
            // AdvisoryCompletion window armed by the guard-blocked exit stays armed.
            state = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(15), null)).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            var advisory = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.AdvisoryCompletion);

            var step = engine.Reduce(state, MakeSignal(21, DecisionSignalKind.DeadlineFired, advisory.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.AdvisoryCompletion }));

            Assert.NotEqual(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.HelloResolvedUtc);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.HelloSafety);
            Assert.DoesNotContain(step.Effects,
                e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                     && e.Parameters != null
                     && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_failed");
        }

        // ------------------------------------------------------------- serialization round-trip

        [Fact]
        public void StateSerializer_roundTrips_HelloWizardStartedUtc()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(1), null)).NewState;

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(state));

            Assert.NotNull(roundtripped.HelloWizardStartedUtc);
            Assert.Equal(T0.AddMinutes(1), roundtripped.HelloWizardStartedUtc!.Value);
            Assert.Equal(1, roundtripped.HelloWizardStartedUtc.SourceSignalOrdinal);
        }

        [Fact]
        public void SnapshotBuilder_serializes_HelloWizardStartedUtc_fact()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(1), null)).NewState;

            var snapshot = DecisionStateSnapshotBuilder.Build(state);
            var facts = Assert.IsType<Dictionary<string, object?>>(snapshot["facts"]);
            var fact = Assert.IsType<Dictionary<string, object?>>(facts["helloWizardStartedUtc"]);
            Assert.Equal(1L, fact["ordinal"]);
        }

        // ====================================================================== test helpers

        /// <summary>
        /// SessionStarted → DeviceSetup → HelloPolicyDetected(false) → AccountSetup →
        /// AccountSetupProvisioningComplete. Identical shape to
        /// <c>ClassicHelloDisabledCompletionTests.ProgressToAccountSetupWithHelloDisabled</c>.
        /// </summary>
        private static DecisionState ProgressToAccountSetupWithHelloDisabled(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("sess-772fe502", "tenant-772fe502", T0);
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

        /// <summary>
        /// The session-772fe502 shape up to the wizard start: strong registry gate starved
        /// (no AccountSetupProvisioningComplete), guard-blocked ESP exit after AccountSetup
        /// entry (arms the AdvisoryCompletion window), real-user desktop, wizard observed.
        /// The session is parked in EspAccountSetup — arm-C / DeferredCompletion sites fire
        /// on whichever signal arrives next.
        /// </summary>
        private static DecisionState ProgressWithoutStrongGateToWizardSeen(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("sess-772fe502", "tenant-772fe502", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0, null)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(9),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // Guard-blocked exit (strong gate unsatisfied) — records EspFinalExitUtc + arms
            // the AdvisoryCompletion resolution window.
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspExiting, T0.AddMinutes(11), null)).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(12), null)).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(12).AddSeconds(1), null)).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);
            Assert.Null(state.HelloResolvedUtc);
            Assert.NotNull(state.HelloWizardStartedUtc);
            return state;
        }

        /// <summary>Fast-path Skipped → Finalizing, then the wizard-start cure → AwaitingHello.</summary>
        private static DecisionState ProgressToUnSkipped(DecisionEngine engine)
        {
            var state = ProgressToAccountSetupWithHelloDisabled(engine);
            state = engine.Reduce(state, MakeSignal(10, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10), null)).NewState;
            state = engine.Reduce(state, MakeSignal(11, DecisionSignalKind.HelloWizardStarted,
                T0.AddMinutes(10).AddMilliseconds(230), null)).NewState;
            Assert.Equal(SessionStage.AwaitingHello, state.Stage);
            Assert.Null(state.HelloResolvedUtc);
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
