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
    /// Continue-Anyway observation mode (tenant c9787ba2, session 53d1e9f6, 2026-08-08).
    /// A Device-phase ESP terminal failure (AccountSetup never entered) on a
    /// Continue-Anyway-enabled profile is a hard fail by default; with the tenant opt-in
    /// (<c>EspContinueAnywayObservationEnabled</c> observation) it is defanged into a 60-min
    /// observation advisory instead. Coverage:
    /// <list type="bullet">
    ///   <item>Defang + 60-min AdvisoryCompletion arming (vs the classic 30-min window).</item>
    ///   <item>Opt-out / ContinueAnyway-off / self-deploying-scenario regressions still hard-fail.</item>
    ///   <item>Real-user desktop + Hello-disabled completes eagerly through Finalizing; the
    ///         terminal enrollment_complete carries the espSoftFailure marker.</item>
    ///   <item>Window expiry without desktop un-defangs to esp_terminal_failure.</item>
    ///   <item>Deadline fire with desktop but unknown Hello promotes to AwaitingHello.</item>
    ///   <item>Category recovery ("Try again") resolves the advisory (classic 4910a5a5 hook).</item>
    /// </list>
    /// </summary>
    public sealed class ContinueAnywayObservationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 7, 15, 16, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test")
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc, string deadlineName) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = deadlineName });

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        /// <summary>
        /// Replays the session-53d1e9f6 shape up to (but excluding) the ESP terminal failure:
        /// Continue-Anyway profile with the tenant observation opt-in stamped into the
        /// EspConfigDetected payload, Hello policy disabled, DeviceSetup entered —
        /// AccountSetup never reached (the 30-min Device-ESP wall kills it first).
        /// </summary>
        private static DecisionState SetupObservationEligibleSession(
            DecisionEngine engine,
            bool observationOptIn = true,
            bool continueAnyway = true,
            bool helloPolicyDisabled = true)
        {
            var state = DecisionState.CreateInitial("sess-53d1e9f6", "tenant-c9787ba2", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            var espConfig = new Dictionary<string, string>
            {
                [SignalPayloadKeys.SkipUserEsp] = "false",
                [SignalPayloadKeys.SkipDeviceEsp] = "false",
                [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "30",
                [SignalPayloadKeys.EspAllowContinueAnyway] = continueAnyway ? "true" : "false",
            };
            if (observationOptIn)
            {
                espConfig[SignalPayloadKeys.EspContinueAnywayObservationEnabled] = "true";
            }
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1), espConfig)).NewState;
            if (helloPolicyDisabled)
            {
                state = engine.Reduce(state, MakeSignal(
                    8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            return state;
        }

        private static DecisionStep ApplyDeviceSetupTerminalFailure(DecisionEngine engine, DecisionState state, long ordinal = 50) =>
            engine.Reduce(state, MakeSignal(
                ordinal, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(38),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Certificates_Failed",
                    ["failedSubcategory"] = "Certificates",
                    ["category"] = "DeviceSetup",
                }));

        // ============================================================ defang path ====

        [Fact]
        public void DeviceSetupFailure_WithOptIn_DefangsToObservationAdvisory()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            Assert.Null(state.AccountSetupEnteredUtc);

            var preStage = state.Stage;
            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            // No terminal — stage unchanged, advisory anchored, no LastFailureTrigger.
            Assert.Equal(preStage, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);
            Assert.Equal("DeviceSetup", step.NewState.EspAdvisoryFailureCategory!.Value);
            Assert.Null(step.NewState.LastFailureTrigger);

            var advisory = SingleTimelineEffect(step, "esp_failure_advisory");
            Assert.Equal("esp_failure_defanged_continueanyway_observation", advisory.Parameters!["advisoryReason"]);
            Assert.Equal("Warning", advisory.Parameters!["severity"]);
            Assert.Equal("true", advisory.Parameters!["mayHaveContinuedAnyway"]);

            Assert.DoesNotContain(step.Effects, e =>
                e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == "enrollment_failed");
        }

        [Fact]
        public void DeviceSetupFailure_WithOptIn_Arms60MinObservationWindow()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);

            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            var deadline = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(deadline);
            // 60-min observation window (user decision 2026-08-08) — NOT the classic 30-min
            // advisory-completion window.
            Assert.Equal(T0.AddMinutes(38).AddMinutes(60), deadline!.DueAtUtc);
        }

        // ==================================================== negative / regression ====

        [Fact]
        public void DeviceSetupFailure_WithoutOptIn_StillHardFails()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine, observationOptIn: false);

            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);
            SingleTimelineEffect(step, "enrollment_failed");
        }

        [Fact]
        public void DeviceSetupFailure_WithOptIn_ButContinueAnywayDisabled_StillHardFails()
        {
            // The observation only makes sense when the ESP profile actually offers the
            // "Continue anyway" button — without it the user cannot reach the desktop.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine, continueAnyway: false);

            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);
            SingleTimelineEffect(step, "enrollment_failed");
        }

        [Fact]
        public void DeviceSetupFailure_WithOptIn_OnSelfDeployingProfile_StillHardFails()
        {
            // Scope gate: self-deploying/kiosk flows have no interactive user who could press
            // "Continue anyway" — observation would only ever expire. Immediate fail is truthful.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EnrollmentFactsObserved, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.IsSelfDeployingProfile] = "true" })).NewState;

            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Null(step.NewState.EspAdvisoryFailureRecordedUtc);
        }

        [Fact]
        public void ClassicAdvisory_WithAccountSetupEntered_KeepsClassicReasonAndWindow()
        {
            // Regression: the opt-in must not change the classic post-AccountSetup advisory —
            // same advisoryReason literal, same 30-min window.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            Assert.NotNull(state.AccountSetupEnteredUtc);

            var step = ApplyDeviceSetupTerminalFailure(engine, state);

            var advisory = SingleTimelineEffect(step, "esp_failure_advisory");
            Assert.Equal("esp_failure_defanged_continueanyway_with_accountsetup", advisory.Parameters!["advisoryReason"]);
            var deadline = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.Equal(T0.AddMinutes(38).AddMinutes(30), deadline!.DueAtUtc);
        }

        // ========================================== real end: desktop completion ====

        [Fact]
        public void ObservationAdvisory_ThenRealUserDesktop_CompletesThroughFinalizing()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;

            // User pressed "Continue anyway" → signed in → DAD-validated real-user desktop.
            var step = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.DesktopArrived, T0.AddMinutes(42)));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Contains(":ContinueAnywayObservation", step.Transition.Trigger);
            // Hello disabled → synthetic skip recorded by the fast-path.
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.FinalizingGrace));
        }

        [Fact]
        public void ObservationCompletion_EnrollmentComplete_CarriesEspSoftFailureMarker()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.DesktopArrived, T0.AddMinutes(42))).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(42.1), DeadlineNames.FinalizingGrace));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, step.NewState.Outcome);

            var complete = SingleTimelineEffect(step, "enrollment_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(complete.TypedPayload);
            Assert.Equal("true", payload["espSoftFailure"]);
            Assert.Equal("continue_anyway_observation", payload["completionSource"]);
            Assert.Equal("DeviceSetup", payload["espSoftFailureCategory"]);
        }

        [Fact]
        public void CleanCompletion_EnrollmentComplete_OmitsEspSoftFailureMarker()
        {
            // A session without any advisory must not carry the marker — regression guard for
            // the BuildEnrollmentCompleteEffect change.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(17),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.AccountSetupProvisioningComplete, T0.AddMinutes(20))).NewState;
            state = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(21))).NewState;
            Assert.Equal(SessionStage.Finalizing, state.Stage);

            var step = engine.Reduce(state, DeadlineFired(50, T0.AddMinutes(21.1), DeadlineNames.FinalizingGrace));

            Assert.Equal(SessionStage.Completed, step.NewState.Stage);
            var complete = SingleTimelineEffect(step, "enrollment_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(complete.TypedPayload);
            Assert.False(payload.ContainsKey("espSoftFailure"));
            Assert.False(payload.ContainsKey("completionSource"));
        }

        // =============================================== window expiry / deadline ====

        [Fact]
        public void ObservationWindow_ExpiresWithoutDesktop_UnDefangsToEspTerminalFailure()
        {
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;
            Assert.Null(state.DesktopArrivedUtc);

            var step = engine.Reduce(state, DeadlineFired(60, T0.AddMinutes(98), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            // The failure cause IS the original ESP failure — same reason literal, likely-stuck
            // promotion stays armed via LastFailureTrigger=EspTerminalFailure.
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), step.NewState.LastFailureTrigger!.Value);

            var failed = SingleTimelineEffect(step, "enrollment_failed");
            Assert.Equal("esp_terminal_failure", failed.Parameters!["reason"]);
            Assert.Equal(
                "continue_anyway_observation_window_expired_without_completion_evidence",
                failed.Parameters!["advisoryReason"]);
        }

        [Fact]
        public void ObservationWindow_FiresWithDesktopAndDisabledHello_CompletesInsteadOfFailing()
        {
            // Ordering variant: desktop arrived but (hypothetically) no eager completion ran —
            // the deadline-fire conjunction must accept desktop + hello for the observation
            // variant without the (structurally impossible) IME user-session gate.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine, helloPolicyDisabled: false);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;
            // Hello policy lands AFTER the failure; desktop arrives; no eager completion since
            // the fast-path needs the policy fact at desktop-arrival time.
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.DesktopArrived, T0.AddMinutes(42))).NewState;
            Assert.NotEqual(SessionStage.Finalizing, state.Stage);
            state = engine.Reduce(state, MakeSignal(
                65, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(43),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(98), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.FinalizingGrace));
        }

        [Fact]
        public void ObservationWindow_FiresWithDesktopButUnknownHello_PromotesToAwaitingHello()
        {
            // Hello policy never observed (no PassportForWork value) — the hello prerequisite is
            // structurally unresolvable. The never-observed promote must apply to the observation
            // variant instead of failing a desktop-proven session.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine, helloPolicyDisabled: false);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;
            state = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.DesktopArrived, T0.AddMinutes(42))).NewState;
            Assert.Null(state.HelloPolicyEnabled);

            var step = engine.Reduce(state, DeadlineFired(70, T0.AddMinutes(98), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.HelloSafety));
        }

        // ======================================================= category recovery ====

        [Fact]
        public void ObservationAdvisory_DeviceSetupRecovery_ResolvesAdvisory()
        {
            // "Try again" path: the ESP re-runs the device phase and DeviceSetup resolves to
            // success — the 4910a5a5 recovery hook must match the observation advisory too.
            var engine = new DecisionEngine();
            var state = SetupObservationEligibleSession(engine);
            state = ApplyDeviceSetupTerminalFailure(engine, state).NewState;

            var step = engine.Reduce(state, MakeSignal(
                60, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(55)));

            Assert.NotNull(step.NewState.EspAdvisoryFailureResolvedUtc);
            Assert.Contains(step.Effects, e =>
                e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == "esp_failure_advisory_resolved");
        }
    }
}
