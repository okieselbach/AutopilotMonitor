#nullable enable
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
    /// Session a4537c36 (2026-07-10) — proactive arm C of <c>ShouldTransitionToAwaitingHello</c>:
    /// AccountSetup entered + normal ESP final exit (post-AccountSetup in ingest order) + genuine
    /// IME user-session completion (at-or-after the AccountSetup anchor) + real-user desktop.
    /// Windows closed the User-ESP page normally while the AccountSetup registry stayed at
    /// 1-of-5 subcategories, so arm A (categorySucceeded) was unsatisfiable by construction; the
    /// session only completed because the Hello policy happened to flip to enabled after a
    /// reboot. Arm C applies the AdvisoryCompletion backstop's conjunction eagerly, per signal
    /// ordering. ALL FOUR facts are mandatory — the 3-of-4 regression shape stays pinned by
    /// <c>ClassicHelloDisabledCompletionTests.HelloDisabled_FastPath_skipped_when_AccountSetup_entered_but_provisioning_never_succeeded</c>
    /// (session 08c99638: no IME fact → parked).
    /// </summary>
    public sealed class ClassicUserSessionEvidenceCompletionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 10, 13, 52, 0, DateTimeKind.Utc);

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
        /// Baseline (a4537c36 shape up to AccountSetup entry): full ESP profile, optional Hello
        /// policy, DeviceSetup → AccountSetup. The three remaining arm-C facts (final exit,
        /// desktop, IME user-session) are appended per-test in the ordering under test.
        /// </summary>
        private static DecisionState SetupThroughAccountSetup(DecisionEngine engine, bool? helloPolicyDisabled = true)
        {
            var state = DecisionState.CreateInitial("sess-a4537c36", "tenant-a4537c36", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                })).NewState;
            if (helloPolicyDisabled != null)
            {
                state = engine.Reduce(state, MakeSignal(
                    8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                    new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.HelloEnabled] = helloPolicyDisabled.Value ? "false" : "true",
                    })).NewState;
            }
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            return state;
        }

        private static void DriveToCompleted(DecisionEngine engine, DecisionStep finalizingStep, long graceOrdinal)
        {
            var grace = FindDeadline(finalizingStep.NewState, DeadlineNames.FinalizingGrace);
            Assert.NotNull(grace);
            var final = engine.Reduce(
                finalizingStep.NewState,
                DeadlineFired(graceOrdinal, grace!.DueAtUtc, DeadlineNames.FinalizingGrace));
            Assert.Equal(SessionStage.Completed, final.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentComplete, final.NewState.Outcome);
            SingleTimelineEffect(final, "enrollment_complete");
        }

        // ============================================== ordering: IME signal arrives LAST ====

        [Fact]
        public void ImeUserSessionLast_HelloDisabled_CompletesThroughFinalizing()
        {
            // The a4537c36 replay: exit (guard-blocked, advisory armed) → desktop → IME last.
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine);

            var exitStep = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(10)));
            Assert.Equal(SessionStage.EspAccountSetup, exitStep.NewState.Stage);
            Assert.NotNull(FindDeadline(exitStep.NewState, DeadlineNames.AdvisoryCompletion));

            var desktopStep = engine.Reduce(exitStep.NewState, MakeSignal(40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(11)));
            // 3 of 4 — the Hello-disabled fast-path must stay blocked without the IME fact.
            Assert.Equal(SessionStage.EspAccountSetup, desktopStep.NewState.Stage);
            Assert.Null(desktopStep.NewState.Outcome);

            var imeStep = engine.Reduce(desktopStep.NewState, MakeSignal(
                50, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(12),
                new Dictionary<string, string> { [SignalPayloadKeys.ImePatternId] = "IME-USER-SESSION-COMPLETED" }));

            Assert.Equal(SessionStage.Finalizing, imeStep.NewState.Stage);
            Assert.Equal(
                nameof(DecisionSignalKind.ImeUserSessionCompleted) + ":UserSessionEvidenceCompletion",
                imeStep.Transition.Trigger);
            Assert.Equal("Skipped", imeStep.NewState.HelloOutcome!.Value);
            Assert.Null(FindDeadline(imeStep.NewState, DeadlineNames.HelloSafety));

            DriveToCompleted(engine, imeStep, graceOrdinal: 60);
        }

        [Fact]
        public void ImeUserSessionLast_HelloEnabledUnresolved_PromotesToAwaitingHello()
        {
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine, helloPolicyDisabled: false);

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(11))).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);

            var imeStep = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(12)));

            // Hello is enabled and unresolved — arm C must NOT bypass it: promote, don't complete.
            Assert.Equal(SessionStage.AwaitingHello, imeStep.NewState.Stage);
            Assert.Equal(
                nameof(DecisionSignalKind.ImeUserSessionCompleted) + ":UserSessionEvidencePromote",
                imeStep.Transition.Trigger);
            var helloSafety = FindDeadline(imeStep.NewState, DeadlineNames.HelloSafety);
            Assert.NotNull(helloSafety);
            Assert.Null(imeStep.NewState.Outcome);

            // A real Hello resolution then completes normally with the live outcome.
            var helloStep = engine.Reduce(imeStep.NewState, MakeSignal(
                60, DecisionSignalKind.HelloResolved, T0.AddMinutes(13),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));
            Assert.Equal(SessionStage.Finalizing, helloStep.NewState.Stage);
            Assert.Equal("Success", helloStep.NewState.HelloOutcome!.Value);
            DriveToCompleted(engine, helloStep, graceOrdinal: 70);
        }

        // ============================================== ordering: desktop arrives LAST ======

        [Fact]
        public void DesktopLast_HelloDisabled_CompletesViaFastPath()
        {
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine);

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            var imeStep = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(11)));
            // 3 of 4 — no desktop yet: fact recorded, session stays parked.
            Assert.Equal(SessionStage.EspAccountSetup, imeStep.NewState.Stage);

            var desktopStep = engine.Reduce(imeStep.NewState, MakeSignal(50, DecisionSignalKind.DesktopArrived, T0.AddMinutes(12)));

            // desktopArrivedInFlight wiring: the desktop fact lives on the builder in this step.
            Assert.Equal(SessionStage.Finalizing, desktopStep.NewState.Stage);
            Assert.Equal(
                nameof(DecisionSignalKind.DesktopArrived) + ":HelloDisabledFastPath",
                desktopStep.Transition.Trigger);
            Assert.Equal("Skipped", desktopStep.NewState.HelloOutcome!.Value);
            DriveToCompleted(engine, desktopStep, graceOrdinal: 60);
        }

        [Fact]
        public void DesktopLast_HelloEnabledUnresolved_WaitsOnHelloResolutionOnly()
        {
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine, helloPolicyDisabled: false);

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(11))).NewState;

            var desktopStep = engine.Reduce(state, MakeSignal(50, DecisionSignalKind.DesktopArrived, T0.AddMinutes(12)));

            // completion_waiting must credit the 4-fact evidence: account_setup_provisioning_complete
            // is no longer reported missing — only the genuinely outstanding hello_resolution.
            Assert.Equal(SessionStage.EspAccountSetup, desktopStep.NewState.Stage);
            var waiting = SingleTimelineEffect(desktopStep, "completion_waiting");
            Assert.Equal(
                CompletionPrerequisitesForTest.HelloResolution,
                waiting.Parameters!["missingPrerequisites"]);

            var helloStep = engine.Reduce(desktopStep.NewState, MakeSignal(
                60, DecisionSignalKind.HelloResolved, T0.AddMinutes(13),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloOutcome] = "Success" }));
            Assert.Equal(SessionStage.Finalizing, helloStep.NewState.Stage);
            DriveToCompleted(engine, helloStep, graceOrdinal: 70);
        }

        // ============================================== ordering: ESP exit arrives LAST =====

        [Fact]
        public void EspExitLast_HelloDisabled_CompletesViaDeferredCompletion()
        {
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine);

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10))).NewState;
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(11))).NewState;
            Assert.Equal(SessionStage.EspAccountSetup, state.Stage);

            var exitStep = engine.Reduce(state, MakeSignal(50, DecisionSignalKind.EspExiting, T0.AddMinutes(12)));

            // espFinalExitInFlight wiring + deferred-completion parity: no AwaitingHello park,
            // no HelloSafety window — straight through Finalizing.
            Assert.Equal(SessionStage.Finalizing, exitStep.NewState.Stage);
            Assert.Equal(
                nameof(DecisionSignalKind.EspExiting) + ":DeferredCompletion",
                exitStep.Transition.Trigger);
            Assert.Equal("Skipped", exitStep.NewState.HelloOutcome!.Value);
            Assert.Null(FindDeadline(exitStep.NewState, DeadlineNames.HelloSafety));
            DriveToCompleted(engine, exitStep, graceOrdinal: 60);
        }

        // ============================================== negative shapes =====================

        [Fact]
        public void DefaultUser0Ghost_ImeBeforeAccountSetupAnchor_StaysParked()
        {
            // The IME completion timestamp predates AccountSetup entry (defaultuser0/OOBE
            // frame) — arm C's genuine-IME guard must reject it even with exit + desktop in.
            var engine = new DecisionEngine();
            var state = SetupThroughAccountSetup(engine); // AccountSetup entered at T0+3min

            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.EspExiting, T0.AddMinutes(10))).NewState;
            state = engine.Reduce(state, MakeSignal(40, DecisionSignalKind.DesktopArrived, T0.AddMinutes(11))).NewState;

            var imeStep = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(2.5)));

            Assert.Equal(SessionStage.EspAccountSetup, imeStep.NewState.Stage);
            Assert.Null(imeStep.NewState.Outcome);
            // The AdvisoryCompletion backstop stays armed — it remains the resolution path.
            Assert.NotNull(FindDeadline(imeStep.NewState, DeadlineNames.AdvisoryCompletion));
        }

        [Fact]
        public void IntermediateExitOnly_PreAccountSetupOrdinal_DoesNotSatisfyArmC()
        {
            // The only recorded exit is the Device→Account handoff (ingest ordinal BEFORE
            // AccountSetup entry) — arm C must not treat it as the final exit, regardless of
            // desktop + genuine IME evidence.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-a4537c36-b", "tenant-a4537c36", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(1),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            // Handoff exit BEFORE AccountSetup entry.
            state = engine.Reduce(state, MakeSignal(15, DecisionSignalKind.EspExiting, T0.AddMinutes(2.5))).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(3),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(30, DecisionSignalKind.DesktopArrived, T0.AddMinutes(10))).NewState;

            var imeStep = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.ImeUserSessionCompleted, T0.AddMinutes(11)));

            Assert.Equal(SessionStage.EspAccountSetup, imeStep.NewState.Stage);
            Assert.Null(imeStep.NewState.Outcome);
        }
    }

    /// <summary>Local mirror of the internal completion-prerequisite literals used in asserts.</summary>
    internal static class CompletionPrerequisitesForTest
    {
        public const string HelloResolution = "hello_resolution";
    }
}
