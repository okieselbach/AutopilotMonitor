using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Session 1924092e (2026-07-10) — false-positive guards for the esp-exit-variant
    /// <c>advisory_completion</c> window. Field failure: the IME logged an AccountSetup phase
    /// line pre-sign-in (defaultuser0/AutoLogon frame), seven seconds later the Device→Account
    /// handoff exit (Shell-Core 62407, errorCode=0) armed the 30-min resolution window, the
    /// deadline survived two reboots via state recovery, and fired mid-AccountSetup while apps
    /// were actively installing (5/39 done) → <c>enrollment_failed:
    /// esp_exit_without_completion_evidence</c> on a live enrollment. 65 sessions platform-wide
    /// hit this between 2026-06-15 and 2026-07-10. Coverage:
    /// <list type="bullet">
    ///   <item>Reboot cancel — <c>SystemRebootObserved</c> / <c>EspResumed</c> cancel the
    ///         esp-exit-variant window (the arming exit predates a reboot ⇒ pre-reboot page
    ///         close, not the 1ec8f4c6 dead-end) but never the advisory variant.</item>
    ///   <item>Enforcement-progress re-arm — a fire while apps kept reaching terminal states
    ///         or the ESP re-asserted a user phase re-arms instead of failing; convergent
    ///         (a second fire without NEW progress fails).</item>
    ///   <item>Full session-1924092e replay: handoff exit → reboot → desktop → active installs
    ///         → stray recovered-timer fire dead-ends → provisioning-complete → Completed.</item>
    ///   <item>Regression pins: the true 1ec8f4c6 dead-end still fails, the advisory variant
    ///         keeps its un-defang semantics, legacy deadlines without baselines resolve as
    ///         before, baselines survive snapshot round-trips.</item>
    /// </list>
    /// </summary>
    public sealed class AdvisoryCompletionFalsePositiveGuardTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

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

        private static DecisionSignal AppInstallCompleted(long ordinal, DateTime occurredAtUtc, string appId) =>
            MakeSignal(ordinal, DecisionSignalKind.AppInstallCompleted, occurredAtUtc,
                new Dictionary<string, string> { ["appId"] = appId, ["newState"] = "Installed" });

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        /// <summary>
        /// Drives a Classic user-driven session into EspAccountSetup: full ESP, Hello policy
        /// disabled, DeviceSetup → AccountSetup phase lines. In the 1924092e replay the
        /// AccountSetup line is the premature pre-sign-in IME false positive — which is
        /// invisible to the reducer (it looks identical to a genuine entry) and is exactly
        /// why the guards act on later evidence instead of the arming condition.
        /// </summary>
        private static DecisionState SetupClassicAccountSetupSession(
            DecisionEngine engine,
            bool allowContinueAnyway = false)
        {
            var state = DecisionState.CreateInitial("sess-1924092e", "tenant-1924092e", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = allowContinueAnyway ? "true" : "false",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(5),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            return state;
        }

        /// <summary>Guard-blocked post-AccountSetup exit — arms the esp-exit-variant window.</summary>
        private static DecisionState ArmEspExitWindow(DecisionEngine engine, DecisionState state, long ordinal = 50)
        {
            var armed = engine.Reduce(state, MakeSignal(ordinal, DecisionSignalKind.EspExiting, T0.AddMinutes(5.1))).NewState;
            Assert.NotNull(FindDeadline(armed, DeadlineNames.AdvisoryCompletion));
            Assert.Null(armed.Outcome);
            return armed;
        }

        // ============================================================ reboot cancel ====

        [Fact]
        public void SystemReboot_CancelsEspExitVariantWindow_AndEmitsCancelEffect()
        {
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state);

            var step = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(19),
                new Dictionary<string, string> { ["previousExitType"] = "reboot_kill" }));

            Assert.Null(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
            var cancel = step.Effects.Single(e => e.Kind == DecisionEffectKind.CancelDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, cancel.CancelDeadlineName);
            // The reboot's own observability is untouched.
            SingleTimelineEffect(step, "system_reboot_detected");
            Assert.Null(step.NewState.Outcome);
        }

        [Fact]
        public void EspResumed_CancelsEspExitVariantWindow()
        {
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state);

            var step = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.EspResumed, T0.AddMinutes(19)));

            Assert.Null(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
            var cancel = step.Effects.Single(e => e.Kind == DecisionEffectKind.CancelDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, cancel.CancelDeadlineName);
        }

        [Fact]
        public void SystemReboot_LeavesAdvisoryVariantWindowArmed()
        {
            // The advisory variant is anchored to a REAL ESP terminal failure — a reboot does
            // not un-happen it, and the window must still un-defang the failure on expiry.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine, allowContinueAnyway: true);
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                })).NewState;
            Assert.NotNull(state.EspAdvisoryFailureRecordedUtc);
            var armed = FindDeadline(state, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(armed);

            var step = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(10)));

            var after = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(after);
            Assert.Equal(armed!.DueAtUtc, after!.DueAtUtc);
            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);
        }

        [Fact]
        public void SystemReboot_WithoutArmedWindow_EmitsNoCancelEffect()
        {
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);

            var step = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(10)));

            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);
            SingleTimelineEffect(step, "system_reboot_detected");
        }

        // ================================================ enforcement-progress re-arm ====

        [Fact]
        public void DeadlineFired_EspExitVariant_AppProgressSinceArming_RearmsInsteadOfFailing()
        {
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);
            // Enforcement demonstrably continued after the arming exit.
            state = engine.Reduce(state, AppInstallCompleted(60, T0.AddMinutes(20), "app-winrar")).NewState;

            var fireAt = T0.AddMinutes(35.1);
            var step = engine.Reduce(state, DeadlineFired(70, fireAt, DeadlineNames.AdvisoryCompletion));

            // Not failed — the window is re-based 30 min from the fire.
            Assert.True(step.Transition.Taken);
            Assert.Equal(state.Stage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            var rearmed = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(rearmed);
            Assert.Equal(fireAt.AddMinutes(30), rearmed!.DueAtUtc);
            var schedule = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.AdvisoryCompletion, schedule.Deadline!.Name);

            // The re-arm is visible on the timeline with the new due-time.
            var waiting = SingleTimelineEffect(step, "completion_waiting");
            Assert.Contains("EnforcementActive", waiting.Parameters!["trigger"]);
            Assert.Equal(rearmed.DueAtUtc.ToString("o"), waiting.Parameters!["resolutionDeadlineDueAtUtc"]);

            // Convergence: a second fire without NEW progress since the re-arm fails.
            var second = engine.Reduce(step.NewState, DeadlineFired(80, fireAt.AddMinutes(30), DeadlineNames.AdvisoryCompletion));
            Assert.Equal(SessionStage.Failed, second.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, second.NewState.Outcome);
            var failed = SingleTimelineEffect(second, "enrollment_failed");
            Assert.Equal("esp_exit_without_completion_evidence", failed.Parameters!["reason"]);
        }

        [Fact]
        public void DeadlineFired_EspExitVariant_PhaseReassertedSinceArming_Rearms()
        {
            // No app terminals, but the ESP/IME re-asserted AccountSetup after the arming exit
            // (session 1924092e: esp_phase_changed AccountSetup at 12:26:37, exit at 12:05:08).
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(24),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(35.1), DeadlineNames.AdvisoryCompletion));

            Assert.Null(step.NewState.Outcome);
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
        }

        [Fact]
        public void DeadlineFired_EspExitVariant_DesktopOnly_NoEnforcementProgress_StillFails()
        {
            // Desktop arrival alone is NOT enforcement progress — the true 1ec8f4c6 dead-end
            // ends exactly there (page closed, user at the desktop, nothing installs). Pins
            // that the re-arm guard does not swallow the genuine dead-end verdict.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);
            state = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.DesktopArrived, T0.AddMinutes(6))).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(35.1), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_exit_without_completion_evidence", failed.Parameters!["reason"]);
        }

        [Fact]
        public void DeadlineFired_AdvisoryVariant_AppProgress_DoesNotRearm_StillUnDefangs()
        {
            // Variant split: the advisory window resolves a REAL ESP failure. App progress
            // after the failure must not postpone the un-defang.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine, allowContinueAnyway: true);
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(6),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                })).NewState;
            state = engine.Reduce(state, AppInstallCompleted(60, T0.AddMinutes(20), "app-other")).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(36), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_terminal_failure", failed.Parameters!["reason"]);
        }

        [Fact]
        public void DeadlineFired_LegacyDeadlineWithoutBaselines_FailsAsBefore()
        {
            // Rollout-safety: a window armed by a pre-fix agent (recovered snapshot) carries no
            // baseline keys. The progress guard must report no progress — the fire resolves
            // with the pre-fix semantics instead of throwing or re-arming forever.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);

            var builder = state.ToBuilder();
            builder.CancelDeadline(DeadlineNames.AdvisoryCompletion);
            builder.AddDeadline(new ActiveDeadline(
                name: DeadlineNames.AdvisoryCompletion,
                dueAtUtc: T0.AddMinutes(35.1),
                firesSignalKind: DecisionSignalKind.DeadlineFired,
                firesPayload: new Dictionary<string, string>
                {
                    [SignalPayloadKeys.Deadline] = DeadlineNames.AdvisoryCompletion,
                }));
            state = builder.Build();
            // App progress that WOULD re-arm if the baselines existed.
            state = engine.Reduce(state, AppInstallCompleted(60, T0.AddMinutes(20), "app-x")).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(35.1), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }

        // ==================================================== session-1924092e replay ====

        [Fact]
        public void Session1924092e_HandoffExitThenReboot_ActiveInstalls_CompletesInsteadOfFailing()
        {
            // The field session end-to-end: premature IME AccountSetup line → handoff exit
            // arms the window → reboot cancels it → real user desktop → apps actively
            // installing → a stray recovered-timer fire dead-ends → AccountSetup provisioning
            // completes → the session finishes Completed. Pre-fix, the recovered deadline
            // fired at 12:35:08 and failed the session mid-install.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);

            // AutoLogon restart chain (12:20 / 12:23 in the field session).
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(19),
                new Dictionary<string, string> { ["previousExitType"] = "reboot_kill" })).NewState;
            Assert.Null(FindDeadline(state, DeadlineNames.AdvisoryCompletion));

            // Real user signs in, Account-Setup ESP resumes visibly.
            state = engine.Reduce(state, MakeSignal(70, DecisionSignalKind.DesktopArrived, T0.AddMinutes(23))).NewState;
            state = engine.Reduce(state, MakeSignal(
                80, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(24),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, AppInstallCompleted(90, T0.AddMinutes(27), "app-winrar")).NewState;
            state = engine.Reduce(state, AppInstallCompleted(95, T0.AddMinutes(28), "app-lansweeper")).NewState;
            Assert.Null(state.Outcome);

            // The pre-reboot wall-clock timer can still fire after recovery (state says
            // cancelled, but the scheduler re-armed it from the recovered snapshot before the
            // reboot signal was processed). The stale guard must dead-end it — NOT fail.
            var strayFire = engine.Reduce(state, DeadlineFired(100, T0.AddMinutes(33), DeadlineNames.AdvisoryCompletion));
            Assert.False(strayFire.Transition.Taken);
            Assert.Equal("advisory_completion_stale_deadline_not_armed", strayFire.Transition.DeadEndReason);
            Assert.Null(strayFire.NewState.Outcome);

            // Account-Setup ESP finishes for real → normal completion path.
            state = engine.Reduce(strayFire.NewState, MakeSignal(
                110, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(40))).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);
            var final = engine.Reduce(state, DeadlineFired(120, T0.AddMinutes(40).AddSeconds(5), DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, final.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, final.NewState.Outcome);
            SingleTimelineEffect(final, "enrollment_complete");
        }

        [Fact]
        public void RebootCancel_LaterGenuineGuardBlockedExit_RearmsFreshWindow()
        {
            // After the reboot cancel, the arming site's fire-once guard must pass again: a
            // later genuine guard-blocked post-AccountSetup exit re-arms a fresh window so
            // the 1ec8f4c6 dead-end closure is not lost for the rest of the session.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);
            state = engine.Reduce(state, MakeSignal(60, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(19))).NewState;
            Assert.Null(FindDeadline(state, DeadlineNames.AdvisoryCompletion));

            var exitAt = T0.AddMinutes(30);
            var step = engine.Reduce(state, MakeSignal(70, DecisionSignalKind.EspExiting, exitAt));

            var rearmed = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(rearmed);
            Assert.Equal(exitAt.AddMinutes(30), rearmed!.DueAtUtc);
        }

        // ================================================== serialization round-trip ====

        [Fact]
        public void StateSerializer_Roundtrip_PreservesArmingBaselinesOnDeadlinePayload()
        {
            // The progress baselines live on the deadline's FiresPayload and MUST survive the
            // snapshot round-trip — recovery after a crash mid-window otherwise loses the
            // ability to tell an idle dead-end from a progressing enrollment.
            var engine = new DecisionEngine();
            var state = SetupClassicAccountSetupSession(engine);
            state = ArmEspExitWindow(engine, state, ordinal: 50);

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(state));

            var deadline = FindDeadline(roundtripped, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(deadline);
            Assert.NotNull(deadline!.FiresPayload);
            Assert.Equal("50", deadline.FiresPayload!["armSignalOrdinal"]);
            Assert.Equal("0", deadline.FiresPayload!["armAppTerminalCount"]);
        }
    }
}
