using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex Finding 1 regression gate for <see cref="DecisionStepProcessor"/>: verifies that
    /// every successfully-journaled <see cref="DecisionTransition"/> is also projected onto
    /// the telemetry transport for backend upload, and that a throwing emitter does NOT
    /// abort the step.
    /// </summary>
    public sealed class DecisionStepProcessorUploadPathTests
    {
        private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
        private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeJournalWriter Journal { get; } = new FakeJournalWriter();
            public FakeEffectRunner Effects { get; } = new FakeEffectRunner();
            public FakeSnapshotPersistence Snapshot { get; } = new FakeSnapshotPersistence();
            public FakeQuarantineSink Quarantine { get; } = new FakeQuarantineSink();
            public DecisionEngine Engine { get; } = new DecisionEngine();
            public DecisionState InitialState { get; } = DecisionState.CreateInitial(SessionId, TenantId);
            public FakeTelemetryTransport Transport { get; } = new FakeTelemetryTransport();

            public Rig() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info); }

            public DecisionStepProcessor Build(TelemetryTransitionEmitter? emitter)
                => new DecisionStepProcessor(
                    initialState: InitialState,
                    journal: Journal,
                    effectRunner: Effects,
                    snapshot: Snapshot,
                    quarantineSink: Quarantine,
                    logger: Logger,
                    transitionEmitter: emitter);

            public (DecisionStep step, DecisionSignal signal) ReduceSessionStarted(long ordinal = 0)
            {
                var signal = TestSignals.Raw(ordinal, DecisionSignalKind.SessionStarted, At);
                var step = Engine.Reduce(InitialState, signal);
                return (step, signal);
            }

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void ApplyStep_emits_transition_to_transport_after_Journal_append()
        {
            using var rig = new Rig();
            var emitter = new TelemetryTransitionEmitter(rig.Transport, SessionId, TenantId);
            var processor = rig.Build(emitter);

            var (step, signal) = rig.ReduceSessionStarted();
            processor.ApplyStep(step, signal);

            Assert.Single(rig.Journal.Appended);
            Assert.Equal(1, rig.Transport.EnqueueCount);
            Assert.Equal(TelemetryItemKind.DecisionTransition, rig.Transport.Enqueued[0].Kind);
            Assert.Equal($"{TenantId}_{SessionId}", rig.Transport.Enqueued[0].PartitionKey);
        }

        [Fact]
        public void ApplyStep_without_emitter_keeps_pre_M5_behaviour_unchanged()
        {
            using var rig = new Rig();
            var processor = rig.Build(emitter: null);

            var (step, signal) = rig.ReduceSessionStarted();
            processor.ApplyStep(step, signal);

            Assert.Single(rig.Journal.Appended);
            Assert.Equal(0, rig.Transport.EnqueueCount);
        }

        [Fact]
        public void Journal_failure_prevents_emit_so_no_ghost_rows_leak_to_backend()
        {
            // Journal is authoritative. If journal.Append throws, the transition NEVER happened
            // locally — and must NEVER be uploaded. Otherwise the backend would see phantom
            // transitions that can't be reconciled.
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("disk full (test)"));
            var emitter = new TelemetryTransitionEmitter(rig.Transport, SessionId, TenantId);
            var processor = rig.Build(emitter);

            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<InvalidOperationException>(() => processor.ApplyStep(step, signal));
            Assert.Equal(0, rig.Transport.EnqueueCount);
        }

        [Fact]
        public void Transport_throw_is_swallowed_and_effects_still_run()
        {
            // Transport failure must NOT break the local post-journal pipeline: snapshot +
            // effects + CurrentState update all happen regardless.
            using var rig = new Rig();
            var throwingTransport = new ThrowingTelemetryTransport();
            var emitter = new TelemetryTransitionEmitter(throwingTransport, SessionId, TenantId);
            var processor = rig.Build(emitter);

            var (step, signal) = rig.ReduceSessionStarted();
            processor.ApplyStep(step, signal);

            Assert.Equal(1, throwingTransport.EnqueueAttempts);
            Assert.Single(rig.Journal.Appended);
            Assert.Equal(1, rig.Effects.CallCount);
            Assert.Equal(step.NewState, processor.CurrentState);
        }

        private sealed class ThrowingTelemetryTransport : ITelemetryTransport
        {
            public int EnqueueAttempts;

            public long LastUploadedItemId => -1;

            public TelemetryItem Enqueue(TelemetryItemDraft draft)
            {
                Interlocked.Increment(ref EnqueueAttempts);
                throw new InvalidOperationException("spool overflow (test)");
            }

            public Task<DrainResult> DrainAllAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(DrainResult.Empty());

            public void Dispose() { }
        }
    }
}
