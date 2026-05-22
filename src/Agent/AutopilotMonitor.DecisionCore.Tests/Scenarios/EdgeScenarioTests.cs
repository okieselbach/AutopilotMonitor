using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.DecisionCore.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.5 — Edge scenarios: hybrid reboot, ESP terminal failure, restart-recovery
    /// determinism.
    /// </summary>
    public sealed class EdgeScenarioTests : ScenarioTestBase
    {
        private static ClassifierAwareReplayHarness NewHarness() =>
            new ClassifierAwareReplayHarness(
                new DecisionEngine(),
                new Dictionary<string, IClassifier>
                {
                    [WhiteGloveSealingClassifier.ClassifierId] = new WhiteGloveSealingClassifier(),
                });

        [Fact]
        public void HybridReboot_midEsp_preservesStage_recordsRebootFact_completesNormally()
        {
            var signals = LoadFixture("hybrid-reboot-v1.jsonl");
            var result = NewHarness().Replay("session-anon-0030", "tenant-anon-0030", signals);

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);

            // Reboot fact is recorded.
            Assert.NotNull(result.FinalState.SystemRebootUtc);

            // Stage flow: EspDeviceSetup -> (Reboot) preserved -> EspResumed preserved ->
            // EspAccountSetup on next EspPhaseChanged -> AwaitingHello -> Completed.
            // Importantly, no WhiteGloveSealed — reboot alone doesn't imply WG.
            Assert.NotEqual(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Null(result.FinalState.ScenarioObservations.ShellCoreWhiteGloveSuccessSeen);
        }

        [Fact]
        public void EspTerminalFailure_transitionsToFailed_withReasonFromPayload()
        {
            var signals = LoadFixture("esp-terminal-failure-v1.jsonl");
            var result = NewHarness().Replay("session-anon-0031", "tenant-anon-0031", signals);

            Assert.Equal(SessionStage.Failed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, result.FinalState.Outcome);
            Assert.Empty(result.FinalState.Deadlines);

            // The last transition carries the payload-derived reason via trigger.
            var last = result.Transitions[^1];
            Assert.Equal("EspTerminalFailure", last.Trigger);
            Assert.True(last.Taken);

            // c117946b debrief (2026-05-12) — EnrollmentTerminationHandler reads
            // LastFailureTrigger to discriminate which terminal-failure path fired and
            // gate the "promote installing apps to likely stuck" pre-hook. The fact
            // must therefore be stamped by the reducer, not by the orchestrator.
            Assert.NotNull(result.FinalState.LastFailureTrigger);
            Assert.Equal("EspTerminalFailure", result.FinalState.LastFailureTrigger!.Value);
        }

        [Fact]
        public void EspFailureAdvisoryContinueAnyway_replays_RealSession4fa5a2d4Pattern_endsInEspAccountSetup_notFailed()
        {
            // PR1 (Session 4fa5a2d4, 2026-05-22) — ContinueAnyway-aware ESP terminal-failure
            // defang. The fixture replays the canonical race that produced 199/202 false
            // positives in tenant c9787ba2: ContinueAnyway=true profile, AccountSetup entered,
            // then DeviceSetup/Certificates registry-write flips to failed. Without PR1 this
            // would land in Failed; PR1 records EspAdvisoryFailureRecordedUtc and keeps the
            // stage at EspAccountSetup so the agent stays in monitoring.
            var signals = LoadFixture("esp-failure-advisory-continueanyway-v1.jsonl");
            var result = NewHarness().Replay("session-anon-advisory", "tenant-anon-advisory", signals);

            // Critical: stage is NOT Failed — the advisory path preserved EspAccountSetup.
            Assert.NotEqual(SessionStage.Failed, result.FinalState.Stage);
            Assert.Equal(SessionStage.EspAccountSetup, result.FinalState.Stage);
            Assert.Null(result.FinalState.Outcome);

            // The fire-once gate is set so the duplicate EspTerminalFailure from
            // ShellCoreTracker (signal #5 in the fixture) dead-ends without effect.
            Assert.NotNull(result.FinalState.EspAdvisoryFailureRecordedUtc);

            // The LastFailureTrigger fact is NOT set on the advisory path — that fact is the
            // EnrollmentTerminationHandler's gate for promote-installing-apps-to-stuck and only
            // fires when the session genuinely terminates.
            Assert.Null(result.FinalState.LastFailureTrigger);

            // Two EspTerminalFailure signals in the fixture, two transitions: first one
            // is the advisory (Taken=true, stage preserved); second one is the duplicate
            // dead-end (Taken=false, DeadEndReason set).
            var failureTransitions = new List<DecisionTransition>();
            foreach (var t in result.Transitions)
            {
                if (t.Trigger == "EspTerminalFailure") failureTransitions.Add(t);
            }
            Assert.Equal(2, failureTransitions.Count);
            Assert.True(failureTransitions[0].Taken);
            Assert.False(failureTransitions[1].Taken);
            Assert.Equal("esp_terminal_failure_advisory_already_recorded", failureTransitions[1].DeadEndReason);
        }

        [Fact]
        public void RestartRecovery_replayTwice_producesIdenticalFinalStepHash()
        {
            // Determinism: the same signal stream through a fresh engine + fresh state
            // yields the same FinalStepHash. Simulates the Orchestrator replaying a
            // persisted SignalLog after an agent crash. Plan L.2 Event-Sourcing.
            var signals = LoadFixture("selfdeploying-happy-v1.jsonl");

            var r1 = NewHarness().Replay("session-anon-replay", "tenant-anon-replay", signals);
            var r2 = NewHarness().Replay("session-anon-replay", "tenant-anon-replay", signals);

            Assert.Equal(r1.FinalStepHash, r2.FinalStepHash);
            Assert.Equal(r1.FinalState.Stage, r2.FinalState.Stage);
            Assert.Equal(r1.FinalState.Outcome, r2.FinalState.Outcome);
            Assert.Equal(r1.Transitions.Count, r2.Transitions.Count);
        }

        [Fact]
        public void RestartRecovery_prefixReplay_yieldsIntermediateState()
        {
            // Replay a truncated log and verify the state matches what full-replay had at
            // the same point (simulates partial recovery after crash between signals).
            var all = LoadFixture("userdriven-happy-v1.jsonl");
            var prefix = new List<AutopilotMonitor.DecisionCore.Signals.DecisionSignal>();
            for (int i = 0; i < 3 && i < all.Count; i++) prefix.Add(all[i]);

            var prefixResult = NewHarness().Replay("s", "t", prefix);

            Assert.Equal(SessionStage.EspAccountSetup, prefixResult.FinalState.Stage);
            Assert.Null(prefixResult.FinalState.Outcome);
            Assert.NotNull(prefixResult.FinalState.CurrentEnrollmentPhase);
            Assert.Equal(EnrollmentPhase.AccountSetup, prefixResult.FinalState.CurrentEnrollmentPhase!.Value);
        }

        [Fact]
        public void AllCommittedFixtures_produceTerminalState()
        {
            // Global sanity: every committed anonymized fixture ends in a terminal stage.
            // This catches regressions where a new reducer change breaks a scenario end.
            string[] fixtures =
            {
                "userdriven-happy-v1.jsonl",
                "userdriven-hello-timeout-v1.jsonl",
                "late-aadj-v1.jsonl",
                "selfdeploying-happy-v1.jsonl",
                "selfdeploying-esp-exit-unknown-v1.jsonl",
                "whiteglove-inline-v1.jsonl",
                "whiteglove-signal-correlated-v1.jsonl",
                "whiteglove-false-positive-v1.jsonl",
                "hybrid-reboot-v1.jsonl",
                "esp-terminal-failure-v1.jsonl",
            };

            foreach (var fx in fixtures)
            {
                var signals = LoadFixture(fx);
                var result = NewHarness().Replay($"session:{fx}", $"tenant:{fx}", signals);

                var isTerminal = result.FinalState.Stage == SessionStage.Completed
                                 || result.FinalState.Stage == SessionStage.Failed
                                 || result.FinalState.Stage == SessionStage.WhiteGloveSealed;

                Assert.True(
                    isTerminal,
                    $"Fixture {fx} ended in non-terminal stage {result.FinalState.Stage}.");
            }
        }
    }
}
