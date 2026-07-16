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
    /// Misclassification audit 2026-07-16 (sessions 2dac8298 / 7635ab18): the ESP page exits
    /// normally, the DAD-validated real-user desktop arrives, but the Hello policy read never
    /// commits (no PassportForWork registry value → no hello_policy_detected ever) and no
    /// wizard is ever seen. The <c>hello_resolution</c> prerequisite is then structurally
    /// unresolvable and the esp-exit advisory window used to hard-fail a provably finished
    /// enrollment with <c>esp_exit_without_completion_evidence</c>.
    /// Coverage:
    /// <list type="bullet">
    ///   <item>Hello-never-observed at advisory fire → promote to AwaitingHello + arm
    ///         hello_safety (NOT Failed), trigger suffix <c>HelloNeverObservedPromote</c>.</item>
    ///   <item>hello_safety fire then completes through Finalizing with synthetic
    ///         HelloOutcome=Timeout.</item>
    ///   <item>Policy read committed (enabled) → fallthrough still fails (no behavior change).</item>
    ///   <item>No desktop → fallthrough still fails.</item>
    ///   <item>Advisory-anchor variant (real ESP terminal failure) is exempt — still fails.</item>
    /// </list>
    /// </summary>
    public sealed class AdvisoryCompletionHelloNeverObservedTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);

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

        /// <summary>
        /// Session-2dac8298 shape: normal ESP run, AccountSetup entered, genuine final exit
        /// (arms the esp-exit advisory window), real-user desktop shortly after — and NO
        /// hello_policy_detected, NO wizard, NO IME user-session completion for the whole run.
        /// </summary>
        private static DecisionState SetupHelloNeverObservedSession(
            DecisionEngine engine,
            bool desktopArrives = true,
            bool helloPolicyEnabledRead = false,
            bool allowContinueAnyway = false)
        {
            var state = DecisionState.CreateInitial("sess-2dac8298", "tenant-047b2e1f", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = allowContinueAnyway ? "true" : "false",
                })).NewState;
            if (helloPolicyEnabledRead)
            {
                state = engine.Reduce(state, MakeSignal(
                    8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                    new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "true" })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(8),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            // Genuine post-AccountSetup final exit — arms the esp-exit advisory window
            // (30 min) because AccountSetup was entered and no promotion arm applies.
            state = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            if (desktopArrives)
            {
                state = engine.Reduce(state, MakeSignal(
                    40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10.5))).NewState;
            }
            return state;
        }

        [Fact]
        public void Setup_ArmsEspExitAdvisoryWindow()
        {
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine);

            Assert.NotNull(FindDeadline(state, DeadlineNames.AdvisoryCompletion));
            Assert.Null(state.HelloPolicyEnabled);
            Assert.Null(state.HelloResolvedUtc);
            Assert.Null(state.Outcome);
        }

        [Fact]
        public void DeadlineFired_HelloNeverObserved_PromotesToAwaitingHello_NotFailed()
        {
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine);

            var fireAt = T0.AddMinutes(40);
            var step = engine.Reduce(state, DeadlineFired(50, fireAt, DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Equal(
                $"DeadlineFired:{DeadlineNames.AdvisoryCompletion}:HelloNeverObservedPromote",
                step.Transition.Trigger);

            // hello_safety armed in state + scheduled as an effect; advisory window retired.
            var safety = FindDeadline(step.NewState, DeadlineNames.HelloSafety);
            Assert.NotNull(safety);
            Assert.Null(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
            var scheduleEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.ScheduleDeadline);
            Assert.Equal(DeadlineNames.HelloSafety, scheduleEffect.Deadline!.Name);
        }

        [Fact]
        public void HelloSafetyFired_AfterPromote_CompletesThroughFinalizing()
        {
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine);
            state = engine.Reduce(state, DeadlineFired(50, T0.AddMinutes(40), DeadlineNames.AdvisoryCompletion)).NewState;

            var step = engine.Reduce(state, DeadlineFired(60, T0.AddMinutes(45), DeadlineNames.HelloSafety));

            Assert.Equal(SessionStage.Finalizing, step.NewState.Stage);
            Assert.NotNull(step.NewState.HelloResolvedUtc);
            Assert.Equal("Timeout", step.NewState.HelloOutcome!.Value);
        }

        [Fact]
        public void DeadlineFired_PolicyReadEnabled_NoWizard_StillFails()
        {
            // A COMMITTED enabled policy read means Hello genuinely never finished — the
            // never-observed promote must not apply and the fallthrough failure stands.
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine, helloPolicyEnabledRead: true);

            var step = engine.Reduce(state, DeadlineFired(50, T0.AddMinutes(40), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
            var failEffect = step.Effects.Single(e => e.Kind == DecisionEffectKind.EmitEventTimelineEntry);
            Assert.Equal("esp_exit_without_completion_evidence", failEffect.Parameters!["reason"]);
        }

        [Fact]
        public void DeadlineFired_NoDesktop_StillFails()
        {
            // Without the DAD-validated desktop there is no user-presence evidence at all —
            // the never-observed promote must not apply.
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine, desktopArrives: false);

            var step = engine.Reduce(state, DeadlineFired(50, T0.AddMinutes(40), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }

        [Fact]
        public void DeadlineFired_AdvisoryAnchorVariant_HelloNeverObserved_StillFails()
        {
            // The advisory variant is anchored to a REAL ESP terminal failure — a missing
            // Hello policy read must not soften that evidence. Only the esp-exit variant
            // (normal page close) qualifies for the never-observed promote.
            var engine = new DecisionEngine();
            var state = SetupHelloNeverObservedSession(engine, allowContinueAnyway: true);
            state = engine.Reduce(state, MakeSignal(
                45, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(11),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["errorCode"] = "0x87d1041c",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                })).NewState;
            Assert.NotNull(state.EspAdvisoryFailureRecordedUtc);

            var step = engine.Reduce(state, DeadlineFired(50, T0.AddMinutes(41), DeadlineNames.AdvisoryCompletion));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);
        }
    }
}
