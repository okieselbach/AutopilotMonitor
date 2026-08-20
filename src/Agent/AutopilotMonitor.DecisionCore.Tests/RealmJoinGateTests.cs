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
        public void RealmJoinDetected_records_ProductVersion_and_ReleaseChannel_facts()
        {
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100",
                    [DecisionEngine.RealmJoinPayloadKeys.ProductVersion] = "4.21.6",
                    [DecisionEngine.RealmJoinPayloadKeys.ReleaseChannel] = "canary",
                }));

            Assert.Equal("4.21.6", step.NewState.RealmJoinFacts.ProductVersion!.Value);
            Assert.Equal("canary", step.NewState.RealmJoinFacts.ReleaseChannel!.Value);

            // Set-once: a replayed Detected signal with different values must not overwrite.
            var replay = engine.Reduce(step.NewState, MakeSignal(6, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100",
                    [DecisionEngine.RealmJoinPayloadKeys.ProductVersion] = "9.9.9",
                    [DecisionEngine.RealmJoinPayloadKeys.ReleaseChannel] = "beta",
                }));
            Assert.Equal("4.21.6", replay.NewState.RealmJoinFacts.ProductVersion!.Value);
            Assert.Equal("canary", replay.NewState.RealmJoinFacts.ReleaseChannel!.Value);
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

        // ============================================================== Aborted first deployment (session 224b2087)

        [Fact]
        public void PhaseChanged_leaving_first_deployment_for_200_releases_gate_with_warning()
        {
            // Session 224b2087: interactive logon 17 s after phase 101 → RJ reclassifies the run
            // as secondary-user deployment and writes 200/210, never 110. The 101 -> 200
            // transition is impossible in a healthy first deployment, so it releases the gate
            // with Outcome=FirstDeploymentIncomplete and a Warning timeline entry.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            Assert.NotEqual(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(10),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "200",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "101",
                }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("FirstDeploymentIncomplete", step.NewState.RealmJoinFacts.Outcome!.Value);
            Assert.NotNull(step.NewState.RealmJoinFacts.ResolvedUtc);
            Assert.Equal(200, step.NewState.RealmJoinFacts.LastDeploymentPhase!.Value);

            // Hard-timeout deadline cancelled in state AND as a scheduler effect.
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline &&
                e.CancelDeadlineName == DeadlineNames.RealmJoinTimeout);

            // Warning timeline entry names both phases.
            var warning = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_first_deployment_incomplete");
            Assert.Equal("Warning", warning.Parameters!["severity"]);
            Assert.Equal("101", warning.Parameters["previousPhase"]);
            Assert.Equal("200", warning.Parameters["deploymentPhase"]);
            Assert.Contains("110", warning.Parameters["message"]);
        }

        [Fact]
        public void PhaseChanged_within_first_deployment_window_is_bookkeeping_only()
        {
            // 100 -> 101 stays inside the first-deployment window — no release, but the phase
            // fact must advance (it is the restart-safe witness for the abort rule).
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;

            var step = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "100",
                }));

            Assert.Equal(101, step.NewState.RealmJoinFacts.LastDeploymentPhase!.Value);
            Assert.Null(step.NewState.RealmJoinFacts.Outcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Empty(step.Effects);
        }

        [Fact]
        public void PhaseChanged_from_completed_deployment_start_does_not_release()
        {
            // Session 6f1959c0 protection: RJ already stood at CompletedDeployment (210) when
            // the agent booted and only deployed AFTERWARDS (210 -> 200 -> 210). Neither
            // transition starts from the first-deployment window, so the abort rule must not
            // fire — this ambiguous shape keeps waiting for 110 / the hard timeout (status quo).
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "210" })).NewState;

            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "200",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "210",
                })).NewState;
            Assert.Null(state.RealmJoinFacts.Outcome);

            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(7),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "210",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "200",
                }));

            Assert.Null(step.NewState.RealmJoinFacts.Outcome);
            Assert.Contains(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
        }

        [Fact]
        public void Detected_replay_after_restart_with_deployment_phase_releases_gate()
        {
            // Agent restarted between the 101 and 200 observations: the watcher re-fires
            // Detected with the CURRENT phase (200) and no PhaseChanged ever carries the
            // transition. The persisted LastDeploymentPhase=101 is the witness — the Detected
            // replay path must release the gate exactly like the PhaseChanged path.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            Assert.NotEqual(SessionStage.Finalizing, state.Stage);
            Assert.Equal(101, state.RealmJoinFacts.LastDeploymentPhase!.Value);

            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(12),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "200" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("FirstDeploymentIncomplete", step.NewState.RealmJoinFacts.Outcome!.Value);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_first_deployment_incomplete");
        }

        [Fact]
        public void Timeout_message_reports_persisted_last_phase_instead_of_zero()
        {
            // Before RealmJoinPhaseChanged became a typed signal, LastDeploymentPhase was only
            // written at detection time — every realmjoin_timeout claimed "last phase: 0" no
            // matter how far RJ actually got, hiding this whole failure class from ops.
            // Since the activity-based extension, the PhaseChanged at T0+6 counts as activity,
            // so the first fire (59 min later) extends once; the timeout lands on the re-armed
            // fire a full inactivity window after the last activity.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "100",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DesktopArrived, T0.AddMinutes(7))).NewState;

            // First fire at the original due (T0+65): activity 59 min ago → extends to T0+66.
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, T0.AddMinutes(65),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout })).NewState;
            var rearmed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Equal(T0.AddMinutes(66), rearmed.DueAtUtc);

            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.DeadlineFired, rearmed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            var timeout = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
            Assert.Contains("last phase: 101", timeout.Parameters!["message"]);
            Assert.Equal("101", timeout.Parameters["lastSeenPhase"]);
        }

        // ============================================================== Activity-based extension (report 55e6afd61c9d)

        [Fact]
        public void Timeout_fire_with_recent_first_deployment_activity_extends_instead_of_timing_out()
        {
            // Report 55e6afd61c9d (Douglas): timer armed at detection (phase 0, RJ agent MSI
            // install during DeviceSetup), first deployment only started 16 min later, Office
            // completed 3 s before the deadline — the hard cut truncated an actively working
            // deployment. The fire must now re-arm to lastActivity + 60 min instead.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "0" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(21),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "0",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.RealmJoinPackageCompleted, T0.AddMinutes(62),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.PackageId] = "generic-microsoft-office-2016-proplus",
                    [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
                    [DecisionEngine.RealmJoinPayloadKeys.Success] = "true",
                    [DecisionEngine.RealmJoinPayloadKeys.LastExitCode] = "0",
                })).NewState;

            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            // No completion, no outcome — the gate stays closed and the window is re-armed.
            Assert.NotEqual(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.RealmJoinFacts.Outcome);
            Assert.Equal($"DeadlineFired:{DeadlineNames.RealmJoinTimeout}:Extended", step.Transition.Trigger);

            var rearmed = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Equal(T0.AddMinutes(62 + 60), rearmed.DueAtUtc); // lastActivity + inactivity window

            Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.ScheduleDeadline &&
                e.Deadline != null &&
                e.Deadline.Name == DeadlineNames.RealmJoinTimeout &&
                e.Deadline.DueAtUtc == rearmed.DueAtUtc);

            var extended = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout_extended");
            Assert.Equal("Info", extended.Parameters!["severity"]);
            Assert.Equal("101", extended.Parameters["deploymentPhase"]);
            Assert.DoesNotContain(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
        }

        [Fact]
        public void Resolved_after_extension_completes_via_classic_path()
        {
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(40),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "100",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "101",
                })).NewState;
            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout })).NewState;
            Assert.Null(state.RealmJoinFacts.Outcome); // extended, not timed out

            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(80),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Resolved", step.NewState.RealmJoinFacts.Outcome!.Value);
            Assert.DoesNotContain(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
        }

        [Fact]
        public void Rearmed_fire_after_quiet_window_times_out_with_inactivity_reason()
        {
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "0" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "0",
                })).NewState;
            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout })).NewState;
            var rearmed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Equal(T0.AddMinutes(90), rearmed.DueAtUtc); // lastActivity (T0+30) + 60 min

            // No further activity — the re-armed fire must now time out for real.
            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.DeadlineFired, rearmed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.RealmJoinFacts.Outcome!.Value);
            var timeout = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
            Assert.Equal("inactivity", timeout.Parameters!["reason"]);
            Assert.Contains("no deployment activity", timeout.Parameters["message"]);
        }

        [Fact]
        public void Extension_is_capped_at_absolute_ceiling_and_cap_fire_times_out_despite_activity()
        {
            // Late activity pushes the sliding window beyond detection + 4 h — the re-arm must
            // clamp to the cap, and the cap fire must time out even though activity is recent.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            // Package completion whose sliding window (T0+230+60) would exceed the cap (T0+245).
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPackageCompleted, T0.AddMinutes(230),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.PackageId] = "generic-big-suite",
                    [DecisionEngine.RealmJoinPayloadKeys.Scope] = RealmJoinPackageFact.ScopeMachine,
                    [DecisionEngine.RealmJoinPayloadKeys.Success] = "true",
                    [DecisionEngine.RealmJoinPayloadKeys.LastExitCode] = "0",
                })).NewState;

            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            state = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout })).NewState;

            var rearmed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Equal(T0.AddMinutes(5).AddHours(4), rearmed.DueAtUtc); // clamped to detection + cap

            var step = engine.Reduce(state, MakeSignal(9, DecisionSignalKind.DeadlineFired, rearmed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.RealmJoinFacts.Outcome!.Value);
            var timeout = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
            Assert.Equal("absolute_cap", timeout.Parameters!["reason"]);
        }

        [Fact]
        public void Stale_fire_of_old_incarnation_after_rearm_is_dead_end()
        {
            // Race: the OLD deadline incarnation's fire was already queued when the extension
            // re-armed. The armed deadline is due LATER than the stale fire — dead-end, no
            // second extension evaluation, no timeout.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "0" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(30),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "0",
                })).NewState;
            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout })).NewState;
            Assert.Null(state.RealmJoinFacts.Outcome); // extended

            var step = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.False(step.Transition.Taken);
            Assert.Null(step.NewState.RealmJoinFacts.Outcome);
            Assert.Empty(step.Effects);
            var stillArmed = Assert.Single(step.NewState.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            Assert.Equal(T0.AddMinutes(90), stillArmed.DueAtUtc);
        }

        [Fact]
        public void Detection_phase_alone_is_not_activity_idle_case_times_out_at_60_min_unchanged()
        {
            // The phase captured AT detection must not count as activity — RJ detected in a
            // first-deployment phase that then never moves still times out after the original
            // 60 min (the extension exists for demonstrable progress, not for standing still).
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            Assert.Null(state.RealmJoinFacts.LastActivityUtc);

            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            var step = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.RealmJoinFacts.Outcome!.Value);
            var timeout = Assert.Single(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_timeout");
            Assert.Equal("hard_timeout", timeout.Parameters!["reason"]);
        }

        [Fact]
        public void Regular_deployment_phase_with_activity_does_not_extend()
        {
            // Session 6f1959c0 shape: RJ stood at 210 when the agent booted and deploys
            // regular (non-first) packages afterwards. Phase 200/210 is outside the
            // first-deployment window — activity there must NOT hold the session hostage.
            var engine = new DecisionEngine();
            var state = PrimeClassicAwaitingDesktop(engine);
            state = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(5),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "210" })).NewState;
            state = engine.Reduce(state, MakeSignal(6, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;
            state = engine.Reduce(state, MakeSignal(7, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(40),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "200",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "210",
                })).NewState;

            var armed = Assert.Single(state.Deadlines, d => d.Name == DeadlineNames.RealmJoinTimeout);
            var step = engine.Reduce(state, MakeSignal(8, DecisionSignalKind.DeadlineFired, armed.DueAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.RealmJoinTimeout }));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Equal("Timeout", step.NewState.RealmJoinFacts.Outcome!.Value);
        }

        [Fact]
        public void SelfDeploying_deferred_terminal_released_by_first_deployment_incomplete()
        {
            // Same deferred-release contract as RealmJoinResolved: when the SelfDeploying
            // terminal was deferred on the closed RJ gate, the aborted-first-deployment release
            // must complete the session directly (Completed + enrollment_complete), with the
            // Warning entry leading the effects.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("rj-sd-3", "rj-tenant", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(1, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(2, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "101" })).NewState;
            state = engine.Reduce(state, MakeSignal(3, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(3))).NewState;
            state = engine.Reduce(state, MakeSignal(4, DecisionSignalKind.DeadlineFired, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.DeviceOnlyEspDetection })).NewState;
            Assert.True(state.RealmJoinFacts.SelfDeployingDeferredCompletion?.Value);

            var step = engine.Reduce(state, MakeSignal(5, DecisionSignalKind.RealmJoinPhaseChanged, T0.AddMinutes(10),
                new Dictionary<string, string>
                {
                    [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "200",
                    [DecisionEngine.RealmJoinPayloadKeys.PreviousPhase] = "101",
                }));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);
            Assert.Equal("FirstDeploymentIncomplete", step.NewState.RealmJoinFacts.Outcome!.Value);
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "realmjoin_first_deployment_incomplete");
            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry &&
                e.Parameters != null &&
                e.Parameters.TryGetValue("eventType", out var et) && et == "enrollment_complete");
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
