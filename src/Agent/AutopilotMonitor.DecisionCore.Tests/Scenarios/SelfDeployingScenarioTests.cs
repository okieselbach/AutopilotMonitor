using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.Scenarios
{
    /// <summary>
    /// Plan §4 M3.2 — SelfDeploying-v1 and Device-Only scenarios.
    /// </summary>
    public sealed class SelfDeployingScenarioTests : ScenarioTestBase
    {
        [Fact]
        public void SelfDeployingHappy_completes_onProvisioningComplete_noUserFacts()
        {
            var result = RunFixture(
                fixtureFilename: "selfdeploying-happy-v1.jsonl",
                sessionId: "session-anon-0004",
                tenantId: "tenant-anon-0004");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Empty(result.FinalState.Deadlines);

            // No user-presence facts should have been produced.
            Assert.Null(result.FinalState.HelloResolvedUtc);
            Assert.Null(result.FinalState.DesktopArrivedUtc);
            Assert.Null(result.FinalState.ScenarioObservations.AadUserJoinWithUserObserved);
            Assert.Null(result.FinalState.AccountSetupEnteredUtc);

            // DeviceOnly hypothesis confirmed as DeviceOnly (no user presence seen).
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // Plan v9 — terminal classification now happens at DeadlineFired, not at signal-time.
            // Reason changed from "selfdeploying_provisioning_complete" to "selfdeploying_deadline_confirmed".
            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", result.FinalState.ScenarioProfile.Reason);

            // Fixture now drives 4 signals (SessionStarted, EspPhaseChanged(DeviceSetup),
            // DeviceSetupProvisioningComplete, DeadlineFired) instead of 3.
            Assert.Equal(4, result.Transitions.Count);
            Assert.All(result.Transitions, t => Assert.True(t.Taken));
        }

        [Fact]
        public void SelfDeployingHappy_armsAndClearsDeviceOnlyEspDetectionViaDeadline()
        {
            // Plan v9 (88a53223 defang) — semantics:
            // - DeviceSetupProvisioningComplete arms the deadline (no longer terminal at signal-time).
            // - DeadlineFired completes the session (after all guards pass) and clears deadlines.
            // Final state still has no active deadlines, just via a different mechanism.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-happy-v1.jsonl",
                sessionId: "session-anon-0004",
                tenantId: "tenant-anon-0004");

            Assert.Empty(result.FinalState.Deadlines);
            // Anchor must be set so downstream stale-fire guards can distinguish "new path" from
            // "rollout-race deadline from old code".
            Assert.NotNull(result.FinalState.DeviceSetupResolvedUtc);
        }

        [Fact]
        public void DeviceOnlyEspExitUnknown_provisioningCompletesArmsDeadline_thenFiresToTerminal()
        {
            // Plan v9 semantics: DeviceSetupProvisioningComplete arms the deadline; DeadlineFired
            // is the sole SelfDeploying-terminal entry. The same fixture (now without the legacy
            // arm-at-DeviceSetup-start DeadlineFired) drives the same final state via the new path.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-esp-exit-unknown-v1.jsonl",
                sessionId: "session-anon-0005",
                tenantId: "tenant-anon-0005");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);

            // DeviceOnlyDeployment is now set to Confirmed exclusively in the deadline-fired terminal
            // branch (previously the v1 path set it twice — first at signal-time, then again at
            // deadline-fire). Final state is identical: Confirmed/DeviceOnly.
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // The DeadlineFired transition is the terminal one (Stage transitions to Completed
            // here, NOT a "hypothesis-only" no-op like in the v1 semantics).
            var deadlineTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger == $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");
            Assert.True(deadlineTransition.Taken);
            Assert.Equal(SessionStage.Completed, deadlineTransition.ToStage);

            // Fixture: SessionStarted + EspPhaseChanged + DeviceSetupProvisioningComplete + DeadlineFired = 4.
            Assert.Equal(4, result.Transitions.Count);
        }

        [Fact]
        public void Classic_AccountSetupPhase_CancelsDeviceOnlyEspDetection()
        {
            // Re-use the UserDriven-Happy fixture (already in M3.1): it goes
            // DeviceSetup -> AccountSetup, so the DeviceOnlyEspDetection deadline is
            // scheduled and then cancelled before it can fire. The final state must
            // carry no DeviceOnlyEspDetection deadline, and the DeviceOnly hypothesis
            // stays Unknown — the UserDriven path does not classify itself as DeviceOnly.
            var result = RunFixture(
                fixtureFilename: "userdriven-happy-v1.jsonl",
                sessionId: "s", tenantId: "t");

            Assert.DoesNotContain(
                result.FinalState.Deadlines,
                d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.Equal(HypothesisLevel.Unknown, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
        }

        // ============================================================ kiosk waiver (320b3bf7)
        // Session 320b3bf7: self-deploying kiosk devices never completed because the IME logs a
        // false-positive AccountSetup phase line for the kioskUser0 autologon session. The
        // OobeConfig 0x20|0x40 seed (EnrollmentFactsObserved isSelfDeployingProfile) plus the
        // kiosk waiver keep the DeviceOnlyEspDetection terminal path alive.

        [Fact]
        public void KioskImeAccountSetup_falsePositiveBeforeArm_completesViaDeadline()
        {
            // 320b3bf7 replay shape: AccountSetup false positive lands BEFORE
            // DeviceSetupProvisioningComplete — pre-fix the arm was short-circuited
            // (":AccountSetupAlreadyEntered") and the session idled to the 5h timeout.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-kiosk-ime-accountsetup-v1.jsonl",
                sessionId: "session-anon-kiosk-01",
                tenantId: "tenant-anon-kiosk");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Empty(result.FinalState.Deadlines);

            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.Equal("selfdeploying_deadline_confirmed", result.FinalState.ScenarioProfile.Reason);

            // The false-positive AccountSetup entry is retained as an audit fact — the census
            // documents it in the enrollment_complete event rather than hiding it.
            Assert.NotNull(result.FinalState.AccountSetupEnteredUtc);
            Assert.Null(result.FinalState.AccountSetupProvisioningSucceededUtc);

            // Desktop (kioskUser0 autologon) was seen → user_present flavor of DeviceOnly.
            Assert.Equal(HypothesisLevel.Confirmed, result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Level);
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.UserPresent,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            var deadlineTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger == $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");
            Assert.True(deadlineTransition.Taken);
            Assert.Equal(SessionStage.Completed, deadlineTransition.ToStage);
        }

        [Fact]
        public void KioskImeAccountSetup_falsePositiveInsideWindow_deadlineSurvivesAndCompletes()
        {
            // Reverse ordering: deadline armed first, false positive lands inside the 5-min
            // window — pre-fix HandleEspPhaseChangedV1 cancelled the armed deadline here.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-kiosk-accountsetup-in-window-v1.jsonl",
                sessionId: "session-anon-kiosk-02",
                tenantId: "tenant-anon-kiosk");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            // No desktop/user facts in this variant → device_only flavor.
            Assert.Equal(
                DecisionEngine.DeviceOnlyReasons.DeviceOnly,
                result.FinalState.ClassifierOutcomes.DeviceOnlyDeployment.Reason);

            // The AccountSetup transition must NOT have produced a CancelDeadline for
            // DeviceOnlyEspDetection — proven by the deadline actually firing terminal.
            var deadlineTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger == $"DeadlineFired:{DeadlineNames.DeviceOnlyEspDetection}");
            Assert.True(deadlineTransition.Taken);
        }

        [Fact]
        public void KioskRebootReplay_staleFireAndDuplicates_completeExactlyOnce()
        {
            var result = RunFixture(
                fixtureFilename: "selfdeploying-kiosk-reboot-replay-v1.jsonl",
                sessionId: "session-anon-kiosk-03",
                tenantId: "tenant-anon-kiosk");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);

            // Exactly ONE taken terminal DeadlineFired transition; the stale fire (DueAtUtc
            // mismatch) and the post-terminal duplicate are dead-ends.
            var terminalFires = result.Transitions
                .Where(t => t.Taken && t.ToStage == SessionStage.Completed)
                .ToList();
            Assert.Single(terminalFires);
            Assert.Contains(result.Transitions, t =>
                t.DeadEndReason == "device_only_esp_detection_stale_due_at_mismatch");
            Assert.Contains(result.Transitions, t =>
                t.DeadEndReason != null && t.DeadEndReason.StartsWith("signal_after_terminal"));
        }

        [Fact]
        public void KioskBitsWithWhiteGloveConfirm_whiteGloveWins_noSelfDeployingTerminal()
        {
            // WG sealing confirm terminates as WhiteGloveSealed and clears deadlines; the late
            // DeviceOnly fire must dead-end. Pins the defensive order for the two anomalous
            // preProv sessions the sweep found carrying the SD bits.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-bits-whiteglove-reclassified-v1.jsonl",
                sessionId: "session-anon-kiosk-04",
                tenantId: "tenant-anon-kiosk");

            Assert.Equal(SessionStage.WhiteGloveSealed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, result.FinalState.Outcome);
            Assert.Equal(EnrollmentMode.WhiteGlove, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            // The post-seal DeadlineFired is a dead-end, never a SelfDeploying terminal: the
            // WG-Confirmed verdict cleared all deadlines, so stale-fire guard B catches the
            // late fire (the after-terminal dispatch only intercepts Completed/Failed —
            // WhiteGloveSealed routes into the handler, whose guards must hold).
            Assert.Contains(result.Transitions, t =>
                t.DeadEndReason == "device_only_esp_detection_stale_deadline_not_armed");
        }

        [Fact]
        public void KioskBitsWithRealUserEspProgress_vetoReenabled_noDeadlineArmed()
        {
            // A genuine AccountSetupProvisioningComplete switches the kiosk waiver OFF: the
            // DSPC handler takes the Classic short-circuit, no DeviceOnly deadline is armed,
            // the session stays on the Classic completion path.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-kiosk-user-esp-progress-v1.jsonl",
                sessionId: "session-anon-kiosk-05",
                tenantId: "tenant-anon-kiosk");

            Assert.NotEqual(SessionStage.Completed, result.FinalState.Stage);
            Assert.DoesNotContain(
                result.FinalState.Deadlines,
                d => d.Name == DeadlineNames.DeviceOnlyEspDetection);
            Assert.NotNull(result.FinalState.AccountSetupProvisioningSucceededUtc);
            Assert.Contains(result.Transitions, t =>
                t.Trigger != null && t.Trigger.EndsWith(":AccountSetupAlreadyEntered"));
        }

        [Fact]
        public void KioskHybridRealmJoinDeferred_releaseHonoursWaiver_completes()
        {
            // Deferred-release path (CompleteIfDeferredOrBookkeep): pre-fix the AccountSetup
            // false positive cleared the deferred flag on RJ-resolve and the session parked
            // forever. The waiver keeps the deferred SelfDeploying terminal.
            var result = RunFixture(
                fixtureFilename: "selfdeploying-kiosk-hybrid-rj-deferred-v1.jsonl",
                sessionId: "session-anon-kiosk-06",
                tenantId: "tenant-anon-kiosk");

            Assert.Equal(SessionStage.Completed, result.FinalState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, result.FinalState.Outcome);
            Assert.Equal(EnrollmentMode.SelfDeploying, result.FinalState.ScenarioProfile.Mode);
            Assert.Equal(ProfileConfidence.High, result.FinalState.ScenarioProfile.Confidence);
            Assert.NotNull(result.FinalState.RealmJoinFacts.ResolvedUtc);

            var releaseTransition = Assert.Single(
                result.Transitions,
                t => t.Trigger != null && t.Trigger.EndsWith(":SelfDeployingDeferred"));
            Assert.True(releaseTransition.Taken);
            Assert.Equal(SessionStage.Completed, releaseTransition.ToStage);
        }
    }
}
