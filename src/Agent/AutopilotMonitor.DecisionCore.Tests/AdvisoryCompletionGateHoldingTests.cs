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
    /// Sessions 81daa77f / 75d6ae8e (2026-08-25) — the 30-min <c>advisory_completion</c> window
    /// overran a completion gate that was deliberately holding the session. The RealmJoin gate
    /// blocks completion until phase 110 and carries its own bounded resolution (60 min from
    /// detection, re-armed on deployment activity, hard-capped at 4 h), so on every device whose
    /// first deployment outlives 30 minutes the enrollment was failed while the engine own
    /// <c>completion_waiting</c> still read "waiting on: realmjoin_resolution". 75d6ae8e
    /// installed 25 RealmJoin packages successfully inside the window, the last one 2 min before
    /// the failure; the customer tenant hit this on 10 of 30 enrollments in one week.
    /// <para>
    /// The guard re-arms while the gate holds. Convergence comes from the gate itself: Resolved,
    /// FirstDeploymentIncomplete and Timeout all set <c>RealmJoinFacts.Outcome</c>, which opens
    /// <c>RealmJoinGateOpen</c> and stops the guard applying.
    /// </para>
    /// </summary>
    public sealed class AdvisoryCompletionGateHoldingTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 25, 6, 10, 0, DateTimeKind.Utc);

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
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = deadlineName });

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        /// <summary>
        /// Replays the shape both field sessions shared: Classic user-driven, full ESP, Hello
        /// policy enabled, AccountSetup entered, then a guard-blocked post-AccountSetup ESP exit
        /// (Shell-Core 62407, "Exiting page normally", errorCode 0) that arms the 30-min window.
        /// The ESP registry froze at "1 of 5" and the device had zero user-targeted Intune apps,
        /// so no arm of ShouldTransitionToAwaitingHello could ever open.
        /// </summary>
        private static DecisionState SetupEspExitDeadEnd(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("sess-81daa77f", "tenant-81daa77f", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2.5),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            // Guard-blocked post-AccountSetup exit — arms advisory_completion at exit + 30 min.
            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(3))).NewState;
            Assert.NotNull(FindDeadline(state, DeadlineNames.AdvisoryCompletion));
            Assert.Null(state.Outcome);
            return state;
        }

        /// <summary>Desktop (DAD-validated real user), RealmJoin detected, Hello provisioned.</summary>
        private static DecisionState AddDesktopRealmJoinAndHello(DecisionEngine engine, DecisionState state)
        {
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(3.5))).NewState;
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(6),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "0" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.HelloResolved, T0.AddMinutes(14),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" })).NewState;
            return state;
        }

        // ==================================================== the gate-holding guard ====

        [Fact]
        public void DeadlineFired_WhileRealmJoinGateHolds_RearmsInsteadOfFailing()
        {
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEnd(engine);
            state = AddDesktopRealmJoinAndHello(engine, state);

            // Preconditions: the gate is closed and owns an armed deadline of its own.
            Assert.False(DecisionEngine.RealmJoinGateOpen(state));
            Assert.NotNull(FindDeadline(state, DeadlineNames.RealmJoinTimeout));

            var fireAt = T0.AddMinutes(33);
            var step = engine.Reduce(state, DeadlineFired(70, fireAt, DeadlineNames.AdvisoryCompletion));

            Assert.True(step.Transition.Taken);
            Assert.Null(step.NewState.Outcome);
            Assert.NotEqual(SessionStage.Failed, step.NewState.Stage);

            var rearmed = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(rearmed);
            Assert.Equal(fireAt.AddMinutes(30), rearmed!.DueAtUtc);

            var schedule = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, schedule.Deadline!.Name);

            var waiting = SingleTimelineEffect(step, "completion_waiting");
            Assert.Contains("CompletionGateHolding", waiting.Parameters!["trigger"]);
            Assert.Contains("realmjoin_resolution", waiting.Parameters!["missingPrerequisites"]);
            Assert.Equal(rearmed.DueAtUtc.ToString("o"), waiting.Parameters!["resolutionDeadlineDueAtUtc"]);
        }

        [Fact]
        public void Session81daa77f_RealmJoinResolvesAfterTheWindow_CompletesInsteadOfFailing()
        {
            // End-to-end proof against the real field shape: before this guard the session was
            // stamped Failed at exit+30 min with esp_exit_without_completion_evidence, 34 min
            // before the RealmJoin gate would have released it.
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEnd(engine);
            state = AddDesktopRealmJoinAndHello(engine, state);

            var fired = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(33), DeadlineNames.AdvisoryCompletion));
            Assert.Null(fired.NewState.Outcome);

            // RealmJoin reaches phase 110 — the gate opens and the session completes.
            var resolved = engine.Reduce(fired.NewState, MakeSignal(
                80, DecisionSignalKind.RealmJoinResolved, T0.AddMinutes(50),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "110" }));

            Assert.Equal(SessionStage.Finalizing, resolved.NewState.Stage);
            Assert.Equal("Resolved", resolved.NewState.RealmJoinFacts.Outcome!.Value);

            var grace = FindDeadline(resolved.NewState, DeadlineNames.FinalizingGrace);
            Assert.NotNull(grace);
            var completed = engine.Reduce(resolved.NewState,
                DeadlineFired(90, grace!.DueAtUtc, DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, completed.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, completed.NewState.Outcome);
        }

        // ============================================================== convergence ====

        [Fact]
        public void DeadlineFired_AfterTheGateOpened_ResolvesNormallyAgain()
        {
            // Convergence proof: the guard is not an escape hatch. Once RealmJoin timed out the
            // gate is open, the guard stops applying, and a session with no completion evidence
            // fails exactly as before.
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEnd(engine);
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.RealmJoinDetected, T0.AddMinutes(6),
                new Dictionary<string, string> { [DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase] = "0" })).NewState;

            var rjDeadline = FindDeadline(state, DeadlineNames.RealmJoinTimeout);
            Assert.NotNull(rjDeadline);
            state = engine.Reduce(state,
                DeadlineFired(60, rjDeadline!.DueAtUtc, DeadlineNames.RealmJoinTimeout)).NewState;

            Assert.True(DecisionEngine.RealmJoinGateOpen(state));
            Assert.Null(FindDeadline(state, DeadlineNames.RealmJoinTimeout));

            // No desktop, no Hello, no IME evidence — the genuine dead-end shape.
            var step = engine.Reduce(state,
                DeadlineFired(70, T0.AddMinutes(80), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_exit_without_completion_evidence", failed.Parameters!["reason"]);
        }

        // =========================================================== mutation proofs ====

        [Fact]
        public void DeadlineFired_WithoutRealmJoin_StillFails()
        {
            // The guard must key on an actually-held gate, never on the absence of evidence.
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEnd(engine);

            var step = engine.Reduce(state,
                DeadlineFired(70, T0.AddMinutes(33), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_exit_without_completion_evidence", failed.Parameters!["reason"]);
        }

        [Fact]
        public void DeadlineFired_GateClosedButItsDeadlineNoLongerArmed_StillFails()
        {
            // A gate that can no longer resolve itself must not park the session forever. The
            // hand-built state drops only the RealmJoin deadline, leaving the gate closed.
            var engine = new DecisionEngine();
            var state = SetupEspExitDeadEnd(engine);
            state = AddDesktopRealmJoinAndHello(engine, state);

            var builder = state.ToBuilder();
            builder.CancelDeadline(DeadlineNames.RealmJoinTimeout);
            state = builder.Build();

            Assert.False(DecisionEngine.RealmJoinGateOpen(state));
            Assert.Null(FindDeadline(state, DeadlineNames.RealmJoinTimeout));

            var step = engine.Reduce(state,
                DeadlineFired(70, T0.AddMinutes(33), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }
    }
}
