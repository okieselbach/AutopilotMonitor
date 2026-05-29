#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// Adapter for <see cref="ImeLogTracker"/> → mehrere DecisionSignalKinds.
    /// Plan §2.1a / §2.2 / §5.9 (single-rail PR #9 — V1 Parity Issue #3).
    /// <para>
    /// ImeLogTracker nutzt Action-Property-Callbacks (nicht Events). Adapter <b>ersetzt</b>
    /// diese Action-Props — Orchestrator darf die nicht parallel belegen. Dispose restauriert
    /// die vorherigen Action-Werte (nur für Clean-Shutdown relevant; normalerweise ist der
    /// Tracker selbst kurz vor Dispose).
    /// </para>
    /// <para>
    /// <b>Dual emission</b> (Plan §5.9): jede Callback postet (a) einen spezifischen
    /// <see cref="DecisionSignalKind"/> für Decision-Relevanz + (b) einen
    /// <see cref="DecisionSignalKind.InformationalEvent"/> für die Events-Timeline-UI. So bleibt
    /// die decision-Logik unverändert, während die V1-Event-Parity (app_install_started,
    /// app_download_started, download_progress, do_telemetry, script_completed, etc.) wieder
    /// hergestellt wird, ohne direkten <c>TelemetryEventEmitter.Emit</c>-Aufruf (Single-Rail
    /// Invariante 1).
    /// </para>
    /// <para>
    /// Mapping (DecisionSignal):
    /// <list type="bullet">
    ///   <item><c>OnEspPhaseChanged(phase)</c> → <see cref="DecisionSignalKind.EspPhaseChanged"/>
    ///     (dedup per distinct phase value — Reducer Plan §2.1a idempotenz-Anforderung).</item>
    ///   <item><c>OnUserSessionCompleted()</c> → <see cref="DecisionSignalKind.ImeUserSessionCompleted"/> (fire-once).</item>
    ///   <item><c>OnAppStateChanged(app, old, new)</c> → <see cref="DecisionSignalKind.AppInstallCompleted"/>
    ///     (state=Installed/Skipped/Postponed) oder <see cref="DecisionSignalKind.AppInstallFailed"/> (state=Error).
    ///     Dedup pro (AppId, terminal-state-tuple).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>WhiteGloveSealingPatternDetected</b> (M4.4.4, Plan §4.x): fired via the newly added
    /// <c>ImeLogTracker.OnPatternMatched</c> hook. Orchestrator passes the configured set of
    /// WG-sealing-Pattern-IDs; this adapter fires the signal at most once per session when any
    /// of those IDs matches. Default empty collection = no emission (backwards-compatible with
    /// the pre-M4.4.4 M3 behavior).
    /// </para>
    /// </summary>
    internal sealed class ImeLogTrackerAdapter : IDisposable
    {
        private const string SourceLabel = "ImeLogTracker";

        private readonly ImeLogTracker _tracker;
        private readonly ISignalIngressSink _ingress;
        private readonly IClock _clock;
        private readonly InformationalEventPost _post;
        private readonly HashSet<string> _whiteGloveSealingPatternIds;
        private readonly object _lock = new object();
        // PR3-D4: optional logger so the largest signal adapter (777 LOC, 0 logger calls
        // before this PR) gets DEBUG-level visibility into emissions. Null in legacy tests.
        private readonly AgentLogger? _logger;

        // Previous action-handlers — restored on Dispose so we don't leave a dead callback
        // hanging on the tracker.
        private readonly Action<string>? _prevOnEspPhaseChanged;
        private readonly Action? _prevOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState>? _prevOnAppStateChanged;
        private readonly Action<string>? _prevOnPatternMatched;
        private readonly Action<string>? _prevOnImeAgentVersion;
        private readonly Action<AppPackageState>? _prevOnDoTelemetryReceived;
        private readonly Action<ScriptExecutionState>? _prevOnScriptCompleted;
        private readonly Action<ScriptStartedInfo>? _prevOnScriptStarted;

        // Our own delegate instances — stored once so Dispose can compare by reference.
        private readonly Action<string> _ourOnEspPhaseChanged;
        private readonly Action _ourOnUserSessionCompleted;
        private readonly Action<AppPackageState, AppInstallationState, AppInstallationState> _ourOnAppStateChanged;
        private readonly Action<string> _ourOnPatternMatched;
        private readonly Action<string> _ourOnImeAgentVersion;
        private readonly Action<AppPackageState> _ourOnDoTelemetryReceived;
        private readonly Action<ScriptExecutionState> _ourOnScriptCompleted;
        private readonly Action<ScriptStartedInfo> _ourOnScriptStarted;

        // Dedup state for DecisionSignals.
        private string? _lastEspPhase;
        private bool _userSessionCompletedPosted;
        private bool _sealingPatternPosted;
        // Fires once per session when a Platform Script stdout contains the Autopilot-Monitor
        // bootstrap marker line. Lets MCP report "which device runs which bootstrap version".
        private bool _bootstrapDetectedPosted;

        // Matches the deterministic marker line the bootstrap script writes via Write-Log,
        // e.g. "Bootstrap script version: v2.0" — see scripts/Bootstrap/Install-AutopilotMonitor.ps1.
        // Same shape as the web-side regex in utils/bootstrapVersion.ts; kept in sync intentionally.
        private static readonly System.Text.RegularExpressions.Regex BootstrapVersionRegex =
            new System.Text.RegularExpressions.Regex(
                @"Bootstrap script version:\s*v(\d+(?:\.\d+){1,3})",
                System.Text.RegularExpressions.RegexOptions.Compiled
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        private readonly HashSet<string> _appsAlreadyPostedTerminal = new HashSet<string>(StringComparer.Ordinal);

        // Plan §5 Fix 2: fire-once per ESP phase — tracks which ESP phases we've already
        // published a sub-phase `phase_transition` declaration for (e.g. "DeviceSetup" ->
        // AppsDevice once, "AccountSetup" -> AppsUser once).
        private readonly HashSet<string> _subPhaseDeclarationsEmitted = new HashSet<string>(StringComparer.Ordinal);

        // Plan §5 Fix 4c — per-app install-lifecycle timing captured from state transitions.
        // StartedAtUtc on first Downloading/Installing/InProgress transition (set-once);
        // CompletedAtUtc on first terminal transition (Installed/Skipped/Postponed/Error).
        // Surfaced via <see cref="AppTimings"/> to peripheral consumers (FinalStatusBuilder,
        // app_tracking_summary emission in EnrollmentTerminationHandler).
        private readonly Dictionary<string, (DateTime? Started, DateTime? Completed)> _appTimings =
            new Dictionary<string, (DateTime?, DateTime?)>(StringComparer.Ordinal);

        public ImeLogTrackerAdapter(
            ImeLogTracker tracker,
            ISignalIngressSink ingress,
            IClock clock,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null,
            AgentLogger? logger = null)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _post = new InformationalEventPost(ingress, clock);
            _logger = logger;

            _whiteGloveSealingPatternIds = whiteGloveSealingPatternIds == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(whiteGloveSealingPatternIds, StringComparer.Ordinal);

            // Chain-preserve: save any previously-wired handlers and invoke them from our
            // dispatcher. Lets the orchestrator co-wire diagnostic callbacks (e.g. logging)
            // without being blocked by the adapter.
            _prevOnEspPhaseChanged = _tracker.OnEspPhaseChanged;
            _prevOnUserSessionCompleted = _tracker.OnUserSessionCompleted;
            _prevOnAppStateChanged = _tracker.OnAppStateChanged;
            _prevOnPatternMatched = _tracker.OnPatternMatched;
            _prevOnImeAgentVersion = _tracker.OnImeAgentVersion;
            _prevOnDoTelemetryReceived = _tracker.OnDoTelemetryReceived;
            _prevOnScriptCompleted = _tracker.OnScriptCompleted;
            _prevOnScriptStarted = _tracker.OnScriptStarted;

            // Store our delegate instances once — implicit method-group conversions
            // create a new delegate each time, which would break Dispose's reference check.
            _ourOnEspPhaseChanged = OnEspPhaseChanged;
            _ourOnUserSessionCompleted = OnUserSessionCompleted;
            _ourOnAppStateChanged = OnAppStateChanged;
            _ourOnPatternMatched = OnPatternMatched;
            _ourOnImeAgentVersion = OnImeAgentVersion;
            _ourOnDoTelemetryReceived = OnDoTelemetryReceived;
            _ourOnScriptCompleted = OnScriptCompleted;
            _ourOnScriptStarted = OnScriptStarted;

            _tracker.OnEspPhaseChanged = _ourOnEspPhaseChanged;
            _tracker.OnUserSessionCompleted = _ourOnUserSessionCompleted;
            _tracker.OnAppStateChanged = _ourOnAppStateChanged;
            _tracker.OnPatternMatched = _ourOnPatternMatched;
            _tracker.OnImeAgentVersion = _ourOnImeAgentVersion;
            _tracker.OnDoTelemetryReceived = _ourOnDoTelemetryReceived;
            _tracker.OnScriptCompleted = _ourOnScriptCompleted;
            _tracker.OnScriptStarted = _ourOnScriptStarted;
        }

        public void Dispose()
        {
            // Restore only if we're still the current handler; otherwise leave it alone
            // (someone else re-wired after us and owns it now).
            if (ReferenceEquals(_tracker.OnEspPhaseChanged, _ourOnEspPhaseChanged))
                _tracker.OnEspPhaseChanged = _prevOnEspPhaseChanged;
            if (ReferenceEquals(_tracker.OnUserSessionCompleted, _ourOnUserSessionCompleted))
                _tracker.OnUserSessionCompleted = _prevOnUserSessionCompleted;
            if (ReferenceEquals(_tracker.OnAppStateChanged, _ourOnAppStateChanged))
                _tracker.OnAppStateChanged = _prevOnAppStateChanged;
            if (ReferenceEquals(_tracker.OnPatternMatched, _ourOnPatternMatched))
                _tracker.OnPatternMatched = _prevOnPatternMatched;
            if (ReferenceEquals(_tracker.OnImeAgentVersion, _ourOnImeAgentVersion))
                _tracker.OnImeAgentVersion = _prevOnImeAgentVersion;
            if (ReferenceEquals(_tracker.OnDoTelemetryReceived, _ourOnDoTelemetryReceived))
                _tracker.OnDoTelemetryReceived = _prevOnDoTelemetryReceived;
            if (ReferenceEquals(_tracker.OnScriptCompleted, _ourOnScriptCompleted))
                _tracker.OnScriptCompleted = _prevOnScriptCompleted;
            if (ReferenceEquals(_tracker.OnScriptStarted, _ourOnScriptStarted))
                _tracker.OnScriptStarted = _prevOnScriptStarted;
        }

        private void OnEspPhaseChanged(string phase)
        {
            _prevOnEspPhaseChanged?.Invoke(phase);
            EmitEspPhase(phase);
        }

        private void OnUserSessionCompleted()
        {
            _prevOnUserSessionCompleted?.Invoke();
            EmitUserSessionCompleted();
        }

        private void OnAppStateChanged(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            _prevOnAppStateChanged?.Invoke(app, oldState, newState);
            EmitAppState(app, oldState, newState);
        }

        private void OnPatternMatched(string patternId)
        {
            _prevOnPatternMatched?.Invoke(patternId);
            MaybeEmitWhiteGloveSealingPattern(patternId);
        }

        private void OnImeAgentVersion(string version)
        {
            _prevOnImeAgentVersion?.Invoke(version);
            EmitImeAgentVersion(version);
        }

        private void OnDoTelemetryReceived(AppPackageState app)
        {
            _prevOnDoTelemetryReceived?.Invoke(app);
            EmitDoTelemetry(app);
        }

        private void OnScriptCompleted(ScriptExecutionState script)
        {
            _prevOnScriptCompleted?.Invoke(script);
            EmitScriptCompleted(script);
        }

        private void OnScriptStarted(ScriptStartedInfo info)
        {
            _prevOnScriptStarted?.Invoke(info);
            EmitScriptStarted(info);
        }

        internal void TriggerEspPhaseFromTest(string phase) => EmitEspPhase(phase);
        internal void TriggerUserSessionCompletedFromTest() => EmitUserSessionCompleted();
        internal void TriggerAppStateFromTest(AppPackageState app, AppInstallationState oldState, AppInstallationState newState) =>
            EmitAppState(app, oldState, newState);
        internal void TriggerPatternMatchedFromTest(string patternId) => MaybeEmitWhiteGloveSealingPattern(patternId);
        internal void TriggerImeAgentVersionFromTest(string version) => EmitImeAgentVersion(version);
        internal void TriggerDoTelemetryFromTest(AppPackageState app) => EmitDoTelemetry(app);
        internal void TriggerScriptCompletedFromTest(ScriptExecutionState script) => EmitScriptCompleted(script);
        internal void TriggerScriptStartedFromTest(ScriptStartedInfo info) => EmitScriptStarted(info);

        /// <summary>
        /// Prefer the CMTrace log entry timestamp recorded by the most recent pattern match
        /// over <see cref="IClock.UtcNow"/>. Replay scenario (agent first boot reads
        /// accumulated IME log content) needs the historical timestamp so the timeline shows
        /// when activity actually happened, not when the agent woke up. Falls back to the
        /// clock when no source timestamp is available (synthetic test events, callbacks not
        /// driven by a pattern match). The fallback case sets <paramref name="derivedFromClock"/>
        /// so the emitted event can flag itself as not-source-grounded for the Inspector.
        /// <para>
        /// Clamping: a source timestamp older than 24 h relative to the clock is treated as
        /// pathological (skewed CMTrace clock, multi-day stale log) — the fallback to
        /// <see cref="IClock.UtcNow"/> kicks in and <c>derivedFromClock</c> is set so the
        /// anomaly is visible. Original value is recorded in the
        /// <paramref name="rawSourceTimestamp"/> out param so the emit-site can attach it as
        /// evidence (PR4 — preserve originals + flag for troubleshooting).
        /// </para>
        /// </summary>
        private DateTime ResolveOccurredAt(out bool derivedFromClock, out DateTime? rawSourceTimestamp)
        {
            rawSourceTimestamp = _tracker.LastMatchedLogTimestamp;
            if (rawSourceTimestamp.HasValue)
            {
                // CMTrace log timestamps from IME are emitted in UTC, but DateTimeKind may
                // arrive as Unspecified depending on the parser path. Normalize via
                // SpecifyKind=Utc rather than ToUniversalTime() to avoid double-conversion
                // when Kind is already UTC.
                var asUtc = rawSourceTimestamp.Value.Kind == DateTimeKind.Utc
                    ? rawSourceTimestamp.Value
                    : DateTime.SpecifyKind(rawSourceTimestamp.Value, DateTimeKind.Utc);

                var clockNow = _clock.UtcNow;
                var ageHours = (clockNow - asUtc).TotalHours;
                if (ageHours > 24.0 || ageHours < -1.0)
                {
                    // Pathologically stale (>24h old) or in the future (>1h skew) — log,
                    // fall back, and let the caller flag the event.
                    _logger?.Warning(
                        $"ImeAdapter: source timestamp {asUtc:o} rejected (ageHours={ageHours:F1}); falling back to clock");
                    derivedFromClock = true;
                    return clockNow;
                }

                derivedFromClock = false;
                return asUtc;
            }

            derivedFromClock = true;
            return _clock.UtcNow;
        }

        private static void TagDerivedTimestamp(IDictionary<string, string> data, bool derivedFromClock, DateTime? rawSourceTs)
        {
            if (!derivedFromClock) return;
            data["derivedTimestamp"] = "true";
            if (rawSourceTs.HasValue)
                data["rejectedSourceTimestamp"] = rawSourceTs.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void EmitEspPhase(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return;

            lock (_lock)
            {
                if (string.Equals(_lastEspPhase, phase, StringComparison.Ordinal))
                {
                    return;  // Idempotent — phase unchanged.
                }
                _lastEspPhase = phase;
            }

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectionSource"] = "IME log regex pattern",
                ["phase"] = phase,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock, rawSourceTs);

            _ingress.Post(
                kind: DecisionSignalKind.EspPhaseChanged,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: $"IME log phase transition → {phase}",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.EspPhase] = phase,
                });

            // Parity with V1: also emit an `esp_phase_changed` InformationalEvent so the
            // Events-table / UI timeline gets the phase declaration. This is one of the two
            // event types allowed to carry a non-Unknown Phase per feedback_phase_strategy.
            var mappedPhase = MapEspPhaseToEnrollmentPhase(phase);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["espPhase"] = phase,
            };
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
                data["patternId"] = patternId!;
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            _post.Emit(
                eventType: SharedEventTypes.EspPhaseChanged,
                source: SourceLabel,
                message: $"ESP phase: {phase}",
                // Phase declaration — UI timeline hinges on it; flush immediately so the
                // backend sees the transition within seconds, not at the next batch boundary.
                immediateUpload: true,
                phase: mappedPhase == EnrollmentPhase.Unknown ? (EnrollmentPhase?)null : mappedPhase,
                data: data,
                occurredAtUtc: now);

            // PR3-D4: phase emissions are decision-relevant — operators want to see them.
            _logger?.Debug($"ImeAdapter: EspPhase '{phase}' posted (mappedTo={mappedPhase}, patternId={patternId ?? "(none)"})");
        }

        private void EmitUserSessionCompleted()
        {
            lock (_lock)
            {
                if (_userSessionCompletedPosted) return;
                _userSessionCompletedPosted = true;
            }

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);

            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectionSource"] = "IME log pattern (UserSessionCompleted)",
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock, rawSourceTs);

            _ingress.Post(
                kind: DecisionSignalKind.ImeUserSessionCompleted,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: "IME user session completed (all user-scope apps finished)",
                    derivationInputs: derivationInputs));

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectedAt"] = now.ToString("o"),
            };
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
                data["patternId"] = patternId!;
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            _post.Emit(
                eventType: SharedEventTypes.ImeUserSessionCompleted,
                source: SourceLabel,
                message: "IME user session completed",
                data: data,
                occurredAtUtc: now);

            // PR3-D4: terminal-ish lifecycle marker — INFO so it's visible at the default level.
            _logger?.Info($"ImeAdapter: user session completed -> posting ImeUserSessionCompleted (patternId={patternId ?? "(none)"})");
        }

        private void EmitAppState(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            if (app == null || string.IsNullOrEmpty(app.Id)) return;

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);

            // Plan §5 Fix 2: first app activity in a given ESP phase opens the sub-phase on
            // the UI timeline via a `phase_transition` declaration event. Fire-once per ESP
            // phase; emission happens BEFORE the actual app_* event so the UI sees the phase
            // boundary before the app entry inside it.
            MaybeEmitSubPhaseDeclaration(now);

            // Plan §5 Fix 4c — set-once per-app timing. Start stamp on the first Downloading/
            // Installing/InProgress transition, complete stamp on the first terminal state.
            var timing = UpdateAppTiming(app.Id, newState, now);

            // (1) DecisionSignal — terminal states only, once per app.
            var terminalKind = ClassifyTerminalState(newState);
            if (terminalKind != null)
            {
                bool fireDecisionSignal;
                lock (_lock)
                {
                    fireDecisionSignal = _appsAlreadyPostedTerminal.Add(app.Id);
                }

                if (fireDecisionSignal)
                {
                    var appDerivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["appId"] = app.Id,
                        ["appName"] = app.Name ?? string.Empty,
                        ["previousState"] = oldState.ToString(),
                        ["newState"] = newState.ToString(),
                    };
                    TagDerivedTimestamp(appDerivationInputs, derivedFromClock, rawSourceTs);

                    _ingress.Post(
                        kind: terminalKind.Value,
                        occurredAtUtc: now,
                        sourceOrigin: SourceLabel,
                        evidence: new Evidence(
                            kind: EvidenceKind.Derived,
                            identifier: "ime-log-tracker-v1",
                            summary: $"App {app.Id} → {newState}",
                            derivationInputs: appDerivationInputs),
                        payload: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["appId"] = app.Id,
                            ["newState"] = newState.ToString(),
                        });
                }
            }

            // (2) InformationalEvent — one per state transition matching V1's wire shape.
            var mapped = MapAppStateToEventType(oldState, newState);
            if (mapped == null) return;

            var (eventType, severity) = mapped.Value;
            var payload = BuildAppStatePayload(app, newState, timing);
            TagDerivedTimestamp(payload, derivedFromClock, rawSourceTs);
            // All app-state transitions flush immediately, including `download_progress`:
            // progress ticks run every 3s and only while a download is actively in flight
            // (bounded by download duration), so live UI responsiveness wins over the small
            // extra request volume.
            _post.Emit(
                eventType: eventType,
                source: SourceLabel,
                message: BuildAppStateMessage(app, newState, eventType),
                severity: severity,
                immediateUpload: true,
                data: payload,
                occurredAtUtc: now);

            // V1 parity: on terminal Installed/Error transitions, also emit a shadow
            // `download_progress` event tagged with status=completed/failed. The Web UI's
            // DownloadProgress panel relies on this to close out apps that never produced
            // byte-level progress (WinGet/Store apps emit no `download_progress` ticks
            // because they have no DO byte data). Without this, Company Portal & friends
            // stay stuck on "started" forever.
            if (newState == AppInstallationState.Installed || newState == AppInstallationState.Error)
            {
                var status = newState == AppInstallationState.Installed ? "completed" : "failed";
                var shadowPayload = new Dictionary<string, string>(payload.Count + 1, StringComparer.Ordinal);
                foreach (var kv in payload) shadowPayload[kv.Key] = kv.Value;
                shadowPayload["status"] = status;
                _post.Emit(
                    eventType: SharedEventTypes.DownloadProgress,
                    source: SourceLabel,
                    message: $"{app.Name ?? app.Id}: {status}",
                    severity: EventSeverity.Debug,
                    immediateUpload: true,
                    data: shadowPayload,
                    occurredAtUtc: now);
            }

            // Event-driven `app_tracking_summary` snapshot: emit a fresh aggregate after
            // every terminal app-state transition. Drives the Web Live-Header
            // (`X of Y installed`) without resorting to periodic ticks. The terminal
            // emit in EnrollmentTerminationHandler still runs at session close to
            // guarantee a final snapshot with `perApp` timings.
            if (terminalKind != null)
            {
                EmitAppTrackingSummarySnapshot(now);
            }

            // PR3-D4: app state transitions are the heaviest cardinality observability target —
            // a forensic reader needs (oldState->newState, terminal flag, sub-phase emit) to
            // reconstruct why the UI shows what it shows.
            var shortId = (app.Id != null && app.Id.Length >= 8) ? app.Id.Substring(0, 8) : (app.Id ?? "?");
            // PR5: surface WasAutoDowngradedToSkipped on the same line as the state-transition
            // log — this is the "why is App X marked Skipped without a download" answer.
            var autoDowngradeNote = app.WasAutoDowngradedToSkipped ? " auto-downgraded=true (inverse-detection)" : string.Empty;
            _logger?.Debug(
                $"ImeAdapter: app '{app.Name ?? "?"}' ({shortId}) {oldState}->{newState} appType={app.AppType ?? "?"} bytesTotal={app.BytesTotal} terminal={(terminalKind != null)} eventType={eventType}{autoDowngradeNote}");
        }

        /// <summary>
        /// Plan §5 Fix 4c — set-once per-app install-lifecycle timing. Called for every
        /// state transition, no-op for duplicate stamps. Returns the up-to-date
        /// <see cref="AppInstallTiming"/> so the emitter can inline the values on the
        /// current event's payload without a second dictionary lookup.
        /// </summary>
        private AppInstallTiming UpdateAppTiming(string appId, AppInstallationState newState, DateTime now)
        {
            lock (_lock)
            {
                _appTimings.TryGetValue(appId, out var current);

                // First lifecycle-active transition → record StartedAt.
                if (current.Started == null && IsLifecycleActive(newState))
                {
                    current.Started = now;
                }

                // First terminal transition → record CompletedAt.
                if (current.Completed == null && IsCompletedState(newState))
                {
                    current.Completed = now;
                }

                _appTimings[appId] = current;
                return new AppInstallTiming(current.Started, current.Completed);
            }
        }

        /// <summary>
        /// Snapshot of per-app install timings captured during this agent run. Fire-once
        /// stamps, safe to read concurrently with adapter emissions.
        /// </summary>
        public IReadOnlyDictionary<string, AppInstallTiming> AppTimings
        {
            get
            {
                lock (_lock)
                {
                    var snapshot = new Dictionary<string, AppInstallTiming>(_appTimings.Count, StringComparer.Ordinal);
                    foreach (var kv in _appTimings)
                    {
                        snapshot[kv.Key] = new AppInstallTiming(kv.Value.Started, kv.Value.Completed);
                    }
                    return snapshot;
                }
            }
        }

        private static bool IsLifecycleActive(AppInstallationState state) =>
            state == AppInstallationState.Downloading
            || state == AppInstallationState.Installing
            || state == AppInstallationState.InProgress;

        /// <summary>
        /// Plan §5 Fix 2 — emit a <c>phase_transition</c> declaration event the first time we
        /// see app activity in a given ESP phase, so the Web UI's PhaseTimeline opens an
        /// "Apps (Device)" / "Apps (User)" sub-phase. Fire-once per ESP phase value; a change
        /// of <see cref="_lastEspPhase"/> is the trigger for the next emission when the next
        /// app event arrives. No-op when no ESP phase has been seen yet (WDP-v2 / device-only
        /// paths stay unchanged in this PR — those phase vocabularies are a follow-up).
        /// </summary>
        private void MaybeEmitSubPhaseDeclaration(DateTime occurredAtUtc)
        {
            string? currentEspPhase;
            lock (_lock) { currentEspPhase = _lastEspPhase; }
            if (string.IsNullOrEmpty(currentEspPhase)) return;

            EnrollmentPhase mapped;
            switch (currentEspPhase)
            {
                case "DeviceSetup":
                    mapped = EnrollmentPhase.AppsDevice;
                    break;
                case "AccountSetup":
                    mapped = EnrollmentPhase.AppsUser;
                    break;
                default:
                    // FinalizingSetup / Finalizing / Complete — not an apps sub-phase, and the
                    // Finalizing declaration is emitted by the reducer on stage entry (Fix 6).
                    return;
            }

            lock (_lock)
            {
                if (!_subPhaseDeclarationsEmitted.Add(currentEspPhase!)) return;
            }

            _post.Emit(
                eventType: SharedEventTypes.PhaseTransition,
                source: SourceLabel,
                message: $"Phase: {mapped}",
                // Phase boundary — flush immediately so the UI timeline opens the sub-phase
                // before the individual app events inside it.
                immediateUpload: true,
                phase: mapped,
                data: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["espPhase"] = currentEspPhase!,
                    ["trigger"] = "first_app_activity_in_esp_phase",
                },
                occurredAtUtc: occurredAtUtc);
        }

        private void MaybeEmitWhiteGloveSealingPattern(string patternId)
        {
            if (string.IsNullOrEmpty(patternId)) return;
            if (_whiteGloveSealingPatternIds.Count == 0) return;   // No configured IDs — no emit.
            if (!_whiteGloveSealingPatternIds.Contains(patternId)) return;

            lock (_lock)
            {
                if (_sealingPatternPosted) return;
                _sealingPatternPosted = true;
            }

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);
            var derivationInputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detectionSource"] = "IME log regex pattern (WG-sealing set)",
                [SignalPayloadKeys.ImePatternId] = patternId,
            };
            TagDerivedTimestamp(derivationInputs, derivedFromClock, rawSourceTs);

            _ingress.Post(
                kind: DecisionSignalKind.WhiteGloveSealingPatternDetected,
                occurredAtUtc: now,
                sourceOrigin: SourceLabel,
                evidence: new Evidence(
                    kind: EvidenceKind.Derived,
                    identifier: "ime-log-tracker-v1",
                    summary: $"WG sealing pattern match → {patternId}",
                    derivationInputs: derivationInputs),
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SignalPayloadKeys.ImePatternId] = patternId,
                });

            // PR3-D4: WG Part-1 boundary — INFO-level lifecycle marker, fires at most once.
            _logger?.Info($"ImeAdapter: WG sealing pattern '{patternId}' detected -> posting WhiteGloveSealingPatternDetected (Part-1 boundary)");
        }

        private void EmitImeAgentVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return;

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agentVersion"] = version,
            };
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId))
                data["patternId"] = patternId!;
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            _post.Emit(
                eventType: SharedEventTypes.ImeAgentVersion,
                source: SourceLabel,
                message: $"IME Agent version: {version}",
                data: data,
                occurredAtUtc: now);

            // PR3-D4: fire-once and useful for compatibility-debugging — INFO level.
            _logger?.Info($"ImeAdapter: IME agent version detected: {version}");
        }

        private void EmitDoTelemetry(AppPackageState app)
        {
            if (app == null || string.IsNullOrEmpty(app.Id)) return;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appId"] = app.Id,
                ["appName"] = app.Name ?? string.Empty,
                ["state"] = app.InstallationState.ToString(),
                ["intent"] = app.Intent.ToString(),
                ["targeted"] = app.Targeted.ToString(),
                ["runAs"] = app.RunAs.ToString(),
                ["progressPercent"] = (app.ProgressPercent ?? 0).ToString(culture),
                ["bytesDownloaded"] = app.BytesDownloaded.ToString(culture),
                ["bytesTotal"] = app.BytesTotal.ToString(culture),
                ["isError"] = "false",
                ["isCompleted"] = IsCompletedState(app.InstallationState).ToString().ToLowerInvariant(),
                ["doFileSize"] = app.DoFileSize.ToString(culture),
                ["doTotalBytesDownloaded"] = app.DoTotalBytesDownloaded.ToString(culture),
                ["doBytesFromPeers"] = app.DoBytesFromPeers.ToString(culture),
                ["doPercentPeerCaching"] = app.DoPercentPeerCaching.ToString(culture),
                ["doBytesFromLanPeers"] = app.DoBytesFromLanPeers.ToString(culture),
                ["doBytesFromGroupPeers"] = app.DoBytesFromGroupPeers.ToString(culture),
                ["doBytesFromInternetPeers"] = app.DoBytesFromInternetPeers.ToString(culture),
                ["doBytesFromLinkLocalPeers"] = app.DoBytesFromLinkLocalPeers.ToString(culture),
                ["doBytesFromCacheServer"] = app.DoBytesFromCacheServer.ToString(culture),
                ["doDownloadMode"] = app.DoDownloadMode.ToString(culture),
                ["doBytesFromHttp"] = app.DoBytesFromHttp.ToString(culture),
            };
            if (!string.IsNullOrEmpty(app.DoCacheHost)) data["doCacheHost"] = app.DoCacheHost!;
            if (!string.IsNullOrEmpty(app.DoDownloadDuration)) data["doDownloadDuration"] = app.DoDownloadDuration!;
            if (!string.IsNullOrEmpty(app.DetectionResult)) data["detectionResult"] = app.DetectionResult!;
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId)) data["patternId"] = patternId!;

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            var label = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name;
            var msg = $"{label}: DO complete - {app.DoPercentPeerCaching}% peers, mode={app.DoDownloadMode}";

            _post.Emit(
                eventType: SharedEventTypes.DoTelemetry,
                source: SourceLabel,
                message: msg,
                // Terminal DO-summary per app — fires once per download, flush immediately so
                // the UI sees peer-caching stats without waiting for the next batch.
                immediateUpload: true,
                data: data,
                occurredAtUtc: now);

            // PR3-D4: per-app DO summary at DEBUG with the headline metrics.
            _logger?.Debug(
                $"ImeAdapter: DO telemetry app='{app.Name ?? "?"}' total={app.DoFileSize} peers={app.DoBytesFromPeers} http={app.DoBytesFromHttp} mode={app.DoDownloadMode}");
        }

        private void EmitScriptCompleted(ScriptExecutionState script)
        {
            if (script == null || string.IsNullOrEmpty(script.PolicyId)) return;

            // Outcome classification — three rules in priority order:
            //   1. stderr present → script_failed. Per user UX preference (debrief 2026-05-11):
            //      consistent failure semantics across script types — any time a script writes
            //      to stderr the user wants visibility, even if exit was 0 and IME reported
            //      compliant. Eliminates the confusing case where a platform script with stderr
            //      shows red but a remediation detection with stderr shows green.
            //   2. Phase-aware exit handling: detection / post-detection use exit code as a
            //      compliance verdict (not a crash signal), so non-zero exit alone is NOT
            //      failure for those phases. Only remediation phase + platform scripts route
            //      non-zero exit to script_failed.
            //   3. Defensive: explicit Result=="Failed" → script_failed (V1 parity).
            var isHealthScript = IsRemediation(script.ScriptType);
            var isHealthComplianceReport = isHealthScript
                && (string.Equals(script.ScriptPart, "detection", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(script.ScriptPart, "post-detection", StringComparison.OrdinalIgnoreCase));

            var hasStderr = !string.IsNullOrWhiteSpace(script.Stderr);
            var isFailure = hasStderr
                            || string.Equals(script.Result, "Failed", StringComparison.OrdinalIgnoreCase)
                            || (!isHealthComplianceReport
                                && script.ExitCode.HasValue && script.ExitCode.Value != 0);
            var severity = isFailure ? EventSeverity.Error : EventSeverity.Info;
            var eventType = isFailure ? SharedEventTypes.ScriptFailed : SharedEventTypes.ScriptCompleted;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policyId"] = script.PolicyId,
            };
            if (!string.IsNullOrEmpty(script.ScriptType)) data["scriptType"] = script.ScriptType!;
            if (!string.IsNullOrEmpty(script.ScriptPart)) data["scriptPart"] = script.ScriptPart!;
            if (script.ExitCode.HasValue) data["exitCode"] = script.ExitCode.Value.ToString(culture);
            if (!string.IsNullOrEmpty(script.RunContext)) data["runContext"] = script.RunContext!;
            if (!string.IsNullOrEmpty(script.Result)) data["result"] = script.Result!;
            if (!string.IsNullOrEmpty(script.ComplianceResult)) data["complianceResult"] = script.ComplianceResult!;
            if (script.RemediationStatus.HasValue) data["remediationStatus"] = script.RemediationStatus.Value.ToString(culture);
            if (script.TargetType.HasValue) data["targetType"] = script.TargetType.Value.ToString(culture);
            if (script.ErrorCode.HasValue) data["errorCode"] = script.ErrorCode.Value.ToString(culture);
            if (!string.IsNullOrEmpty(script.ErrorDetails)) data["errorDetails"] = script.ErrorDetails!;
            if (!string.IsNullOrEmpty(script.Stdout)) data["stdout"] = script.Stdout!;
            if (!string.IsNullOrEmpty(script.Stderr)) data["stderr"] = script.Stderr!;
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId)) data["patternId"] = patternId!;

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            var label = IsRemediation(script.ScriptType) ? "Remediation script" : "Platform script";
            var shortId = script.PolicyId.Length >= 8 ? script.PolicyId.Substring(0, 8) : script.PolicyId;
            string messageCore;
            if (IsRemediation(script.ScriptType))
            {
                messageCore = !string.IsNullOrEmpty(script.ComplianceResult)
                    ? $"compliance={script.ComplianceResult}"
                    : script.Result ?? "";
            }
            else
            {
                messageCore = script.Result ?? "";
            }
            var exitSuffix = script.ExitCode.HasValue
                ? $" (exit: {script.ExitCode.Value.ToString(culture)})"
                : string.Empty;
            var msg = $"{label} {shortId}: {messageCore}{exitSuffix}";

            _post.Emit(
                eventType: eventType,
                source: SourceLabel,
                message: msg,
                severity: severity,
                data: data,
                occurredAtUtc: now);

            // PR3-D4: per-script summary at DEBUG (failures already surface via Severity=Error
            // in the event itself; the log line carries forensic context for both outcomes).
            _logger?.Debug(
                $"ImeAdapter: script completed policyId={shortId} type={script.ScriptType ?? "?"} result={script.Result ?? "?"} exit={(script.ExitCode.HasValue ? script.ExitCode.Value.ToString() : "n/a")}");

            MaybeEmitBootstrapDetected(script, now);
        }

        /// <summary>
        /// Best-effort: when a Platform Script's captured stdout contains the bootstrap marker
        /// line, emit a one-shot <c>agent_trace</c> with the parsed version so MCP can report
        /// the bootstrap-version distribution across the fleet. No UI surface; queryable only.
        /// </summary>
        private void MaybeEmitBootstrapDetected(ScriptExecutionState script, DateTime now)
        {
            if (_bootstrapDetectedPosted) return;
            if (IsRemediation(script.ScriptType)) return;
            if (string.IsNullOrEmpty(script.Stdout)) return;

            var match = BootstrapVersionRegex.Match(script.Stdout!);
            if (!match.Success) return;

            _bootstrapDetectedPosted = true;
            var version = match.Groups[1].Value;

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["decision"] = "bootstrap_detected",
                ["bootstrapVersion"] = version,
            };
            if (!string.IsNullOrEmpty(script.PolicyId)) data["policyId"] = script.PolicyId!;

            _post.Emit(
                eventType: SharedEventTypes.AgentTrace,
                source: SourceLabel,
                message: $"Autopilot-Monitor bootstrap v{version} detected via Platform Script stdout",
                severity: EventSeverity.Info,
                data: data,
                occurredAtUtc: now);

            _logger?.Debug($"ImeAdapter: bootstrap version detected v{version} (policyId={script.PolicyId ?? "?"})");
        }

        private void EmitScriptStarted(ScriptStartedInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.PolicyId)) return;

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["policyId"] = info.PolicyId,
            };
            if (!string.IsNullOrEmpty(info.ScriptType)) data["scriptType"] = info.ScriptType!;
            if (!string.IsNullOrEmpty(info.PolicyType)) data["policyType"] = info.PolicyType!;
            var patternId = _tracker.LastMatchedPatternId;
            if (!string.IsNullOrEmpty(patternId)) data["patternId"] = patternId!;

            var now = ResolveOccurredAt(out var derivedFromClock, out var rawSourceTs);
            TagDerivedTimestamp(data, derivedFromClock, rawSourceTs);

            var label = IsRemediation(info.ScriptType) ? "Health script" : "Platform script";
            var shortId = info.PolicyId.Length >= 8 ? info.PolicyId.Substring(0, 8) : info.PolicyId;

            _post.Emit(
                eventType: SharedEventTypes.ScriptStarted,
                source: SourceLabel,
                message: $"{label} {shortId}: started",
                // Live UI indicator — flush immediately so the running card appears within seconds,
                // not at the next batch boundary (which would defeat the purpose of a live signal).
                immediateUpload: true,
                data: data,
                occurredAtUtc: now);

            _logger?.Debug($"ImeAdapter: script started policyId={shortId} type={info.ScriptType ?? "?"} policyType={info.PolicyType ?? "?"}");
        }

        private static bool IsRemediation(string? scriptType) =>
            string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase);

        private static DecisionSignalKind? ClassifyTerminalState(AppInstallationState state)
        {
            switch (state)
            {
                case AppInstallationState.Installed:
                case AppInstallationState.Skipped:
                case AppInstallationState.Postponed:
                    return DecisionSignalKind.AppInstallCompleted;
                case AppInstallationState.Error:
                    return DecisionSignalKind.AppInstallFailed;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Builds and emits an <c>app_tracking_summary</c> snapshot from the current
        /// <see cref="ImeLogTracker.PackageStates"/> + <see cref="AppTimings"/>. Called
        /// from <see cref="EmitAppState"/> after every terminal app-state transition.
        /// Best-effort: any accessor failure is logged at warning level and swallowed —
        /// the per-app event itself has already been emitted.
        /// </summary>
        private void EmitAppTrackingSummarySnapshot(DateTime now)
        {
            try
            {
                // Use the deduped phase-snapshot+live union so DeviceSetup apps that were
                // cleared from `_packageStates` on the AccountSetup transition still appear
                // in the live snapshot. Mirrors the termination path in AgentRuntimeHost
                // (`componentFactory.AllKnownPackageStates`); without this, the Web header
                // counters would drop back to user-only apps after AccountSetup begins.
                var packagesSnapshot = _tracker.GetAllKnownPackageStates();
                var ignoredCount = _tracker.PackageStates?.IgnoreList?.Count ?? 0;
                var data = AppTrackingSummaryBuilder.Build(packagesSnapshot, ignoredCount);
                var totalApps = (int)data["totalApps"];
                var completedApps = (int)data["completedApps"];
                var failed = (int)data["failed"];

                _post.Emit(new EnrollmentEvent
                {
                    EventType = SharedEventTypes.AppTrackingSummary,
                    Severity = EventSeverity.Info,
                    Source = SourceLabel,
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"App summary: {completedApps}/{totalApps} completed, {failed} failed.",
                    Timestamp = now,
                    Data = data,
                    // Aggregate header counter — sub-second freshness not required, ride
                    // the next regular telemetry batch instead of triggering an extra
                    // HTTP roundtrip per terminal app transition.
                    ImmediateUpload = false,
                });
            }
            catch (Exception ex)
            {
                _logger?.Warning($"ImeAdapter: app_tracking_summary snapshot failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps an IME app state-transition tuple to the V1-compatible event-type string +
        /// severity. Returns null when the transition is not user-visible in the timeline
        /// (e.g. Unknown → NotInstalled intermediate states).
        /// </summary>
        private static (string EventType, EventSeverity Severity)? MapAppStateToEventType(
            AppInstallationState oldState,
            AppInstallationState newState)
        {
            switch (newState)
            {
                case AppInstallationState.Downloading:
                    // First transition into Downloading → `app_download_started`; subsequent
                    // byte updates (old == new == Downloading) → `download_progress` (Debug).
                    return oldState == AppInstallationState.Downloading
                        ? (SharedEventTypes.DownloadProgress, EventSeverity.Debug)
                        : (SharedEventTypes.AppDownloadStarted, EventSeverity.Info);

                case AppInstallationState.Installing:
                case AppInstallationState.InProgress:
                    return (SharedEventTypes.AppInstallStart, EventSeverity.Info);

                case AppInstallationState.Installed:
                case AppInstallationState.Skipped:
                case AppInstallationState.Postponed:
                    return (SharedEventTypes.AppInstallComplete, EventSeverity.Info);

                case AppInstallationState.Error:
                    return (SharedEventTypes.AppInstallFailed, EventSeverity.Error);

                default:
                    return null;
            }
        }

        private static string BuildAppStateMessage(AppPackageState app, AppInstallationState newState, string eventType)
        {
            var label = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name;
            if (string.Equals(eventType, SharedEventTypes.AppInstallFailed, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(app.ErrorDetail))
            {
                return $"{label}: {app.ErrorDetail}";
            }
            if (string.Equals(eventType, SharedEventTypes.DownloadProgress, StringComparison.Ordinal))
            {
                return $"{label}: {(app.ProgressPercent ?? 0)}%";
            }
            return $"{label}: {newState}";
        }

        private static Dictionary<string, string> BuildAppStatePayload(
            AppPackageState app,
            AppInstallationState newState,
            AppInstallTiming timing)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appId"] = app.Id,
                ["appName"] = app.Name ?? string.Empty,
                ["state"] = newState.ToString(),
                ["intent"] = app.Intent.ToString(),
                ["targeted"] = app.Targeted.ToString(),
                ["runAs"] = app.RunAs.ToString(),
                ["progressPercent"] = (app.ProgressPercent ?? 0).ToString(culture),
                ["bytesDownloaded"] = app.BytesDownloaded.ToString(culture),
                ["bytesTotal"] = app.BytesTotal.ToString(culture),
                ["isError"] = (newState == AppInstallationState.Error).ToString().ToLowerInvariant(),
                ["isCompleted"] = IsCompletedState(newState).ToString().ToLowerInvariant(),
            };

            if (!string.IsNullOrEmpty(app.AppVersion)) data["appVersion"] = app.AppVersion!;
            if (!string.IsNullOrEmpty(app.AppType)) data["appType"] = app.AppType!;
            if (app.AttemptNumber > 0) data["attemptNumber"] = app.AttemptNumber.ToString(culture);
            if (!string.IsNullOrEmpty(app.DetectionResult)) data["detectionResult"] = app.DetectionResult!;

            // Plan §5 Fix 4c — per-app install-lifecycle timing. StartedAt appears on the
            // first Downloading/Installing/InProgress event; CompletedAt + DurationSeconds
            // light up on the terminal event. Values are omitted when not yet known so the
            // UI can distinguish "not yet started" from "stamp=epoch".
            if (timing.StartedAtUtc.HasValue)
                data["startedAt"] = timing.StartedAtUtc.Value.ToString("o", culture);
            if (timing.CompletedAtUtc.HasValue)
                data["completedAt"] = timing.CompletedAtUtc.Value.ToString("o", culture);
            if (timing.DurationSeconds.HasValue)
                data["durationSeconds"] = timing.DurationSeconds.Value.ToString("F2", culture);

            if (newState == AppInstallationState.Error)
            {
                if (!string.IsNullOrEmpty(app.ErrorPatternId)) data["errorPatternId"] = app.ErrorPatternId!;
                if (!string.IsNullOrEmpty(app.ErrorDetail)) data["errorDetail"] = app.ErrorDetail!;
                if (!string.IsNullOrEmpty(app.ErrorCode)) data["errorCode"] = app.ErrorCode!;
                if (!string.IsNullOrEmpty(app.ExitCode)) data["exitCode"] = app.ExitCode!;
                if (!string.IsNullOrEmpty(app.HResultFromWin32)) data["hresultFromWin32"] = app.HResultFromWin32!;

                // Likely-stuck / detection-failure / install-failure classification: when the
                // agent itself promoted this app from Installing -> Error (because the ESP
                // terminated), tag the event with the canonical failureType so the backend's
                // AppInstallSummary carries it as FailureCode and the UI / analyze rules can
                // distinguish a confirmed detection-rule mismatch (esp_apps_detection_failure
                // / HRESULT 0x87D1041C) or installer error (esp_apps_install_failure) from
                // the genuine "no HRESULT available" timeout (esp_apps_timeout). See
                // EnrollmentTerminationHandler.MaybePromoteActiveInstallsAsStuck +
                // AppFailureTypes.ClassifyEspAppsFailure for the assignment.
                if (string.Equals(app.ErrorPatternId, AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsTimeout, StringComparison.Ordinal)
                    || string.Equals(app.ErrorPatternId, AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsDetectionFailure, StringComparison.Ordinal)
                    || string.Equals(app.ErrorPatternId, AutopilotMonitor.Shared.Constants.AppFailureTypes.EspAppsInstallFailure, StringComparison.Ordinal))
                {
                    data["failureType"] = app.ErrorPatternId!;
                    data["confidence"] = "presumed";
                    data["terminationTrigger"] = "EspTerminalFailure";
                }
            }

            return data;
        }

        private static bool IsCompletedState(AppInstallationState state) =>
            state == AppInstallationState.Installed
            || state == AppInstallationState.Skipped
            || state == AppInstallationState.Postponed
            || state == AppInstallationState.Error;

        /// <summary>
        /// Mirrors <see cref="DecisionEngine.MapEspPhaseToEnrollmentPhase(string)"/> so the
        /// adapter does not depend on the engine's <c>internal</c> helper (kept here for build
        /// isolation; drift should be caught by an xUnit parity test in a later PR).
        /// </summary>
        private static EnrollmentPhase MapEspPhaseToEnrollmentPhase(string rawPhase)
        {
            if (string.IsNullOrEmpty(rawPhase)) return EnrollmentPhase.Unknown;
            return rawPhase switch
            {
                "DeviceSetup" => EnrollmentPhase.DeviceSetup,
                "AccountSetup" => EnrollmentPhase.AccountSetup,
                "FinalizingSetup" => EnrollmentPhase.FinalizingSetup,
                "Finalizing" => EnrollmentPhase.FinalizingSetup,
                "Complete" => EnrollmentPhase.Complete,
                _ => EnrollmentPhase.Unknown,
            };
        }
    }
}
