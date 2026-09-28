using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// The sealing classifier's AccountSetup excluder (-40) applies only when the IME reported
    /// the AccountSetup phase AND the WhiteGlove_Success signal did not report an untouched
    /// AccountSetup registry. IME logs "EspPhase: AccountSetup" from the device session once
    /// the device apps are done — on pre-provisioning with the user ESP enabled that line
    /// routinely precedes the technician's success page and used to leave Part 1 unsealed.
    /// The gate may only ever lift the excluder, never add one.
    /// </summary>
    public sealed class WhiteGloveAccountSetupProgressGateTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 7, 25, 9, DateTimeKind.Utc);

        [Fact]
        public void Ime_account_setup_with_untouched_registry_seals_on_the_fast_path()
        {
            var step = new DecisionEngine().Reduce(
                StateWithImeAccountSetup(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: "false"));

            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.WhiteGlovePart1Sealed, step.NewState.Outcome);
            Assert.Equal(HypothesisLevel.Confirmed, step.NewState.ClassifierOutcomes.WhiteGloveSealing.Level);
            Assert.Equal(WhiteGloveSealingClassifier.WeightShellCoreWhiteGloveSuccess,
                step.NewState.ClassifierOutcomes.WhiteGloveSealing.Score);

            var fact = step.NewState.ScenarioObservations.AccountSetupProgressAtWhiteGloveSuccess;
            Assert.NotNull(fact);
            Assert.False(fact!.Value);
            Assert.Equal(3, fact.SourceSignalOrdinal);
        }

        [Fact]
        public void Ime_account_setup_with_registry_progress_keeps_the_excluder()
        {
            var step = new DecisionEngine().Reduce(
                StateWithImeAccountSetup(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: "true"));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.Contains(step.Effects, e => e.Kind == DecisionEffectKind.RunClassifier);
            Assert.True(DecisionEngine.BuildWhiteGloveSealingSnapshot(step.NewState).HasAccountSetupActivity);
        }

        [Fact]
        public void Missing_progress_payload_keeps_the_excluder_for_older_agents()
        {
            var step = new DecisionEngine().Reduce(
                StateWithImeAccountSetup(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: null));

            Assert.Equal(SessionStage.EspAccountSetup, step.NewState.Stage);
            Assert.Null(step.NewState.ScenarioObservations.AccountSetupProgressAtWhiteGloveSuccess);
            Assert.True(DecisionEngine.BuildWhiteGloveSealingSnapshot(step.NewState).HasAccountSetupActivity);
        }

        [Theory]
        [InlineData("")]
        [InlineData("yes")]
        public void Unparseable_progress_payload_is_treated_as_missing(string raw)
        {
            var step = new DecisionEngine().Reduce(
                StateWithImeAccountSetup(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: raw));

            Assert.Null(step.NewState.ScenarioObservations.AccountSetupProgressAtWhiteGloveSuccess);
            Assert.NotEqual(SessionStage.WhiteGloveSealed, step.NewState.Stage);
        }

        [Fact]
        public void Registry_progress_without_ime_account_setup_adds_no_excluder()
        {
            var state = DecisionState.CreateInitial("s-gate", "t-gate").ToBuilder()
                .WithStage(SessionStage.EspDeviceSetup)
                .WithStepIndex(1)
                .WithLastAppliedSignalOrdinal(0)
                .Build();

            var step = new DecisionEngine().Reduce(state, MakeShellCoreSignal(ordinal: 1, accountSetupProgress: "true"));

            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);
        }

        [Fact]
        public void Untouched_registry_does_not_lift_a_hard_excluder()
        {
            var builder = DecisionState.CreateInitial("s-gate", "t-gate").ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2);
            builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(T0.AddMinutes(-3), 1);
            builder.DesktopArrivedUtc = new SignalFact<DateTime>(T0.AddMinutes(-1), 2);

            var step = new DecisionEngine().Reduce(builder.Build(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: "false"));

            Assert.NotEqual(SessionStage.WhiteGloveSealed, step.NewState.Stage);
            var snapshot = DecisionEngine.BuildWhiteGloveSealingSnapshot(step.NewState);
            Assert.False(snapshot.HasAccountSetupActivity);
            Assert.True(snapshot.DesktopArrived);
        }

        [Fact]
        public void Progress_fact_survives_the_state_serializer()
        {
            var step = new DecisionEngine().Reduce(
                StateWithImeAccountSetup(), MakeShellCoreSignal(ordinal: 3, accountSetupProgress: "false"));

            var roundTripped = StateSerializer.Deserialize(StateSerializer.Serialize(step.NewState));

            var fact = roundTripped.ScenarioObservations.AccountSetupProgressAtWhiteGloveSuccess;
            Assert.NotNull(fact);
            Assert.False(fact!.Value);
            Assert.Equal(3, fact.SourceSignalOrdinal);
        }

        private static DecisionState StateWithImeAccountSetup()
        {
            // The IME device-session line: AccountSetup entered before the success page.
            var builder = DecisionState.CreateInitial("s-gate", "t-gate").ToBuilder()
                .WithStage(SessionStage.EspAccountSetup)
                .WithStepIndex(3)
                .WithLastAppliedSignalOrdinal(2);
            builder.AccountSetupEnteredUtc = new SignalFact<DateTime>(T0.AddMinutes(-3), 2);
            return builder.Build();
        }

        private static DecisionSignal MakeShellCoreSignal(long ordinal, string? accountSetupProgress)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal);
            if (accountSetupProgress != null)
            {
                payload[SignalPayloadKeys.AccountSetupProgress] = accountSetupProgress;
            }

            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                kindSchemaVersion: 1,
                occurredAtUtc: T0,
                sourceOrigin: "EspAndHelloTracker",
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: "ShellCore-62407",
                    summary: "WhiteGlove_Success"),
                payload: payload);
        }
    }
}
