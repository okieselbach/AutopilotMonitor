#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Signals;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Top-Level-Lifecycle-Owner der V2-Agent-Runtime. Plan §2.1a / §2.7 / §4.x M4.4.5.
    /// <para>
    /// <b>Scope M4.4.5.b</b>: verdrahtet die Kern-Pipeline — Persistenz (SignalLog + Journal +
    /// Snapshot), Telemetry-Transport (Spool + Uploader), Event-Emitter-Kette,
    /// DeadlineScheduler → Ingress-Bridge, ClassifierRegistry, EffectRunner,
    /// DecisionStepProcessor, SignalIngress. Sub-c erweitert um Collectors + SignalAdapters.
    /// </para>
    /// <para>
    /// <b>Startup-Reihenfolge</b> (ctor → <see cref="Start"/>):
    /// <list type="number">
    ///   <item>Persistenz-Writer instanziieren (Sofort-Flush aktiv ab hier)</item>
    ///   <item><c>snapshot.Load()</c> → <c>initialState</c>; fallback <see cref="DecisionState.CreateInitial"/></item>
    ///   <item>TelemetrySpool + BackendUploader + TelemetryUploadOrchestrator</item>
    ///   <item>EventSequenceCounter → TelemetryEventEmitter → EventTimelineEmitter + BackPressureObserver</item>
    ///   <item>DeadlineScheduler + ClassifierRegistry</item>
    ///   <item>LazyIngressSinkRelay (für die EffectRunner-↔-SignalIngress-Zirkel-Dep)</item>
    ///   <item>EffectRunner (sink = Relay)</item>
    ///   <item>DecisionStepProcessor (initial state)</item>
    ///   <item>SessionTraceOrdinalProvider (seeded from max across SignalLog + Journal + Spool)</item>
    ///   <item>SignalIngress — Relay.Target = ingress</item>
    ///   <item>Scheduler.Fired → Ingress.Post-Bridge subscriben</item>
    ///   <item><c>signalIngress.Start()</c> — Worker-Thread läuft</item>
    ///   <item>Periodischer Drain-Loop starten (Fire-and-forget Task)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Shutdown-Reihenfolge</b> (<see cref="Stop"/>, idempotent):
    /// <list type="number">
    ///   <item>Scheduler.Fired-Handler abmelden (keine neuen DeadlineFired-Signals)</item>
    ///   <item>DeadlineScheduler disposen (stoppt alle Timer)</item>
    ///   <item>SignalIngress.Stop — drainiert verbleibende Items</item>
    ///   <item>Drain-Loop Token cancellen, Task joinen</item>
    ///   <item>Terminaler Drain — finale Batches ans Backend</item>
    ///   <item>Snapshot.Save — letzter konsistenter State</item>
    ///   <item>Dispose aller Disposables</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EnrollmentOrchestrator : IQuarantineSink, IDisposable
    {
        /// <summary>Default-Intervall zwischen periodischen Drain-Versuchen.</summary>
        public static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(30);

        /// <summary>Default Timeout für den Terminal-Drain beim Stop.</summary>
        public static readonly TimeSpan DefaultTerminalDrainTimeout = TimeSpan.FromSeconds(30);

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly string _stateDirectory;
        private readonly string _transportDirectory;
        private readonly IClock _clock;
        private readonly AgentLogger _logger;
        private readonly IBackendTelemetryUploader _uploader;
        private readonly IReadOnlyList<IClassifier> _classifiers;
        private readonly IComponentFactory? _componentFactory;
        private readonly IReadOnlyCollection<string> _whiteGloveSealingPatternIds;
        private readonly int _channelCapacity;
        private readonly int _quarantineThreshold;
        private readonly TimeSpan _drainInterval;
        private readonly TimeSpan _terminalDrainTimeout;
        private readonly TimeSpan? _agentMaxLifetime;
        private readonly int _uploadBatchSize;

        // Test-only override so tests can drive rehydrate-failure via FakeDeadlineScheduler.
        // Production always passes null → Start() creates the real DeadlineScheduler.
        private readonly IDeadlineScheduler? _schedulerOverride;

        // Max-lifetime watchdog (M4.6.α). Timer is armed in Start() when _agentMaxLifetime != null.
        private System.Threading.Timer? _maxLifetimeTimer;
        private int _terminatedFired;

        // Built in Start()
        private DecisionEngine? _engine;
        private SignalLogWriter? _signalLog;
        private JournalWriter? _journal;
        private SnapshotPersistence? _snapshot;
        private EventSequencePersistence? _eventSequencePersistence;
        private EventSequenceCounter? _eventSequenceCounter;
        private TelemetrySpool? _spool;
        private TelemetryUploadOrchestrator? _transport;
        private TelemetrySignalEmitter? _signalEmitter;
        private TelemetryTransitionEmitter? _transitionEmitter;
        private EventTimelineEmitter? _timelineEmitter;
        private BackPressureEventObserver? _backPressureObserver;
        private IDeadlineScheduler? _scheduler;
        private ClassifierRegistry? _classifierRegistry;
        private LazyIngressSinkRelay? _sinkRelay;
        private EffectRunner? _effectRunner;
        private DecisionStepProcessor? _processor;
        private SessionTraceOrdinalProvider? _traceCounter;
        private SignalIngress? _ingress;
        private IReadOnlyList<ICollectorHost>? _collectorHosts;

        private EventHandler<DeadlineFiredEventArgs>? _deadlineBridge;

        // Drain-Loop
        private CancellationTokenSource? _drainCts;
        private Task? _drainTask;
        // Set by TelemetrySpool.ImmediateFlushRequested. Replaced with a fresh instance at the
        // start of each drain iteration so wakeups don't leak across iterations. Swapped via
        // Volatile.Read/Write because producer (spool thread) and consumer (drain loop) run on
        // different threads.
        private TaskCompletionSource<bool>? _immediateFlushSignal;
        private EventHandler? _immediateFlushBridge;

        // Lifecycle
        private int _started;
        private int _stopRequested;
        private int _disposed;
        private int _collectorHostsStopped;

        // Quarantine flag — set mid-run, read on next start.
        private bool _quarantineRequested;
        private string? _quarantineReason;

        // Recovery flags (populated during Start()).
        // V1-symmetric Part-2 hint (analog to V1 MonitoringService._isWhiteGlovePart2):
        // set when Start() detects a persisted WhiteGloveSealed snapshot and archives the
        // state folder before fresh-starting the recovery pipeline. Read by
        // AgentRuntimeHost (for "skip facts/SessionStarted on resume" semantics) and
        // shutdown analyzers (V1: runShutdownAnalyzers(part: 2)).
        private bool _isWhiteGlovePart2;
        private bool _wasStartupQuarantine;

        public EnrollmentOrchestrator(
            string sessionId,
            string tenantId,
            string stateDirectory,
            string transportDirectory,
            IClock clock,
            AgentLogger logger,
            IBackendTelemetryUploader uploader,
            IEnumerable<IClassifier> classifiers,
            IComponentFactory? componentFactory = null,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null,
            int channelCapacity = SignalIngress.DefaultChannelCapacity,
            int quarantineThreshold = DecisionStepProcessor.DefaultQuarantineThreshold,
            TimeSpan? drainInterval = null,
            TimeSpan? terminalDrainTimeout = null,
            TimeSpan? agentMaxLifetime = null,
            int uploadBatchSize = 100,
            IDeadlineScheduler? schedulerOverride = null)
        {
            if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("SessionId is mandatory.", nameof(sessionId));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));
            if (string.IsNullOrEmpty(stateDirectory)) throw new ArgumentException("StateDirectory is mandatory.", nameof(stateDirectory));
            if (string.IsNullOrEmpty(transportDirectory)) throw new ArgumentException("TransportDirectory is mandatory.", nameof(transportDirectory));

            _sessionId = sessionId;
            _tenantId = tenantId;
            _stateDirectory = stateDirectory;
            _transportDirectory = transportDirectory;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));

            if (classifiers == null) throw new ArgumentNullException(nameof(classifiers));
            var list = new List<IClassifier>();
            foreach (var c in classifiers)
            {
                if (c == null) throw new ArgumentException("Classifier enumerable must not contain null.", nameof(classifiers));
                list.Add(c);
            }
            _classifiers = list;
            _componentFactory = componentFactory;
            _whiteGloveSealingPatternIds = whiteGloveSealingPatternIds ?? Array.Empty<string>();

            _channelCapacity = channelCapacity;
            _quarantineThreshold = quarantineThreshold;
            _drainInterval = drainInterval ?? DefaultDrainInterval;
            _terminalDrainTimeout = terminalDrainTimeout ?? DefaultTerminalDrainTimeout;

            if (agentMaxLifetime.HasValue && agentMaxLifetime.Value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(agentMaxLifetime), "AgentMaxLifetime must be positive when set.");
            _agentMaxLifetime = agentMaxLifetime;

            if (uploadBatchSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(uploadBatchSize), "UploadBatchSize must be positive.");
            _uploadBatchSize = uploadBatchSize;

            _schedulerOverride = schedulerOverride;
        }

        /// <summary>
        /// Terminal event — fires once when the orchestrator declares the session done. Plan §4.x M4.6.α.
        /// <para>
        /// M4.6.α fires only <see cref="EnrollmentTerminationReason.MaxLifetimeExceeded"/>; the
        /// <see cref="EnrollmentTerminationReason.DecisionTerminalStage"/> path is wired in M4.6.β
        /// together with <c>CleanupService</c> self-destruct + SummaryDialog launch.
        /// </para>
        /// <para>
        /// Handlers may run on a ThreadPool thread (Timer callback). They must NOT call
        /// <see cref="Stop"/> directly (re-entrant) — raise a shutdown <see cref="ManualResetEventSlim"/>
        /// from the handler and let the main thread call <see cref="Stop"/>.
        /// </para>
        /// </summary>
        public event EventHandler<EnrollmentTerminatedEventArgs>? Terminated;

        // ---------------------------------------------------------------- Observability

        /// <summary>Aktueller <see cref="DecisionState"/> — nur nach <see cref="Start"/> verfügbar.</summary>
        public DecisionState CurrentState =>
            _processor?.CurrentState ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>True wenn ein Processor-Callback Quarantine-Eskalation ausgelöst hat. M4.4.5.f liest das.</summary>
        public bool IsQuarantineRequested => _quarantineRequested;

        /// <summary>Letzter Quarantine-Reason oder <c>null</c>.</summary>
        public string? QuarantineReason => _quarantineReason;

        /// <summary>
        /// <c>true</c> when Start() detected a persisted <see cref="SessionStage.WhiteGloveSealed"/>
        /// snapshot, archived the prior state folder to <c>state/.part1-&lt;ts&gt;/</c>, and
        /// began a fresh Classic enrollment flow. V1-symmetric to
        /// <c>MonitoringService._isWhiteGlovePart2</c>. Plan §2.7 Sonderfall 1.
        /// </summary>
        public bool IsWhiteGlovePart2 => _isWhiteGlovePart2;

        /// <summary>
        /// <c>true</c> wenn der Start auf einen korrupten State-Segment traf und
        /// Snapshot + Log-Segmente nach <c>.quarantine/{ts}/</c> bewegt wurden.
        /// Plan §2.7 Sonderfall 2.
        /// </summary>
        public bool WasStartupQuarantine => _wasStartupQuarantine;

        /// <summary>Exposed für Sub-c-Wiring (SignalAdapters + Collector-Callbacks).</summary>
        public ISignalIngressSink IngressSink =>
            (ISignalIngressSink?)_ingress ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>
        /// Test-only accessor for the internal <see cref="ITelemetrySpool"/>. Used by the
        /// drain-wakeup regression tests to enqueue an immediate-flush item directly and
        /// assert the drain loop wakes up early (without having to drive a real collector).
        /// Production callers never need this — the orchestrator manages the spool lifecycle.
        /// </summary>
        internal ITelemetrySpool? SpoolForTests => _spool;

        /// <summary>
        /// Exposed so Program.cs can subscribe to <see cref="TelemetryUploadOrchestrator.ServerResponseReceived"/>
        /// for M4.6.ε DeviceBlocked / DeviceKillSignal / AdminAction / Actions plumbing.
        /// </summary>
        public TelemetryUploadOrchestrator Transport =>
            _transport ?? throw new InvalidOperationException("Orchestrator not started.");

        /// <summary>
        /// Number of telemetry items currently queued in the spool that have not yet been
        /// acknowledged as uploaded. Returns <c>0</c> before <see cref="Start"/> has wired the
        /// spool so callers can poll this safely from the termination path without guarding
        /// the orchestrator lifecycle. Used by <c>EnrollmentTerminationHandler.DrainSpool</c>
        /// to short-circuit the bounded wait once the spool is actually empty (Option 1 of
        /// the WG Part 1 graceful-exit hardening, 2026-04-30).
        /// </summary>
        public int PendingItemCount => _spool?.PendingItemCount ?? 0;

        /// <summary>
        /// Number of signals accepted by <c>SignalIngress.Post</c> that have not yet
        /// finished reduce + effect-run. Codex Finding 2 (2026-04-30): the termination
        /// handler — now dispatched off the ingress worker — uses this to wait until the
        /// lifecycle events it just posted (<c>agent_shutting_down</c>,
        /// <c>whiteglove_part1_complete</c>, analyzer events) have actually been
        /// processed by the worker (and therefore reached the spool) BEFORE polling
        /// <see cref="PendingItemCount"/> for spool-empty. Returns <c>0</c> before
        /// <see cref="Start"/> wires the ingress.
        /// </summary>
        public long IngressPendingSignalCount => _ingress?.PendingSignalCount ?? 0L;

        // ---------------------------------------------------------------- Lifecycle

        /// <summary>
        /// Wires all components and starts the Ingress-Worker + periodischen Drain-Loop.
        /// Idempotent per <see cref="Interlocked.Exchange(ref int, int)"/> — zweiter Aufruf wirft.
        /// <para>
        /// <b><paramref name="onIngressReady"/></b> (single-rail refactor, plan §5.1): an optional
        /// caller hook invoked after the ingress worker is running but before any collector host
        /// is started. Use this slot to post agent-lifecycle signals (e.g. <c>agent_started</c>)
        /// so they land on the signal log — and therefore on the backend Events timeline — with
        /// sequence numbers lower than anything the collectors produce. The callback is invoked
        /// synchronously on the calling thread; exceptions are caught and logged so a malformed
        /// hook cannot abort Start. The WhiteGlove Part-2 recovery bridge (when applicable) fires
        /// first, then this hook, then collector hosts.
        /// </para>
        /// </summary>
        /// <param name="onIngressReady">Optional hook, invoked with the live <see cref="ISignalIngressSink"/> after ingress start and before collector start.</param>
        public void Start(Action<ISignalIngressSink>? onIngressReady = null)
        {
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(EnrollmentOrchestrator));
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                throw new InvalidOperationException("EnrollmentOrchestrator already started.");
            }

            EnsureDirectories();

            var snapshotPath = Path.Combine(_stateDirectory, "snapshot.json");
            var signalLogPath = Path.Combine(_stateDirectory, "signal-log.jsonl");
            var journalPath = Path.Combine(_stateDirectory, "journal.jsonl");
            var eventSequencePath = Path.Combine(_stateDirectory, "event-sequence.json");

            // 0) WhiteGlove Part-2 resume detection. V1-symmetric Archive-and-Reset
            //    pattern: peek the persisted snapshot via the lock-free static reader; if
            //    the prior run sealed Part-1, move the reducer-state segment files
            //    (snapshot, signal-log, journal) aside to a timestamped
            //    <c>.part1-&lt;ts&gt;/</c> bucket BEFORE the writers below open them for
            //    scanning. <c>event-sequence.json</c> is intentionally preserved so the
            //    session-wide event sequence stays monotonic across the resume boundary
            //    (the backend orders events by <c>(SessionId, Sequence)</c> and the
            //    Web UI splits on <c>resumed.sequence - 1</c>). The orchestrator then
            //    runs through the normal recovery branches as a fresh first-boot
            //    (branch c1), re-emits a <c>whiteglove_resumed</c> lifecycle event
            //    after the onIngressReady hook, and lets the standard Classic
            //    enrollment flow drive completion.
            //    Anti-loop: once archived, the WhiteGloveSealed snapshot is gone, so a
            //    subsequent restart sees no marker and continues as a Classic session.
            //    The hint bool is in-memory only — also matches V1 (which clears the
            //    on-disk marker file before emitting <c>whiteglove_resumed</c>).
            if (Directory.Exists(_stateDirectory))
            {
                var rawSnapshot = SnapshotPersistence.TryReadRaw(snapshotPath);
                if (rawSnapshot != null && rawSnapshot.Stage == SessionStage.WhiteGloveSealed)
                {
                    StateArchiver.ArchiveStateFolder(
                        stateDirectory: _stateDirectory,
                        reason: "wg_part1_resume_archive",
                        utcNow: () => _clock.UtcNow,
                        logger: _logger);
                    _isWhiteGlovePart2 = true;
                    _logger.Info(
                        "EnrollmentOrchestrator: WhiteGlove Part-1 resume detected — state archived; " +
                        "starting fresh Classic enrollment flow for Part-2.");
                }
            }

            // 1) Persistenz. Writer scannen bestehende Files im Ctor (LastOrdinal / LastStepIndex).
            _signalLog = new SignalLogWriter(signalLogPath);
            _journal = new JournalWriter(journalPath, () => _clock.UtcNow);
            _snapshot = new SnapshotPersistence(snapshotPath, () => _clock.UtcNow);
            _eventSequencePersistence = new EventSequencePersistence(eventSequencePath);

            // 2) Recovery (Plan §2.7 Sonderfälle 1+2, Codex follow-up #1).
            //
            //    Invariant: the SignalLog is authoritative truth, the Snapshot is a cache.
            //    Therefore on every recovery we prefer reconstructing state from the log over
            //    trusting a stale/corrupt snapshot. ReducerReplay.Replay (Phase 1) performs the
            //    pure fold; this method decides the seed + which signals to feed it.
            //
            //    Branches:
            //      a) Snapshot present but corrupt → quarantine the snapshot ONLY (not the log!)
            //         and rebuild from the full SignalLog. If the log itself is head-corrupt
            //         (file non-empty but ReadAll yields zero signals) we escalate to a total-
            //         loss quarantine of the log segments and start fresh.
            //      b) Snapshot loaded cleanly → replay any SignalLog tail past the snapshot's
            //         LastAppliedSignalOrdinal so a pre-crash Journal/Snapshot lag is closed.
            //      c) No snapshot file — two sub-cases (Codex follow-up post-#50 #A):
            //         c1) SignalLog also empty → genuinely fresh start.
            //         c2) SignalLog has entries → crash before the first snapshot save ever
            //             landed; rebuild from the full SignalLog like branch (a) does after
            //             snapshot quarantine. Anything else would keep stale log entries on
            //             disk while state resets to StepIndex=0 and break monotonicity on
            //             the next append.
            //
            //    After the seed + replay-stream are selected the Journal is aligned AHEAD-OF
            //    replay (phantom suffix beyond seed.StepIndex-1 goes to the forensic bucket)
            //    and backfilled DURING replay via the onTransition callback. This closes two
            //    crash windows in one mechanism (Codex follow-up post-#50 #C):
            //      • AHEAD — Journal was flushed but Snapshot not; phantom transitions beyond
            //        the seed get quarantined before replay so live Append monotonicity holds.
            //      • BEHIND — SignalLog was flushed but Journal not; the replay callback
            //        rematerialises the missing StepIndex entries onto the Journal so it
            //        ends up in exact lockstep with initialState.
            var snapshotFileExistsPreLoad = File.Exists(snapshotPath);
            var loadedState = _snapshot.Load();
            DecisionState initialState;

            // Reducer is stateless — a transient instance is enough for the pure replay below.
            // The live ingress pipeline creates its own instance further down the Start() body.
            var replayEngine = new DecisionEngine();

            // Dispatch: pick the seed + signal stream per branch. Null seed is impossible past
            // this block (every path assigns it), but the compiler needs the explicit init.
            // AgentBootUtc gets stamped from the live clock so reducer deadline-arming sites
            // can floor replayed-signal timestamps at the current run's start (replay-safety).
            var agentBootUtc = _clock.UtcNow;
            DecisionState seed = DecisionState.CreateInitial(_sessionId, _tenantId, agentBootUtc);
            IReadOnlyList<DecisionSignal> signalsToReplay = Array.Empty<DecisionSignal>();
            string branchTag;

            if (loadedState == null && snapshotFileExistsPreLoad)
            {
                // Branch (a) — Snapshot corrupt. Quarantine the snapshot and attempt log replay.
                _logger.Error(
                    "EnrollmentOrchestrator: snapshot present but Load returned null (checksum mismatch or parse error) — quarantining snapshot and attempting SignalLog replay.");
                _snapshot.Quarantine("checksum-mismatch-on-startup");

                var loggedSignals = _signalLog.ReadAll();
                var logFileInfo = new FileInfo(signalLogPath);
                var logIsHeadCorrupt =
                    logFileInfo.Exists && logFileInfo.Length > 0 && loggedSignals.Count == 0;

                if (logIsHeadCorrupt)
                {
                    _logger.Warning(
                        "EnrollmentOrchestrator: SignalLog unreadable from the first line — escalating to total-loss quarantine.");
                    SegmentQuarantine.QuarantineAll(
                        _stateDirectory, "log-head-corrupt-after-snapshot-loss", () => _clock.UtcNow);

                    // Writer hold paths, not handles — but their in-memory counters are stale
                    // after the quarantine move. Recreate to reset them to -1.
                    _signalLog = new SignalLogWriter(signalLogPath);
                    _journal = new JournalWriter(journalPath, () => _clock.UtcNow);
                    _eventSequencePersistence = new EventSequencePersistence(eventSequencePath);

                    branchTag = "a-total-loss";
                }
                else
                {
                    signalsToReplay = loggedSignals;
                    branchTag = "a-full-log-replay";
                }

                _wasStartupQuarantine = true;
            }
            else if (loadedState != null)
            {
                // Branch (b) — Snapshot loaded. Catch up any SignalLog tail past the snapshot.
                // Re-stamp AgentBootUtc so the current run's deadlines floor at "now" — the
                // persisted boot anchor is from the prior run and using it would let replayed
                // tail signals arm deadlines that are already past-due (premature fire).
                seed = loadedState.ToBuilder().WithAgentBootUtc(agentBootUtc).Build();
                signalsToReplay = CollectSignalLogTailAfter(loadedState.LastAppliedSignalOrdinal);
                branchTag = signalsToReplay.Count > 0 ? "b-tail-replay" : "b-snapshot-current";
            }
            else
            {
                // Branch (c) — no snapshot on disk. Two sub-cases:
                //   (c1) SignalLog also empty → genuinely fresh start.
                //   (c2) SignalLog has entries (crash BEFORE first snapshot save) → full
                //        log replay from CreateInitial (Codex follow-up post-#50 #A).
                var loggedSignals = _signalLog.ReadAll();
                if (loggedSignals.Count == 0)
                {
                    branchTag = "c1-fresh";
                }
                else
                {
                    signalsToReplay = loggedSignals;
                    _wasStartupQuarantine = true;
                    branchTag = "c2-full-log-replay";
                }
            }

            // Align Journal AHEAD phantoms before replay. Engine semantics: a transition
            // produced for state at StepIndex=K carries StepIndex=K+1 (see
            // DecisionEngine.BuildTakenTransition); after K reduces from initial the journal
            // holds entries [1..K] with LastStepIndex=K. So any on-disk entry beyond
            // seed.StepIndex is a phantom from a crash between Journal.Append and
            // Snapshot.Save. Truncating first lets the replay callback Append monotonically
            // from seed.StepIndex+1 without colliding.
            var journalBoundary = seed.StepIndex;
            if (_journal.LastStepIndex > journalBoundary)
            {
                _logger.Warning(
                    $"EnrollmentOrchestrator: Journal ahead of seed state " +
                    $"(journal.LastStepIndex={_journal.LastStepIndex}, seed.StepIndex={seed.StepIndex}) — " +
                    $"truncating phantom transitions to boundary={journalBoundary}.");
                _journal.TruncateAfter(journalBoundary);
            }

            // Replay + Journal backfill via the onTransition callback (Codex follow-up
            // post-#50 #C). For BEHIND crashes (SignalLog flushed, Journal not) this
            // rematerialises every missing StepIndex. For no-replay branches the callback
            // is simply never invoked; the journal stays aligned from the pre-replay
            // truncate step above.
            initialState = ReducerReplay.Replay(
                engine: replayEngine,
                seed: seed,
                signals: signalsToReplay,
                onTransition: _journal.Append);

            _logger.Info(
                $"EnrollmentOrchestrator: recovery branch={branchTag}, " +
                $"stage={initialState.Stage}, stepIndex={initialState.StepIndex}, " +
                $"signalsReplayed={signalsToReplay.Count}, " +
                $"journal.LastStepIndex={_journal.LastStepIndex}.");

            // 3) Telemetry-Transport. Batch size flows from AgentConfiguration.MaxBatchSize
            //    via Program.cs (P1 fix: previously the remote-config knob was merged but
            //    never applied, so tenants saw drainInterval/MaxBatchSize have no effect).
            _spool = new TelemetrySpool(_transportDirectory, _clock, _logger);
            _transport = new TelemetryUploadOrchestrator(_spool, _uploader, _clock, batchSize: _uploadBatchSize);

            // Late-bind the spool reference into the component factory so peripheral
            // collectors (AgentSelfMetricsCollector via PeriodicCollectorLifecycleHost) can
            // read PendingItemCount / SpoolFileSizeBytes for the agent_metrics_snapshot
            // payload. Done as a setter call rather than a CreateCollectorHosts parameter
            // because IComponentFactory is a public seam with test fakes — adding a
            // parameter would force every fake to be touched, and the spool stats are an
            // optional capability that test fakes don't need to surface.
            if (_componentFactory is DefaultComponentFactory defaultFactory)
            {
                defaultFactory.SetTelemetrySpool(_spool);
            }

            // 3a) Immediate-flush wakeup — lifecycle-critical items (agent_started, hello wizard,
            //     auth-failure shutdown, version-check, enrollment_failed on max-lifetime, …)
            //     enqueue with RequiresImmediateFlush=true. Without this bridge they would sit in
            //     the spool for up to _drainInterval before the periodic loop uploads them,
            //     producing the ~30 s "session-register then silence" gap at session start.
            //
            //     The signal TCS is installed BEFORE the subscription, and both BEFORE the drain
            //     task starts at step 3b — so any Enqueue fired during the rest of Start() (e.g.
            //     the EspConfigDetected bootstrap at step 13c or the synchronous Classifier-Start
            //     at step 11.5) sets the current signal rather than hitting a null field.
            _immediateFlushSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _immediateFlushBridge = OnImmediateFlushRequested;
            _spool.ImmediateFlushRequested += _immediateFlushBridge;

            // 3b) Periodic drain pump (fire-and-forget). Started here — directly after the spool
            //     and the immediate-flush bridge are wired — instead of after collector startup,
            //     because steps 4-14 do non-trivial synchronous work (gather-rule compile, Hello
            //     event-log subscriptions, PowerShell runspace open in DeliveryOptimizationCollector,
            //     ipinfo.io GeoLocation, analyzer scheduling, …). On a constrained VM that whole
            //     stretch ran ~6.7 s in session 3808befb-…, during which 100 startup signals
            //     accumulated in the spool. The first batch then hit the upload right when the
            //     pump finally got a turn, surfacing in the Web UI as "session opens, silence,
            //     then a wall of 100 events" rather than the V1 streaming feel.
            //
            //     With the pump live from step 3b onward, the very first immediate-flush wakeup
            //     (e.g. agent_started in onIngressReady at step 13b) sets the TCS and the drain
            //     loop's WhenAny returns within milliseconds, draining whatever the spool has
            //     accumulated up to that moment via DrainAllAsync's peek-flush-peek loop.
            //
            //     Safety: the loop only references _spool and _transport (both built at step 3),
            //     and the wakeup TCS (this step). No collector-state, classifier-state, or
            //     ingress dependency. An empty Peek returns OK(0) without an HTTP round-trip,
            //     so a pump tick that races ahead of any enqueue is a no-op.
            _drainCts = new CancellationTokenSource();
            _drainTask = Task.Run(() => DrainLoopAsync(_drainCts.Token));

            // 4) Event-Emitter-Kette. Single-rail (Plan §5.10): der TelemetryEventEmitter wird
            //    lokal gebaut und nur in die zwei erlaubten Caller (EventTimelineEmitter,
            //    BackPressureEventObserver) injiziert. Kein Feld mehr auf dem Orchestrator —
            //    strukturelle Abhängigkeit erlischt damit, Architektur-Baseline-Test gate
            //    shrinks to exactly two permitted callers.
            _eventSequenceCounter = new EventSequenceCounter(_eventSequencePersistence);
            var eventEmitter = new TelemetryEventEmitter(_transport, _eventSequenceCounter, _sessionId, _tenantId);
            _signalEmitter = new TelemetrySignalEmitter(_transport, _sessionId, _tenantId);
            _transitionEmitter = new TelemetryTransitionEmitter(_transport, _sessionId, _tenantId);
            _timelineEmitter = new EventTimelineEmitter(eventEmitter);
            _backPressureObserver = new BackPressureEventObserver(eventEmitter, _clock);

            // 5) Deadlines + Classifiers.
            _scheduler = _schedulerOverride ?? new DeadlineScheduler(_clock);
            _classifierRegistry = new ClassifierRegistry(_classifiers);

            // 6) Relay — EffectRunner braucht ISignalIngressSink, aber Ingress wird erst später
            //    gebaut. Relay löst den Zirkel über einen setzbaren Target-Pointer.
            _sinkRelay = new LazyIngressSinkRelay();

            // 7) EffectRunner.
            _effectRunner = new EffectRunner(
                scheduler: _scheduler,
                classifiers: _classifierRegistry,
                ingress: _sinkRelay,
                emitter: _timelineEmitter,
                snapshot: _snapshot,
                clock: _clock,
                logger: _logger);

            // 8) Processor — owns the initial state + journal + snapshot + quarantine hook +
            //    M4.6.β terminal-stage hook (fires Terminated event when the engine reaches a
            //    terminal SessionStage).
            _processor = new DecisionStepProcessor(
                initialState: initialState,
                journal: _journal,
                effectRunner: _effectRunner,
                snapshot: _snapshot,
                quarantineSink: this,
                logger: _logger,
                quarantineThreshold: _quarantineThreshold,
                onTerminalStageReached: OnDecisionTerminalStage,
                transitionEmitter: _transitionEmitter);

            // 9) Trace-Ordinal. Recovery-seed = max(SignalLog, Journal, Spool) so restart after a
            //    crash never re-uses a SessionTraceOrdinal already persisted by a prior session.
            //    Every signal + transition goes through the Spool in M5+, but we still consult all
            //    three sources because:
            //      - SignalLog may contain signals whose spool-enqueue later failed (emit swallows
            //        transport exceptions per SignalIngress.cs §2.7c).
            //      - Journal likewise for transitions with failed spool-enqueue.
            //      - Spool.LastAssignedItemId is the single-source-of-truth for everything that
            //        reached the transport (including Events that bypass the provider entirely).
            var traceSeed = System.Math.Max(
                System.Math.Max(_signalLog.LastTraceOrdinal, _journal.LastTraceOrdinal),
                _spool.LastAssignedItemId);
            _traceCounter = new SessionTraceOrdinalProvider(seedLastAssigned: traceSeed);

            // 10) DecisionEngine + SignalIngress.
            _engine = new DecisionEngine();
            _ingress = new SignalIngress(
                engine: _engine,
                signalLog: _signalLog,
                traceCounter: _traceCounter,
                processor: _processor,
                clock: _clock,
                backPressureObserver: _backPressureObserver,
                signalEmitter: _signalEmitter,
                channelCapacity: _channelCapacity,
                logger: _logger);

            // 11) Relay auf den echten Ingress umbiegen.
            _sinkRelay.Target = _ingress;

            // 12) Scheduler.Fired → synthetic DeadlineFired-Signal.
            _deadlineBridge = OnDeadlineFired;
            _scheduler.Fired += _deadlineBridge;

            // 13) Ingress-Worker starten (vor Collectors — sonst race-prone).
            _ingress.Start();

            // 13.5) Codex follow-up #1 / plan §3 Phase 3 — re-arm persisted deadlines.
            //       Without this any deadline armed pre-crash (HelloSafety, Part2Safety,
            //       FinalizingGrace, ClassifierTick, …) would silently disappear on restart
            //       and the session could hang forever. Must run AFTER _scheduler.Fired
            //       subscription (step 12) AND AFTER _ingress.Start (step 13) because
            //       past-due deadlines fire immediately via ThreadPool and the bridge
            //       handler posts synthetic DeadlineFired signals through _ingress. Running
            //       it before either one would either lose the fire (no subscriber) or drop
            //       the signal (OnDeadlineFired returns early when _ingress is null).
            //       Sits before the WG Part-2 bridge and before collectors start so
            //       rehydrated past-due fires precede any fresh adapter-generated signals.
            if (initialState.Deadlines.Count > 0)
            {
                _logger.Info(
                    $"EnrollmentOrchestrator: re-arming {initialState.Deadlines.Count} " +
                    $"persisted deadline(s) post-recovery.");
                try
                {
                    _scheduler.RehydrateFromSnapshot(initialState.Deadlines);
                }
                catch (Exception ex)
                {
                    // Codex follow-up (post-#50 #E): rehydrate failure is the same class of
                    // phantom-deadline hang as live ScheduleDeadline/CancelDeadline failures.
                    // DecisionState.Deadlines still claims the deadline exists, but the live
                    // scheduler never registered it — so only the max-lifetime watchdog would
                    // ever terminate the session. Post a synthetic EffectInfrastructureFailure
                    // with the v1 contract payload so the reducer transitions cleanly to
                    // Failed / EnrollmentFailed on the next worker tick. Ingress is already
                    // running at this point in Start(), so the signal gets the normal
                    // durable SignalLog.Append + Reduce treatment.
                    _logger.Error(
                        "EnrollmentOrchestrator: deadline rehydration failed; posting EffectInfrastructureFailure to terminate the session.",
                        ex);
                    try
                    {
                        var reason = $"deadline_rehydrate_failure: {ex.GetType().Name}: {ex.Message}";
                        _ingress.Post(
                            kind: DecisionSignalKind.EffectInfrastructureFailure,
                            occurredAtUtc: _clock.UtcNow,
                            sourceOrigin: "effectrunner:critical:ScheduleDeadline",
                            evidence: new Evidence(
                                kind: EvidenceKind.Synthetic,
                                identifier: "effect_infrastructure_failure:ScheduleDeadline",
                                summary: $"Critical effect ScheduleDeadline failed: {reason}"),
                            payload: new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["reason"] = reason,
                                ["failingEffect"] = nameof(DecisionEffectKind.ScheduleDeadline),
                            });
                    }
                    catch (Exception postEx)
                    {
                        _logger.Error(
                            "EnrollmentOrchestrator: failed to post EffectInfrastructureFailure after rehydrate failure — max-lifetime watchdog remains the last-resort terminator.",
                            postEx);
                    }
                }
            }

            // 13b) Caller-owned pre-collector hook. Single-rail refactor uses this slot to post
            //      agent-lifecycle signals (agent_started, agent_version_check, …) so they land
            //      on the signal log before any collector-generated signal — fixes the seq=13
            //      ordering regression from the V2 parity audit. Exceptions are caught so a
            //      malformed hook cannot abort Start or prevent collectors from running.
            if (onIngressReady != null)
            {
                try
                {
                    onIngressReady(_ingress);
                }
                catch (Exception ex)
                {
                    _logger.Error("EnrollmentOrchestrator: onIngressReady hook threw — continuing startup.", ex);
                }
            }

            // 13b-WG) WhiteGlove Part-2 resume marker event. V1-symmetric to
            //         MonitoringService.cs:518 — after Part-1 was sealed and the state
            //         archived in step 0, announce the resume on the session timeline so
            //         the backend can flip the session row from Pending → InProgress and
            //         downstream UI splitters can render a "User Enrollment Part 2"
            //         block. Posted AFTER the onIngressReady hook so agent_started lands
            //         first (matches V1 ordering).
            if (_isWhiteGlovePart2)
            {
                try
                {
                    var lifecyclePost = new InformationalEventPost(_ingress, _clock);
                    var resumedAtUtc = _clock.UtcNow;
                    lifecyclePost.Emit(
                        eventType: "whiteglove_resumed",
                        source: "Agent",
                        message: "WhiteGlove Part 2 resumed after reseal-reboot; Part-1 state archived.",
                        severity: Shared.Models.EventSeverity.Info,
                        immediateUpload: true,
                        data: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["previousStage"] = SessionStage.WhiteGloveSealed.ToString(),
                            ["resumedAtUtc"] = resumedAtUtc.ToString(
                                "o", System.Globalization.CultureInfo.InvariantCulture),
                        },
                        occurredAtUtc: resumedAtUtc,
                        sourceOrigin: "EnrollmentOrchestrator",
                        evidenceSummary:
                            "WhiteGlove Part-1 state archived; resuming as fresh Classic enrollment for Part-2.");
                }
                catch (Exception ex)
                {
                    _logger.Error("EnrollmentOrchestrator: failed to post whiteglove_resumed for Part-2 resume.", ex);
                }
            }

            // 13c) Plan §6 Fix 9 bootstrap — probe the FirstSync SkipUser/SkipDevice flags
            //      synchronously and post EspConfigDetected BEFORE any collector host starts.
            //      Rationale: DeviceInfoHost.CollectAll runs fire-and-forget on the ThreadPool,
            //      so on SkipUser=true enrollments the Shell-Core esp_exiting event can fire
            //      (via EspAndHelloHost which started here already) BEFORE the reducer has seen
            //      EspConfigDetected. Without this bootstrap, Fix 8's guard
            //      (ShouldTransitionToAwaitingHello) would block the legitimate AwaitingHello
            //      promotion because SkipUserEsp is still null in state — and the adapter's
            //      _finalizingPosted fire-once flag prevents a second attempt, leaving the
            //      session stuck in EspDeviceSetup/EspAccountSetup forever. Reducer has
            //      per-fact set-once semantics, so a later re-post from DeviceInfoCollector on
            //      CollectAll filling previously-missing facts is both allowed and safe.
            //      Always-on: production correctness > test convenience. Tests that count
            //      signals or transitions use <c>EspSkipConfigurationProbe.ScopedOverride</c>
            //      to force the probe to (null, null) which makes the bootstrap a no-op.
            PostEspConfigDetectedBootstrap();

            // 14) Collector-Hosts via Factory — nach Plan §5.10 (single-rail enforcement) gibt
            //     es keine Action<EnrollmentEvent>-Bridge mehr; jede Collector-Emission fließt
            //     über den Ingress als InformationalEvent.
            if (_componentFactory != null)
            {
                _collectorHosts = _componentFactory.CreateCollectorHosts(
                    _sessionId, _tenantId, _logger, _whiteGloveSealingPatternIds,
                    ingress: _ingress, clock: _clock);
                foreach (var host in _collectorHosts)
                {
                    try
                    {
                        host.Start();
                        _logger.Debug($"EnrollmentOrchestrator: started collector host '{host.Name}'.");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"EnrollmentOrchestrator: failed to start collector host '{host.Name}'.", ex);
                    }
                }
            }

            // 15) (moved) Periodic drain loop is now started at step 3b — directly after the
            //     spool / immediate-flush bridge are wired — so it does not have to wait for
            //     collector startup to finish. See the rationale at step 3b.

            // 16) Max-lifetime watchdog (M4.6.α). Fires once after the configured duration when
            //     no real terminal stage has been reached. Timer is a best-effort System.Threading.Timer
            //     because VirtualClock-driven delay would not map to an OS-level wall-clock wait.
            if (_agentMaxLifetime.HasValue)
            {
                _maxLifetimeTimer = new System.Threading.Timer(
                    state: null,
                    dueTime: _agentMaxLifetime.Value,
                    period: System.Threading.Timeout.InfiniteTimeSpan,
                    callback: _ => RaiseMaxLifetimeExceeded());
                _logger.Info($"EnrollmentOrchestrator: max-lifetime watchdog armed ({_agentMaxLifetime.Value.TotalMinutes:F0}min).");
            }

            // PR3-A3: previous "started." line had zero context. Add the cardinality info that
            // tells a forensic reader what the orchestrator is actually running (initial stage,
            // collector-host count, short session id) and how long max-lifetime is set for.
            var sessionShort = _sessionId != null && _sessionId.Length >= 8 ? _sessionId.Substring(0, 8) : (_sessionId ?? string.Empty);
            var initialStage = CurrentState?.Stage.ToString() ?? "Unknown";
            var hostCount = _collectorHosts?.Count ?? 0;
            var maxLifetimeMin = _agentMaxLifetime.HasValue ? _agentMaxLifetime.Value.TotalMinutes.ToString("F0") + "min" : "off";
            _logger.Info($"EnrollmentOrchestrator: started (sessionId={sessionShort}, initialStage={initialStage}, hosts={hostCount}, maxLifetime={maxLifetimeMin}).");
        }

        /// <summary>
        /// Plan §6 Fix 9 bootstrap. Probes <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c>
        /// synchronously and posts <see cref="DecisionSignalKind.EspConfigDetected"/> so the
        /// reducer's <c>SkipUserEsp</c> / <c>SkipDeviceEsp</c> facts are set BEFORE any
        /// collector-driven <c>EspPhaseChanged(FinalizingSetup)</c> can arrive from
        /// <c>EspAndHelloHost</c>. No-op when neither flag can be read (FirstSync missing or
        /// registry probe fails) — the reducer's guards treat <c>null</c> as "unknown" and
        /// defensively keep the current stage, which is also the correct behavior in that
        /// edge case (the collector's subsequent <c>CollectAll</c> post will pick up the
        /// values once FirstSync populates).
        /// </summary>
        private void PostEspConfigDetectedBootstrap()
        {
            try
            {
                var (skipUser, skipDevice) = Monitoring.Enrollment.SystemSignals.EspSkipConfigurationProbe.Read(_logger);
                if (skipUser == null && skipDevice == null)
                {
                    _logger.Debug(
                        "EnrollmentOrchestrator: EspConfigDetected bootstrap skipped — FirstSync not yet populated; DeviceInfoCollector will post when CollectAll runs.");
                    return;
                }

                var payload = new Dictionary<string, string>(StringComparer.Ordinal);
                if (skipUser.HasValue)
                    payload[SignalPayloadKeys.SkipUserEsp] = skipUser.Value ? "true" : "false";
                if (skipDevice.HasValue)
                    payload[SignalPayloadKeys.SkipDeviceEsp] = skipDevice.Value ? "true" : "false";

                _ingress!.Post(
                    kind: DecisionSignalKind.EspConfigDetected,
                    occurredAtUtc: _clock.UtcNow,
                    sourceOrigin: "EnrollmentOrchestrator",
                    evidence: new Evidence(
                        kind: EvidenceKind.Raw,
                        identifier: "esp_config_detected_bootstrap",
                        summary: $"SkipUser={skipUser?.ToString() ?? "unknown"}, SkipDevice={skipDevice?.ToString() ?? "unknown"}",
                        derivationInputs: new Dictionary<string, string>(payload, StringComparer.Ordinal)
                        {
                            ["source"] = "registry_firstsync_bootstrap",
                        }),
                    payload: payload);

                _logger.Info(
                    $"EnrollmentOrchestrator: EspConfigDetected bootstrap posted (SkipUser={skipUser?.ToString() ?? "unknown"}, SkipDevice={skipDevice?.ToString() ?? "unknown"}).");
            }
            catch (Exception ex)
            {
                // Never fail Start over a bootstrap probe — the DeviceInfoCollector is the fallback.
                _logger.Error("EnrollmentOrchestrator: EspConfigDetected bootstrap threw — continuing startup.", ex);
            }
        }

        private void RaiseMaxLifetimeExceeded()
        {
            if (Volatile.Read(ref _stopRequested) == 1) return;
            if (Interlocked.Exchange(ref _terminatedFired, 1) == 1) return;

            _logger.Warning(
                $"EnrollmentOrchestrator: max-lifetime ({_agentMaxLifetime?.TotalMinutes:F0}min) exceeded — firing Terminated(MaxLifetimeExceeded).");

            var currentStage = _processor?.CurrentState?.Stage.ToString();

            var terminatedArgs = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.MaxLifetimeExceeded,
                outcome: EnrollmentTerminationOutcome.TimedOut,
                stageName: currentStage,
                terminatedAtUtc: _clock.UtcNow,
                details: $"Agent exceeded AgentMaxLifetimeMinutes cap ({_agentMaxLifetime?.TotalMinutes:F0}min) without reaching a terminal stage.");

            try { Terminated?.Invoke(this, terminatedArgs); }
            catch (Exception ex) { _logger.Error("EnrollmentOrchestrator: Terminated handler threw.", ex); }
        }

        /// <summary>
        /// <summary>
        /// Recovery helper — collect all SignalLog entries whose <c>SessionSignalOrdinal</c>
        /// is strictly greater than the snapshot's last-consumed ordinal. Signals equal-or-less
        /// are already baked into the seed state and must never be re-applied.
        /// </summary>
        private IReadOnlyList<DecisionSignal> CollectSignalLogTailAfter(long lastConsumedOrdinal)
        {
            var all = _signalLog!.ReadAll();
            var tail = new List<DecisionSignal>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].SessionSignalOrdinal > lastConsumedOrdinal)
                {
                    tail.Add(all[i]);
                }
            }
            return tail;
        }

        /// <summary>
        /// M4.6.β — DecisionStepProcessor callback: the engine has transitioned the session
        /// into a terminal <see cref="SessionStage"/>. Fires the public <see cref="Terminated"/>
        /// event with <see cref="EnrollmentTerminationReason.DecisionTerminalStage"/> and an
        /// outcome derived from the stage (Completed/Part2→Succeeded, Failed→Failed,
        /// WhiteGloveSealed→Succeeded but with <see cref="SessionStageExtensions.IsPauseBeforePart2"/>
        /// signalled to callers via the stage name — callers decide whether to self-destruct).
        /// <para>
        /// <b>Codex Finding 2 (2026-04-30)</b> — the <see cref="Terminated"/> handler is
        /// dispatched on a thread-pool task instead of synchronously, because this callback
        /// fires on the SignalIngress worker thread. A synchronous handler that posts
        /// lifecycle events back to the ingress (e.g. <c>agent_shutting_down</c>,
        /// <c>whiteglove_part1_complete</c>) would enqueue items the worker can only
        /// process AFTER the handler returns — making any in-handler "wait for ingress to
        /// drain" impossible (deadlock) and any "wait for spool to drain" trivially
        /// returning before the just-posted events are even reduced. Off-worker dispatch
        /// frees the worker to keep processing while the handler waits.
        /// </para>
        /// </summary>
        private void OnDecisionTerminalStage(DecisionState terminalState)
        {
            if (Volatile.Read(ref _stopRequested) == 1) return;
            if (Interlocked.Exchange(ref _terminatedFired, 1) == 1) return;

            // Stop the max-lifetime watchdog — the real terminal arrived before it could trip.
            try { _maxLifetimeTimer?.Dispose(); _maxLifetimeTimer = null; } catch { }

            var outcome = terminalState.Stage switch
            {
                SessionStage.Completed or SessionStage.WhiteGloveSealed
                    => EnrollmentTerminationOutcome.Succeeded,
                SessionStage.Failed => EnrollmentTerminationOutcome.Failed,
                _ => EnrollmentTerminationOutcome.TimedOut,
            };

            _logger.Info(
                $"EnrollmentOrchestrator: decision terminal stage reached (stage={terminalState.Stage}, outcome={outcome}) — dispatching Terminated(DecisionTerminalStage) off worker.");

            var terminatedArgs = new EnrollmentTerminatedEventArgs(
                reason: EnrollmentTerminationReason.DecisionTerminalStage,
                outcome: outcome,
                stageName: terminalState.Stage.ToString(),
                terminatedAtUtc: _clock.UtcNow,
                details: terminalState.Stage.IsPauseBeforePart2()
                    ? "WhiteGlove Part 1 sealed — session will resume on Part 2 post-reboot; self-destruct suppressed."
                    : null);

            // Codex Finding 2 — off-worker dispatch. Task.Run unblocks the ingress worker
            // immediately; the worker keeps draining the channel (including events the
            // termination handler is about to post). The handler's _signalShutdown still
            // fires from its finally block on whatever exception path, so an unhandled
            // throw inside the handler does NOT prevent the agent from shutting down.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { Terminated?.Invoke(this, terminatedArgs); }
                catch (Exception ex) { _logger.Error("EnrollmentOrchestrator: Terminated handler threw on background task.", ex); }
            });
        }

        /// <summary>
        /// Stop and dispose the collector hosts ahead of full <see cref="Stop"/>. Idempotent —
        /// callable from <see cref="EnrollmentTerminationHandler"/> right before the
        /// diagnostics ZIP is built so periodic collectors (PerformanceCollector,
        /// AgentSelfMetricsCollector, …) don't keep emitting <c>performance_snapshot</c> /
        /// <c>agent_metrics_snapshot</c> events while the package is being assembled.
        /// </summary>
        public void StopCollectorHosts()
        {
            if (Interlocked.Exchange(ref _collectorHostsStopped, 1) == 1) return;
            if (_collectorHosts == null) return;

            foreach (var host in _collectorHosts)
            {
                try { host.Stop(); }
                catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: host '{host.Name}' stop failed: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Stop the pipeline. Idempotent; calls <see cref="Stop"/> from <see cref="Dispose"/> as well.
        /// </summary>
        public void Stop()
        {
            if (Volatile.Read(ref _started) == 0) return;
            if (Interlocked.Exchange(ref _stopRequested, 1) == 1) return;

            // PR3-A3: include current stage + observed signal count so forensic readers can see
            // what the orchestrator was working on at stop time.
            var stopStage = CurrentState?.Stage.ToString() ?? "Unknown";
            var stopSignalsApplied = CurrentState?.LastAppliedSignalOrdinal ?? -1;
            _logger.Info($"EnrollmentOrchestrator: stopping (currentStage={stopStage}, signalsApplied={stopSignalsApplied + 1}).");

            // -1) Max-lifetime watchdog stoppen (M4.6.α). Idempotent — safe even if never armed.
            try { _maxLifetimeTimer?.Dispose(); _maxLifetimeTimer = null; }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: max-lifetime timer dispose failed: {ex.Message}"); }

            // 0) Collector-Hosts stoppen — keine neuen Events / DecisionSignals aus dem Feld.
            //    No-op when EnrollmentTerminationHandler already drained them via
            //    StopCollectorHosts() before the diagnostics ZIP was built.
            StopCollectorHosts();

            // 1) Scheduler.Fired-Handler abmelden — keine neuen DeadlineFired-Signals mehr.
            try
            {
                if (_scheduler != null && _deadlineBridge != null)
                {
                    _scheduler.Fired -= _deadlineBridge;
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: unsubscribe scheduler failed: {ex.Message}"); }

            // 1a) Immediate-flush-Bridge abmelden — spätere Enqueues sollen den Drain-Loop nicht
            //     wecken wenn wir ihn gerade terminieren.
            try
            {
                if (_spool != null && _immediateFlushBridge != null)
                {
                    _spool.ImmediateFlushRequested -= _immediateFlushBridge;
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: unsubscribe immediate-flush failed: {ex.Message}"); }

            // 2) Scheduler disposen — stoppt alle aktiven Timer.
            try { _scheduler?.Dispose(); }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: scheduler dispose failed: {ex.Message}"); }

            // 3) SignalIngress stoppen — drainiert verbleibende Items, wartet auf Worker-Join.
            try { _ingress?.Stop(); }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: ingress stop failed: {ex.Message}"); }

            // 4) Drain-Loop Token cancellen + warten.
            try
            {
                _drainCts?.Cancel();
                _drainTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException) { /* wrapped OperationCanceledException */ }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: drain loop join failed: {ex.Message}"); }

            // 5) Terminaler Drain — finale Batches.
            try
            {
                using (var timeoutCts = new CancellationTokenSource(_terminalDrainTimeout))
                {
                    _transport?.DrainAllAsync(timeoutCts.Token).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: terminal drain failed: {ex.Message}"); }

            // 6) Finales Snapshot.
            try
            {
                if (_processor != null && _snapshot != null)
                {
                    _snapshot.Save(_processor.CurrentState);
                }
            }
            catch (Exception ex) { _logger.Warning($"EnrollmentOrchestrator: final snapshot save failed: {ex.Message}"); }

            // 7) Dispose aller Disposables.
            if (_collectorHosts != null)
            {
                foreach (var host in _collectorHosts)
                {
                    try { host.Dispose(); } catch { /* best-effort */ }
                }
            }
            try { _transport?.Dispose(); } catch { /* best-effort */ }
            try { _ingress?.Dispose(); } catch { /* best-effort */ }
            try { _drainCts?.Dispose(); } catch { /* best-effort */ }

            _logger.Info("EnrollmentOrchestrator: stopped.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { Stop(); }
            catch { /* Dispose darf nicht werfen. */ }
        }

        // ---------------------------------------------------------------- IQuarantineSink

        public void TriggerQuarantine(string reason)
        {
            _quarantineRequested = true;
            _quarantineReason = reason ?? string.Empty;
            _logger.Error($"EnrollmentOrchestrator: quarantine requested — {reason}");
            // Actual segment-quarantine happens on next Start (M4.4.5.f), not mid-run.
        }

        // ---------------------------------------------------------------- Test helpers

        /// <summary>
        /// Explicit drain for tests. Returns the <see cref="DrainResult"/> of one flush cycle.
        /// </summary>
        internal Task<DrainResult> DrainAsync(CancellationToken cancellationToken = default)
        {
            if (_transport == null) throw new InvalidOperationException("Orchestrator not started.");
            return _transport.DrainAllAsync(cancellationToken);
        }

        // ---------------------------------------------------------------- Private

        private void EnsureDirectories()
        {
            if (!Directory.Exists(_stateDirectory)) Directory.CreateDirectory(_stateDirectory);
            if (!Directory.Exists(_transportDirectory)) Directory.CreateDirectory(_transportDirectory);
        }

        private void OnDeadlineFired(object? sender, DeadlineFiredEventArgs e)
        {
            if (Volatile.Read(ref _stopRequested) == 1 || _ingress == null) return;

            try
            {
                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: e.Deadline.Name,
                    summary: $"deadline '{e.Deadline.Name}' fired at {e.Deadline.DueAtUtc:O}");

                // The reducer reads the deadline name under SignalPayloadKeys.Deadline
                // (see HandleDeadlineFiredV1 in DecisionEngine.Shared). Forward the
                // originating ActiveDeadline.FiresPayload — it already carries that key
                // (see e.g. DecisionEngine.Classic.TransitionToFinalizing). Using a
                // different key here dead-ends every deadline with "deadline_fired_without_name".
                var payload = new Dictionary<string, string>(StringComparer.Ordinal);
                if (e.Deadline.FiresPayload != null)
                {
                    foreach (var kv in e.Deadline.FiresPayload)
                    {
                        payload[kv.Key] = kv.Value;
                    }
                }

                if (!payload.ContainsKey(SignalPayloadKeys.Deadline))
                {
                    payload[SignalPayloadKeys.Deadline] = e.Deadline.Name;
                }

                // OccurredAtUtc = DueAtUtc (not firedAt) — replay-determinism per DeadlineFiredEventArgs doc.
                _ingress.Post(
                    kind: DecisionSignalKind.DeadlineFired,
                    occurredAtUtc: e.Deadline.DueAtUtc,
                    sourceOrigin: "DeadlineScheduler",
                    evidence: evidence,
                    payload: payload);
            }
            catch (Exception ex)
            {
                // Deadline-Bridge darf den Scheduler-Thread nicht killen.
                _logger.Error($"EnrollmentOrchestrator: failed to post DeadlineFired for '{e.Deadline.Name}'.", ex);
            }
        }

        private void OnImmediateFlushRequested(object? sender, EventArgs e)
        {
            // Release the drain loop's current iteration early. Multiple rapid wakeups coalesce
            // naturally into a single drain (WhenAny returns once; the next iteration installs a
            // fresh TCS). TrySetResult is idempotent, so the second wakeup is a no-op.
            Volatile.Read(ref _immediateFlushSignal)?.TrySetResult(true);
        }

        private async Task DrainLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Read the current wakeup signal. Step 3a installs the very first TCS
                    // BEFORE subscribing the bridge and before the drain task starts; every
                    // subsequent iteration receives a freshly-installed TCS from the drain
                    // that just completed below. OnImmediateFlushRequested races with
                    // Task.Delay so RequiresImmediateFlush=true items reach the backend within
                    // milliseconds instead of waiting for the full _drainInterval window.
                    var signal = Volatile.Read(ref _immediateFlushSignal);
                    if (signal == null)
                    {
                        // Defensive: if Start() didn't seed the TCS (should never happen), fall
                        // back to a periodic-only drain so the loop still makes progress.
                        signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Volatile.Write(ref _immediateFlushSignal, signal);
                    }

                    try
                    {
                        // Wall-clock delay, NOT _clock.Delay: drain is a real network cadence
                        // independent of the decision-engine's logical time. VirtualClock.Delay
                        // returns immediately and would cause a tight loop in unit tests.
                        var delayTask = Task.Delay(_drainInterval, ct);
                        await Task.WhenAny(delayTask, signal.Task).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }

                    if (ct.IsCancellationRequested) return;

                    // Swap in a fresh TCS BEFORE draining so wakeups raised during this drain
                    // land on the next iteration's signal rather than on the stale completed
                    // TCS (which TrySetResult is a no-op for, losing the wakeup). The tiny
                    // window between WhenAny returning and this Volatile.Write is acceptable:
                    // a wakeup there sees the just-completed TCS, is dropped, but we're
                    // already about to drain in the same iteration — so the item ships anyway.
                    Volatile.Write(
                        ref _immediateFlushSignal,
                        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

                    try
                    {
                        var result = await _transport!.DrainAllAsync(ct).ConfigureAwait(false);
                        if (result.FailedBatches > 0)
                        {
                            _logger.Warning(
                                $"EnrollmentOrchestrator: periodic drain had {result.FailedBatches} failed batch(es); " +
                                $"last error: {result.LastErrorReason}");
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        _logger.Error("EnrollmentOrchestrator: periodic drain threw.", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentOrchestrator: drain loop faulted.", ex);
            }
        }

        /// <summary>
        /// Pass-through <see cref="ISignalIngressSink"/> to break the Effect-↔-Ingress ctor
        /// cycle: EffectRunner requires a sink, but the real Ingress wants the processor +
        /// effect-runner to already exist. Relay is constructed first, wired to the real
        /// Ingress last.
        /// </summary>
        private sealed class LazyIngressSinkRelay : ISignalIngressSink
        {
            internal ISignalIngressSink? Target { get; set; }

            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                var t = Target;
                if (t == null)
                {
                    throw new InvalidOperationException(
                        "LazyIngressSinkRelay.Target is null — SignalIngress was not wired yet.");
                }
                t.Post(kind, occurredAtUtc, sourceOrigin, evidence, payload, kindSchemaVersion, typedPayload);
            }
        }
    }
}
