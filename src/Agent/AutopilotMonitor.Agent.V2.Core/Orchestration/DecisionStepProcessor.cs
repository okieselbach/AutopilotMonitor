#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Transitions;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Produktions-<see cref="IDecisionStepProcessor"/>. Plan §2.5 / §2.7 / L.1.
    /// <para>
    /// Sequence pro <see cref="ApplyStep"/>:
    /// <list type="number">
    ///   <item><see cref="IJournalWriter.Append"/> (Sofort-Flush, L.12) — wenn das wirft,
    ///         ist der Step nicht committed und der Failure-Counter zählt hoch.</item>
    ///   <item><see cref="IEffectRunner.RunAsync"/> synchron (sync-over-async auf
    ///         Ingress-Worker-Thread — kein SynchronizationContext, kein Deadlock-Risiko).
    ///         <see cref="EffectRunResult.SessionMustAbort"/> und Failures werden geloggt;
    ///         kein Throw, da EffectRunner bereits alle Failure-Klassen sauber mapped.</item>
    ///   <item><see cref="ISnapshotPersistence.Save"/> best-effort — Exception wird
    ///         geloggt, aber der Step ist trotzdem committed (Journal ist die Wahrheit).</item>
    ///   <item><see cref="CurrentState"/> wird auf <see cref="DecisionStep.NewState"/>
    ///         fortgesetzt; Failure-Counter reset.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Quarantine-Eskalation</b>: Journal-Failures werden gezählt. Nach
    /// <see cref="_quarantineThreshold"/> aufeinanderfolgenden Failures (default 3) ruft der
    /// Processor <see cref="IQuarantineSink.TriggerQuarantine"/> und wirft weiter. Der
    /// Ingress-Worker schluckt die Exception (SignalIngress.cs:300-307), aber die
    /// Quarantine-Senke hat dann den Alarm gespeichert → nächstes Agent-Start räumt auf.
    /// </para>
    /// <para>
    /// <b>Thread-Modell</b>: Einzelner Schreiber — Ingress-Worker-Thread. Keine Locks nötig.
    /// Lesen von <see cref="CurrentState"/> erfolgt aus demselben Thread direkt vor dem
    /// nächsten Reduce, ebenfalls unlockbar sicher.
    /// </para>
    /// </summary>
    public sealed class DecisionStepProcessor : IDecisionStepProcessor, IDisposable
    {
        /// <summary>Default — 3 Failures hintereinander → Quarantine.</summary>
        public const int DefaultQuarantineThreshold = 3;

        /// <summary>
        /// M1 (delta review 2026-07-02) — dwell before the parked tripwire fires. The parked
        /// predicate is true during a HEALTHY transient window: Classic HelloResolved (or the
        /// HelloSafety timeout) cancels the only armed deadline and moves to AwaitingDesktop
        /// seconds-to-minutes before DesktopArrived; firing on the step that produces that state
        /// is a false alarm AND consumes the one-shot, masking a real variant-4 park later in
        /// the run. 10 min comfortably clears DesktopArrivalDetector's validation latency while
        /// staying far below the 360-min max-lifetime watchdog.
        /// </summary>
        public static readonly TimeSpan DefaultParkedTripwireDwell = TimeSpan.FromMinutes(10);

        /// <summary>
        /// H1a (Wave 2) — the snapshot is a recovery CACHE, not the source of truth (SignalLog +
        /// Journal are, both still fsync'd per step). Pure InformationalEvent pass-throughs (Stage
        /// unchanged + only a telemetry <see cref="DecisionEffectKind.EmitEventTimelineEntry"/>
        /// effect) skip the snapshot — they are replayed from the SignalLog as deterministic state
        /// reconstruction on the next start. This floor forces a snapshot at least every N such
        /// steps so the worst-case restart replay tail stays bounded.
        /// </summary>
        public const int SnapshotPassThroughFloor = 100;

        private readonly IJournalWriter _journal;
        private readonly IEffectRunner _effectRunner;
        private readonly ISnapshotPersistence _snapshot;
        private readonly IQuarantineSink _quarantineSink;
        private readonly AgentLogger _logger;
        private readonly int _quarantineThreshold;
        private readonly Action<DecisionState>? _onTerminalStageReached;
        private readonly TelemetryTransitionEmitter? _transitionEmitter;
        private readonly InformationalEventPost? _informationalEvents;

        private DecisionState _currentState;
        private int _consecutiveJournalFailures;
        private bool _quarantineTriggered;
        private bool _terminalNotified;
        private int _passThroughStepsSinceSnapshot; // H1a — pass-through steps skipped since last snapshot
        private bool _parkedTripwireFired; // liveness PR1 — one-shot per agent run, no state-schema touch

        // M1 — parked-tripwire dwell machinery. The timer thread is also what fixes L8: the
        // tripwire post no longer runs on the ingress worker, so a full channel cannot
        // self-deadlock on it. Guarded by _parkedLock because the callback races ApplyStep.
        private readonly object _parkedLock = new object();
        private readonly TimeSpan _parkedTripwireDwell;
        private Timer? _parkedDwellTimer;
        private DecisionSignal? _parkedCandidateSignal;
        private bool _disposed;

        public DecisionStepProcessor(
            DecisionState initialState,
            IJournalWriter journal,
            IEffectRunner effectRunner,
            ISnapshotPersistence snapshot,
            IQuarantineSink quarantineSink,
            AgentLogger logger,
            int quarantineThreshold = DefaultQuarantineThreshold,
            Action<DecisionState>? onTerminalStageReached = null,
            TelemetryTransitionEmitter? transitionEmitter = null,
            InformationalEventPost? informationalEvents = null,
            TimeSpan? parkedTripwireDwell = null)
        {
            if (quarantineThreshold <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(quarantineThreshold),
                    "Threshold must be > 0.");
            }

            _currentState = initialState ?? throw new ArgumentNullException(nameof(initialState));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _effectRunner = effectRunner ?? throw new ArgumentNullException(nameof(effectRunner));
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _quarantineSink = quarantineSink ?? throw new ArgumentNullException(nameof(quarantineSink));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _quarantineThreshold = quarantineThreshold;
            _onTerminalStageReached = onTerminalStageReached;
            _transitionEmitter = transitionEmitter;
            _informationalEvents = informationalEvents;
            _parkedTripwireDwell = parkedTripwireDwell ?? DefaultParkedTripwireDwell;

            // If recovery loaded a state that already sits on a terminal stage (e.g. a crash
            // after a success-path step but before Stop()), treat it as already-notified so we
            // do not re-fire the hook.
            _terminalNotified = initialState.Stage.IsTerminal();
        }

        public DecisionState CurrentState => _currentState;

        /// <summary>Test-Observability — anzahl aufeinanderfolgender Journal-Failures.</summary>
        public int ConsecutiveJournalFailureCount => _consecutiveJournalFailures;

        public EffectRunResult ApplyStep(DecisionStep step, DecisionSignal signal)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (signal == null) throw new ArgumentNullException(nameof(signal));

            // PR3-D1: capture pre-step stage so the post-step log line can show the transition.
            var previousStage = _currentState.Stage;

            // 1) Journal-Append — einziger Fehler-Pfad der hart wirft + Quarantine eskaliert.
            try
            {
                _journal.Append(step.Transition);
            }
            catch (Exception ex)
            {
                _consecutiveJournalFailures++;
                _logger.Error(
                    $"DecisionStepProcessor: journal append failed (consecutive={_consecutiveJournalFailures}/{_quarantineThreshold}) " +
                    $"for signal ordinal={signal.SessionSignalOrdinal} kind={signal.Kind}.",
                    ex);

                if (_consecutiveJournalFailures >= _quarantineThreshold && !_quarantineTriggered)
                {
                    _quarantineTriggered = true;
                    TryTriggerQuarantine(
                        $"journal append failed {_consecutiveJournalFailures}x consecutively; " +
                        $"last signal ordinal={signal.SessionSignalOrdinal} kind={signal.Kind}");
                }

                throw;
            }

            // 1a) Project the transition onto the telemetry transport for backend upload. Journal
            // is authoritative (§2.7c / L.1); a transport enqueue failure here must NOT abort the
            // step — the local journal already committed and effects below must still run.
            if (_transitionEmitter != null)
            {
                try { _transitionEmitter.Emit(step.Transition); }
                catch { /* best-effort upload; local state already consistent */ }
            }

            // 2) EffectRunner — Async-Methode vom Ingress-Worker-Thread sync ausführen.
            // Kein SynchronizationContext auf einem reinen Background-Thread, deshalb
            // GetAwaiter().GetResult() hier deadlock-frei (L.1 Ingress-Worker-Thread).
            EffectRunResult effectResult;
            try
            {
                effectResult = _effectRunner
                    .RunAsync(step.Effects, step.NewState, signal.OccurredAtUtc, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                // EffectRunner.RunAsync ist vertraglich exception-frei (Plan §2.7b); ein
                // unerwartetes Throw loggen wir hart, lassen den Step aber committed (Journal
                // ist bereits persistiert). Weiterwerfen würde den Ingress-Worker stoppen.
                _logger.Error(
                    $"DecisionStepProcessor: effect runner threw unexpectedly for signal ordinal={signal.SessionSignalOrdinal}.",
                    ex);
                effectResult = EffectRunResult.Empty();
            }

            if (effectResult.SessionMustAbort)
            {
                // Codex follow-up (post-#50 #B): the phantom state must NOT be snapshotted
                // because its ActiveDeadline was never actually armed on the live scheduler.
                // Recovery from a phantom snapshot would try to re-arm and re-fail, leaving
                // the session dangling for the max-lifetime watchdog. Responsibility for
                // flipping the session to EnrollmentFailed lives with the caller
                // (SignalIngress): it synthesises + DURABLY appends the
                // EffectInfrastructureFailure signal, re-enters ApplyStep, and the terminal
                // step's snapshot replaces the stale N-1 snapshot.
                _logger.Error(
                    $"DecisionStepProcessor: effect run signaled session abort " +
                    $"(reason='{effectResult.AbortReason}') for signal ordinal={signal.SessionSignalOrdinal}; " +
                    $"skipping snapshot of phantom state — caller will synthesise EffectInfrastructureFailure durably.");
            }
            else
            {
                if (effectResult.Failures.Count > 0)
                {
                    _logger.Warning(
                        $"DecisionStepProcessor: effect run completed with {effectResult.Failures.Count} non-fatal failure(s) " +
                        $"for signal ordinal={signal.SessionSignalOrdinal}.");
                }

                // 3) Snapshot — best-effort recovery CACHE (Journal ist die Wahrheit). H1a: only
                //    cache on a meaningful step (Stage transition / non-telemetry effect) or once
                //    every SnapshotPassThroughFloor pass-through steps. Pure InformationalEvent
                //    pass-throughs are skipped — replayed from the SignalLog (pure state
                //    reconstruction, no effects) on the next start, so nothing is lost.
                if (ShouldSnapshot(step, previousStage))
                {
                    try
                    {
                        _snapshot.Save(step.NewState);
                        _passThroughStepsSinceSnapshot = 0;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(
                            $"DecisionStepProcessor: snapshot save failed (transient, not escalated) " +
                            $"for signal ordinal={signal.SessionSignalOrdinal}: {ex.Message}");
                        // Floor counter intentionally NOT reset — a failed save leaves the cache
                        // stale, so the next step should attempt the snapshot again.
                    }
                }
                else
                {
                    _passThroughStepsSinceSnapshot++;
                }
            }

            // 4) State-Forward + Counter-Reset. Happens regardless of abort so the caller's
            //    follow-up synthetic signal reduces FROM the correct pre-terminal state.
            _currentState = step.NewState;
            _consecutiveJournalFailures = 0;

            // PR3-D1: per-step observability. Stage transitions and effect-bearing steps fire
            // at DEBUG (operator-relevant); pure no-op steps (Stage unchanged + no effects)
            // fire at VERBOSE so a steady stream of e.g. ClassifierTick + InformationalEvent
            // pass-throughs doesn't drown the log.
            var newStage = step.NewState.Stage;
            if (previousStage != newStage || step.Effects.Count > 0)
            {
                _logger.Debug(
                    $"DecisionStep: ord={signal.SessionSignalOrdinal} kind={signal.Kind} " +
                    $"stage={previousStage}->{newStage} effects=[{string.Join(",", step.Effects.Select(e => e.Kind))}] " +
                    $"abort={effectResult.SessionMustAbort} failures={effectResult.Failures.Count}");
            }
            else
            {
                _logger.Verbose($"DecisionStep: ord={signal.SessionSignalOrdinal} kind={signal.Kind} stage={previousStage} (no-op)");
            }

            // 5) Terminal-stage detection (M4.6.β). Fires exactly once per agent run when the
            //    DecisionEngine transitions the session into a terminal SessionStage — the
            //    orchestrator turns this into the public EnrollmentTerminated event so peripheral
            //    consumers (CleanupService, SummaryDialog, DiagnosticsPackageService) can react
            //    without touching the kernel state machine.
            if (!_terminalNotified && _currentState.Stage.IsTerminal())
            {
                _terminalNotified = true;
                try { _onTerminalStageReached?.Invoke(_currentState); }
                catch (Exception ex)
                {
                    _logger.Error(
                        $"DecisionStepProcessor: onTerminalStageReached handler threw for stage {_currentState.Stage}.",
                        ex);
                }
            }

            // 6) "No silent parking" tripwire (liveness plan PR1). The engine is edge-triggered:
            //    facts get recorded, but nothing guarantees another signal ever re-asks the
            //    completion question — a session can be parked with no deadline armed. The three
            //    known dead-end variants (8bc1180f, caa6cf50, 1ec8f4c6) are covered by dedicated
            //    arming sites; this invariant check is the tripwire for variant 4. Expected to
            //    NEVER fire in production — every occurrence is a bug report. Skipped on abort
            //    steps: the caller is about to synthesise a terminal signal anyway.
            //
            //    M1 (delta review 2026-07-02): the predicate is true during a HEALTHY transient
            //    window (HelloResolved → AwaitingDesktop, no deadline, desktop arrives seconds
            //    later), so a parked state only ARMS a dwell timer here; the tripwire fires —
            //    and consumes its one-shot — only if the session is STILL parked when the dwell
            //    elapses. Any step that leaves the parked condition cancels the pending arm.
            if (_informationalEvents != null && !_parkedTripwireFired)
            {
                if (!effectResult.SessionMustAbort && IsParkedWithoutDeadline(_currentState))
                    ArmParkedDwell(signal);
                else
                    CancelParkedDwell();
            }

            return effectResult;
        }

        /// <summary>
        /// Arms the parked dwell timer if not already pending. Deliberately does NOT re-base an
        /// already-armed timer: a truly parked session still produces InformationalEvent
        /// pass-through steps (perf snapshots, IME telemetry), and re-basing on each of them
        /// would push the dwell forever. The first parked step of an episode starts the clock;
        /// only a step that clears the parked condition resets it.
        /// </summary>
        private void ArmParkedDwell(DecisionSignal signal)
        {
            lock (_parkedLock)
            {
                if (_disposed || _parkedTripwireFired || _parkedDwellTimer != null) return;
                _parkedCandidateSignal = signal;
                _parkedDwellTimer = new Timer(OnParkedDwellElapsed, null, _parkedTripwireDwell, Timeout.InfiniteTimeSpan);
            }
        }

        private void CancelParkedDwell()
        {
            lock (_parkedLock)
            {
                if (_parkedDwellTimer == null) return;
                _parkedDwellTimer.Dispose();
                _parkedDwellTimer = null;
                _parkedCandidateSignal = null;
            }
        }

        private void OnParkedDwellElapsed(object? _)
        {
            try
            {
                DecisionState state;
                DecisionSignal? armingSignal;
                lock (_parkedLock)
                {
                    if (_disposed || _parkedTripwireFired || _parkedDwellTimer == null) return;
                    _parkedDwellTimer.Dispose();
                    _parkedDwellTimer = null;
                    armingSignal = _parkedCandidateSignal;
                    _parkedCandidateSignal = null;

                    // Re-check against the LIVE state: a step that resolved the park in the last
                    // instant may not have reached CancelParkedDwell yet. _currentState is an
                    // immutable object written by the single ingress worker; the reference read
                    // is atomic.
                    state = _currentState;
                    if (armingSignal == null || !IsParkedWithoutDeadline(state)) return;
                    _parkedTripwireFired = true;
                }

                EmitParkedTripwire(state, armingSignal);
            }
            catch (Exception ex)
            {
                // Observability must never break anything — and this runs on a timer thread.
                _logger.Error("DecisionStepProcessor: parked-dwell evaluation failed.", ex);
            }
        }

        public void Dispose()
        {
            lock (_parkedLock)
            {
                if (_disposed) return;
                _disposed = true;
                _parkedDwellTimer?.Dispose();
                _parkedDwellTimer = null;
                _parkedCandidateSignal = null;
            }
        }

        /// <summary>
        /// Liveness invariant (plan PR1): a non-terminal session that has entered the
        /// post-AccountSetup dead-end zone must always have a resolution-capable deadline armed.
        /// The dead-end zone begins when the ESP side is gone or has failed — i.e. the ESP final
        /// exit happened at-or-after AccountSetup entry, OR an advisory-defanged failure was
        /// recorded — because from that point on no further ESP/IME registry signal is guaranteed
        /// to arrive. <see cref="DeadlineNames.ClassifierTick"/> does not count: it re-arms itself
        /// and never resolves a session, so it would mask the invariant. DeviceSetup-phase hangs
        /// are out of scope here (covered by the stall-probe path); pre-final-exit AccountSetup is
        /// healthy (ESP/IME are actively producing signals).
        /// </summary>
        private static bool IsParkedWithoutDeadline(DecisionState state)
        {
            if (state.Stage.IsTerminal()) return false;
            if (state.Stage == SessionStage.Unknown || state.Stage == SessionStage.SessionStarted) return false;
            if (state.AccountSetupEnteredUtc == null) return false;

            var deadEndZoneEntered =
                state.EspAdvisoryFailureRecordedUtc != null
                || (state.EspFinalExitUtc != null
                    && state.EspFinalExitUtc.Value >= state.AccountSetupEnteredUtc.Value);
            if (!deadEndZoneEntered) return false;

            foreach (var deadline in state.Deadlines)
            {
                if (!string.Equals(deadline.Name, DeadlineNames.ClassifierTick, StringComparison.Ordinal))
                    return false; // a resolution-capable deadline is armed — not parked
            }

            return true;
        }

        private void EmitParkedTripwire(DecisionState state, DecisionSignal signal)
        {
            try
            {
                var census = DecisionStateSignalCensus.Build(state);
                var data = new Dictionary<string, string>(capacity: 7, comparer: StringComparer.Ordinal)
                {
                    ["stage"] = state.Stage.ToString(),
                    ["stepIndex"] = state.StepIndex.ToString(CultureInfo.InvariantCulture),
                    ["signalOrdinal"] = signal.SessionSignalOrdinal.ToString(CultureInfo.InvariantCulture),
                    ["accountSetupEnteredUtc"] = state.AccountSetupEnteredUtc!.Value
                        .ToString("o", CultureInfo.InvariantCulture),
                    ["signalsSeen"] = string.Join(",", census.SignalsSeen),
                    ["armedDeadlines"] = string.Join(",", state.Deadlines.Select(d => d.Name)),
                    ["dwellSeconds"] = _parkedTripwireDwell.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
                };

                _informationalEvents!.Emit(
                    eventType: Constants.EventTypes.SessionParkedWithoutDeadline,
                    source: "DecisionStepProcessor",
                    message: $"Session has been parked without a resolution-capable deadline at stage {state.Stage} " +
                             $"for {_parkedTripwireDwell.TotalMinutes:F0} min — no signal is guaranteed to ever " +
                             "re-ask the completion question.",
                    severity: EventSeverity.Warning,
                    immediateUpload: true,
                    data: data);

                _logger.Warning(
                    $"DecisionStepProcessor: session parked without deadline at stage={state.Stage} " +
                    $"stepIndex={state.StepIndex} signalsSeen=[{data["signalsSeen"]}] " +
                    $"armedDeadlines=[{data["armedDeadlines"]}] — tripwire fired (one-shot).");
            }
            catch (Exception ex)
            {
                // Observability must never break the step pipeline.
                _logger.Error("DecisionStepProcessor: parked-tripwire emission failed.", ex);
            }
        }

        /// <summary>
        /// H1a snapshot policy. The snapshot is a recovery cache; skipping a step is always
        /// correctness-safe (the SignalLog tail is replayed deterministically on the next start,
        /// running no effects). We cache when the step is "meaningful" so the restart replay tail
        /// stays short: a Stage transition (covers every terminal stage + the SessionMustAbort
        /// replacement), or any effect other than the pure telemetry projection
        /// (<see cref="DecisionEffectKind.EmitEventTimelineEntry"/>). Pure InformationalEvent
        /// pass-throughs (download_progress, IME/perf snapshots, …) are the high-frequency case we
        /// skip; <see cref="SnapshotPassThroughFloor"/> bounds the worst-case replay length.
        /// </summary>
        private bool ShouldSnapshot(DecisionStep step, SessionStage previousStage)
        {
            if (step.NewState.Stage != previousStage) return true;

            foreach (var effect in step.Effects)
            {
                if (effect.Kind != DecisionEffectKind.EmitEventTimelineEntry)
                    return true;
            }

            return _passThroughStepsSinceSnapshot >= SnapshotPassThroughFloor;
        }

        private void TryTriggerQuarantine(string reason)
        {
            try
            {
                _quarantineSink.TriggerQuarantine(reason);
            }
            catch (Exception ex)
            {
                _logger.Error("DecisionStepProcessor: quarantine sink threw unexpectedly; continuing.", ex);
            }
        }
    }
}
