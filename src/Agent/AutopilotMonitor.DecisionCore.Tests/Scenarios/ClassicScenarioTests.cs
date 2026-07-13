using System;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.1 — Classic UserDriven-v1 scenarios.
    /// Each test loads a committed, anonymized DecisionSignal JSONL fixture and asserts
    /// on the reducer's terminal state + key hypothesis / fact outcomes.
    /// </summary>
    public sealed class ClassicScenarioTests : ScenarioTestBase
    {
        [Fact]
        public void UserDrivenHappy_reaches_Completed_withEnrollmentComplete()
        {
            var result = RunFixture(
                fixtureFilename: "userdriven-happy-v1.jsonl",
                sessionId: "session-anon-0001",
                tenantId: "tenant-anon-0001");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.NotNull(result.FinalState.EspFinalExitUtc);
            Assert.Equal(EnrollmentPhase.AccountSetup, result.FinalState.CurrentEnrollmentPhase!.Value);
            Assert.Empty(result.FinalState.Deadlines);

            // Codex follow-up #5 — the legacy Hypothesis EnrollmentType was absorbed into
            // ScenarioProfile. Mode=Classic @ High confidence is the equivalent of the old
            // "Strong" level after ImeUserSessionCompleted.
            Assert.Equal(EnrollmentMode.Classic, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.Equal("ime_user_session_completed", result.FinalState.ScenarioProfile.Reason);

            // 7 fixture signals + 1 auto-fired FinalizingGrace deadline (plan §5 Fix 6 — the
            // reducer now parks in Finalizing after both prerequisites resolve; the harness
            // flushes the deadline so the test asserts on the true terminal state).
            Assert.Equal(8, result.Transitions.Count);
            Assert.All(result.Transitions, t => Assert.True(t.Taken));

            // Penultimate transition is DesktopArrived → Finalizing (both-gate resolved).
            var penultimate = result.Transitions[^2];
            Assert.Equal("DesktopArrived", penultimate.Trigger);
            Assert.Equal(SessionStage.Finalizing, penultimate.ToStage);

            // Last transition is the FinalizingGrace fire → Completed + enrollment_complete.
            var last = result.Transitions[^1];
            Assert.EndsWith(DeadlineNames.FinalizingGrace, last.Trigger);
            Assert.Equal(SessionStage.Completed, last.ToStage);
        }

        [Fact]
        public void HelloWizardUnskip_completesWithRealHelloOutcome_notMidWizardSkip()
        {
            // Session 772fe502 (2026-07-13): a flip-flopping user-scoped WHfB CSP read
            // "disabled" once, arm-C synthesized HelloOutcome="Skipped" + armed the 5s
            // finalizing_grace, and the wizard demonstrably started 230 ms later. The
            // HelloWizardStarted cure must retract the synthetic skip, return to AwaitingHello
            // and complete only on the real HelloResolved(outcome=completed).
            var result = RunFixture(
                fixtureFilename: "userdriven-hello-wizard-unskip-v1.jsonl",
                sessionId: "session-anon-772fe502",
                tenantId: "tenant-anon-772fe502");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            // The real tracker-posted outcome — NOT the synthetic "Skipped" the production
            // session recorded mid-wizard.
            Assert.Equal("completed", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloWizardStartedUtc);
            Assert.False(result.FinalState.HelloPolicyEnabled!.Value);
            Assert.Empty(result.FinalState.Deadlines);

            // The arm-C completion fired first (synthetic skip → Finalizing), then the cure.
            Assert.Contains(result.Transitions,
                t => t.Trigger == "ImeUserSessionCompleted:UserSessionEvidenceCompletion"
                     && t.ToStage == SessionStage.Finalizing);
            Assert.Contains(result.Transitions,
                t => t.Trigger == "HelloWizardStarted:UnSkip"
                     && t.ToStage == SessionStage.AwaitingHello);

            // Exactly one terminal enrollment_complete, driven by the real HelloResolved.
            var last = result.Transitions[^1];
            Assert.EndsWith(DeadlineNames.FinalizingGrace, last.Trigger);
            Assert.Equal(SessionStage.Completed, last.ToStage);
        }

        [Fact]
        public void UserSessionEvidence_completesProactively_withHelloOutcomeSkipped()
        {
            // Session a4537c36 shape: guard-blocked normal ESP exit (registry gate unsatisfiable),
            // desktop, then the IME user-session completion LAST — arm C completes at the IME
            // signal instead of idling to the 30-min AdvisoryCompletion backstop.
            var result = RunFixture(
                fixtureFilename: "userdriven-user-session-evidence-v1.jsonl",
                sessionId: "session-anon-a4537c36",
                tenantId: "tenant-anon-a4537c36");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Skipped", result.FinalState.HelloOutcome!.Value);
            Assert.Null(result.FinalState.AccountSetupProvisioningSucceededUtc);
            Assert.NotNull(result.FinalState.ImeUserSessionCompletedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.NotNull(result.FinalState.EspFinalExitUtc);
            Assert.Empty(result.FinalState.Deadlines);

            // The completing transition is the IME user-session signal (arm C), routed through
            // the non-terminal Finalizing stage; the harness then flushes FinalizingGrace.
            var penultimate = result.Transitions[^2];
            Assert.Equal("ImeUserSessionCompleted:UserSessionEvidenceCompletion", penultimate.Trigger);
            Assert.Equal(SessionStage.Finalizing, penultimate.ToStage);

            var last = result.Transitions[^1];
            Assert.EndsWith(DeadlineNames.FinalizingGrace, last.Trigger);
            Assert.Equal(SessionStage.Completed, last.ToStage);
        }

        [Fact]
        public void UserDrivenHelloTimeout_completes_withHelloOutcomeTimeout()
        {
            var result = RunFixture(
                fixtureFilename: "userdriven-hello-timeout-v1.jsonl",
                sessionId: "session-anon-0002",
                tenantId: "tenant-anon-0002");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Timeout", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.Empty(result.FinalState.Deadlines);

            // Plan §5 Fix 6: hello_safety fire (Desktop-already-arrived path) now routes the
            // synthetic-timeout through Finalizing instead of Completed. The harness then
            // auto-fires finalizing_grace so the terminal state is still Completed.
            var helloSafetyTransition = result.Transitions[^2];
            Assert.EndsWith("hello_safety", helloSafetyTransition.Trigger);
            Assert.Equal(SessionStage.Finalizing, helloSafetyTransition.ToStage);

            var finalizingFire = result.Transitions[^1];
            Assert.EndsWith(DeadlineNames.FinalizingGrace, finalizingFire.Trigger);
            Assert.Equal(SessionStage.Completed, finalizingFire.ToStage);
            Assert.True(finalizingFire.Taken);
        }

        [Fact]
        public void LateAadj_neverCompletesPrematurely_hypothesisAnnotated()
        {
            var result = RunFixture(
                fixtureFilename: "late-aadj-v1.jsonl",
                sessionId: "session-anon-0003",
                tenantId: "tenant-anon-0003");

            // Session still completes normally — via Hello + Desktop, NOT via late-AADJ.
            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);

            // AadUserJoinWithUserObserved fact is recorded from the Late-AADJ signal.
            Assert.NotNull(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved);
            Assert.True(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved!.Value);

            // Profile reason carries the last-strengthening reason token — after the IME
            // signal that is "ime_user_session_completed", not the prior late-AADJ annotation.
            Assert.Equal("ime_user_session_completed", result.FinalState.ScenarioProfile.Reason);

            // Profile chain:
            //   1. EspPhaseChanged(AccountSetup) -> Mode=Classic @ Medium / account_setup_observed
            //   2. AadUserJoinedLate -> reason annotation only, stage unchanged
            //   3. ImeUserSessionCompleted -> Mode=Classic @ High / ime_user_session_completed
            Assert.Equal(EnrollmentMode.Classic, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);

            // Critical regression guard: the AadUserJoinedLate transition did NOT take the
            // session to a terminal stage (stayed on EspAccountSetup). It is taken=true (the
            // fact was applied) but FromStage==ToStage.
            var aadTransition = Assert.Single(result.Transitions, t => t.Trigger == "AadUserJoinedLate");
            Assert.True(aadTransition.Taken);
            Assert.Equal(aadTransition.FromStage, aadTransition.ToStage);
            Assert.Equal(SessionStage.EspAccountSetup, aadTransition.ToStage);
        }

        [Fact]
        public void UserDrivenHappy_replayIsDeterministic_sameHashAcrossRuns()
        {
            var r1 = RunFixture("userdriven-happy-v1.jsonl", "s", "t");
            var r2 = RunFixture("userdriven-happy-v1.jsonl", "s", "t");
            Assert.Equal(r1.FinalStepHash, r2.FinalStepHash);
            Assert.Equal(16, r1.FinalStepHash.Length);
        }

        [Fact]
        public void LateProvisioningComplete_afterAccountSetup_doesNotCompletePrematurely()
        {
            // Plan §6.1 regression guard — reproduces session e259c121-dc13-46d6-8e96-118f1da9845e.
            // DeviceSetupProvisioningComplete arrives AFTER AccountSetup has started and
            // DesktopArrived was observed, but BEFORE HelloResolved. The SelfDeploying terminal
            // handler must NOT complete the session on the Classic path.
            var result = RunFixture(
                fixtureFilename: "userdriven-late-provisioning-complete-v1.jsonl",
                sessionId: "session-anon-0006",
                tenantId: "tenant-anon-0006");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal("Success", result.FinalState.HelloOutcome!.Value);
            Assert.NotNull(result.FinalState.HelloResolvedUtc);
            Assert.NotNull(result.FinalState.DesktopArrivedUtc);
            Assert.NotNull(result.FinalState.AccountSetupEnteredUtc);
            Assert.Empty(result.FinalState.Deadlines);

            // Completion must come from HelloResolved (AND-gate with prior DesktopArrived),
            // NOT from DeviceSetupProvisioningComplete. Per Fix 6, HelloResolved parks in
            // Finalizing; the auto-fired FinalizingGrace deadline transitions to Completed.
            var helloResolved = result.Transitions[^2];
            Assert.Equal("HelloResolved", helloResolved.Trigger);
            Assert.Equal(SessionStage.Finalizing, helloResolved.ToStage);
            Assert.True(helloResolved.Taken);

            var finalizingFire = result.Transitions[^1];
            Assert.EndsWith(DeadlineNames.FinalizingGrace, finalizingFire.Trigger);
            Assert.Equal(SessionStage.Completed, finalizingFire.ToStage);

            // The DeviceSetupProvisioningComplete signal is observed as a taken but stage-unchanged
            // step (informational on Classic path — not a terminal trigger). Plan v9: handler tags
            // its trigger with ":AccountSetupAlreadyEntered" to discriminate from the other paths.
            var provTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger.StartsWith(nameof(AutopilotMonitor.DecisionCore.Signals.DecisionSignalKind.DeviceSetupProvisioningComplete), System.StringComparison.Ordinal));
            Assert.True(provTransition.Taken);
            Assert.Equal(SessionStage.EspAccountSetup, provTransition.FromStage);
            Assert.Equal(SessionStage.EspAccountSetup, provTransition.ToStage);
            Assert.EndsWith(":AccountSetupAlreadyEntered", provTransition.Trigger);

            // DeviceOnlyDeployment hypothesis must stay Unknown — this is a Classic UserDriven path.
            Assert.Equal(HypothesisLevel.Unknown, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);

            // Plan v9: the DeviceSetupResolvedUtc anchor is set UNCONDITIONALLY (even on the
            // Classic-already-AccountSetup short-circuit) so the audit trail surfaces that the
            // signal was observed.
            Assert.NotNull(result.FinalState.DeviceSetupResolvedUtc);
        }
    }
}
