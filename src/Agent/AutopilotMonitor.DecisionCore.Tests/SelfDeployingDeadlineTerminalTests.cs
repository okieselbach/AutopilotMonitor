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
    /// Targeted coverage for the SelfDeploying-defang refactor (Plan v9, session
    /// <c>88a53223-9795-4188-8352-7df9f0af9bb7</c>). Validates that:
    /// <list type="bullet">
    ///   <item><see cref="DecisionSignalKind.DeviceSetupProvisioningComplete"/> is no longer
    ///         terminal — it only anchors <see cref="DecisionState.DeviceSetupResolvedUtc"/>
    ///         and arms the 5-min <see cref="DeadlineNames.DeviceOnlyEspDetection"/> deadline.</item>
    ///   <item>The deadline-fired handler is the sole SelfDeploying-terminal entry point and
    ///         re-checks all guards (stale-fire / Stage-terminal / AccountSetup / monotonic mode)
    ///         before classifying.</item>
    ///   <item>The RJ-deferred release path (CompleteIfDeferredOrBookkeep) re-checks the same
    ///         guards and demotes back to Classic flow when post-deferral signals arrive.</item>
    ///   <item>Race conditions (DueAtUtc mismatch, rollout snapshots) are handled defensively.</item>
    /// </list>
    /// </summary>
    public sealed class SelfDeployingDeadlineTerminalTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"t-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);

        private static DecisionState PrimeDeviceSetup(DecisionEngine engine, DecisionState initialState)
        {
            var state = engine.Reduce(initialState, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            return state;
        }

        // ============================================================== #1 Happy path

        [Fact]
        public void HappyPath_signalArmsDeadline_deadlineFiresTerminal()
        {
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-1", "t", T0));

            // Signal arms deadline, NO terminal.
            var signalStep = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3)));
            Assert.Equal(SessionStage.EspDeviceSetup, signalStep.NewState.Stage);
            Assert.NotNull(signalStep.NewState.DeviceSetupResolvedUtc);
            var deadline = Assert.Single(signalStep.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(8), deadline.DueAtUtc);

            // Deadline fires at DueAtUtc → terminal SelfDeploying with phase declarations.
            var terminalStep = engine.Reduce(signalStep.NewState, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.Equal(SessionStage.Completed, terminalStep.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, terminalStep.NewState.Outcome);
            Assert.Equal(EnrollmentMode.SelfDeploying, terminalStep.NewState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, terminalStep.NewState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", terminalStep.NewState.ScenarioProfile.Reason);

            // Effects sequence: phase_transition(FinalizingSetup) + phase_transition(Complete) + enrollment_complete
            Assert.Equal(3, terminalStep.Effects.Count);
            var finalizingParams = terminalStep.Effects[0].Parameters!;
            Assert.Equal("phase_transition", finalizingParams["eventType"]);
            Assert.Equal("FinalizingSetup", finalizingParams["phase"]);
            var completeParams = terminalStep.Effects[1].Parameters!;
            Assert.Equal("phase_transition", completeParams["eventType"]);
            Assert.Equal("Complete", completeParams["phase"]);
            Assert.Equal("enrollment_complete", terminalStep.Effects[2].Parameters!["eventType"]);
        }

        // ============================================================== #1b phase_transition payload (observability)

        [Fact]
        public void PhaseTransition_carriesTriggerAndScenario_notEmptyData()
        {
            // Observability (session 62e603c9): phase_transition effects used to emit empty {}
            // DataJson. They must now carry a structured typedPayload with the driving trigger +
            // resolved scenario so the timeline is self-describing.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-1b", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            var finalizing = step.Effects[0];
            Assert.Equal("phase_transition", finalizing.Parameters!["eventType"]);
            var payload = Assert.IsType<Dictionary<string, object>>(finalizing.TypedPayload);
            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal($"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}", payload["trigger"]);
            Assert.Equal(nameof(EnrollmentMode.SelfDeploying), payload["scenarioMode"]);
            Assert.Equal(nameof(ProfileConfidence.High), payload["scenarioConfidence"]);
        }

        // ============================================================== #2 Race guard: Stage already terminal

        [Fact]
        public void RaceGuardA_stageAlreadyTerminal_deadEndsWithoutDoubleComplete()
        {
            // Scenario: signal arms deadline, then Classic completes via Hello+Desktop+Finalizing
            // grace fire BEFORE the SelfDeploying deadline fires. Stage is already Completed when
            // the (now-stale) DeviceOnlyEspDetection deadline fires.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-2", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            // Simulate Classic completion: AccountSetup → EspExiting → HelloResolved + DesktopArrived → FinalizingGrace fires.
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(5))).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.EspExiting, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.HelloResolved, T0.AddMinutes(7),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DesktopArrived, T0.AddMinutes(7))).NewState;
            var finalizingGrace = state.Deadlines.Single(d => d.Name == DeadlineNames.FinalizingGrace);
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, finalizingGrace.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.FinalizingGrace })).NewState;
            Assert.Equal(SessionStage.Completed, state.Stage);

            // The DeviceOnlyEspDetection deadline was cancelled by the EspPhaseChanged→AccountSetup
            // path long ago, but let's simulate a stale DeadlineFired that races through anyway —
            // either guard A (Stage.IsTerminal) or guard B (deadline not armed) catches it.
            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            // The engine's after-terminal dispatch intercepts the signal before our deadline
            // handler even runs (DispatchSignalAfterTerminal sets DeadEndReason="signal_after_terminal:Completed").
            // Either way, the post-state must remain terminal-Completed with no spurious effects.
            Assert.Contains("signal_after_terminal", step.Transition.DeadEndReason!, StringComparison.Ordinal);
            Assert.Empty(step.Effects);
            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
        }

        // ============================================================== #3 Race guard: AccountSetup entered

        [Fact]
        public void RaceGuardB_accountSetupEnteredBetweenArmAndFire_deadEnds()
        {
            // Defensive case: AccountSetup-cancel-deadline path normally prevents this, but
            // simulate the race via a manually-constructed state.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-3", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            // Force AccountSetupEnteredUtc set + keep deadline armed via manual builder mutation.
            var builder = state.ToBuilder();
            builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(T0.AddMinutes(4), 99);
            state = builder.Build();
            Assert.Contains(state.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            var deadline = state.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeadlineFired, deadline.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_account_setup_entered", step.Transition.DeadEndReason);
            Assert.Equal(SessionStage.EspDeviceSetup, step.NewState.Stage);
            // CRITICAL: the past-due deadline MUST be cancelled state-side, otherwise a snapshot
            // reload would have the scheduler re-arm it → repeat dead-end loop.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        // ============================================================== #3b Kiosk waiver (session 320b3bf7)

        private static DecisionState SeedSelfDeployingProfile(DecisionEngine engine, DecisionState state, long ordinal)
            => engine.Reduce(state, MakeSignal(ordinal, DecisionSignalKind.EnrollmentFactsObserved, T0.AddSeconds(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "false",
                    [SignalPayloadKeys.IsSelfDeployingProfile] = "true",
                })).NewState;

        [Fact]
        public void KioskWaiver_dspcArmsDeadline_despiteAccountSetupEntered()
        {
            // Session 320b3bf7 ordering: the IME AccountSetup false positive lands BEFORE
            // DeviceSetupProvisioningComplete. With the OobeConfig seed the step-3
            // short-circuit is waived and the deadline still arms.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-kiosk-1", "t", T0));
            state = SeedSelfDeployingProfile(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            Assert.NotNull(state.AccountSetupEnteredUtc);

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3)));

            Assert.EndsWith(":DeadlineArmed", step.Transition.Trigger);
            var deadline = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(8), deadline.DueAtUtc);

            // And the fire completes terminal — race guard B is waived for the seeded profile.
            var terminal = engine.Reduce(step.NewState, MakeSignal(5, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            Assert.Equal(SessionStage.Completed, terminal.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, terminal.NewState.Outcome);
        }

        [Fact]
        public void KioskWaiver_accountSetupInsideWindow_doesNotCancelDeadline()
        {
            // Reverse ordering: deadline armed, false positive lands inside the 5-min window.
            // HandleEspPhaseChangedV1 must NOT cancel the deadline for the seeded profile.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-kiosk-2", "t", T0));
            state = SeedSelfDeployingProfile(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            Assert.Contains(state.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            var accountSetupStep = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" }));

            Assert.Contains(accountSetupStep.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.DoesNotContain(accountSetupStep.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);

            var terminal = engine.Reduce(accountSetupStep.NewState, MakeSignal(5, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            Assert.Equal(SessionStage.Completed, terminal.NewState.Stage);
        }

        [Fact]
        public void KioskWaiver_notAppliedWithoutSeed_hotpathUnchanged()
        {
            // Byte-identical hotpath proof at the unit level: WITHOUT the OobeConfig seed the
            // AccountSetup entry cancels the deadline exactly as before (existing behaviour,
            // duplicated here as an explicit invariant next to the waiver tests).
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-kiosk-3", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;

            var accountSetupStep = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" }));

            Assert.DoesNotContain(accountSetupStep.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Contains(accountSetupStep.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline
                && e.CancelDeadlineName == DeadlineNames.DeviceOnlyEspDetection);
        }

        [Fact]
        public void KioskWaiver_userEspProgress_reenablesVeto_shortCircuitsArm()
        {
            // A genuine AccountSetupProvisioningComplete on a seeded profile switches the
            // waiver OFF — DSPC takes the Classic short-circuit and never arms the deadline.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-kiosk-4", "t", T0));
            state = SeedSelfDeployingProfile(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(3))).NewState;

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(4)));

            Assert.EndsWith(":AccountSetupAlreadyEntered", step.Transition.Trigger);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        [Fact]
        public void KioskWaiver_guardB_notWaivedWhenUserEspProgressedAfterArm()
        {
            // Deadline armed first, then genuine user-ESP progress before the fire — guard B
            // must veto the terminal (unwaived) and cancel the past-due deadline state-side.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-kiosk-5", "t", T0));
            state = SeedSelfDeployingProfile(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(5))).NewState;
            var deadline = state.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DeadlineFired, deadline.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_account_setup_entered", step.Transition.DeadEndReason);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        // ============================================================== #5 RJ closed at signal, resolved before deadline

        [Fact]
        public void RJClosedAtSignal_resolvedBeforeDeadline_doesNotPrematurelyTerminate()
        {
            // Plan v9 F1 verification: under v1 code, DeviceSetupProvisioningComplete with RJ-gate
            // closed set SelfDeployingDeferredCompletion at signal-time. Then if RJ resolved before
            // 5min, the RJ-resolve handler would complete the session — premature termination.
            // The defang moves deferral to deadline-fire-time; RJ resolving before the deadline
            // (without the deferred flag set) does NOT complete the session.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-5", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            // Signal arrives with RJ-gate closed → arms deadline, does NOT set deferred flag.
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            Assert.Null(state.RealmJoinFacts.SelfDeployingDeferredCompletion);

            // RJ resolves at T+4min (well before deadline at T+8min). Without the deferred flag,
            // CompleteIfDeferredOrBookkeep takes the bookkeeping path — Stage stays non-terminal.
            var rjResolveStep = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));
            Assert.NotEqual(SessionStage.Completed, rjResolveStep.NewState.Stage);
            Assert.DoesNotContain(rjResolveStep.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");

            // The deadline still fires at T+8min and now completes (gate open, no AccountSetup).
            var deadlineStep = engine.Reduce(rjResolveStep.NewState, MakeSignal(5, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            Assert.Equal(SessionStage.Completed, deadlineStep.NewState.Stage);
        }

        // ============================================================== #6 Replay idempotency

        [Fact]
        public void Replay_signalTwice_anchorSetOnce_deadlineNotReArmed()
        {
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-6", "t", T0));

            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var anchorOrdinal1 = state.DeviceSetupResolvedUtc!.SourceSignalOrdinal;
            var firstDueAt = state.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection).DueAtUtc;

            // Replay the signal (different ordinal, same kind). Anchor must stay at first ordinal,
            // deadline DueAtUtc must NOT advance — second signal is a no-op passthrough.
            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(4)));

            Assert.Equal(anchorOrdinal1, step.NewState.DeviceSetupResolvedUtc!.SourceSignalOrdinal);
            Assert.Equal(firstDueAt, step.NewState.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection).DueAtUtc);
            // Transition is taken (LastAppliedSignalOrdinal advanced) but tagged with AnchorAlreadySet.
            Assert.EndsWith(":AnchorAlreadySet", step.Transition.Trigger);
            Assert.Empty(step.Effects);
        }

        // ============================================================== #8 Snapshot-migration A: signal arrives first

        [Fact]
        public void SnapshotMigration_oldStyleDeadlineArmed_signalReplacesIt()
        {
            // Pre-load state with an OLD-style DeviceOnlyEspDetection deadline (armed at
            // DeviceSetup-START by legacy code) and no DeviceSetupResolvedUtc anchor. The new
            // signal handler must cancel the old deadline and arm a fresh one from the signal time.
            var engine = new DecisionEngine();
            var oldDeadline = new ActiveDeadline(
                name: DeadlineNames.DeviceOnlyEspDetection,
                dueAtUtc: T0.AddMinutes(6),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection });
            var legacyState = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-8", "t", T0))
                .ToBuilder()
                .AddDeadline(oldDeadline)
                .Build();

            var step = engine.Reduce(legacyState, MakeSignal(99, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(10)));

            Assert.NotNull(step.NewState.DeviceSetupResolvedUtc);
            var newDeadline = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(15), newDeadline.DueAtUtc);
            // Effects include CancelDeadline (for the old one) and ScheduleDeadline (for the new one).
            Assert.Contains(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);
            Assert.Contains(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
        }

        // ============================================================== #9 Stale-fire guard A: no anchor

        [Fact]
        public void StaleFireGuardA_noAnchor_deadEnds_andCancelsDeadlineStateSide()
        {
            // Pre-load state with the deadline armed but DeviceSetupResolvedUtc=null (rollout race:
            // old code armed deadline at DeviceSetup-START, new code fires under post-upgrade).
            // The guard must dead-end AND cancel the deadline state-side (otherwise snapshot reload
            // re-arms it via the scheduler → infinite loop).
            var engine = new DecisionEngine();
            var oldDeadline = new ActiveDeadline(
                name: DeadlineNames.DeviceOnlyEspDetection,
                dueAtUtc: T0.AddMinutes(6),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection });
            var rolloutState = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-9", "t", T0))
                .ToBuilder()
                .AddDeadline(oldDeadline)
                .Build();
            Assert.Null(rolloutState.DeviceSetupResolvedUtc);

            var step = engine.Reduce(rolloutState, MakeSignal(99, DecisionSignalKind.DeadlineFired, T0.AddMinutes(6),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_stale_no_anchor", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
            // Critical: deadline removed from state so snapshot reload won't re-arm it.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        // ============================================================== #10 Stale-fire guard B: cancelled deadline

        [Fact]
        public void StaleFireGuardB_deadlineCancelled_deadEnds()
        {
            // State has DeviceSetupResolvedUtc set, deadline was cancelled (e.g. by AccountSetup),
            // but a stale DeadlineFired raced through.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-10", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            Assert.DoesNotContain(state.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            // Stale fire arrives anyway.
            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_stale_deadline_not_armed", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
        }

        // ============================================================== #11 Stale-fire guard C: DueAtUtc mismatch

        [Fact]
        public void StaleFireGuardC_dueAtUtcMismatch_oldFireAfterCancelThenRearm_keepsNewDeadline()
        {
            // Cancel-then-rearm race: old deadline (DueAt=T+8) fires queued; before processing,
            // DeviceSetupProvisioningComplete arrives (signal at T+10) → cancel+rearm to DueAt=T+15.
            // The queued OLD fire (OccurredAtUtc=T+8) arrives. Guard C must dead-end WITHOUT
            // cancelling the new deadline.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-11", "t", T0));

            // First signal at T+3 arms deadline at T+8.
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var firstDueAt = state.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection).DueAtUtc;
            Assert.Equal(T0.AddMinutes(8), firstDueAt);

            // Manually simulate the cancel-then-rearm: clear DeviceSetupResolvedUtc + deadline,
            // then re-feed a second DeviceSetupProvisioningComplete with a later timestamp.
            // (In production the first signal's anchor wouldn't clear, but here we want to test
            // the DueAtUtc-mismatch guard specifically — so we force a second arm via builder.)
            var rearmedBuilder = state.ToBuilder();
            rearmedBuilder.DeviceSetupResolvedUtc = new SignalFact<DateTime>(T0.AddMinutes(10), 5);
            rearmedBuilder.CancelDeadline(DeadlineNames.DeviceOnlyEspDetection);
            rearmedBuilder.AddDeadline(new ActiveDeadline(
                name: DeadlineNames.DeviceOnlyEspDetection,
                dueAtUtc: T0.AddMinutes(15),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            state = rearmedBuilder.Build();

            // OLD queued fire arrives with OccurredAtUtc=T+8 — mismatches new deadline at T+15.
            var step = engine.Reduce(state, MakeSignal(99, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_stale_due_at_mismatch", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
            // CRITICAL: the new deadline (T+15) must STILL be in state — guard C must NOT cancel it.
            var stillArmed = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(15), stillArmed.DueAtUtc);

            // Subsequent real fire at the new DueAt completes terminal.
            var realFireStep = engine.Reduce(step.NewState, MakeSignal(100, DecisionSignalKind.DeadlineFired, T0.AddMinutes(15),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            Assert.Equal(SessionStage.Completed, realFireStep.NewState.Stage);
        }

        // ============================================================== #13 Audit-trail census

        [Fact]
        public void AuditTrail_includesDeviceSetupProvisioningComplete_inSignalsSeen()
        {
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-13", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            // Find the enrollment_complete effect; its typedPayload (built by DecisionAuditTrailBuilder)
            // must include device_setup_provisioning_complete in signalsSeen.
            var completeEffect = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
            Assert.NotNull(completeEffect.TypedPayload);
            var data = Assert.IsType<Dictionary<string, object>>(completeEffect.TypedPayload);

            var signalsSeen = Assert.IsType<List<string>>(data["signalsSeen"]);
            Assert.Contains("device_setup_provisioning_complete", signalsSeen);
            var timestamps = Assert.IsType<Dictionary<string, string>>(data["signalTimestamps"]);
            Assert.True(timestamps.ContainsKey("deviceSetupResolved"));
        }

        // ============================================================== #14 Monotonic mode conflict

        [Fact]
        public void RaceGuardC_monotonicModeConflict_ClassicHigh_deadEndsWithoutRelabel()
        {
            // Pre-set ScenarioProfile.Mode=Classic/High (as if ApplyImeUserSessionCompleted had run)
            // and feed the DeviceOnlyEspDetection deadline. The guard must dead-end without
            // relabeling Mode to SelfDeploying.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-14", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var classicProfile = state.ScenarioProfile.With(
                mode: EnrollmentMode.Classic,
                confidence: ProfileConfidence.High,
                reason: "ime_user_session_completed");
            state = state.ToBuilder().WithScenarioProfile(classicProfile).Build();

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_monotonic_mode_conflict", step.Transition.DeadEndReason);
            Assert.Empty(step.Effects);
            // Mode must remain Classic/High.
            Assert.Equal(EnrollmentMode.Classic, step.NewState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, step.NewState.ScenarioProfile.Confidence);
            // CRITICAL: the past-due deadline MUST be cancelled state-side, otherwise a snapshot
            // reload would have the scheduler re-arm it → repeat dead-end loop.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        // ============================================================== #15 RJ-deferred release race: AccountSetup arrives

        [Fact]
        public void RJDeferredRelease_accountSetupArrivedAfterDeferral_demotesToClassicFlow()
        {
            // Sequence: signal arms deadline → RJ-detected (gate closed) → deadline fires
            // (deferred, DeviceOnly=Confirmed) → AccountSetup arrives later → RJ resolves.
            // The deferred-release re-check guard must bail out, clear deferred flag, reset
            // DeviceOnly hypothesis, and NOT terminate as SelfDeploying.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-15", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            // Deadline fires with RJ-gate closed → deferred + DeviceOnly Confirmed.
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection })).NewState;
            Assert.True(state.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);
            Assert.Equal(HypothesisLevel.Confirmed, state.ClassifierOutcomes.DeviceOnlyDeployment.Level);

            // AccountSetup arrives unexpectedly (e.g. Windows delayed ESP transition).
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(9),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            Assert.NotNull(state.AccountSetupEnteredUtc);

            // RJ resolves — the deferred-release branch must re-check and bail.
            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(10),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            // (1) No premature SelfDeploying terminal.
            Assert.NotEqual(SessionStage.Completed, step.NewState.Stage);
            Assert.DoesNotContain(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
            // (2) ScenarioProfile.Mode NOT relabeled to SelfDeploying.
            Assert.NotEqual(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
            // (3) DeviceOnly hypothesis reset to Unknown (prevents WhiteGlove-classifier false positive).
            Assert.Equal(HypothesisLevel.Unknown, step.NewState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            // (4) RJ gate is open post-release — the WithResolved fact survived ClearSelfDeployingDeferred.
            Assert.NotNull(step.NewState.RealmJoinFacts.ResolvedUtc);
            // (5) Deferred flag cleared.
            Assert.Null(step.NewState.RealmJoinFacts.SelfDeployingDeferredCompletion);
        }

        // ============================================================== #16 RJ-deferred release race: monotonic conflict

        [Fact]
        public void RJDeferredRelease_classicHighAfterDeferral_demotesToClassicFlow()
        {
            // Variant of #15: instead of AccountSetup arriving, Classic/High Mode gets set
            // (simulating ApplyImeUserSessionCompleted or similar).
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-16", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection })).NewState;
            Assert.True(state.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);

            // Promote profile to Classic/High via builder (simulates ApplyImeUserSessionCompleted).
            state = state.ToBuilder()
                .WithScenarioProfile(state.ScenarioProfile.With(
                    mode: EnrollmentMode.Classic,
                    confidence: ProfileConfidence.High,
                    reason: "ime_user_session_completed"))
                .Build();

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(10),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.NotEqual(SessionStage.Completed, step.NewState.Stage);
            // Mode stays Classic/High.
            Assert.Equal(EnrollmentMode.Classic, step.NewState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, step.NewState.ScenarioProfile.Confidence);
            // DeviceOnly hypothesis reset.
            Assert.Equal(HypothesisLevel.Unknown, step.NewState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            // RJ gate open.
            Assert.NotNull(step.NewState.RealmJoinFacts.ResolvedUtc);
            Assert.Null(step.NewState.RealmJoinFacts.SelfDeployingDeferredCompletion);
        }

        // ============================================================== #16b RJ-deferred release race: Timeout variant

        [Fact]
        public void RJDeferredRelease_timeoutVariant_withAccountSetup_demotesAndPreservesTimeoutFact()
        {
            // Same as #15 but RJ never resolves — RealmJoinTimeout deadline fires.
            // Asserts the RJ-gate-open and Outcome=Timeout invariants hold post-bailout (the
            // Plan v9 F1 regression would have discarded WithTimeoutOutcome).
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-16b", "t", T0));
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection })).NewState;
            // AccountSetup arrives unexpectedly post-deferral.
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(9),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            // RJ timeout deadline fires — release path runs WithTimeoutOutcome into builder, then
            // CompleteIfDeferredOrBookkeep re-check bails out.
            var rjTimeout = state.Deadlines.Single(d => d.Name == DeadlineNames.RealmJoinTimeout);
            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DeadlineFired, rjTimeout.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            // No SelfDeploying terminal.
            Assert.NotEqual(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
            // CRITICAL (v9 F1 regression test): RJ Outcome=Timeout must SURVIVE the bailout.
            Assert.Equal(RealmJoinFacts.OutcomeTimeout, step.NewState.RealmJoinFacts.Outcome!.Value);
            // Deferred flag cleared, DeviceOnly hypothesis reset.
            Assert.Null(step.NewState.RealmJoinFacts.SelfDeployingDeferredCompletion);
            Assert.Equal(HypothesisLevel.Unknown, step.NewState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
        }

        // ============================================================== #17 Race guard D: registry NOT self-deploying (session 62e603c9)

        private static DecisionState SeedRegistryNotSelfDeploying(DecisionEngine engine, DecisionState state, long ordinal)
            => engine.Reduce(state, MakeSignal(ordinal, DecisionSignalKind.EnrollmentFactsObserved, T0.AddSeconds(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EnrollmentType] = "v1",
                    [SignalPayloadKeys.IsHybridJoin] = "true",
                    [SignalPayloadKeys.IsSelfDeployingProfile] = "false",
                })).NewState;

        [Fact]
        public void EnrollmentFactsObserved_false_recordsRegistryObservation_withoutSelfDeployingSeed()
        {
            // The reducer records the raw registry fact for BOTH values. A `false` reading is
            // stored as an observation but must NOT seed Mode=SelfDeploying (positive-only seed).
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-obs-1", "t", T0));
            state = SeedRegistryNotSelfDeploying(engine, state, 2);

            Assert.NotNull(state.ScenarioObservations.RegistrySelfDeployingProfile);
            Assert.False(state.ScenarioObservations.RegistrySelfDeployingProfile!.Value);
            Assert.NotEqual(EnrollmentMode.SelfDeploying, state.ScenarioProfile.Mode);
        }

        [Fact]
        public void RaceGuardD_registrySaysNotSelfDeploying_deadEndsAndCancels()
        {
            // The device-only ESP-detection deadline fires, but the deterministic registry probe
            // (CloudAssignedOobeConfig 0x20|0x40) explicitly read `false`. The weak behavioural
            // deadline must NOT override that authoritative fact — dead-end + state-side cancel.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-guardd-1", "t", T0));
            state = SeedRegistryNotSelfDeploying(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            var deadline = state.Deadlines.Single(d => d.Name == DeadlineNames.DeviceOnlyEspDetection);

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, deadline.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("device_only_esp_detection_registry_not_self_deploying", step.Transition.DeadEndReason);
            Assert.NotEqual(SessionStage.Completed, step.NewState.Stage);
            Assert.NotEqual(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
            Assert.Empty(step.Effects);
            // CRITICAL: past-due deadline cancelled state-side so a snapshot reload won't re-arm it.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
        }

        [Fact]
        public void RaceGuardD_registryTrue_completesNormally()
        {
            // Guard D must NOT interfere with a genuine self-deploying device: registry `true`
            // (0x20|0x40 present) still reaches the terminal SelfDeploying branch.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-guardd-2", "t", T0));
            state = SeedSelfDeployingProfile(engine, state, 2);
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
        }

        [Fact]
        public void RaceGuardD_registryUnobserved_completesNormally()
        {
            // Null (fact never observed) must NOT trip guard D — only an explicit `false` vetoes.
            // Registry-blind sessions keep the legacy behavioural-deadline completion.
            var engine = new DecisionEngine();
            var state = PrimeDeviceSetup(engine, DecisionState.CreateInitial("sd-guardd-3", "t", T0));
            Assert.Null(state.ScenarioObservations.RegistrySelfDeployingProfile);
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;

            var step = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
        }
    }
}
