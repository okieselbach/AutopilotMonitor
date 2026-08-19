using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// The reducer half of the "a replayed 62407 must never reach the decision engine" contract
    /// (2026-08-19). The agent half — ShellCoreTracker never re-raising a replayed exit — lives in
    /// <c>ShellCoreTrackerReplayScopeTests</c>; this file proves WHY that upstream restriction is
    /// the only place the problem can be solved.
    /// <para>
    /// The reducer is itself a completion gate, and by design it cannot tell a replayed exit from
    /// a live one:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>HandleEspExitingV1</c> passes <c>espFinalExitInFlight: true</c> for EVERY
    ///     arriving exit — the signal carries no provenance.</item>
    ///   <item><c>IsPostAccountSetupFinalExit</c> orders exits by INGEST ORDINAL rather than by
    ///     timestamp (deliberately: replayed CMTrace lines carry backdated source times). A
    ///     historic exit replayed today is assigned a fresher ordinal than reality, so it reads as
    ///     post-AccountSetup by construction.</item>
    /// </list>
    /// <para>
    /// After a restart the state is restored from the snapshot, so AccountSetupEntered, the IME
    /// user-session completion and the desktop arrival are all already present. Arm C of
    /// <c>ShouldTransitionToAwaitingHello</c> then needs exactly one more fact: an exit. These
    /// tests pin that it opens — which is correct behaviour for a live exit and would be a
    /// premature completion for a replayed one. Hence the upstream ban.
    /// </para>
    /// </summary>
    public sealed class ClassicEspExitingOnRestoredStateTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 8, 19, 8, 43, 16, DateTimeKind.Utc);

        [Fact]
        public void Arm_C_opens_on_any_arriving_exit_once_restored_state_carries_the_other_three_facts()
        {
            // This is the danger, stated as a test: with restored facts in place, a single
            // EspExiting signal is all that separates EspAccountSetup from AwaitingHello. The
            // reducer has no way to ask "was that exit real, or did we dig it out of the event
            // log?" — so the answer has to be that a replayed exit is never turned into a signal.
            var engine = new DecisionEngine();
            var state = BuildRestoredState(
                accountSetupEntered: true,
                imeUserSessionCompleted: true,
                desktopArrived: true);

            var step = engine.Reduce(state, MakeEspExitingSignal(ordinal: 10));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
        }

        [Theory]
        // Each row drops exactly one of arm C's mandatory facts — the transition must not happen.
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void Arm_C_stays_shut_when_any_of_its_mandatory_facts_is_missing(
            bool accountSetupEntered, bool imeUserSessionCompleted, bool desktopArrived)
        {
            var engine = new DecisionEngine();
            var state = BuildRestoredState(accountSetupEntered, imeUserSessionCompleted, desktopArrived);

            var step = engine.Reduce(state, MakeEspExitingSignal(ordinal: 10));

            Assert.NotEqual(SessionStage.AwaitingHello, step.NewState.Stage);
        }

        [Fact]
        public void A_historic_source_timestamp_does_not_hold_the_gate_shut()
        {
            // The exit's OccurredAtUtc is an hour older than the AccountSetup anchor — exactly the
            // shape a replayed record has. It makes no difference: the ordering that arm C uses is
            // the ingest ordinal, and the in-flight flag bypasses even that. Timestamps are not a
            // defence, which is why the fix cannot live in the reducer.
            var engine = new DecisionEngine();
            var state = BuildRestoredState(
                accountSetupEntered: true,
                imeUserSessionCompleted: true,
                desktopArrived: true);

            var step = engine.Reduce(
                state,
                MakeEspExitingSignal(ordinal: 10, occurredAtUtc: Fixed.AddHours(-1)));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
        }

        // ------------------------------------------------------------------------------

        private static DecisionState BuildRestoredState(
            bool accountSetupEntered,
            bool imeUserSessionCompleted,
            bool desktopArrived)
        {
            var anchor = Fixed.AddMinutes(-90);

            var builder = DecisionState.CreateInitial("s", "t", Fixed.AddDays(-1))
                .ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(452)             // the restored StepIndex the sits-d sessions showed
                .WithLastAppliedSignalOrdinal(9);

            if (accountSetupEntered)
                builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(anchor, 1);

            // IsImeUserSessionGenuine requires the completion to be at-or-after the AccountSetup
            // anchor (the defaultuser0-ghost guard).
            if (imeUserSessionCompleted)
                builder.ImeUserSessionCompletedUtc = new SignalFact<DateTime>(anchor.AddMinutes(5), 2);

            if (desktopArrived)
                builder.DesktopArrivedUtc = new SignalFact<DateTime>(anchor.AddMinutes(6), 3);

            return builder.Build();
        }

        private static DecisionSignal MakeEspExitingSignal(long ordinal, DateTime? occurredAtUtc = null) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.EspExiting,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc ?? Fixed,
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "esp-hello-detector-v1",
                    summary: "ESP exiting (coordinator-forwarded Shell-Core 62407)",
                    derivationInputs: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["subSource"] = "ShellCoreTracker",
                        ["eventSource"] = "Microsoft-Windows-Shell-Core",
                        ["eventId"] = "62407",
                    }));
    }
}
