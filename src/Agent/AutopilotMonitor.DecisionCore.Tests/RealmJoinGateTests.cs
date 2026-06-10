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
    /// Reducer integration tests for the RealmJoin (RJ) completion-gate. The Classic and
    /// SelfDeploying completion paths must defer terminal transitions while RJ is detected
    /// and unresolved, and release them once <see cref="DecisionSignalKind.RealmJoinResolved"/>
    /// arrives or the <see cref="DeadlineNames.RealmJoinTimeout"/> deadline fires.
    /// </summary>
    public sealed class RealmJoinGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"t-{kind}-{ordinal}", $"synthetic {kind}"),
                payload: payload);
        }

        private static DecisionState PrimeClassicAwaitingDesktop(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("rj-sess", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.EspExiting, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.HelloResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            Assert.Equal(SessionStage.AwaitingDesktop, state.Stage);
            return state;
        }

        // ============================================================== Classic flow

        [Fact]
        public void Baseline_without_realmjoin_detected_completes_normally()
        {
            // Regression guard — on devices where RJ is not installed (DetectedUtc stays null),
            // the gate is open and the Hello+Desktop AND-gate must reach Finalizing exactly as
            // before Phase A.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            Assert.Null(state.RealmJoinFacts.DetectedUtc);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.DesktopArrived, T0.AddMinutes(5)));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
        }

        [Fact]
        public void RealmJoinDetected_arms_the_60_min_hard_timeout_deadline()
        {
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" }));

            Assert.NotNull(step.NewState.RealmJoinFacts.DetectedUtc);
            Assert.Equal(100, step.NewState.RealmJoinFacts.LastDeploymentPhase!.Value);

            var deadline = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            // 60-min hard timeout from the post-PrimeClassic AgentBoot anchor (T0) — within rounding.
            Assert.InRange(deadline.DueAtUtc, T0.AddMinutes(55), T0.AddMinutes(70));

            // ScheduleDeadline effect emitted exactly once.
            Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.ScheduleDeadline &&
                e.Deadline != null &&
                e.Deadline.Name == DeadlineNames.RealmJoinTimeout);
        }

        [Fact]
        public void RealmJoinGate_closed_defers_finalizing_when_DesktopArrived_arrives_with_hello_already_resolved()
        {
            // Sequence: Hello + RealmJoinDetected before Desktop, then DesktopArrived. The AND-gate
            // would normally TransitionToFinalizing — but the closed gate must keep the session
            // out of Finalizing until RJ resolves.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);

            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6)));

            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.NotNull(step.NewState.DesktopArrivedUtc); // fact still recorded
        }

        [Fact]
        public void Closed_gate_tags_the_deferred_transition_with_the_gate_name_suffix()
        {
            // ARCH-F1 forward-proof: the defer trigger suffix is derived from the closed gate's
            // CompletionGate.Name (":<Name>Closed") inside CompleteThroughFinalizingOrDefer, not
            // hardcoded at the call site. A new gate registered in s_completionGates inherits the
            // same deferral shape with its own suffix — this test locks the derivation so a rename
            // or a regression back to per-site hardcoding is caught.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6)));

            Assert.True(step.Transition.Taken);
            Assert.Equal(nameof(DecisionSignalKind.DesktopArrived) + ":RealmJoinGateClosed", step.Transition.Trigger);
        }

        [Fact]
        public void RealmJoinResolved_after_hello_and_desktop_triggers_finalizing_via_classic_path()
        {
            // Same setup as the previous test, then RealmJoinResolved (phase 110) lands. The
            // resolved handler routes through CompleteIfDeferredOrBookkeep → TransitionToFinalizing.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            Assert.NotEqual(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(7),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            // RealmJoinTimeout deadline got cancelled both in state and as a scheduler effect.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.RealmJoinTimeout);
            Assert.Equal("Resolved", step.NewState.RealmJoinFacts.Outcome!.Value);
        }

        [Fact]
        public void Stale_RealmJoinTimeout_after_Resolved_is_bookkept_dead_end_no_effects()
        {
            // Race: RealmJoinResolved arrives + cancels the timeout, but the queued
            // DeadlineFired:realmjoin_timeout was already in flight on the signal worker. The
            // idempotency guard must short-circuit before emitting a spurious realmjoin_timeout
            // timeline event or re-entering TransitionToFinalizing.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(7),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" })).NewState;
            Assert.Equal("Resolved", state.RealmJoinFacts.Outcome!.Value);
            // Deadline already cancelled in state — but the queued DeadlineFired hasn't
            // been informed yet.

            var step = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, T0.AddMinutes(65),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            // State must NOT mutate to Timeout outcome.
            Assert.Equal("Resolved", step.NewState.RealmJoinFacts.Outcome!.Value);
            // Transition is recorded as DeadEnd with the stale reason — no taken-step.
            Assert.False(step.Transition.Taken);
            Assert.Equal("realmjoin_timeout_stale_outcome_already_set", step.Transition.DeadEndReason);
            // No effects at all — no spurious realmjoin_timeout event, no FinalizingGrace re-arm.
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void RealmJoinTimeout_with_hello_and_desktop_in_completes_with_timeout_outcome()
        {
            // Hard 60-min timeout fires while RJ is still incomplete but Hello+Desktop already
            // landed. Handler routes through CompleteIfDeferredOrBookkeep → TransitionToFinalizing
            // and records realmjoinOutcome="Timeout".
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;

            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DeadlineFired, T0.AddMinutes(65),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.RealmJoinFacts.Outcome!.Value);

            // The reducer emitted a realmjoin_timeout timeline entry alongside the
            // Finalizing-transition effects.
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
        }

        // ============================================================== SelfDeploying flow

        [Fact]
        public void SelfDeploying_DeviceSetupProvisioningComplete_with_realmjoin_open_armsDeadline_thenDeadlineFiredCompletes()
        {
            // Baseline — RJ never detected → SelfDeploying terminal path completes via the new
            // 5-min deadline (Plan v9 88a53223 defang). The signal itself is no longer terminal;
            // it just arms the deadline. Then DeadlineFired drives the terminal transition.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-sd", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;

            // Signal arms the deadline; Stage stays EspDeviceSetup.
            var signalStep = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(2)));
            Assert.Equal(SessionStage.EspDeviceSetup, signalStep.NewState.Stage);
            Assert.NotNull(signalStep.NewState.DeviceSetupResolvedUtc);
            var deadline = Assert.Single(signalStep.NewState.Deadlines, d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(T0.AddMinutes(7), deadline.DueAtUtc);

            // DeadlineFired (OccurredAtUtc = DueAtUtc per scheduler contract) → terminal.
            var step = engine.Reduce(signalStep.NewState, MakeSignal(3, DecisionSignalKind.DeadlineFired, T0.AddMinutes(7),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            Assert.Equal(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
            Assert.Equal("selfdeploying_deadline_confirmed", step.NewState.ScenarioProfile.Reason);
        }

        [Fact]
        public void SelfDeploying_with_realmjoin_detected_defers_terminal_until_resolved()
        {
            // Plan v9 (88a53223 defang): RJ-deferral moves from signal-time to deadline-fire-time.
            // The DeviceSetupProvisioningComplete signal arms the deadline; the deadline-fired
            // handler observes the RJ gate is closed and marks SelfDeployingDeferredCompletion.
            // RealmJoinResolved then routes through CompleteIfDeferredOrBookkeep to terminal.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-sd-2", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            // Signal arms deadline. Deferred flag NOT yet set (Plan v9: only at deadline-fire).
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            Assert.NotEqual(SessionStage.Completed, state.Stage);
            Assert.Null(state.RealmJoinFacts.SelfDeployingDeferredCompletion);

            // Deadline fires. RJ-gate still closed → set deferred flag, NO terminal.
            var deferredStep = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection }));
            var deferred = deferredStep.NewState;
            Assert.NotEqual(SessionStage.Completed, deferred.Stage);
            Assert.True(deferred.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);

            // RealmJoinResolved releases the deferred SelfDeploying terminal.
            var step = engine.Reduce(deferred, MakeSignal(5, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(10),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            // Direct Completed path emits enrollment_complete; no FinalizingGrace deadline needed
            // because the RJ-deferred branch clears deadlines.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
            // Plan v9 F2: ScenarioProfile promoted to SelfDeploying/High in RJ-deferred-release.
            Assert.Equal(EnrollmentMode.SelfDeploying, step.NewState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, step.NewState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", step.NewState.ScenarioProfile.Reason);
            // RJ gate is open post-release (the WithResolved fact survived ClearSelfDeployingDeferred).
            Assert.NotNull(step.NewState.RealmJoinFacts.ResolvedUtc);
        }

        // ============================================================== Per-package tracking

        [Fact]
        public void Per_package_started_and_completed_signals_update_RealmJoinFacts_packages()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-pkg", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinPackageStarted, T0.AddMinutes(2),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.PackageId] = "generic-vlc",
                    [DecisionEngine.RealmJoinPayloadKeys.DisplayName] = "VLC media player",
                    [DecisionEngine.RealmJoinPayloadKeys.Version] = "3.0.21.0",
                    [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
                })).NewState;
            Assert.Single(state.RealmJoinFacts.Packages, p => p.PackageId == "generic-vlc" && p.CompletedUtc == null);

            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.RealmJoinPackageCompleted, T0.AddMinutes(3),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.PackageId] = "generic-vlc",
                    [DecisionEngine.RealmJoinPayloadKeys.DisplayName] = "VLC media player",
                    [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
                    [DecisionEngine.RealmJoinPayloadKeys.Success] = "true",
                    [DecisionEngine.RealmJoinPayloadKeys.LastExitCode] = "0",
                })).NewState;
            var completed = Assert.Single(state.RealmJoinFacts.Packages, p => p.PackageId == "generic-vlc");
            Assert.True(completed.Success);
            Assert.Equal(0, completed.LastExitCode);
            Assert.NotNull(completed.CompletedUtc);
        }

        // ============================================================== Audit trail

        [Fact]
        public void Audit_trail_attaches_realmjoin_fields_when_enrollment_completes_after_resolved()
        {
            // After SelfDeploying-deadline-fired (deferred via RJ-gate) + RealmJoinResolved, the
            // enrollment_complete effect must carry the realmjoin* audit-trail fields built by
            // DecisionAuditTrailBuilder. Plan v9: signal arms deadline, deadline-fire defers when
            // RJ gate closed, RJ-resolve releases via CompleteIfDeferredOrBookkeep.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-audit", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            // Deadline fires at T+8 (signal at T+3 + 5min) → RJ gate closed → deferred.
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection })).NewState;
            Assert.True(state.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(10),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            var completeEffect = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");

            Assert.NotNull(completeEffect.TypedPayload);
            var data = Assert.IsType<Dictionary<string, object>>(completeEffect.TypedPayload);
            Assert.Equal("Resolved", data["realmjoinOutcome"]);
            Assert.Equal(110, data["realmjoinLastPhase"]);
            Assert.True(data.ContainsKey("realmjoinDetectedUtc"));
            Assert.True(data.ContainsKey("realmjoinResolvedUtc"));
        }
    }
}
