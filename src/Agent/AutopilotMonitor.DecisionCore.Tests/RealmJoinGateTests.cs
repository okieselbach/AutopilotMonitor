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
        public void SelfDeploying_DeviceSetupProvisioningComplete_with_realmjoin_open_completes_immediately()
        {
            // Baseline — RJ never detected → SelfDeploying terminal path completes directly.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-sd", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;

            var step = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(2)));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
        }

        [Fact]
        public void SelfDeploying_with_realmjoin_detected_defers_terminal_until_resolved()
        {
            // SelfDeploying terminal handler observes the gate is closed → marks
            // SelfDeployingDeferredCompletion on RealmJoinFacts, stays non-terminal, lets the
            // RealmJoinResolved handler complete directly to Completed (not via Finalizing).
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-sd-2", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            var deferred = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            Assert.NotEqual(SessionStage.Completed, deferred.Stage);
            Assert.True(deferred.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);

            var step = engine.Reduce(deferred, MakeSignal(4, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(4),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            // Direct Completed path emits enrollment_complete inline; no FinalizingGrace deadline
            // needed because the SelfDeploying handler clears deadlines.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.FinalizingGrace);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
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
            // After SelfDeploying + RealmJoinResolved, the enrollment_complete effect must carry
            // the realmjoin* audit-trail fields built by DecisionAuditTrailBuilder.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-audit", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;

            var step = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(4),
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
