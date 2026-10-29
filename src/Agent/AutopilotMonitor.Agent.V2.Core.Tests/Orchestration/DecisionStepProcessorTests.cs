using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class DecisionStepProcessorTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Rig wires the processor against in-memory fakes + a real <see cref="DecisionEngine"/>
        /// so we get genuine <see cref="DecisionStep"/> instances (ctor-validated transition +
        /// effects) without duplicating the reducer shape in test builders.
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeJournalWriter Journal { get; } = new FakeJournalWriter();
            public FakeEffectRunner Effects { get; } = new FakeEffectRunner();
            public FakeSnapshotPersistence Snapshot { get; } = new FakeSnapshotPersistence();
            public FakeQuarantineSink Quarantine { get; } = new FakeQuarantineSink();
            public DecisionEngine Engine { get; } = new DecisionEngine();
            public DecisionState InitialState { get; } = DecisionState.CreateInitial("S1", "T1");

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
            }

            public DecisionStepProcessor Build(int threshold = DecisionStepProcessor.DefaultQuarantineThreshold) =>
                new DecisionStepProcessor(
                    initialState: InitialState,
                    journal: Journal,
                    effectRunner: Effects,
                    snapshot: Snapshot,
                    quarantineSink: Quarantine,
                    logger: Logger,
                    quarantineThreshold: threshold);

            /// <summary>Builds a real DecisionStep by Reducing SessionStarted against the current state.</summary>
            public (DecisionStep step, DecisionSignal signal) ReduceSessionStarted(long ordinal = 0)
            {
                var signal = TestSignals.Raw(ordinal, DecisionSignalKind.SessionStarted, At);
                var step = Engine.Reduce(InitialState, signal);
                return (step, signal);
            }

            /// <summary>Reduces SessionAborted — a stage change (SessionStarted → Failed) that H1a always snapshots.</summary>
            public (DecisionStep step, DecisionSignal signal) ReduceAbort(long ordinal = 0)
            {
                var signal = TestSignals.Raw(ordinal, DecisionSignalKind.SessionAborted, At);
                var step = Engine.Reduce(InitialState, signal);
                return (step, signal);
            }

            /// <summary>
            /// Reduces an InformationalEvent pass-through (stage unchanged, only a telemetry
            /// EmitEventTimelineEntry effect) from the processor's CURRENT state — the high-frequency
            /// step class H1a skips for snapshots.
            /// </summary>
            public (DecisionStep step, DecisionSignal signal) ReduceInformational(DecisionStepProcessor sut, long ordinal)
            {
                var payload = new Dictionary<string, string>
                {
                    [SignalPayloadKeys.EventType] = "download_progress",
                    [SignalPayloadKeys.Source] = "DeliveryOptimizationCollector",
                };
                var signal = TestSignals.Raw(ordinal, DecisionSignalKind.InformationalEvent, At, payload: payload);
                var step = Engine.Reduce(sut.CurrentState, signal);
                return (step, signal);
            }

            public void Dispose() => Tmp.Dispose();
        }

        // ========================================================================= Ctor

        [Fact]
        public void Ctor_rejects_null_dependencies()
        {
            using var rig = new Rig();
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(null!, rig.Journal, rig.Effects, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, null!, rig.Effects, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, null!, rig.Snapshot, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, null!, rig.Quarantine, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, rig.Snapshot, null!, rig.Logger));
            Assert.Throws<ArgumentNullException>(() =>
                new DecisionStepProcessor(rig.InitialState, rig.Journal, rig.Effects, rig.Snapshot, rig.Quarantine, null!));
        }

        [Fact]
        public void Ctor_rejects_non_positive_threshold()
        {
            using var rig = new Rig();
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Build(threshold: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => rig.Build(threshold: -1));
        }

        [Fact]
        public void CurrentState_starts_at_initial_state()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            Assert.Same(rig.InitialState, sut.CurrentState);
        }

        // ========================================================================= Happy path

        [Fact]
        public void ApplyStep_happy_path_appends_journal_runs_effects_forwards_state()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.Transition, rig.Journal.Appended[0]);
            Assert.Equal(1, rig.Effects.CallCount);
            Assert.Same(step.NewState, rig.Effects.Calls[0].State);
            Assert.Equal(signal.OccurredAtUtc, rig.Effects.Calls[0].OccurredAtUtc);
            // H1a: SessionStarted is a pure pass-through (stage unchanged, no effects) → the
            // snapshot cache is intentionally NOT written; recovery replays this trivially from
            // the SignalLog. The authoritative writes (journal above) still happen per step.
            Assert.Empty(rig.Snapshot.Saved);
            Assert.Same(step.NewState, sut.CurrentState);
            Assert.Equal(0, sut.ConsecutiveJournalFailureCount);
            Assert.Empty(rig.Quarantine.Reasons);
        }

        // ========================================================================= Snapshot policy (H1a)

        [Fact]
        public void ApplyStep_snapshots_on_stage_change()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceAbort(); // SessionStarted -> Failed

            sut.ApplyStep(step, signal);

            Assert.Single(rig.Snapshot.Saved);
            Assert.Same(step.NewState, rig.Snapshot.Saved[0]);
        }

        [Fact]
        public void ApplyStep_skips_snapshot_for_informational_pass_throughs_until_floor()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            // SnapshotPassThroughFloor consecutive pass-throughs → none snapshotted.
            for (long i = 0; i < DecisionStepProcessor.SnapshotPassThroughFloor; i++)
            {
                var (step, signal) = rig.ReduceInformational(sut, i);
                sut.ApplyStep(step, signal);
            }
            Assert.Equal(0, rig.Snapshot.SaveCallCount);

            // The next pass-through crosses the floor → exactly one forced snapshot (bounds the
            // worst-case restart replay tail), then the counter resets.
            var (s2, sig2) = rig.ReduceInformational(sut, DecisionStepProcessor.SnapshotPassThroughFloor);
            sut.ApplyStep(s2, sig2);
            Assert.Equal(1, rig.Snapshot.SaveCallCount);
        }

        // ========================================================================= Journal failures

        [Fact]
        public void ApplyStep_journal_fails_once_does_not_trigger_quarantine()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("disk-hiccup"));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));

            Assert.Equal(1, sut.ConsecutiveJournalFailureCount);
            Assert.Empty(rig.Quarantine.Reasons);
            Assert.Equal(0, rig.Effects.CallCount);   // no effects run after append fails
            Assert.Empty(rig.Snapshot.Saved);
            Assert.Same(rig.InitialState, sut.CurrentState);   // state NOT advanced
        }

        [Fact]
        public void ApplyStep_journal_fails_threshold_times_triggers_quarantine_once()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("disk-full"), count: 5);
            var sut = rig.Build(threshold: 3);
            var (step, signal) = rig.ReduceSessionStarted();

            // Failures 1 + 2 must not quarantine.
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Empty(rig.Quarantine.Reasons);

            // Failure 3 crosses the threshold.
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Single(rig.Quarantine.Reasons);
            Assert.Contains("journal append failed", rig.Quarantine.Reasons[0]);
            Assert.Contains("3x", rig.Quarantine.Reasons[0]);

            // Failure 4 must not re-trigger quarantine (fire-once).
            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Single(rig.Quarantine.Reasons);

            Assert.Same(rig.InitialState, sut.CurrentState);
        }

        [Fact]
        public void ApplyStep_journal_recovers_after_transient_failure_resets_counter()
        {
            using var rig = new Rig();
            rig.Journal.ScriptThrow(new InvalidOperationException("blip")).ScriptOk();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<InvalidOperationException>(() => sut.ApplyStep(step, signal));
            Assert.Equal(1, sut.ConsecutiveJournalFailureCount);

            sut.ApplyStep(step, signal);   // succeeds second time
            Assert.Equal(0, sut.ConsecutiveJournalFailureCount);
            Assert.Same(step.NewState, sut.CurrentState);
        }

        // ========================================================================= Snapshot best-effort

        [Fact]
        public void ApplyStep_snapshot_fails_does_not_fail_step()
        {
            using var rig = new Rig();
            rig.Snapshot.ScriptThrow(new InvalidOperationException("snapshot-disk-full"));
            var sut = rig.Build();
            // Use a stage-changing step so the snapshot actually runs (H1a skips pass-throughs).
            var (step, signal) = rig.ReduceAbort();

            sut.ApplyStep(step, signal);   // does NOT throw

            Assert.Single(rig.Journal.Appended);
            Assert.Equal(1, rig.Effects.CallCount);
            Assert.Same(step.NewState, sut.CurrentState);   // state still advanced
            Assert.Empty(rig.Quarantine.Reasons);
        }

        // ========================================================================= EffectRunner behavior

        [Fact]
        public void ApplyStep_effect_runner_SessionMustAbort_skips_snapshot_and_returns_result()
        {
            // Codex follow-up (post-#50 #B): when the effects signal SessionMustAbort,
            // the processor MUST NOT snapshot the phantom state — a crash-and-recovery from
            // that snapshot would re-arm the same failed deadline and hang the session.
            // The caller (SignalIngress) sees the abort flag on the returned result and
            // durably synthesises a terminal EffectInfrastructureFailure signal inline.
            using var rig = new Rig();
            var abortResult = new EffectRunResult(
                sessionMustAbort: true,
                abortReason: "timer_infrastructure_failure: timer-broken",
                failures: Array.Empty<AutopilotMonitor.Agent.V2.Core.Orchestration.EffectFailure>(),
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0);
            rig.Effects.ScriptResult(abortResult);

            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            var result = sut.ApplyStep(step, signal);

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.NewState, sut.CurrentState);   // state forwards so the synthetic signal reduces from here
            Assert.Equal(0, rig.Snapshot.SaveCallCount);     // phantom snapshot suppressed
            Assert.True(result.SessionMustAbort);
            Assert.Equal(abortResult.AbortReason, result.AbortReason);
            Assert.Empty(rig.Quarantine.Reasons);
        }

        [Fact]
        public void ApplyStep_effect_runner_unexpected_throw_logged_but_step_committed()
        {
            using var rig = new Rig();
            rig.Effects.ScriptThrow(new InvalidOperationException("unexpected"));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            sut.ApplyStep(step, signal);   // swallowed

            Assert.Single(rig.Journal.Appended);
            Assert.Same(step.NewState, sut.CurrentState);
        }

        // ========================================================================= Step log levels

        [Fact]
        public void ApplyStep_logs_a_stage_change_at_info()
        {
            using var rig = new Rig();
            rig.Logger.SetLogLevel(AgentLogLevel.Trace);
            var sut = rig.Build();
            var (step, signal) = rig.ReduceAbort(); // SessionStarted -> Failed

            sut.ApplyStep(step, signal);

            Assert.Contains(StepLines(rig), l => l.Contains("[INFO] DecisionStep:") && l.Contains("stage=SessionStarted->Failed"));
        }

        [Fact]
        public void ApplyStep_logs_informational_pass_through_and_no_op_at_trace()
        {
            using var rig = new Rig();
            rig.Logger.SetLogLevel(AgentLogLevel.Trace);
            var sut = rig.Build();

            var (noOp, noOpSignal) = rig.ReduceSessionStarted(ordinal: 0);
            sut.ApplyStep(noOp, noOpSignal);
            var (info, infoSignal) = rig.ReduceInformational(sut, ordinal: 1);
            sut.ApplyStep(info, infoSignal);

            var lines = StepLines(rig);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, l => Assert.Contains("[TRACE] DecisionStep:", l));
            Assert.Contains("(no-op)", lines[0]);
            Assert.Contains("kind=InformationalEvent", lines[1]);
        }

        [Fact]
        public void ApplyStep_logs_informational_pass_through_with_failed_effect_at_debug()
        {
            using var rig = new Rig();
            rig.Logger.SetLogLevel(AgentLogLevel.Trace);
            rig.Effects.ScriptResult(new EffectRunResult(
                sessionMustAbort: false,
                abortReason: null,
                failures: new[] { new EffectFailure(DecisionEffectKind.EmitEventTimelineEntry, "emit failed", isTransient: false, exhaustedRetries: true) },
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0));
            var sut = rig.Build();
            var (step, signal) = rig.ReduceInformational(sut, ordinal: 0);

            sut.ApplyStep(step, signal);

            Assert.Contains(StepLines(rig), l => l.Contains("[DEBUG] DecisionStep:") && l.Contains("failures=1"));
        }

        private static string[] StepLines(Rig rig) =>
            System.IO.File.ReadAllLines(System.IO.Path.Combine(rig.Tmp.Path, "agent.log"))
                .Where(l => l.Contains("DecisionStep:"))
                .ToArray();

        // ========================================================================= Argument validation

        [Fact]
        public void ApplyStep_rejects_null_step_or_signal()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            var (step, signal) = rig.ReduceSessionStarted();

            Assert.Throws<ArgumentNullException>(() => sut.ApplyStep(null!, signal));
            Assert.Throws<ArgumentNullException>(() => sut.ApplyStep(step, null!));
        }
    }
}
