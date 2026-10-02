using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Report 3a2207978e4c (2026-10-01, D-310) — the OOBE quality update. With the ESP setting
    /// "Install Windows quality updates" Windows installs the update after the Device-ESP page
    /// closes and before the user signs in: 20–40 minutes plus a restart to the lock screen. The
    /// IME had logged its AccountSetup phase line before that handoff exit, so the exit armed the
    /// 30-min <c>advisory_completion</c> window, and the session was failed
    /// (<c>esp_exit_without_completion_evidence</c>) while the update was still installing.
    /// <para>
    /// The OOBE update phase holds the window, every state bounded on its own (user decision
    /// 2026-10-02): the update runs at most 3 h, then fails as not finished; after the update the
    /// user has 60 min to sign in, then the engine stops waiting — no verdict, the backend
    /// classifies; after the sign-in the user's own setup gets one window.
    /// </para>
    /// <para>
    /// Only the Active mode acts (measurement first, user decision 2026-10-02). In the Shadow mode
    /// — the default, and what an unstamped session counts as — the fire reports what the phase
    /// would decide and today's rule decides; Off evaluates nothing.
    /// </para>
    /// </summary>
    public sealed class OobeUpdatePhaseTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);
        private const string Lcu = "2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5099999)";

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

        private static DecisionSignal OsUpdate(long ordinal, DateTime at, string step, string? update = Lcu)
        {
            var payload = new Dictionary<string, string>
            {
                [OsUpdatePayloadKeys.Step] = step,
                [OsUpdatePayloadKeys.Source] = "wu",
            };
            if (update != null) payload[OsUpdatePayloadKeys.Update] = update;
            return MakeSignal(ordinal, DecisionSignalKind.OsUpdateActivity, at, payload);
        }

        private static DecisionSignal AdvisoryFired(long ordinal, DateTime at) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, at,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.AdvisoryCompletion });

        private static DecisionSignal Reboot(long ordinal, DateTime at) =>
            MakeSignal(ordinal, DecisionSignalKind.SystemRebootObserved, at);

        /// <summary>The agent's mode stamp on <c>EnrollmentFactsObserved</c>, posted at every start.</summary>
        private static DecisionSignal PhaseMode(long ordinal, DateTime at, string mode) =>
            MakeSignal(ordinal, DecisionSignalKind.EnrollmentFactsObserved, at,
                new Dictionary<string, string> { [SignalPayloadKeys.OobeUpdatePhaseMode] = mode });

        private static bool HasTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Any(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        /// <summary>
        /// The report's shape up to the arming exit: Classic user-driven, full ESP, DeviceSetup,
        /// the IME's pre-sign-in AccountSetup line, then the Device-ESP handoff exit at T0+21,
        /// which arms the window (due T0+51). <paramref name="beforeExit"/> runs before the exit;
        /// <paramref name="mode"/> is the agent's mode stamp (null: none, an older agent).
        /// </summary>
        private static DecisionState HandoffExitArmsTheWindow(
            DecisionEngine engine,
            Func<DecisionState, DecisionState>? beforeExit = null,
            string? mode = OobeUpdatePhaseModes.Active)
        {
            var state = DecisionState.CreateInitial("sess-3a220797", "tenant-3a220797", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            if (mode != null) state = engine.Reduce(state, PhaseMode(1, T0, mode)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(20),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            if (beforeExit != null) state = beforeExit(state);

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(21))).NewState;
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(armed);
            Assert.Equal(T0.AddMinutes(51), armed!.DueAtUtc);
            Assert.Null(state.Outcome);
            return state;
        }

        /// <summary>
        /// Fires the armed advisory deadline at its due time until a fire does not re-arm it;
        /// returns every step. Proves each re-arm lands on time — the last one on the bound.
        /// </summary>
        private static List<DecisionStep> FireUntilResolved(DecisionEngine engine, DecisionState state, long firstOrdinal)
        {
            var steps = new List<DecisionStep>();
            var ordinal = firstOrdinal;
            while (true)
            {
                var deadline = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
                Assert.NotNull(deadline);
                var step = engine.Reduce(state, AdvisoryFired(ordinal++, deadline!.DueAtUtc));
                steps.Add(step);
                state = step.NewState;
                if (FindDeadline(state, DeadlineNames.AdvisoryCompletion) == null) return steps;
                Assert.True(steps.Count < 50, "the phase never resolved");
            }
        }

        // ================================================================= handler ====

        [Fact]
        public void ActivityAfterTheHandoffExit_AnchorsThePhase()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            var first = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted));
            Assert.True(first.Transition.Taken);
            Assert.Equal("OsUpdateActivity:OobeUpdatePhaseStarted", first.Transition.Trigger);
            Assert.Equal(state.Stage, first.NewState.Stage);
            Assert.Empty(first.Effects);

            var facts = first.NewState.OsUpdateFacts;
            Assert.Equal(T0.AddMinutes(22), facts.PostExitActivityUtc!.Value);
            Assert.Equal(40, facts.PostExitActivityUtc.SourceSignalOrdinal);
            Assert.True(DecisionEngine.IsInOobeUpdatePhase(first.NewState));

            var second = engine.Reduce(first.NewState, OsUpdate(41, T0.AddMinutes(35), OsUpdateSteps.InstallStarted));
            Assert.Equal("OsUpdateActivity", second.Transition.Trigger);
            facts = second.NewState.OsUpdateFacts;
            Assert.Equal(T0.AddMinutes(22), facts.PostExitActivityUtc!.Value);
            Assert.Equal(T0.AddMinutes(35), facts.LastActivityUtc!.Value);
            Assert.Equal(OsUpdateSteps.InstallStarted, facts.LastStep!.Value);
            Assert.Equal(Lcu, facts.LastUpdate!.Value);
            Assert.Equal(2, facts.ActivityCount);
        }

        [Fact]
        public void RebootRequiredAndFailure_AreRecorded()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(30), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(31), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, OsUpdate(42, T0.AddMinutes(32), OsUpdateSteps.Failed, update: null)).NewState;

            var facts = state.OsUpdateFacts;
            Assert.Equal(T0.AddMinutes(30), facts.RebootRequiredUtc!.Value); // earliest
            Assert.Equal(T0.AddMinutes(32), facts.FailedUtc!.Value);
            Assert.Equal("KB5099999", facts.LastUpdate!.Value); // a step without an update keeps the last one
        }

        [Fact]
        public void ActivityWithoutStep_IsADeadEnd()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            var step = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.OsUpdateActivity, T0.AddMinutes(22),
                new Dictionary<string, string> { [OsUpdatePayloadKeys.Source] = "wu" }));

            Assert.False(step.Transition.Taken);
            Assert.Equal("os_update_activity_without_step", step.Transition.DeadEndReason);
            Assert.Equal(0, step.NewState.OsUpdateFacts.ActivityCount);
        }

        [Fact]
        public void ActivityBeforeTheExit_IsNoPhase()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, beforeExit: s =>
                engine.Reduce(s, OsUpdate(25, T0.AddMinutes(15), OsUpdateSteps.Installed)).NewState);

            Assert.NotNull(state.OsUpdateFacts.FirstActivityUtc);
            Assert.Null(state.OsUpdateFacts.PostExitActivityUtc);
            Assert.False(DecisionEngine.IsInOobeUpdatePhase(state));
        }

        [Fact]
        public void BackfilledActivityThatPredatesTheExit_IsNoPhase()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(18), OsUpdateSteps.InstallStarted)).NewState;

            Assert.Null(state.OsUpdateFacts.PostExitActivityUtc);
            Assert.Equal(1, state.OsUpdateFacts.ActivityCount);
        }

        [Fact]
        public void ActivityAfterTheUserSignedIn_IsNoPhase()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, MakeSignal(35, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(25))).NewState;

            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(30), OsUpdateSteps.InstallStarted)).NewState;

            Assert.Null(state.OsUpdateFacts.PostExitActivityUtc);
            Assert.False(DecisionEngine.IsInOobeUpdatePhase(state));
        }

        // ============================================================ restart facts ====

        [Fact]
        public void RestartDuringThePhase_IsRecorded()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;

            state = engine.Reduce(state, Reboot(50, T0.AddMinutes(53))).NewState;

            Assert.Equal(T0.AddMinutes(53), state.OsUpdateFacts.RestartUtc!.Value);
            Assert.Equal(50, state.OsUpdateFacts.RestartUtc.SourceSignalOrdinal);
        }

        [Fact]
        public void RestartBeforeThePhase_OrAfterTheSignIn_IsNotTheUpdatesRestart()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, Reboot(35, T0.AddMinutes(21.5))).NewState; // no anchor yet
            Assert.Null(state.OsUpdateFacts.RestartUtc);

            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, MakeSignal(45, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(30))).NewState;
            state = engine.Reduce(state, Reboot(50, T0.AddMinutes(40))).NewState; // user already there

            Assert.Null(state.OsUpdateFacts.RestartUtc);
        }

        // ======================================================= advisory window ====

        [Fact]
        public void Report3a2207978e4c_FireDuringTheUpdate_RearmsInsteadOfFailing()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(35), OsUpdateSteps.InstallStarted)).NewState;

            var fireAt = T0.AddMinutes(51);
            var step = engine.Reduce(state, AdvisoryFired(50, fireAt));

            Assert.True(step.Transition.Taken);
            Assert.Null(step.NewState.Outcome);
            Assert.NotEqual(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("DeadlineFired:advisory_completion:OobeUpdatePhase", step.Transition.Trigger);

            var rearmed = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.Equal(fireAt.AddMinutes(30), rearmed!.DueAtUtc);
            var schedule = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, schedule.Deadline!.Name);

            var waiting = SingleTimelineEffect(step, "completion_waiting");
            Assert.Contains("installing an update in OOBE", waiting.Parameters!["message"]);
            Assert.Equal(OsUpdateSteps.InstallStarted, waiting.Parameters!["osUpdateLastStep"]);
            Assert.Equal(Lcu, waiting.Parameters!["osUpdate"]);
            Assert.Equal(rearmed.DueAtUtc.ToString("o"), waiting.Parameters!["resolutionDeadlineDueAtUtc"]);
            // The update may run 3 h from its first activity.
            Assert.Equal(T0.AddMinutes(22).AddHours(3).ToString("o"), waiting.Parameters!["oobeUpdateHoldsUntilUtc"]);
        }

        [Fact]
        public void WithoutUpdateActivity_TheFireStillFails()
        {
            // Mutation proof: the guard keys on observed update activity, never on its absence.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("esp_exit_without_completion_evidence", SingleTimelineEffect(step, "enrollment_failed").Parameters!["reason"]);
        }

        [Fact]
        public void ActivityOnlyBeforeTheExit_TheFireStillFails()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, beforeExit: s =>
                engine.Reduce(s, OsUpdate(25, T0.AddMinutes(15), OsUpdateSteps.Installed)).NewState);

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
        }

        [Fact]
        public void UpdateStillRunningAtTheCap_FailsAsNotFinished()
        {
            // A hanging update: the last step says it still runs, and no restart ever comes.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(30), OsUpdateSteps.InstallStarted)).NewState;

            var steps = FireUntilResolved(engine, state, firstOrdinal: 100);

            Assert.All(steps.Take(steps.Count - 1), s =>
                Assert.Equal("DeadlineFired:advisory_completion:OobeUpdatePhase", s.Transition.Trigger));
            var cap = T0.AddMinutes(22).AddHours(3);
            // The last re-arm lands on the cap, not up to 30 min after it.
            Assert.Equal(cap, FindDeadline(steps[steps.Count - 2].NewState, DeadlineNames.AdvisoryCompletion)!.DueAtUtc);

            var resolved = steps.Last();
            Assert.Equal(SessionStage.Failed, resolved.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, resolved.NewState.Outcome);
            Assert.Equal("DeadlineFired:advisory_completion:OobeUpdateNotFinished", resolved.Transition.Trigger);
            var failed = SingleTimelineEffect(resolved, "enrollment_failed");
            Assert.Equal("oobe_update_not_finished", failed.Parameters!["reason"]);
            Assert.Equal(OsUpdateSteps.InstallStarted, failed.Parameters!["osUpdateLastStep"]);
            Assert.Equal("3", failed.Parameters!["oobeUpdateCapHours"]);
            Assert.Null(resolved.NewState.StoppedWaitingReason);
        }

        [Fact]
        public void UpdateWithRestart_WaitsSixtyMinutesForTheSignIn_ThenStopsWaiting()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(40), OsUpdateSteps.InstallStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(42, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;

            var beforeRestart = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));
            Assert.Equal("DeadlineFired:advisory_completion:OobeUpdateAwaitingSignIn", beforeRestart.Transition.Trigger);
            state = beforeRestart.NewState;

            // The update page restarts the device; the lock screen waits for the user.
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState;
            Assert.Equal(T0.AddMinutes(83), FindDeadline(state, DeadlineNames.AdvisoryCompletion)!.DueAtUtc); // re-based

            var steps = FireUntilResolved(engine, state, firstOrdinal: 100);

            var waiting = steps.Take(steps.Count - 1).ToList();
            Assert.All(waiting, s => Assert.Equal("DeadlineFired:advisory_completion:OobeUpdateAwaitingSignIn", s.Transition.Trigger));
            Assert.Contains("waiting for the user to sign in",
                SingleTimelineEffect(waiting[0], "completion_waiting").Parameters!["message"]);

            var stopped = steps.Last();
            Assert.Equal(T0.AddMinutes(53).AddMinutes(60), stopped.Transition.OccurredAtUtc);
            Assert.Equal("DeadlineFired:advisory_completion:OobeUpdateSignInWaitExpired", stopped.Transition.Trigger);
            Assert.True(stopped.Transition.Taken);
            Assert.Equal(StoppedWaitingReasons.OobeUpdateNoSignIn, stopped.NewState.StoppedWaitingReason!.Value);
            // No verdict: the stage stays, nothing is failed, nothing is armed any more.
            Assert.Equal(state.Stage, stopped.NewState.Stage);
            Assert.Null(stopped.NewState.Outcome);
            Assert.Empty(stopped.NewState.Deadlines);

            var expired = SingleTimelineEffect(stopped, "completion_wait_expired");
            Assert.Equal("Warning", expired.Parameters!["severity"]);
            Assert.Equal("true", expired.Parameters!["immediateUpload"]);
            Assert.Equal(StoppedWaitingReasons.OobeUpdateNoSignIn, expired.Parameters!["reason"]);
            Assert.Equal("60", expired.Parameters!["signInWindowMinutes"]);
            Assert.Equal(T0.AddMinutes(53).ToString("o"), expired.Parameters!["oobeUpdateEndedUtc"]);
            Assert.Contains("Not a failure", expired.Parameters!["message"]);
        }

        [Fact]
        public void UpdateEndingWithoutRestart_SignInWindowStartsAtItsLastStep()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(40), OsUpdateSteps.Installed)).NewState;

            var steps = FireUntilResolved(engine, state, firstOrdinal: 100);

            var stopped = steps.Last();
            Assert.Equal(T0.AddMinutes(100), stopped.Transition.OccurredAtUtc);
            Assert.Equal(StoppedWaitingReasons.OobeUpdateNoSignIn, stopped.NewState.StoppedWaitingReason!.Value);
        }

        [Fact]
        public void BackfilledOlderStepAfterTheRestart_DoesNotReopenTheUpdate()
        {
            // The watchers backfill after the restart: a record written before it arrives late.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState;

            state = engine.Reduce(state, OsUpdate(70, T0.AddMinutes(45), OsUpdateSteps.InstallStarted)).NewState;

            Assert.Equal(OsUpdateSteps.RebootRequired, state.OsUpdateFacts.LastStep!.Value);
            var phase = DecisionEngine.EvaluateOobeUpdatePhase(state, T0.AddMinutes(83));
            Assert.Equal(DecisionEngine.OobeUpdatePhaseKind.AwaitingSignIn, phase.Kind);
            Assert.Equal(T0.AddMinutes(113), phase.BoundUtc);
        }

        [Fact]
        public void NewerUpdateStepAfterTheRestart_ReopensTheUpdate_BoundedByTheCap()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState;

            state = engine.Reduce(state, OsUpdate(70, T0.AddMinutes(60), OsUpdateSteps.DownloadStarted)).NewState;

            var phase = DecisionEngine.EvaluateOobeUpdatePhase(state, T0.AddMinutes(83));
            Assert.Equal(DecisionEngine.OobeUpdatePhaseKind.Updating, phase.Kind);
            Assert.Equal(T0.AddMinutes(22).AddHours(3), phase.BoundUtc); // the cap counts from the first activity
        }

        [Fact]
        public void UserSignIn_GetsOneFullWindow_ThenTheFireResolvesNormally()
        {
            // The transition to AwaitingHello does not cancel the window. Without the grace a fire
            // right after the sign-in would fail the session inside the Hello wizard.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51))).NewState; // due T0+81

            state = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.HelloWizardStarted, T0.AddMinutes(75))).NewState;
            Assert.False(DecisionEngine.IsInOobeUpdatePhase(state));

            // 6 minutes after the sign-in — inside the grace, which ends at T0+105.
            var grace = engine.Reduce(state, AdvisoryFired(70, T0.AddMinutes(81)));
            Assert.Null(grace.NewState.Outcome);
            Assert.Equal("DeadlineFired:advisory_completion:OobeUpdatePhaseUserArrived", grace.Transition.Trigger);
            Assert.Contains("signed in after the OOBE update", SingleTimelineEffect(grace, "completion_waiting").Parameters!["message"]);
            Assert.Equal(T0.AddMinutes(105), FindDeadline(grace.NewState, DeadlineNames.AdvisoryCompletion)!.DueAtUtc);

            // One full window after the sign-in, still no completion evidence — resolved the normal way.
            var resolved = engine.Reduce(grace.NewState, AdvisoryFired(80, T0.AddMinutes(105)));
            Assert.Equal(SessionStage.Failed, resolved.NewState.Stage);
            Assert.Equal("esp_exit_without_completion_evidence", SingleTimelineEffect(resolved, "enrollment_failed").Parameters!["reason"]);
            Assert.Null(resolved.NewState.StoppedWaitingReason);
            // Active decides itself — no shadow report.
            Assert.False(HasTimelineEffect(resolved, "oobe_update_phase_shadow"));
        }

        [Fact]
        public void AdvisoryVariant_IsExempt()
        {
            // Mutation proof: an unresolved ESP terminal failure does not un-happen because
            // Windows installs an update afterwards.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            Assert.True(DecisionEngine.IsInOobeUpdatePhase(state));

            var builder = state.ToBuilder();
            builder.EspAdvisoryFailureRecordedUtc = new SignalFact<DateTime>(T0.AddMinutes(21), 30);
            state = builder.Build();

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("esp_terminal_failure", SingleTimelineEffect(step, "enrollment_failed").Parameters!["reason"]);
        }

        [Fact]
        public void NoPhase_WithoutAnAnchor()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);

            Assert.Equal(DecisionEngine.OobeUpdatePhaseKind.None,
                DecisionEngine.EvaluateOobeUpdatePhase(state, T0.AddMinutes(51)).Kind);
        }

        // ================================================= serialization + census ====

        [Fact]
        public void SnapshotRoundTrip_PreservesTheUpdateFacts_AndTheStop()
        {
            // The update ends with a restart; the agent restores the engine state from its
            // snapshot, so the phase — and a decided stop — must survive it.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState;
            var stopped = FireUntilResolved(engine, state, firstOrdinal: 100).Last().NewState;

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(stopped));

            var facts = roundtripped.OsUpdateFacts;
            Assert.Equal(T0.AddMinutes(22), facts.PostExitActivityUtc!.Value);
            Assert.Equal(T0.AddMinutes(48), facts.RebootRequiredUtc!.Value);
            Assert.Equal(T0.AddMinutes(53), facts.RestartUtc!.Value);
            Assert.Equal(OsUpdateSteps.RebootRequired, facts.LastStep!.Value);
            Assert.Equal(StoppedWaitingReasons.OobeUpdateNoSignIn, roundtripped.StoppedWaitingReason!.Value);
            Assert.Equal(OobeUpdatePhaseModes.Active, roundtripped.ScenarioObservations.OobeUpdatePhaseMode!.Value);
        }

        [Fact]
        public void LegacySnapshot_WithoutTheNewFacts_DeserializesToEmpty()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;

            var legacy = JObject.Parse(StateSerializer.Serialize(state));
            foreach (var prop in legacy.Properties()
                .Where(p => p.Name.IndexOf("OsUpdateFacts", StringComparison.OrdinalIgnoreCase) >= 0
                         || p.Name.IndexOf("StoppedWaitingReason", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList())
            {
                prop.Remove();
            }

            var roundtripped = StateSerializer.Deserialize(legacy.ToString());
            Assert.Same(OsUpdateFacts.Empty, roundtripped.OsUpdateFacts);
            Assert.Null(roundtripped.StoppedWaitingReason);
            Assert.False(DecisionEngine.IsInOobeUpdatePhase(roundtripped));
        }

        [Fact]
        public void Census_SurfacesThePhase_AndTheStop()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine);
            Assert.DoesNotContain("oobe_update_phase", DecisionStateSignalCensus.Build(state).SignalsSeen);

            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.Installed)).NewState;
            var census = DecisionStateSignalCensus.Build(state);
            Assert.Contains("oobe_update_phase", census.SignalsSeen);
            Assert.Equal(T0.AddMinutes(22).ToString("o"), census.SignalTimestamps["oobeUpdatePhase"]);
            Assert.DoesNotContain("stopped_waiting", census.SignalsSeen);

            var stopped = FireUntilResolved(engine, state, firstOrdinal: 100).Last().NewState;
            Assert.Contains("stopped_waiting", DecisionStateSignalCensus.Build(stopped).SignalsSeen);
        }

        [Theory]
        [InlineData(OobeUpdatePhaseModes.Active, "Active")]
        [InlineData(OobeUpdatePhaseModes.Shadow, "Shadow")]
        [InlineData(null, "Shadow")] // never stamped
        public void Census_NamesTheModeThePhaseRanIn(string? mode, string expected)
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: mode);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;

            var evidence = (IDictionary<string, object>)DecisionStateSignalCensus.Build(state).SignalEvidence["oobeUpdatePhase"];
            Assert.Equal(expected, evidence["mode"]);
        }

        // ======================================================= measurement mode ====

        [Fact]
        public void ShadowMode_ReportsWhatThePhaseWouldDecide_AndTodaysRuleDecides()
        {
            // Report 3a2207978e4c in the Shadow mode: the fire mid-update fails the session as
            // before, and says that the phase would have kept waiting.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: OobeUpdatePhaseModes.Shadow);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(35), OsUpdateSteps.InstallStarted)).NewState;

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("DeadlineFired:advisory_completion", step.Transition.Trigger);
            Assert.Equal("esp_exit_without_completion_evidence", SingleTimelineEffect(step, "enrollment_failed").Parameters!["reason"]);

            var shadow = SingleTimelineEffect(step, "oobe_update_phase_shadow");
            var parameters = shadow.Parameters!;
            Assert.Equal("Debug", parameters["severity"]);
            Assert.Equal(OobeUpdatePhaseModes.Shadow, parameters["phaseMode"]);
            Assert.Equal("update_running", parameters["phaseState"]);
            Assert.Equal("hold", parameters["wouldDecide"]);
            Assert.Equal("esp_exit_without_completion_evidence", parameters["decidedReason"]);
            Assert.Equal(T0.AddMinutes(22).AddHours(3).ToString("o"), parameters["phaseBoundUtc"]);
            Assert.Equal(T0.AddMinutes(22).ToString("o"), parameters["oobeUpdatePhaseStartedUtc"]);
            Assert.Equal(OsUpdateSteps.InstallStarted, parameters["osUpdateLastStep"]);
            Assert.Equal(Lcu, parameters["osUpdate"]);
            Assert.Contains("still installing an update", parameters["message"]);
            // The report precedes the verdict on the timeline.
            Assert.True(step.Effects.ToList().IndexOf(shadow) < step.Effects.ToList().IndexOf(SingleTimelineEffect(step, "enrollment_failed")));
        }

        [Fact]
        public void ShadowMode_AfterTheUpdatesRestart_ReportsTheSignInWait()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: OobeUpdatePhaseModes.Shadow);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState; // re-bases the window to T0+83

            var step = engine.Reduce(state, AdvisoryFired(70, T0.AddMinutes(83)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            var parameters = SingleTimelineEffect(step, "oobe_update_phase_shadow").Parameters!;
            Assert.Equal("awaiting_sign_in", parameters["phaseState"]);
            Assert.Equal("hold", parameters["wouldDecide"]);
            Assert.Equal(T0.AddMinutes(53).ToString("o"), parameters["oobeUpdateEndedUtc"]);
            Assert.Equal(T0.AddMinutes(113).ToString("o"), parameters["phaseBoundUtc"]);
        }

        [Fact]
        public void ShadowMode_HangingUpdate_ReportsTheNotFinishedFailure()
        {
            // A fire after the cap (a shadow session lives this long only when another guard held
            // it, e.g. enforcement progress) reports the not-finished failure the phase would give.
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: OobeUpdatePhaseModes.Shadow);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.InstallStarted)).NewState;
            var afterCap = T0.AddMinutes(22).AddHours(3).AddMinutes(5);
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion)!;
            state = state.ToBuilder()
                .CancelDeadline(DeadlineNames.AdvisoryCompletion)
                .AddDeadline(new ActiveDeadline(armed.Name, afterCap, armed.FiresSignalKind, armed.FiresPayload))
                .Build();

            var step = engine.Reduce(state, AdvisoryFired(50, afterCap));

            Assert.Equal("esp_exit_without_completion_evidence", SingleTimelineEffect(step, "enrollment_failed").Parameters!["reason"]);
            var parameters = SingleTimelineEffect(step, "oobe_update_phase_shadow").Parameters!;
            Assert.Equal("update_not_finished", parameters["phaseState"]);
            Assert.Equal("fail_oobe_update_not_finished", parameters["wouldDecide"]);
        }

        [Fact]
        public void UnstampedSession_CountsAsShadow()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: null);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("update_running", SingleTimelineEffect(step, "oobe_update_phase_shadow").Parameters!["phaseState"]);
        }

        [Fact]
        public void OffMode_EvaluatesNothing()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: OobeUpdatePhaseModes.Off);
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.False(HasTimelineEffect(step, "oobe_update_phase_shadow"));
        }

        [Fact]
        public void ShadowMode_WithoutPhase_ReportsNothing()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine, mode: OobeUpdatePhaseModes.Shadow);

            var step = engine.Reduce(state, AdvisoryFired(50, T0.AddMinutes(51)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.False(HasTimelineEffect(step, "oobe_update_phase_shadow"));
        }

        [Fact]
        public void ModeStamp_ActiveCanOnlyBeTakenAway_NeverGranted()
        {
            var engine = new DecisionEngine();
            var initial = engine.Reduce(DecisionState.CreateInitial("s", "t", T0), MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            // A later agent start in Shadow or Off takes Active away (kill switch) ...
            var state = engine.Reduce(initial, PhaseMode(1, T0, "active")).NewState;
            Assert.Equal(OobeUpdatePhaseModes.Active, state.ScenarioObservations.OobeUpdatePhaseMode!.Value);
            state = engine.Reduce(state, PhaseMode(2, T0.AddMinutes(60), OobeUpdatePhaseModes.Active)).NewState;
            Assert.Equal(1, state.ScenarioObservations.OobeUpdatePhaseMode!.SourceSignalOrdinal);
            state = engine.Reduce(state, PhaseMode(3, T0.AddMinutes(90), OobeUpdatePhaseModes.Off)).NewState;
            Assert.Equal(OobeUpdatePhaseModes.Off, state.ScenarioObservations.OobeUpdatePhaseMode!.Value);
            Assert.Equal(3, state.ScenarioObservations.OobeUpdatePhaseMode.SourceSignalOrdinal);

            // ... and switching it on mid-session applies from the next session only.
            state = engine.Reduce(state, PhaseMode(4, T0.AddMinutes(120), OobeUpdatePhaseModes.Active)).NewState;
            Assert.Equal(OobeUpdatePhaseModes.Off, state.ScenarioObservations.OobeUpdatePhaseMode!.Value);

            var shadow = engine.Reduce(initial, PhaseMode(1, T0, "unknown-mode")).NewState;
            Assert.Equal(OobeUpdatePhaseModes.Shadow, shadow.ScenarioObservations.OobeUpdatePhaseMode!.Value);
            shadow = engine.Reduce(shadow, PhaseMode(2, T0.AddMinutes(60), OobeUpdatePhaseModes.Active)).NewState;
            Assert.Equal(OobeUpdatePhaseModes.Shadow, shadow.ScenarioObservations.OobeUpdatePhaseMode!.Value);
        }

        [Fact]
        public void ActiveTakenAwayAfterTheRestart_TheFireFallsBackToTodaysRule()
        {
            var engine = new DecisionEngine();
            var state = HandoffExitArmsTheWindow(engine); // Active
            state = engine.Reduce(state, OsUpdate(40, T0.AddMinutes(22), OsUpdateSteps.DownloadStarted)).NewState;
            state = engine.Reduce(state, OsUpdate(41, T0.AddMinutes(48), OsUpdateSteps.RebootRequired, "KB5099999")).NewState;
            state = engine.Reduce(state, Reboot(60, T0.AddMinutes(53))).NewState;
            // The agent comes back after the update's restart with the phase switched back to Shadow.
            state = engine.Reduce(state, PhaseMode(61, T0.AddMinutes(54), OobeUpdatePhaseModes.Shadow)).NewState;

            var step = engine.Reduce(state, AdvisoryFired(70, T0.AddMinutes(83)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal("awaiting_sign_in", SingleTimelineEffect(step, "oobe_update_phase_shadow").Parameters!["phaseState"]);
        }
    }
}
