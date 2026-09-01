#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Single-writer Signal-Ingress. Plan §2.1a / §2.7c / L.1.
    /// <para>
    /// Alle Collector-Threads (und der <see cref="EffectRunner"/> via
    /// <see cref="ISignalIngressSink"/>) rufen <see cref="Post"/>. Ein dedizierter Worker-Thread
    /// liest die Items sequenziell, vergibt <c>SessionSignalOrdinal</c> +
    /// <c>SessionTraceOrdinal</c>, appendet ins SignalLog (Sofort-Flush) und delegiert den
    /// Reduce+Apply-Schritt an <see cref="IDecisionStepProcessor"/>.
    /// </para>
    /// <para>
    /// <b>Back-Pressure</b>: bounded <see cref="BlockingCollection{T}"/> (default 256) —
    /// volle Channel blockiert den Producer beim <c>Add</c>. Der Ingress misst die Block-Dauer
    /// und meldet sie throttled (1×/min/origin) an <see cref="IBackPressureObserver"/>.
    /// Die Back-Pressure-Emission läuft NICHT durch den Signal-Channel (würde deadlocken).
    /// </para>
    /// <para>
    /// <b>Worker re-entrancy</b>: a <see cref="Post"/> issued ON the worker thread itself
    /// (EffectRunner posting a <c>ClassifierVerdictIssued</c> from inside
    /// <c>ApplyStep</c>) must never touch the bounded channel — the worker is the channel's
    /// only consumer, so a full channel would block the consumer as a producer and freeze
    /// the whole pipeline for the rest of the run. Such posts go to a worker-local
    /// follow-up queue that the worker drains (ahead of the channel) right after the
    /// current item finishes. Same processing path, same ordinal/log/reduce semantics —
    /// just never blocking.
    /// </para>
    /// <para>
    /// <b>Shutdown</b>: <see cref="Stop"/> ruft <see cref="BlockingCollection{T}.CompleteAdding"/>,
    /// wartet auf das Drain-Ende via <c>Thread.Join</c>. Nach <c>Stop</c> wirft jeder
    /// weitere <see cref="Post"/> <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    public sealed class SignalIngress : ISignalIngressSink, IDisposable
    {
        /// <summary>Plan §2.1a — Default-Channel-Kapazität.</summary>
        public const int DefaultChannelCapacity = 256;

        /// <summary>Plan §2.1a — max. 1 Back-Pressure-Event pro Minute pro Origin.</summary>
        public static readonly TimeSpan DefaultBackPressureThrottle = TimeSpan.FromMinutes(1);

        private readonly IDecisionEngine _engine;
        private readonly ISignalLogWriter _signalLog;
        private readonly ISessionTraceOrdinalProvider _traceCounter;
        private readonly IDecisionStepProcessor _processor;
        private readonly IBackPressureObserver? _observer;
        private readonly TelemetrySignalEmitter? _signalEmitter;
        private readonly IClock _clock;
        private readonly TimeSpan _backPressureThrottle;
        private readonly int _channelCapacity;
        // PR3-D2: optional logger so production wiring gets observability into the previously
        // silent paths (SignalLog append failures, DecisionSignal ctor rejections, worker
        // faults). Null in unit-test rigs that don't care.
        private readonly AgentLogger? _logger;
        private long _processedCount;
        private long _stopRequestedAtTicks;
        // Codex review follow-up (Finding 2, 2026-04-30): items posted but not yet fully
        // processed by the worker. Incremented in <see cref="Post"/> *before* the channel
        // accepts the item (any add-failure path decrements again), decremented in the
        // <see cref="ProcessItem"/> finally so every successful enqueue eventually
        // accounts for itself. A foreign thread polling <see cref="PendingSignalCount"/>
        // is guaranteed: a steady read of 0 means all items have finished both reduce and
        // effect — there is no race window where a dequeued-but-not-yet-incremented item
        // would be invisible. Used by the termination drain to wait for ingress idle
        // before draining the spool.
        private long _unprocessedCount;

        // Codex follow-up (Low, 2026-04-30) — internal test seam, called between the
        // post-stop check and channel.TryAdd inside <see cref="Post"/>. Lets a unit test
        // deterministically reproduce the Stop-in-the-gap race that would otherwise
        // require a stress run with non-trivial false-negative odds. Always null in
        // production; only set from <c>SignalIngressTests</c>.
        internal Action? _testHookBetweenPostStopCheckAndAdd;

        private readonly BlockingCollection<IngressItem> _channel;
        private readonly Dictionary<string, DateTime> _lastBackPressureEmittedUtc =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _backPressureLock = new object();

        private Thread? _worker;
        // Worker-thread-only: signals posted re-entrantly from ApplyStep (EffectRunner
        // classifier verdicts). Drained by WorkerLoop after every channel item, before the
        // next channel item is taken. Only the worker enqueues and dequeues — no lock.
        private readonly Queue<IngressItem> _workerLocalQueue = new Queue<IngressItem>();
        private long _reentrantPostCount;
        private long _queueLengthPeak;
        private long _nextSignalOrdinal;     // worker-thread-only after Start; seeded from SignalLog
        private int _started;                 // 0 = not started, 1 = running
        private int _stopRequested;           // 0 = live, 1 = Stop called
        private Exception? _workerFault;     // captured if worker dies unexpectedly

        public SignalIngress(
            IDecisionEngine engine,
            ISignalLogWriter signalLog,
            ISessionTraceOrdinalProvider traceCounter,
            IDecisionStepProcessor processor,
            IClock clock,
            IBackPressureObserver? backPressureObserver = null,
            TelemetrySignalEmitter? signalEmitter = null,
            int channelCapacity = DefaultChannelCapacity,
            TimeSpan? backPressureThrottle = null,
            AgentLogger? logger = null)
        {
            if (channelCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelCapacity), "Channel capacity must be > 0.");
            }

            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _signalLog = signalLog ?? throw new ArgumentNullException(nameof(signalLog));
            _traceCounter = traceCounter ?? throw new ArgumentNullException(nameof(traceCounter));
            _processor = processor ?? throw new ArgumentNullException(nameof(processor));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _observer = backPressureObserver;
            _signalEmitter = signalEmitter;
            _channelCapacity = channelCapacity;
            _backPressureThrottle = backPressureThrottle ?? DefaultBackPressureThrottle;
            _logger = logger;

            _channel = new BlockingCollection<IngressItem>(boundedCapacity: channelCapacity);
            _nextSignalOrdinal = signalLog.LastOrdinal + 1;
        }

        /// <summary>Zuletzt vergebener <c>SessionSignalOrdinal</c>; <c>-1</c> bevor das erste Signal verarbeitet wurde.</summary>
        public long LastAssignedSignalOrdinal => Interlocked.Read(ref _nextSignalOrdinal) - 1;

        /// <summary>Konfigurierte Channel-Kapazität.</summary>
        public int ChannelCapacity => _channelCapacity;

        /// <summary>Aktuelle Queue-Länge. Nur für Observability/Tests — enthält Race-Fenster.</summary>
        public int ApproximateQueueLength => _channel.Count;

        /// <summary>
        /// Total signals accepted by <see cref="Post"/> that have not yet finished
        /// <see cref="ProcessItem"/>. Used by the termination drain (which runs off the
        /// worker thread) to wait for "ingress fully idle" before draining the telemetry
        /// spool — otherwise events the termination handler itself just posted would
        /// still be sitting in the channel when the spool-drain check fires and the
        /// handler would exit early. Codex Finding 2 (2026-04-30).
        /// </summary>
        public long PendingSignalCount => Interlocked.Read(ref _unprocessedCount);

        /// <summary>
        /// Signals that were posted from the worker thread itself and routed through the
        /// worker-local follow-up queue instead of the bounded channel. Observability/tests.
        /// </summary>
        public long ReentrantPostCount => Interlocked.Read(ref _reentrantPostCount);

        /// <summary>
        /// Total signals the worker has fully processed (reduce + effects). Monotonic — the
        /// agent_metrics_snapshot exports this so two snapshots prove whether the pipeline
        /// worked in between (a frozen worker keeps posting-side counters growing while this
        /// one stands still; a momentary queue-length sample could never show that).
        /// </summary>
        public long ProcessedSignalCount => Interlocked.Read(ref _processedCount);

        /// <summary>
        /// Highest channel queue length observed at any Post since Start. Monotonic
        /// non-decreasing, so snapshot sampling cannot alias it away (same rationale as the
        /// spool's PeakPendingItemCount). Worker-local follow-up items never count here.
        /// </summary>
        public long QueueLengthPeak => Interlocked.Read(ref _queueLengthPeak);

        /// <summary>
        /// Fires synchronously from <see cref="Post"/> after a signal has been accepted into
        /// the channel (for both the fast-path <see cref="BlockingCollection{T}.TryAdd"/> and
        /// the back-pressured <c>Add</c> slow-path). Exposed so idle-activity observers — e.g.
        /// <c>PeriodicCollectorLifecycleHost</c> — can see <b>every</b> signal, regardless of
        /// which <c>InformationalEventPost</c> instance posted it. Codex Finding 4.
        /// <para>
        /// <b>Handler contract</b>: must be fast and must not throw. Exceptions are caught and
        /// swallowed so a buggy observer cannot disrupt the production ingress path.
        /// </para>
        /// </summary>
        public event Action<DecisionSignalKind, IReadOnlyDictionary<string, string>?>? SignalPosted;

        /// <summary>
        /// Startet den Worker-Thread. Muss vor dem ersten <see cref="Post"/> aufgerufen werden.
        /// Mehrfach-Aufruf wirft.
        /// </summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("SignalIngress already started.");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SignalIngress.Worker",
            };
            _worker.Start();
        }

        /// <summary>
        /// Signalisiert Shutdown, drained verbleibende Items und joined den Worker-Thread.
        /// Idempotent — zweiter Aufruf ist ein No-Op. Wirft, wenn der Worker-Thread eine
        /// unerwartete Exception gefangen hat.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1)
            {
                return;
            }

            var stopStartUtc = _clock.UtcNow;
            Interlocked.Exchange(ref _stopRequestedAtTicks, stopStartUtc.Ticks);
            var pendingAtStop = _channel.Count;

            // CompleteAdding unblockt GetConsumingEnumerable sobald die Queue leer ist.
            _channel.CompleteAdding();

            var worker = _worker;
            if (worker != null && worker.IsAlive)
            {
                worker.Join();
            }

            // PR3-D2: lifecycle marker so forensic readers see how many items remained queued
            // and how long the drain took. Worker fault is logged here (not just thrown) so a
            // crash dump catches the message before the exception unwinds.
            var drainMs = (long)(_clock.UtcNow - stopStartUtc).TotalMilliseconds;
            var lastOrd = LastAssignedSignalOrdinal;
            var processedTotal = Interlocked.Read(ref _processedCount);
            _logger?.Info($"SignalIngress: stop requested — drained pending={pendingAtStop} items, processedTotal={processedTotal}, lastOrd={lastOrd}, durationMs={drainMs}.");

            if (_workerFault != null)
            {
                throw new InvalidOperationException(
                    "SignalIngress worker faulted unexpectedly.", _workerFault);
            }
        }

        public void Dispose()
        {
            try { Stop(); }
            catch { /* Dispose darf nicht werfen. */ }
            _channel.Dispose();
        }

        /// <summary>
        /// Reicht ein Raw-/Derived-/Synthetic-Signal in den Ingress ein. Thread-safe; darf von
        /// beliebig vielen Collector-Threads (und vom <see cref="EffectRunner"/>) parallel
        /// gerufen werden. Blockiert, wenn der Channel voll ist — Plan §2.1a Back-Pressure.
        /// Ausnahme: Aufrufe vom Worker-Thread selbst blockieren nie (worker-local queue).
        /// </summary>
        public void Post(
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            string sourceOrigin,
            Evidence evidence,
            IReadOnlyDictionary<string, string>? payload = null,
            int kindSchemaVersion = 1,
            object? typedPayload = null)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }
            if (string.IsNullOrEmpty(sourceOrigin))
            {
                throw new ArgumentException("SourceOrigin is mandatory.", nameof(sourceOrigin));
            }

            var item = new IngressItem(kind, kindSchemaVersion, occurredAtUtc, sourceOrigin, evidence, payload, typedPayload);

            // Worker re-entrancy (see class doc): a post from the worker thread itself goes to
            // the worker-local follow-up queue, never into the bounded channel. Checked before
            // the stop gate on purpose — while Stop() drains the channel the worker may still
            // produce verdicts for the items it is finishing, and those must not be lost.
            if (ReferenceEquals(Thread.CurrentThread, _worker))
            {
                Interlocked.Increment(ref _unprocessedCount);
                _workerLocalQueue.Enqueue(item);
                Interlocked.Increment(ref _reentrantPostCount);
                if (_workerLocalQueue.Count > 1)
                {
                    // One follow-up per step is the expected shape (one RunClassifier effect).
                    // More means effects are chaining on the worker — worth seeing in the log.
                    _logger?.Debug($"SignalIngress: worker-local follow-up queue depth={_workerLocalQueue.Count} kind={kind} origin={sourceOrigin}");
                }
                RaiseSignalPosted(kind, payload);
                return;
            }

            if (Volatile.Read(ref _stopRequested) == 1 || _channel.IsAddingCompleted)
            {
                throw new InvalidOperationException("SignalIngress has been stopped.");
            }

            // Codex Finding 2 (2026-04-30): bump the pending-counter BEFORE handing the item
            // to the channel. Decrement in three places: (a) when TryAdd throws because
            // CompleteAdding raced between the post-stop check and the enqueue, (b) when
            // the slow-path Add throws for the same reason, and (c) at the end of
            // ProcessItem's finally for items that successfully made it to the worker. The
            // increment-before-Add ordering is what eliminates the otherwise-unavoidable
            // race where a foreign poller reads PendingSignalCount==0 between
            // channel.Take and the worker's "now in flight" bookkeeping.
            Interlocked.Increment(ref _unprocessedCount);

            // Test hook (Codex Low follow-up, 2026-04-30) — invoked between the post-stop
            // check and the channel.TryAdd call so a deterministic test can simulate the
            // production race where Stop runs in this exact gap. Always null in production.
            _testHookBetweenPostStopCheckAndAdd?.Invoke();

            // Fast path: non-blocking enqueue. Codex follow-up (Low, 2026-04-30) — TryAdd
            // throws InvalidOperationException (NOT returns false) if CompleteAdding raced
            // between our post-stop check above and the enqueue here. Without this catch
            // the just-incremented counter would leak. Mirror the slow-path Add handler.
            bool added;
            try
            {
                added = _channel.TryAdd(item);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _unprocessedCount);
                throw new InvalidOperationException("SignalIngress has been stopped.");
            }

            if (added)
            {
                UpdateQueueLengthPeak(_channel.Count);
                RaiseSignalPosted(kind, payload);
                return;
            }

            // Slow path: bounded channel full → block, measure duration, notify observer throttled.
            var blockStartedUtc = _clock.UtcNow;
            int queueLenAtBlock = _channel.Count;
            UpdateQueueLengthPeak(queueLenAtBlock);
            try
            {
                _channel.Add(item);
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding wurde zwischen TryAdd und Add gerufen.
                Interlocked.Decrement(ref _unprocessedCount);
                throw new InvalidOperationException("SignalIngress has been stopped.");
            }

            var blockedFor = _clock.UtcNow - blockStartedUtc;
            if (blockedFor < TimeSpan.Zero)
            {
                blockedFor = TimeSpan.Zero;
            }

            NotifyBackPressureThrottled(sourceOrigin, queueLenAtBlock, blockedFor);
            RaiseSignalPosted(kind, payload);
        }

        /// <summary>Lock-free monotonic max (net48 has no Interlocked max primitive).</summary>
        private void UpdateQueueLengthPeak(int observedLength)
        {
            long observed = observedLength;
            long current = Interlocked.Read(ref _queueLengthPeak);
            while (observed > current)
            {
                var prior = Interlocked.CompareExchange(ref _queueLengthPeak, observed, current);
                if (prior == current) return;
                current = prior;
            }
        }

        private void RaiseSignalPosted(DecisionSignalKind kind, IReadOnlyDictionary<string, string>? payload)
        {
            // Snapshot the delegate once — standard pattern to tolerate concurrent subscribe /
            // unsubscribe around the invocation.
            var handler = SignalPosted;
            if (handler == null) return;
            try { handler(kind, payload); }
            catch
            {
                // Observer exceptions MUST NOT disrupt the production ingress path. The idle-
                // activity observer (Codex Finding 4) is advisory — dropping one observation
                // is strictly better than surfacing a mid-Post exception to a collector thread.
            }
        }

        private void NotifyBackPressureThrottled(string origin, int queueLengthAtBlock, TimeSpan blockedFor)
        {
            if (_observer == null)
            {
                return;
            }

            var now = _clock.UtcNow;
            lock (_backPressureLock)
            {
                if (_lastBackPressureEmittedUtc.TryGetValue(origin, out var last) &&
                    (now - last) < _backPressureThrottle)
                {
                    return;
                }
                _lastBackPressureEmittedUtc[origin] = now;
            }

            try
            {
                _observer.OnBackPressure(origin, _channelCapacity, queueLengthAtBlock, blockedFor);
            }
            catch
            {
                // Observer-Fehler dürfen den Produktions-Pfad nicht aufhalten. Weglucken.
            }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _channel.GetConsumingEnumerable())
                {
                    ProcessItem(item);
                    DrainWorkerLocalQueue();
                }
            }
            catch (Exception ex)
            {
                // PR3-D2: this branch should never fire — ProcessItem swallows everything. If it
                // does, the worker is now stopped and no further signals will be processed.
                // Surface that catastrophe explicitly so the agent log shows it before the next
                // Post() throws "ingress has been stopped".
                _workerFault = ex;
                _logger?.Error("SignalIngress.Worker faulted unexpectedly — ingress STOPPED. No further signals will be processed.", ex);
            }
        }

        /// <summary>
        /// Processes signals the worker posted to itself during the previous item (see class
        /// doc, worker re-entrancy). Iterative, not recursive: a follow-up that produces
        /// another follow-up is simply appended and handled in the same loop.
        /// </summary>
        private void DrainWorkerLocalQueue()
        {
            while (_workerLocalQueue.Count > 0)
            {
                ProcessItem(_workerLocalQueue.Dequeue());
            }
        }

        private void ProcessItem(IngressItem item)
        {
            // Codex Finding 2 (2026-04-30): match the Interlocked.Increment that Post()
            // performed against this same item. Multiple early-return paths below — using
            // try/finally guarantees the counter always settles, even on
            // DecisionSignal-ctor-rejection or SignalLog.Append-failure paths.
            try
            {
                ProcessItemInner(item);
            }
            finally
            {
                Interlocked.Decrement(ref _unprocessedCount);
            }
        }

        private void ProcessItemInner(IngressItem item)
        {
            // Single-writer: _nextSignalOrdinal wird ausschließlich hier verändert.
            long signalOrdinal = _nextSignalOrdinal;
            long traceOrdinal = _traceCounter.Next();

            DecisionSignal signal;
            try
            {
                signal = new DecisionSignal(
                    sessionSignalOrdinal: signalOrdinal,
                    sessionTraceOrdinal: traceOrdinal,
                    kind: item.Kind,
                    kindSchemaVersion: item.KindSchemaVersion,
                    occurredAtUtc: item.OccurredAtUtc,
                    sourceOrigin: item.SourceOrigin,
                    evidence: item.Evidence,
                    payload: item.Payload,
                    typedPayload: item.TypedPayload);
            }
            catch (Exception ex)
            {
                // DecisionSignal-Ctor validiert Pflichtfelder. Invalid input → überspringen,
                // Ordinal NICHT verbrauchen (sonst entsteht eine Lücke im SignalLog).
                _logger?.Warning(
                    $"SignalIngress: dropped invalid signal kind={item.Kind} origin={item.SourceOrigin} " +
                    $"— DecisionSignal ctor threw, ordinal NOT consumed: {ex.Message}");
                return;
            }

            try
            {
                _signalLog.Append(signal);
            }
            catch (Exception ex)
            {
                // Append fehlgeschlagen (z.B. Disk-Full). Ohne on-disk-Persistenz darf der
                // Reducer nicht laufen (L.1 Signal-Log-Determinismus). Ordinal bleibt „geplant",
                // wird beim nächsten Versuch neu vergeben — monoton, da _nextSignalOrdinal
                // noch nicht inkrementiert wurde.
                _logger?.Error(
                    $"SignalIngress: SignalLog.Append failed for ord={signalOrdinal} kind={item.Kind} " +
                    $"— ordinal will be re-attempted on next post: {ex.Message}",
                    ex);
                return;
            }

            _nextSignalOrdinal = signalOrdinal + 1;
            // PR3-D2: 1:1 correlation marker for D1 (DecisionStepProcessor logs the same ord +
            // kind on the receive side). Verbose because the steady-state happy-path is the
            // dominant cardinality.
            _logger?.Verbose($"SignalIngress: processed ord={signalOrdinal} kind={item.Kind} origin={item.SourceOrigin}.");
            Interlocked.Increment(ref _processedCount);

            // Project the signal onto the telemetry transport for backend upload. Local
            // SignalLog is authoritative (§2.7c / L.1); a transport enqueue failure must NOT
            // break the ingress worker. Spool overflow / serialization faults get swallowed
            // here so the reducer path below still runs on the just-committed local signal.
            if (_signalEmitter != null)
            {
                try { _signalEmitter.Emit(signal); }
                catch { /* best-effort upload; local state already consistent */ }
            }

            var state = _processor.CurrentState;
            // IDecisionEngine.Reduce ist fail-safe (DecisionEngine.cs:39-47): wirft nicht.
            var step = _engine.Reduce(state, signal);

            EffectRunResult? effectResult = null;
            try
            {
                effectResult = _processor.ApplyStep(step, signal);
            }
            catch
            {
                // Processor-Fehler (z.B. Journal-Write, Effect-Transient-Exhaust) werden in
                // M4.4.5 vom Orchestrator geloggt + ggf. Quarantine ausgelöst. M4.4.0
                // verschluckt bewusst, damit der Ingress-Worker nicht verstummt.
            }

            // Codex follow-up (post-#50 #B): inline durable-abort path. When the step's
            // effects signal a critical infrastructure failure (e.g. timer scheduler died)
            // we must land a synthetic EffectInfrastructureFailure signal on the SignalLog
            // BEFORE returning to the worker loop — otherwise a crash between here and the
            // next Post() would leave recovery replaying the phantom deadline state with no
            // terminal signal to bridge it to SessionStage.Failed.
            //
            // The recursion guard prevents processing an abort that the synthetic signal's
            // own reducer handler might somehow report (shouldn't happen — HandleEffect-
            // InfrastructureFailureV1 transitions to terminal without queuing critical
            // effects — but the guard makes that invariant failure-safe).
            if (effectResult != null
                && effectResult.SessionMustAbort
                && signal.Kind != DecisionSignalKind.EffectInfrastructureFailure)
            {
                // Extract the failing critical effect kind from the result so the synthetic
                // signal payload matches the v1 contract (reason + failingEffect). The
                // critical failure is always the LAST entry that was appended before
                // EffectRunner returned with SessionMustAbort — transient failures before
                // it are harmless observability. Fall back to any non-transient failure if
                // ordering ever changes.
                DecisionEffectKind? failingEffect = null;
                for (var i = effectResult.Failures.Count - 1; i >= 0; i--)
                {
                    if (!effectResult.Failures[i].IsTransient)
                    {
                        failingEffect = effectResult.Failures[i].EffectKind;
                        break;
                    }
                }

                ProcessInlineAbortSignal(effectResult.AbortReason, signal.OccurredAtUtc, failingEffect);
            }
        }

        /// <summary>
        /// Synthesise + durably persist + reduce a <c>EffectInfrastructureFailure</c> signal
        /// inline within the current worker iteration. See ProcessItem for the rationale —
        /// this closes the crash-window identified by Codex follow-up post-#50 #B. The
        /// signal payload matches the v1 contract documented on
        /// <see cref="DecisionSignalKind.EffectInfrastructureFailure"/>:
        /// <c>{ reason, failingEffect }</c>, with source/evidence identifying the critical
        /// effect kind that triggered the abort so forensic consumers can distinguish
        /// ScheduleDeadline-vs-CancelDeadline failures.
        /// </summary>
        private void ProcessInlineAbortSignal(
            string? abortReason,
            DateTime occurredAtUtc,
            DecisionEffectKind? failingEffect)
        {
            long signalOrdinal = _nextSignalOrdinal;
            long traceOrdinal = _traceCounter.Next();

            var failingEffectLabel = failingEffect?.ToString() ?? "Unknown";

            DecisionSignal syntheticSignal;
            try
            {
                syntheticSignal = new DecisionSignal(
                    sessionSignalOrdinal: signalOrdinal,
                    sessionTraceOrdinal: traceOrdinal,
                    kind: DecisionSignalKind.EffectInfrastructureFailure,
                    kindSchemaVersion: 1,
                    occurredAtUtc: occurredAtUtc,
                    sourceOrigin: $"effectrunner:critical:{failingEffectLabel}",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: $"effect_infrastructure_failure:{failingEffectLabel}",
                        summary: $"Critical effect {failingEffectLabel} failed: {abortReason ?? "<unspecified>"}"),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = abortReason ?? string.Empty,
                        ["failingEffect"] = failingEffectLabel,
                    });
            }
            catch
            {
                // DecisionSignal ctor validation must not fail for a locally-constructed
                // signal, but defensively bail — the max-lifetime watchdog remains the
                // last-resort termination path.
                return;
            }

            try
            {
                _signalLog.Append(syntheticSignal);
            }
            catch
            {
                // Persist-Failure — we must NOT advance the ordinal (log gap) and cannot
                // safely reduce. Recovery will see the phantom state; the watchdog will
                // clean up the hung session.
                return;
            }

            _nextSignalOrdinal = signalOrdinal + 1;

            if (_signalEmitter != null)
            {
                try { _signalEmitter.Emit(syntheticSignal); }
                catch { /* best-effort upload */ }
            }

            var state = _processor.CurrentState;
            var step = _engine.Reduce(state, syntheticSignal);

            try
            {
                _processor.ApplyStep(step, syntheticSignal);
            }
            catch
            {
                // Processor fault on the terminal transition — Journal has the abort
                // transition committed (reducer produces Stage=Failed unconditionally).
                // Swallowed by convention; worker must not die from this path.
            }
        }

        private readonly struct IngressItem
        {
            public IngressItem(
                DecisionSignalKind kind,
                int kindSchemaVersion,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload,
                object? typedPayload)
            {
                Kind = kind;
                KindSchemaVersion = kindSchemaVersion;
                OccurredAtUtc = occurredAtUtc;
                SourceOrigin = sourceOrigin;
                Evidence = evidence;
                Payload = payload;
                TypedPayload = typedPayload;
            }

            public DecisionSignalKind Kind { get; }
            public int KindSchemaVersion { get; }
            public DateTime OccurredAtUtc { get; }
            public string SourceOrigin { get; }
            public Evidence Evidence { get; }
            public IReadOnlyDictionary<string, string>? Payload { get; }
            public object? TypedPayload { get; }
        }
    }
}
