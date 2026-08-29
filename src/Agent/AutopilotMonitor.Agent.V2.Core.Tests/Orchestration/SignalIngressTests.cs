using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

// xUnit1031: Task.Wait / ManualResetEventSlim.Wait are used here intentionally to drive
// deterministic multi-thread scenarios (producer is blocked inside BlockingCollection.Add by
// design; switching to async/await would not match the semantics under test).
#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    [Collection("SerialThreading")]
    public sealed class SignalIngressTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private static Evidence RawEvidence(string id = "raw-1") =>
            new Evidence(EvidenceKind.Raw, id, $"evidence-{id}");

        /// <summary>
        /// Konstruktionshelfer — baut eine SignalIngress gegen In-Memory-Writer + Fake-Engine.
        /// </summary>
        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public SignalLogWriter SignalLog { get; }
            public SessionTraceOrdinalProvider TraceCounter { get; } = new SessionTraceOrdinalProvider();
            public FakeDecisionStepProcessor Processor { get; } = new FakeDecisionStepProcessor();
            public FakeBackPressureObserver Observer { get; } = new FakeBackPressureObserver();
            public VirtualClock Clock { get; } = new VirtualClock(new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc));
            public DecisionEngine Engine { get; } = new DecisionEngine();

            public Rig()
            {
                SignalLog = new SignalLogWriter(Tmp.File("signal-log.jsonl"));
            }

            public SignalIngress Build(int channelCapacity = 256, bool wireObserver = true, TimeSpan? throttle = null) =>
                new SignalIngress(
                    engine: Engine,
                    signalLog: SignalLog,
                    traceCounter: TraceCounter,
                    processor: Processor,
                    clock: Clock,
                    backPressureObserver: wireObserver ? Observer : null,
                    channelCapacity: channelCapacity,
                    backPressureThrottle: throttle);

            public void Dispose() => Tmp.Dispose();
        }

        // 5000ms cushion: 2000ms was racy under ThreadPool contention from parallel test classes
        // even with [Collection("SerialThreading")] (timers on the same pool can still lag).
        // Plan §4.x M4.5.c.
        private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
        {
            return SpinWait.SpinUntil(condition, timeoutMs);
        }

        // ============================================================ Lifecycle

        [Fact]
        public void Start_twice_throws()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            Assert.Throws<InvalidOperationException>(() => ing.Start());
            ing.Stop();
        }

        [Fact]
        public void Stop_is_idempotent()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Stop();
            ing.Stop();   // does not throw
        }

        [Fact]
        public void Post_after_Stop_throws()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Stop();

            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));
        }

        [Fact]
        public void Dispose_stops_worker_and_releases_channel()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Dispose();

            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));
        }

        [Fact]
        public void Post_before_Start_queues_until_worker_runs()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            // Post before Start — item sits in the channel.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());
            Assert.Equal(0, rig.Processor.ApplyCallCount);

            ing.Start();
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));
            ing.Stop();
        }

        // ============================================================ Ordinals + SignalLog

        [Fact]
        public void Worker_assigns_monotonic_ordinals_from_zero_on_empty_log()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            for (int i = 0; i < 5; i++)
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence($"id-{i}"));
            }

            ing.Stop();

            var persisted = rig.SignalLog.ReadAll();
            Assert.Equal(5, persisted.Count);
            for (int i = 0; i < 5; i++) Assert.Equal(i, persisted[i].SessionSignalOrdinal);
            Assert.Equal(4, rig.SignalLog.LastOrdinal);
            Assert.Equal(4, ing.LastAssignedSignalOrdinal);
        }

        [Fact]
        public void Worker_seeds_ordinal_from_existing_SignalLog_on_recovery()
        {
            using var tmp = new TempDirectory();
            var logPath = tmp.File("signal-log.jsonl");

            // Pre-populate log with 3 signals, ordinals 0..2.
            var pre = new SignalLogWriter(logPath);
            for (int i = 0; i < 3; i++)
            {
                pre.Append(new DecisionSignal(
                    sessionSignalOrdinal: i,
                    sessionTraceOrdinal: i,
                    kind: DecisionSignalKind.SessionStarted,
                    kindSchemaVersion: 1,
                    occurredAtUtc: At,
                    sourceOrigin: "Seed",
                    evidence: RawEvidence($"pre-{i}")));
            }

            var recovered = new SignalLogWriter(logPath);
            var ing = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: recovered,
                traceCounter: new SessionTraceOrdinalProvider(2),
                processor: new FakeDecisionStepProcessor(),
                clock: new VirtualClock(At));
            ing.Start();

            ing.Post(DecisionSignalKind.DesktopArrived, At, "Collector", RawEvidence("fresh"));
            ing.Stop();

            Assert.Equal(3, ing.LastAssignedSignalOrdinal);
            var all = recovered.ReadAll();
            Assert.Equal(4, all.Count);
            Assert.Equal(3, all[3].SessionSignalOrdinal);
            Assert.Equal(DecisionSignalKind.DesktopArrived, all[3].Kind);
        }

        [Fact]
        public void SignalLog_append_happens_before_processor_ApplyStep()
        {
            // §2.7c / L.1: signal must be on disk before the reducer sees it.
            using var rig = new Rig();
            DecisionSignal? observedAtApply = null;
            long logOrdinalAtApply = -1;

            rig.Processor.ScriptOk(1);   // dummy to exercise the queue
            var processorSpy = new FakeDecisionStepProcessor();

            var ing = new SignalIngress(
                engine: rig.Engine,
                signalLog: rig.SignalLog,
                traceCounter: rig.TraceCounter,
                processor: new ApplyInspectingProcessor(rig.SignalLog, (step, signal, logAtApply) =>
                {
                    observedAtApply = signal;
                    logOrdinalAtApply = logAtApply;
                }),
                clock: rig.Clock,
                backPressureObserver: rig.Observer);
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());
            ing.Stop();

            Assert.NotNull(observedAtApply);
            Assert.Equal(0, observedAtApply!.SessionSignalOrdinal);
            Assert.Equal(0, logOrdinalAtApply);   // bereits appendet beim ApplyStep-Call
        }

        private sealed class ApplyInspectingProcessor : IDecisionStepProcessor
        {
            private readonly ISignalLogWriter _log;
            private readonly Action<DecisionStep, DecisionSignal, long> _inspect;
            private DecisionCore.State.DecisionState _state = DecisionCore.State.DecisionState.CreateInitial("S1", "T1");

            public ApplyInspectingProcessor(ISignalLogWriter log, Action<DecisionStep, DecisionSignal, long> inspect)
            {
                _log = log;
                _inspect = inspect;
            }

            public DecisionCore.State.DecisionState CurrentState => _state;

            public EffectRunResult ApplyStep(DecisionStep step, DecisionSignal signal)
            {
                _inspect(step, signal, _log.LastOrdinal);
                _state = step.NewState;
                return EffectRunResult.Empty();
            }
        }

        // ============================================================ Reducer dispatch

        [Fact]
        public void ApplyStep_receives_state_at_step_begin_and_advances_for_next_signal()
        {
            // Reducer dispatches SessionStarted → state with Stage=EnrollmentStarted (or similar).
            // Second signal should see updated state from first.
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            ing.Stop();

            Assert.Equal(2, rig.Processor.ApplyCallCount);
            var calls = rig.Processor.Calls;
            Assert.Equal(0, calls[0].Signal.SessionSignalOrdinal);
            Assert.Equal(1, calls[1].Signal.SessionSignalOrdinal);

            // StepIndex monoton.
            Assert.True(calls[1].Step.NewState.StepIndex > calls[0].Step.NewState.StepIndex);
        }

        [Fact]
        public void ApplyStep_exception_does_not_kill_worker_subsequent_signals_still_processed()
        {
            using var rig = new Rig();
            rig.Processor.ScriptThrow(new InvalidOperationException("journal-disk-full"));
            var ing = rig.Build();
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            // First ApplyStep throws — worker must survive.
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 1));

            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));
            ing.Stop();

            Assert.Equal(2, rig.SignalLog.ReadAll().Count);
        }

        // ============================================================ Codex follow-up post-#50 #B — durable inline abort

        [Fact]
        public void SessionMustAbort_synthesises_EffectInfrastructureFailure_signal_durably_before_worker_returns()
        {
            // When the processor returns SessionMustAbort=true, the ingress must:
            //  (1) append a synthetic EffectInfrastructureFailure signal to the SignalLog,
            //  (2) invoke the processor with that signal a second time (terminal transition),
            // all within the current worker iteration — so a crash after the iteration
            // returns can recover to a terminal state, not a phantom deadline state.
            //
            // The synthetic signal MUST match the v1 payload contract defined on
            // DecisionSignalKind.EffectInfrastructureFailure (Codex follow-up post-#50 #F):
            // { reason, failingEffect }, sourceOrigin "effectrunner:critical:<EffectKind>",
            // Evidence.Identifier "effect_infrastructure_failure:<EffectKind>".
            using var rig = new Rig();
            var critical = new EffectFailure(
                effectKind: DecisionEffectKind.ScheduleDeadline,
                errorReason: "timer-broken",
                isTransient: false,
                exhaustedRetries: false);
            var abort = new EffectRunResult(
                sessionMustAbort: true,
                abortReason: "timer_infrastructure_failure: timer-broken",
                failures: new[] { critical },
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0);
            rig.Processor.ScriptResult(abort);

            var ing = rig.Build();
            ing.Start();
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("original"));

            // Wait for BOTH the original apply and the synthetic abort apply to land.
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount >= 2, timeoutMs: 5000));
            ing.Stop();

            // SignalLog has [original, synthetic] — both durable, monotone ordinals.
            var logged = rig.SignalLog.ReadAll();
            Assert.Equal(2, logged.Count);
            Assert.Equal(DecisionSignalKind.SessionStarted, logged[0].Kind);
            Assert.Equal(DecisionSignalKind.EffectInfrastructureFailure, logged[1].Kind);
            Assert.Equal(0L, logged[0].SessionSignalOrdinal);
            Assert.Equal(1L, logged[1].SessionSignalOrdinal);

            // v1 contract: payload carries reason AND failingEffect.
            Assert.NotNull(logged[1].Payload);
            Assert.Equal(abort.AbortReason, logged[1].Payload!["reason"]);
            Assert.Equal("ScheduleDeadline", logged[1].Payload!["failingEffect"]);
            // sourceOrigin + evidence discriminate between ScheduleDeadline and CancelDeadline
            // failures so forensic parsers (and the v1 fixture file) see a consistent shape.
            Assert.Equal("effectrunner:critical:ScheduleDeadline", logged[1].SourceOrigin);
            Assert.Equal("effect_infrastructure_failure:ScheduleDeadline", logged[1].Evidence.Identifier);

            // Processor received both signals, in order, with matching signals.
            var calls = rig.Processor.Calls;
            Assert.True(calls.Count >= 2);
            Assert.Equal(DecisionSignalKind.SessionStarted, calls[0].Signal.Kind);
            Assert.Equal(DecisionSignalKind.EffectInfrastructureFailure, calls[1].Signal.Kind);
        }

        [Fact]
        public void SessionMustAbort_CancelDeadline_failure_labels_signal_with_CancelDeadline_kind()
        {
            // Same contract as the ScheduleDeadline case but for the CancelDeadline variant,
            // so forensic queries can distinguish the two failure modes by sourceOrigin alone.
            using var rig = new Rig();
            var critical = new EffectFailure(
                effectKind: DecisionEffectKind.CancelDeadline,
                errorReason: "cancel-failed",
                isTransient: false,
                exhaustedRetries: false);
            var abort = new EffectRunResult(
                sessionMustAbort: true,
                abortReason: "timer_infrastructure_failure: cancel-failed",
                failures: new[] { critical },
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0);
            rig.Processor.ScriptResult(abort);

            var ing = rig.Build();
            ing.Start();
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("original"));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount >= 2, timeoutMs: 5000));
            ing.Stop();

            var logged = rig.SignalLog.ReadAll();
            Assert.Equal(2, logged.Count);
            Assert.Equal("CancelDeadline", logged[1].Payload!["failingEffect"]);
            Assert.Equal("effectrunner:critical:CancelDeadline", logged[1].SourceOrigin);
            Assert.Equal("effect_infrastructure_failure:CancelDeadline", logged[1].Evidence.Identifier);
        }

        [Fact]
        public void Synthetic_abort_signal_itself_reporting_abort_does_not_recurse()
        {
            // Defence-in-depth against infinite recursion: if ApplyStep for the synthetic
            // EffectInfrastructureFailure signal were (wrongly) to report another abort,
            // ingress must NOT produce a third synthetic signal — the reducer's terminal
            // handler is trusted as the end of the abort chain.
            using var rig = new Rig();
            var abortEverything = new EffectRunResult(
                sessionMustAbort: true,
                abortReason: "simulated: every step aborts",
                failures: Array.Empty<EffectFailure>(),
                classifierInvocations: 0,
                classifierSkippedByAntiLoop: 0);
            // Two scripted abort results: one for the original signal, one for the
            // synthetic. The third call (if any) would use the default Empty result.
            rig.Processor.ScriptResult(abortEverything, count: 2);

            var ing = rig.Build();
            ing.Start();
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("original"));

            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount >= 2, timeoutMs: 5000));
            // Give the worker a moment to potentially recurse (it must not).
            Thread.Sleep(150);
            ing.Stop();

            // Exactly 2 apply calls — original + one synthetic. No recursion.
            Assert.Equal(2, rig.Processor.ApplyCallCount);
            Assert.Equal(2, rig.SignalLog.ReadAll().Count);
        }

        // ============================================================ Codex Finding 4 — SignalPosted

        [Fact]
        public void SignalPosted_fires_for_every_successful_post_regardless_of_kind()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            var observed = new List<(DecisionSignalKind Kind, string? EventType)>();
            ing.SignalPosted += (kind, payload) =>
            {
                string? eventType = null;
                payload?.TryGetValue("eventType", out eventType);
                lock (observed) observed.Add((kind, eventType));
            };

            // Cross-source simulation: different InformationalEventPost instances all hitting
            // the same ingress. Before Finding 4's fix, only one instance's posts would reach
            // the observer; now all of them do because the event lives on the ingress itself.
            ISignalIngressSink sink = ing;
            var postA = new InformationalEventPost(sink, rig.Clock);
            var postB = new InformationalEventPost(sink, rig.Clock);

            postA.Emit(eventType: "esp_phase_changed", source: "EspAndHelloTracker");
            postB.Emit(eventType: "ime_user_session_completed", source: "ImeLogTracker");
            sink.Post(
                DecisionSignalKind.ClassifierVerdictIssued,
                At,
                "effectrunner:classifier:whiteglove_sealing",
                new Evidence(EvidenceKind.Synthetic, "classifier:abc", "Strong/85"),
                new Dictionary<string, string> { ["level"] = "Strong" });

            ing.Stop();

            Assert.Equal(3, observed.Count);
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.InformationalEvent && o.EventType == "esp_phase_changed");
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.InformationalEvent && o.EventType == "ime_user_session_completed");
            Assert.Contains(observed, o => o.Kind == DecisionSignalKind.ClassifierVerdictIssued);
        }

        [Fact]
        public void SignalPosted_handler_exception_does_not_break_ingress()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            ing.SignalPosted += (_, _) => throw new InvalidOperationException("observer bug");

            // The post MUST succeed and the signal MUST still reach the reducer — observer
            // exceptions are advisory-only.
            ing.Post(
                DecisionSignalKind.SessionStarted,
                At,
                "test",
                new Evidence(EvidenceKind.Synthetic, "session-start", "first signal"));

            ing.Stop();

            Assert.Single(rig.Processor.Calls);
        }

        [Fact]
        public void SignalPosted_is_raised_even_when_channel_took_slow_path()
        {
            // The back-pressured slow path (BlockingCollection.Add after TryAdd fails) must
            // still trigger the observer so the idle clock is reset under load.
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);
            ing.Start();

            var observedCount = 0;
            ing.SignalPosted += (_, _) => Interlocked.Increment(ref observedCount);

            for (int i = 0; i < 5; i++)
            {
                ing.Post(
                    DecisionSignalKind.SessionStarted,
                    At,
                    "test",
                    new Evidence(EvidenceKind.Synthetic, $"signal-{i}", "slow-path probe"));
            }

            ing.Stop();

            Assert.Equal(5, observedCount);
        }

        // ============================================================ Worker re-entrancy (2026-08-29)

        [Fact]
        public void Worker_thread_post_with_full_channel_does_not_deadlock_and_is_processed_next()
        {
            // Channel capacity 1, the worker is parked inside ApplyStep on a gate while a
            // second producer keeps the channel full. When the gate opens, ApplyStep posts a
            // verdict from the worker thread. Before the fix, Post would block in
            // BlockingCollection.Add forever (the worker is the only consumer). After the fix,
            // the verdict goes to the worker-local queue, is processed BEFORE the queued channel
            // item, and the worker keeps draining.
            using var tmp = new TempDirectory();
            var log = new SignalLogWriter(tmp.File("signal-log.jsonl"));
            SignalIngress? ingRef = null;
            var gate = new ManualResetEventSlim(false);
            var inApply = new ManualResetEventSlim(false);
            var processor = new GatedReentrantProcessor(() => ingRef!, gate, inApply);
            var ing = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: log,
                traceCounter: new SessionTraceOrdinalProvider(),
                processor: processor,
                clock: new VirtualClock(At),
                channelCapacity: 1);
            ingRef = ing;
            ing.Start();

            // Item 0 — worker takes it and parks inside ApplyStep on the gate.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            Assert.True(inApply.Wait(5000));

            // Item 1 fills the single slot; item 2 blocks a foreign producer (channel full).
            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            var blockedProducer = Task.Run(() =>
                ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(2), "Collector", RawEvidence("s2")));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            Assert.False(blockedProducer.Wait(200));   // genuinely blocked by back-pressure

            // Release the worker: ApplyStep(s0) now posts the verdict from the worker thread
            // into a full channel.
            gate.Set();

            Assert.True(blockedProducer.Wait(5000), "foreign producer must be released — worker must keep draining");
            Assert.True(WaitFor(() => processor.CallCount == 4));
            ing.Stop();

            Assert.Equal(
                new[]
                {
                    DecisionSignalKind.SessionStarted,
                    DecisionSignalKind.ClassifierVerdictIssued,   // follow-up drained BEFORE the next channel item
                    DecisionSignalKind.DesktopArrived,
                    DecisionSignalKind.DesktopArrived,
                },
                processor.Kinds);
            Assert.Equal(1, ing.ReentrantPostCount);
            Assert.Equal(0, ing.PendingSignalCount);
            // Same worker thread for every step — single-writer invariant intact.
            Assert.Single(processor.ThreadIds.Distinct());
            // Follow-up went through the normal path: ordinal assigned + durably logged.
            var logged = log.ReadAll();
            Assert.Equal(4, logged.Count);
            Assert.Equal(DecisionSignalKind.ClassifierVerdictIssued, logged[1].Kind);
            Assert.Equal(1, logged[1].SessionSignalOrdinal);
        }

        [Fact]
        public void Worker_thread_post_chain_is_drained_iteratively_in_order()
        {
            // A follow-up whose own ApplyStep posts another follow-up: both are processed on
            // the worker, in order, before the next channel item, without recursion.
            using var tmp = new TempDirectory();
            var log = new SignalLogWriter(tmp.File("signal-log.jsonl"));
            SignalIngress? ingRef = null;
            var processor = new ChainingProcessor(() => ingRef!);
            var ing = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: log,
                traceCounter: new SessionTraceOrdinalProvider(),
                processor: processor,
                clock: new VirtualClock(At));
            ingRef = ing;
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            ing.Post(DecisionSignalKind.DesktopArrived, At.AddSeconds(1), "Collector", RawEvidence("s1"));
            ing.Stop();

            Assert.Equal(
                new[] { "Collector", "chain:1", "chain:2", "Collector" },
                processor.Origins);
            Assert.Equal(2, ing.ReentrantPostCount);
            Assert.Equal(4, log.ReadAll().Count);
        }

        [Fact]
        public void Worker_thread_post_during_Stop_drain_is_still_processed()
        {
            // Stop() completes adding, but a verdict posted by the worker while it finishes
            // the last channel item must not be dropped — it belongs to that step.
            using var tmp = new TempDirectory();
            var log = new SignalLogWriter(tmp.File("signal-log.jsonl"));
            SignalIngress? ingRef = null;
            var gate = new ManualResetEventSlim(false);
            var inApply = new ManualResetEventSlim(false);
            var processor = new GatedReentrantProcessor(() => ingRef!, gate, inApply);
            var ing = new SignalIngress(
                engine: new DecisionEngine(),
                signalLog: log,
                traceCounter: new SessionTraceOrdinalProvider(),
                processor: processor,
                clock: new VirtualClock(At));
            ingRef = ing;
            ing.Start();

            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("s0"));
            Assert.True(inApply.Wait(5000));
            var stopTask = Task.Run(() => ing.Stop());
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0 && !stopTask.IsCompleted));
            gate.Set();
            Assert.True(stopTask.Wait(5000));

            Assert.Equal(
                new[] { DecisionSignalKind.SessionStarted, DecisionSignalKind.ClassifierVerdictIssued },
                processor.Kinds);
            Assert.Equal(2, log.ReadAll().Count);
        }

        /// <summary>
        /// Parks inside ApplyStep(SessionStarted) on a gate, then posts a verdict back into the
        /// ingress from the worker thread — the EffectRunner/RunClassifier shape.
        /// </summary>
        private sealed class GatedReentrantProcessor : IDecisionStepProcessor
        {
            private readonly Func<ISignalIngressSink> _sink;
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _inApply;
            private readonly List<(DecisionSignalKind Kind, int ThreadId)> _calls = new List<(DecisionSignalKind, int)>();
            private DecisionCore.State.DecisionState _state = DecisionCore.State.DecisionState.CreateInitial("S1", "T1");

            public GatedReentrantProcessor(Func<ISignalIngressSink> sink, ManualResetEventSlim gate, ManualResetEventSlim inApply)
            {
                _sink = sink;
                _gate = gate;
                _inApply = inApply;
            }

            public int CallCount { get { lock (_calls) { return _calls.Count; } } }
            public DecisionSignalKind[] Kinds { get { lock (_calls) { return _calls.Select(c => c.Kind).ToArray(); } } }
            public int[] ThreadIds { get { lock (_calls) { return _calls.Select(c => c.ThreadId).ToArray(); } } }

            public DecisionCore.State.DecisionState CurrentState => _state;

            public EffectRunResult ApplyStep(DecisionStep step, DecisionSignal signal)
            {
                lock (_calls) { _calls.Add((signal.Kind, Thread.CurrentThread.ManagedThreadId)); }
                _state = step.NewState;
                if (signal.Kind == DecisionSignalKind.SessionStarted)
                {
                    _inApply.Set();
                    _gate.Wait();
                    _sink().Post(
                        DecisionSignalKind.ClassifierVerdictIssued,
                        signal.OccurredAtUtc,
                        "effectrunner:classifier:test",
                        new Evidence(EvidenceKind.Synthetic, "classifier:test:h", "Strong/90"),
                        new Dictionary<string, string> { ["level"] = "Strong" });
                }
                return EffectRunResult.Empty();
            }
        }

        private sealed class ChainingProcessor : IDecisionStepProcessor
        {
            private readonly Func<ISignalIngressSink> _sink;
            private DecisionCore.State.DecisionState _state = DecisionCore.State.DecisionState.CreateInitial("S1", "T1");
            public List<string> Origins { get; } = new List<string>();

            public ChainingProcessor(Func<ISignalIngressSink> sink) { _sink = sink; }

            public DecisionCore.State.DecisionState CurrentState => _state;

            public EffectRunResult ApplyStep(DecisionStep step, DecisionSignal signal)
            {
                Origins.Add(signal.SourceOrigin);
                _state = step.NewState;
                string? next = signal.Kind == DecisionSignalKind.SessionStarted ? "chain:1"
                    : signal.SourceOrigin == "chain:1" ? "chain:2"
                    : null;
                if (next != null)
                {
                    _sink().Post(
                        DecisionSignalKind.ClassifierVerdictIssued,
                        signal.OccurredAtUtc,
                        next,
                        new Evidence(EvidenceKind.Synthetic, next, next));
                }
                return EffectRunResult.Empty();
            }
        }

        [Fact]
        public void Post_via_ISignalIngressSink_reaches_reducer_like_any_signal()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ISignalIngressSink sink = ing;
            ing.Start();

            sink.Post(
                DecisionSignalKind.ClassifierVerdictIssued,
                At,
                "effectrunner:classifier:whiteglove_sealing",
                new Evidence(EvidenceKind.Synthetic, "classifier:whiteglove_sealing:abc", "Strong/85"),
                new Dictionary<string, string> { ["level"] = "Strong" });

            ing.Stop();

            Assert.Single(rig.Processor.Calls);
            Assert.Equal(DecisionSignalKind.ClassifierVerdictIssued, rig.Processor.Calls[0].Signal.Kind);
            Assert.Equal("Strong", rig.Processor.Calls[0].Signal.Payload!["level"]);
        }

        [Fact]
        public void Post_null_evidence_throws_synchronously_without_enqueuing()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            Assert.Throws<ArgumentNullException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", null!));

            ing.Stop();
            Assert.Empty(rig.SignalLog.ReadAll());
        }

        [Fact]
        public void Post_empty_origin_throws_synchronously()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            Assert.Throws<ArgumentException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "", RawEvidence()));

            ing.Stop();
        }

        // ============================================================ Back-pressure

        [Fact]
        public void Fast_path_does_not_notify_back_pressure()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 32);
            ing.Start();

            for (int i = 0; i < 20; i++)
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence($"id-{i}"));
            }
            ing.Stop();

            Assert.Equal(20, rig.Processor.ApplyCallCount);
            Assert.Equal(0, rig.Observer.CallCount);
        }

        /// <summary>
        /// Posts <paramref name="signalCount"/>-1 signals to fill the channel, then starts
        /// a producer task that will block on Add. Returns once the producer is highly
        /// likely blocked inside Add (task has started + signaled readiness + small sleep).
        /// </summary>
        private static Task SpawnBlockedProducer(SignalIngress ing, string origin, string evidenceId)
        {
            var producerRunning = new ManualResetEventSlim(false);
            var task = Task.Run(() =>
            {
                producerRunning.Set();   // task has been scheduled and is executing
                ing.Post(DecisionSignalKind.DesktopArrived, At, origin, new Evidence(EvidenceKind.Raw, evidenceId, $"ev-{evidenceId}"));
            });
            Assert.True(producerRunning.Wait(2000), "Producer task did not start within 2s");
            // Small grace period: giving the producer time to reach the blocking Add call.
            // After Set() the lambda still has to step into Post → TryAdd fails → Add blocks.
            Thread.Sleep(100);
            return task;
        }

        [Fact]
        public void Full_channel_triggers_back_pressure_observer_once()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);

            // Fill channel without running worker.
            ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence("seed"));
            Assert.Equal(1, ing.ApproximateQueueLength);

            // Producer that will block on Add().
            var producer = SpawnBlockedProducer(ing, "Collector", "blocked");

            // Let worker drain; producer's Add returns after one slot frees.
            ing.Start();
            Assert.True(producer.Wait(5000));
            ing.Stop();

            Assert.Equal(1, rig.Observer.CallCount);
            var call = rig.Observer.Calls[0];
            Assert.Equal("Collector", call.Origin);
            Assert.Equal(1, call.ChannelCapacity);
            Assert.True(call.BlockDuration >= TimeSpan.Zero);
        }

        [Fact]
        public void Back_pressure_is_throttled_within_window_per_origin()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, throttle: TimeSpan.FromMinutes(1));

            // --- Round 1: trigger one back-pressure event from "A".
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));

            Assert.Equal(1, rig.Observer.CallCount);

            // --- Round 2 (same origin, no clock advance): observer should NOT fire (throttled).
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a3"));
            // Wait until worker has pulled it and is blocked on the BlockHandle; channel empty.
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            // Fill channel then spawn blocked producer.
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a4"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "A", "a5");
            blocker.Set();
            Assert.True(t2.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 5));

            Assert.Equal(1, rig.Observer.CallCount);   // STILL 1 — throttle active.
            ing.Stop();
        }

        [Fact]
        public void Different_origins_do_not_share_back_pressure_throttle()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1);

            // Origin "A" triggers BP.
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));

            // Origin "B" triggers BP in the same throttle window.
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "B", RawEvidence("b1"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            ing.Post(DecisionSignalKind.SessionStarted, At, "B", RawEvidence("b2"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "B", "b3");
            blocker.Set();
            Assert.True(t2.Wait(5000));

            ing.Stop();

            Assert.Equal(2, rig.Observer.CallCount);
            Assert.Contains(rig.Observer.Calls, c => c.Origin == "A");
            Assert.Contains(rig.Observer.Calls, c => c.Origin == "B");
        }

        [Fact]
        public void Clock_advance_past_throttle_window_re_arms_emission()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, throttle: TimeSpan.FromMinutes(1));

            // Round 1 — BP for "A".
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            Assert.True(WaitFor(() => rig.Processor.ApplyCallCount == 2));
            Assert.Equal(1, rig.Observer.CallCount);

            // Advance past throttle window.
            rig.Clock.Advance(TimeSpan.FromMinutes(2));

            // Round 2 — same origin, should fire again.
            var blocker = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = blocker;
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a3"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 0));
            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a4"));
            Assert.True(WaitFor(() => ing.ApproximateQueueLength == 1));
            var t2 = SpawnBlockedProducer(ing, "A", "a5");
            blocker.Set();
            Assert.True(t2.Wait(5000));

            ing.Stop();
            Assert.Equal(2, rig.Observer.CallCount);
        }

        [Fact]
        public void Null_observer_is_supported_and_back_pressure_is_silent()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 1, wireObserver: false);

            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            ing.Stop();

            // Observer is null — verifying no NRE crashed the producer or worker.
            Assert.Equal(2, rig.Processor.ApplyCallCount);
        }

        [Fact]
        public void Observer_exception_does_not_halt_producer_or_worker()
        {
            using var rig = new Rig();
            rig.Observer.ThrowOnNotify = new InvalidOperationException("observer bug");
            var ing = rig.Build(channelCapacity: 1);

            ing.Post(DecisionSignalKind.SessionStarted, At, "A", RawEvidence("a1"));
            var t1 = SpawnBlockedProducer(ing, "A", "a2");
            ing.Start();
            Assert.True(t1.Wait(5000));
            ing.Stop();

            Assert.Equal(2, rig.Processor.ApplyCallCount);
            Assert.Equal(1, rig.Observer.CallCount);   // recorded despite the throw
        }

        // ============================================================ Ordering + stress

        [Fact]
        public async Task Concurrent_producers_result_in_strictly_monotonic_ordinals()
        {
            using var rig = new Rig();
            var ing = rig.Build(channelCapacity: 64);
            ing.Start();

            const int Producers = 8;
            const int PerProducer = 50;

            var tasks = new Task[Producers];
            for (int p = 0; p < Producers; p++)
            {
                int idx = p;
                tasks[p] = Task.Run(() =>
                {
                    for (int i = 0; i < PerProducer; i++)
                    {
                        ing.Post(DecisionSignalKind.SessionStarted, At, $"p{idx}", RawEvidence($"p{idx}-{i}"));
                    }
                });
            }

            await Task.WhenAll(tasks);
            ing.Stop();

            var persisted = rig.SignalLog.ReadAll();
            Assert.Equal(Producers * PerProducer, persisted.Count);

            // Strictly increasing ordinals, contiguous from 0.
            for (int i = 0; i < persisted.Count; i++)
            {
                Assert.Equal(i, persisted[i].SessionSignalOrdinal);
            }
            // Trace ordinals must be strictly increasing (not necessarily = signal ordinal).
            var traces = persisted.Select(s => s.SessionTraceOrdinal).ToArray();
            for (int i = 1; i < traces.Length; i++)
            {
                Assert.True(traces[i] > traces[i - 1]);
            }
        }

        // ============================================================ Codex Finding 2 —
        // PendingSignalCount: posted-but-not-yet-fully-processed counter used by the
        // termination-handler off-worker drain.

        [Fact]
        public void PendingSignalCount_is_zero_when_idle()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            try
            {
                Assert.Equal(0L, ing.PendingSignalCount);
            }
            finally { ing.Stop(); }
        }

        [Fact]
        public void PendingSignalCount_returns_to_zero_after_post_is_processed()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            try
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());

                // The worker is async — give it room to fully process and decrement.
                Assert.True(WaitFor(() => ing.PendingSignalCount == 0L),
                    "PendingSignalCount did not return to 0 after post was processed.");
            }
            finally { ing.Stop(); }
        }

        [Fact]
        public void PendingSignalCount_observes_in_flight_item()
        {
            using var rig = new Rig();
            // Block ApplyStep on a gate so we can observe a non-zero pending count while
            // the worker is mid-process.
            var gate = new ManualResetEventSlim(false);
            rig.Processor.BlockHandle = gate;

            var ing = rig.Build();
            ing.Start();
            try
            {
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence());
                // The worker has either dequeued and is waiting on gate, or is about to —
                // both states are observable as PendingSignalCount >= 1 because the Post
                // increment happens before the channel.Add publishes the item.
                Assert.True(WaitFor(() => ing.PendingSignalCount >= 1L),
                    "PendingSignalCount stayed at 0 even though an item was in flight.");
            }
            finally
            {
                gate.Set();
                ing.Stop();
            }
        }

        [Fact]
        public void PendingSignalCount_decremented_on_post_after_stop_failure()
        {
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();
            ing.Stop();

            // Post after Stop throws — the increment in Post must NOT leak. Verify the
            // counter is still at 0 after the throw.
            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));
            Assert.Equal(0L, ing.PendingSignalCount);
        }

        [Fact]
        public void PendingSignalCount_does_not_leak_when_TryAdd_throws_after_completed_race()
        {
            // Codex follow-up (Low, 2026-04-30) — Post() has a narrow race window:
            //   1. Top-of-Post check sees _stopRequested==0 && !IsAddingCompleted (live)
            //   2. Stop runs concurrently: _stopRequested=1, then channel.CompleteAdding()
            //   3. Post continues to Interlocked.Increment(_unprocessedCount)
            //   4. Post calls _channel.TryAdd(item) → InvalidOperationException (channel
            //      is now completed)
            //
            // Without the symmetric catch on TryAdd the just-incremented counter would
            // leak. The window is too narrow to hit stochastically, so we reproduce it
            // deterministically via the internal _testHookBetweenPostStopCheckAndAdd
            // seam: the hook fires AFTER the post-stop check but BEFORE TryAdd, so we
            // can call CompleteAdding on the private channel inside it — exactly the
            // ordering the production race produces.
            using var rig = new Rig();
            var ing = rig.Build();
            ing.Start();

            // Grab the private _channel via reflection — we'll invoke CompleteAdding
            // from inside the test hook to simulate Stop running in the race window.
            var channelField = typeof(SignalIngress).GetField(
                "_channel",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(channelField);
            var channel = channelField!.GetValue(ing);
            Assert.NotNull(channel);
            var completeAdding = channel!.GetType().GetMethod("CompleteAdding");
            Assert.NotNull(completeAdding);

            // Wire the hook — fires once, between the post-stop check and TryAdd. By
            // calling CompleteAdding inside it, the next TryAdd will throw exactly the
            // production-race InvalidOperationException.
            int hookFired = 0;
            ing._testHookBetweenPostStopCheckAndAdd = () =>
            {
                if (Interlocked.Exchange(ref hookFired, 1) == 0)
                {
                    completeAdding!.Invoke(channel, Array.Empty<object>());
                }
            };

            Assert.Throws<InvalidOperationException>(() =>
                ing.Post(DecisionSignalKind.SessionStarted, At, "Collector", RawEvidence()));

            // Hook ran exactly once and the catch reset the counter. Without the catch
            // (regression), this assert would observe 1 — proving the test actually
            // exercises the bug.
            Assert.Equal(1, hookFired);
            Assert.Equal(0L, ing.PendingSignalCount);

            // Don't call Stop() — the channel is already completed; Stop's internal
            // CompleteAdding would throw. Dispose handles cleanup via the Rig.
        }
    }
}
