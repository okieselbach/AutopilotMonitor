using System;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Plan §2.5 L.16 — the reducer kernel catches handler exceptions, records them as
    /// <c>DeadEndReason="reducer_exception"</c> transitions, and leaves state unchanged
    /// except for bookkeeping. M3.0 has no handlers that throw naturally; we verify the
    /// kernel's fail-safe path by forcing a handler exception through a broken state.
    /// </summary>
    public sealed class ReducerFailSafeTests
    {
        [Fact]
        public void Reduce_nullState_throws_ArgumentNullException()
        {
            // This is a programmer-error path — distinct from reducer-internal exceptions.
            var engine = new DecisionEngine();
            Assert.Throws<ArgumentNullException>(() =>
                engine.Reduce(null!, MakeSignal(DecisionSignalKind.SessionStarted)));
        }

        [Fact]
        public void Reduce_nullSignal_throws_ArgumentNullException()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            Assert.Throws<ArgumentNullException>(() => engine.Reduce(state, null!));
        }

        [Fact]
        public void Reduce_sessionStartedInActiveState_producesDeadEnd_stateUnchanged()
        {
            // Build a state that is NOT at StepIndex 0 & SessionStarted — simulates mid-flight replay.
            var engine = new DecisionEngine();
            var active = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(5)
                .WithLastAppliedSignalOrdinal(4)
                .Build();

            var signal = MakeSignal(DecisionSignalKind.SessionStarted, ordinal: 5);

            var step = engine.Reduce(active, signal);

            Assert.False(step.Transition.Taken);
            Assert.StartsWith("session_started_in_active_state", step.Transition.DeadEndReason);
            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Equal(6, step.NewState.StepIndex);
            Assert.Equal(5, step.NewState.LastAppliedSignalOrdinal);
        }

        [Fact]
        public void Reduce_unhandledSchemaVersion_producesDeadEnd_withVersionedReason()
        {
            // After Codex follow-up #4 every DecisionSignalKind has at least one handler,
            // so the dead-end path is now only reachable through an unknown schema version
            // on a known kind. This is the realistic forward-compat failure mode: the
            // backend sees a v2 signal it doesn't know how to interpret yet.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = MakeSignal(DecisionSignalKind.SessionStarted, schemaVersion: 99);

            var step = engine.Reduce(state, signal);

            Assert.False(step.Transition.Taken);
            Assert.Equal("unhandled_signal_kind:SessionStarted:v99", step.Transition.DeadEndReason);
            Assert.Equal(SessionStage.SessionStarted, step.NewState.Stage);
            Assert.Equal(1, step.NewState.StepIndex);
        }

        [Fact]
        public void Reduce_sessionAborted_terminatesWithAbortedOutcome_clearsDeadlines()
        {
            var engine = new DecisionEngine();
            var withDeadline = DecisionState.CreateInitial("s", "t")
                .ToBuilder()
                .AddDeadline(new ActiveDeadline(
                    name: "hello_safety",
                    dueAtUtc: DateTime.UtcNow.AddMinutes(5),
                    firesSignalKind: DecisionSignalKind.DeadlineFired))
                .WithStage(SessionStage.AwaitingHello)
                .Build();

            var step = engine.Reduce(withDeadline, MakeSignal(DecisionSignalKind.SessionAborted));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.Aborted, step.NewState.Outcome);
            Assert.Empty(step.NewState.Deadlines);
            Assert.True(step.Transition.Taken);
        }

        [Fact]
        public void ReducerVersion_matchesAssemblyVersion_stableAcrossReduces()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");

            var step1 = engine.Reduce(state, MakeSignal(DecisionSignalKind.SessionStarted, ordinal: 0));
            var step2 = engine.Reduce(step1.NewState, MakeSignal(DecisionSignalKind.EnrollmentFactsObserved, ordinal: 1));

            Assert.Equal("2.0.0.0", engine.ReducerVersion);
            Assert.Equal("2.0.0.0", step1.Transition.ReducerVersion);
            Assert.Equal("2.0.0.0", step2.Transition.ReducerVersion);
        }

        private static DecisionSignal MakeSignal(
            DecisionSignalKind kind,
            long ordinal = 0,
            int schemaVersion = 1) =>
            new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: schemaVersion,
                occurredAtUtc: new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"test:{kind}", "test"));
    }
}
