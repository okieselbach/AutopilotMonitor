using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// M4.6.β — <see cref="DecisionStepProcessor"/> fires the terminal-stage callback exactly
    /// once, at the transition from a non-terminal to a terminal <see cref="SessionStage"/>.
    /// </summary>
    public sealed class DecisionStepProcessorTerminalStageTests
    {
        private static DateTime At => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeJournalWriter Journal { get; } = new FakeJournalWriter();
            public FakeEffectRunner Effects { get; } = new FakeEffectRunner();
            public FakeSnapshotPersistence Snapshot { get; } = new FakeSnapshotPersistence();
            public FakeQuarantineSink Quarantine { get; } = new FakeQuarantineSink();

            public Rig() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info); }

            public DecisionStepProcessor Build(DecisionState initial, Action<DecisionState>? onTerminal) =>
                new DecisionStepProcessor(
                    initialState: initial,
                    journal: Journal,
                    effectRunner: Effects,
                    snapshot: Snapshot,
                    quarantineSink: Quarantine,
                    logger: Logger,
                    quarantineThreshold: DecisionStepProcessor.DefaultQuarantineThreshold,
                    onTerminalStageReached: onTerminal);

            public void Dispose() => Tmp.Dispose();
        }

        private static DecisionSignal Sig(long ordinal, DateTime at) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: at,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Raw, $"raw-{ordinal}", $"evidence-{ordinal}"),
                payload: null);

        private static DecisionStep StepToStage(DecisionState oldState, SessionStage newStage, long signalOrdinal, DateTime at)
        {
            var builder = new DecisionStateBuilder(oldState) { Stage = newStage, StepIndex = oldState.StepIndex + 1 };
            var newState = builder.Build();
            var transition = new DecisionTransition(
                stepIndex: newState.StepIndex,
                sessionTraceOrdinal: signalOrdinal,
                signalOrdinalRef: signalOrdinal,
                occurredAtUtc: at,
                trigger: "TestTerminalTrigger",
                fromStage: oldState.Stage,
                toStage: newStage,
                taken: true,
                deadEndReason: null,
                reducerVersion: "2.0.0.0");
            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        [Theory]
        [InlineData(SessionStage.Completed)]
        [InlineData(SessionStage.Failed)]
        [InlineData(SessionStage.WhiteGloveSealed)]
        public void Terminal_stage_transition_fires_callback_with_terminal_state(SessionStage terminal)
        {
            using var rig = new Rig();
            DecisionState? captured = null;
            var callCount = 0;
            var sut = rig.Build(DecisionState.CreateInitial("S1", "T1"),
                onTerminal: state =>
                {
                    Interlocked.Increment(ref callCount);
                    captured = state;
                });

            var step = StepToStage(sut.CurrentState, terminal, signalOrdinal: 1, at: At);
            sut.ApplyStep(step, Sig(1, At));

            Assert.Equal(1, callCount);
            Assert.NotNull(captured);
            Assert.Equal(terminal, captured!.Stage);
        }

        [Fact]
        public void Terminal_callback_does_not_fire_for_non_terminal_transitions()
        {
            using var rig = new Rig();
            var callCount = 0;
            var sut = rig.Build(DecisionState.CreateInitial("S1", "T1"),
                onTerminal: _ => Interlocked.Increment(ref callCount));

            var step = StepToStage(sut.CurrentState, SessionStage.AwaitingEspPhaseChange, signalOrdinal: 1, at: At);
            sut.ApplyStep(step, Sig(1, At));

            Assert.Equal(0, callCount);
        }

        [Fact]
        public void Terminal_callback_fires_at_most_once_per_processor_instance()
        {
            using var rig = new Rig();
            var callCount = 0;
            var sut = rig.Build(DecisionState.CreateInitial("S1", "T1"),
                onTerminal: _ => Interlocked.Increment(ref callCount));

            // Transition to Completed, then apply a second step that leaves the stage terminal.
            var firstStep = StepToStage(sut.CurrentState, SessionStage.Completed, signalOrdinal: 1, at: At);
            sut.ApplyStep(firstStep, Sig(1, At));

            // Synthetic second step — stage still terminal (same value).
            var secondStep = StepToStage(sut.CurrentState, SessionStage.Completed, signalOrdinal: 2, at: At.AddSeconds(1));
            sut.ApplyStep(secondStep, Sig(2, At.AddSeconds(1)));

            Assert.Equal(1, callCount);
        }

        [Fact]
        public void Terminal_callback_suppressed_when_initial_state_is_already_terminal()
        {
            // Recovery scenario: snapshot loaded a state whose stage is already terminal
            // (e.g. crash after terminal transition but before Stop). The processor must not
            // re-fire the callback on the first post-recovery step.
            using var rig = new Rig();
            var callCount = 0;
            var recoveredState = new DecisionStateBuilder(DecisionState.CreateInitial("S1", "T1"))
            {
                Stage = SessionStage.Completed,
            }.Build();

            var sut = rig.Build(recoveredState,
                onTerminal: _ => Interlocked.Increment(ref callCount));

            // No ApplyStep needed — the ctor already observed the terminal state. Even if we
            // apply a follow-up terminal-preserving step, callback stays silent.
            var step = StepToStage(sut.CurrentState, SessionStage.Completed, signalOrdinal: 1, at: At);
            sut.ApplyStep(step, Sig(1, At));

            Assert.Equal(0, callCount);
        }

        [Fact]
        public void Terminal_callback_null_is_supported()
        {
            using var rig = new Rig();
            var sut = rig.Build(DecisionState.CreateInitial("S1", "T1"), onTerminal: null);
            var step = StepToStage(sut.CurrentState, SessionStage.Completed, signalOrdinal: 1, at: At);

            // Should not throw when callback is null.
            sut.ApplyStep(step, Sig(1, At));
            Assert.Equal(SessionStage.Completed, sut.CurrentState.Stage);
        }

        [Fact]
        public void Terminal_callback_exception_is_swallowed_and_logged()
        {
            using var rig = new Rig();
            var sut = rig.Build(DecisionState.CreateInitial("S1", "T1"),
                onTerminal: _ => throw new InvalidOperationException("handler bug"));

            var step = StepToStage(sut.CurrentState, SessionStage.Failed, signalOrdinal: 1, at: At);
            // Must not bubble the handler's exception — the step is already committed.
            sut.ApplyStep(step, Sig(1, At));
            Assert.Equal(SessionStage.Failed, sut.CurrentState.Stage);
        }
    }
}
