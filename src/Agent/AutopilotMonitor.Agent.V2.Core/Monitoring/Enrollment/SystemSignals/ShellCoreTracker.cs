using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches Microsoft-Windows-Shell-Core/Operational for ESP-related events:
    ///   62404 — CloudExperienceHost Web App Activity Started (CXID: 'AADHello' / 'NGC' = Hello wizard)
    ///   62407 — CloudExperienceHost Web App Event 2:
    ///             CommercialOOBE_ESPProgress_Page_Exiting       — normal ESP exit
    ///             CommercialOOBE_ESPProgress_WhiteGlove_Success — WhiteGlove complete
    ///             CommercialOOBE_ESPProgress_Failure/_Timeout/_Abort/WhiteGlove_Failed — ESP failure
    ///
    /// NOTE (session b2e890c1, 2026-07-20): a bare "RebootCoalescing" substring match on 62407 was
    /// tried and REMOVED — the token appears in the ROUTINE SubcategoryProcessing_Started marker
    /// ("Starting subcategory DeviceSetup.RebootCoalescing...") on every enrollment, so it proves
    /// nothing about an actual coalesced reboot. Policy-reboot attribution lives entirely in
    /// MdmRebootPolicyTracker (EventID 2800) + the ANALYZE-ESP-005 rule gated on an observed reboot.
    ///
    /// Raises <see cref="FinalizingSetupPhaseTriggered"/>, <see cref="WhiteGloveCompleted"/>,
    /// and <see cref="EspFailureDetected"/>. Cross-notifies the <see cref="HelloTracker"/>
    /// on Hello wizard start and ESP exit so Hello timers can react.
    /// </summary>
    internal sealed class ShellCoreTracker : IDisposable
    {
        internal const string ShellCoreEventLogChannel = "Microsoft-Windows-Shell-Core/Operational";
        internal const int EventId_ShellCore_WebAppStarted = 62404;
        internal const int EventId_ShellCore_WebAppEvent = 62407;
        internal const int BackfillLookbackMinutes = 5;

        /// <summary>
        /// Upper bound for the caller-supplied backfill lookback. Matches the agent's max
        /// lifetime (360 min) — a window wider than the agent can ever live would only widen
        /// the blast radius of a stale Shell-Core record without recovering anything the
        /// current enrollment could still act on.
        /// </summary>
        internal const int BackfillLookbackMaxMinutes = 360;

        private static readonly HashSet<int> TrackedShellCoreEventIds = new HashSet<int>
        {
            EventId_ShellCore_WebAppStarted,
            EventId_ShellCore_WebAppEvent
        };

        private static readonly Regex EspExitingPattern = new Regex(
            @"OOBE_ESP.*Exiting", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly HelloTracker _helloTracker;

        private EventLogWatcher _watcher;
        private bool _espExitDetected;
        private bool _whiteGloveDetected;
        private bool _helloWizardStartDetected;
        private readonly object _stateLock = new object();

        /// <summary>
        /// UTC timestamp of the most recent event whose handlers are currently running.
        /// Set immediately before each event is raised (live or backfill); cleared back to
        /// <c>null</c> after the synchronous invoke chain returns. Subscribers read this
        /// in their handler to get the source-event timestamp without a signature change
        /// to the event delegates — preserves the historical time across backfill (where
        /// we'd otherwise collapse to wall-clock-now) without touching every callsite.
        /// </summary>
        public DateTime? LastEventOccurredAtUtc { get; private set; }

        public event EventHandler<string> FinalizingSetupPhaseTriggered;
        public event EventHandler WhiteGloveCompleted;
        public event EventHandler<string> EspFailureDetected;

        // ESP exit (Shell-Core 62407 OOBE_ESP*Exiting). Fires once per occurrence — Shell-Core
        // emits this event at each phase transition (Device→Account, Account→End), and the
        // DecisionEngine reducer (HandleEspExitingV1 + ShouldTransitionToAwaitingHello) decides
        // which occurrence is the genuine post-ESP exit that arms HelloSafety. The tracker does
        // not dedup live events. Backfill is single-shot under _espExitDetected.
        // Args carry the source-event timestamp (live = log time, backfill = record.TimeCreated).
        public event EventHandler<EspExitedEventArgs> EspExited;

        // Hello wizard launch (Shell-Core 62404 with CXID AADHello/NGC). Session 772fe502:
        // feeds the dedicated DecisionSignalKind.HelloWizardStarted rail (coordinator forward →
        // EspAndHelloTrackerAdapter) so the engine can veto/retract the policy-disabled
        // Hello-skip while the wizard is demonstrably running. Raised BEFORE
        // FinalizingSetupPhaseTriggered so the engine records the wizard fact before the
        // EspPhaseChanged(FinalizingSetup) signal lands. Backfill is single-shot under
        // _helloWizardStartDetected. Args carry the source-event timestamp.
        public event EventHandler<HelloWizardStartedEventArgs> HelloWizardStarted;

        public ShellCoreTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            HelloTracker helloTracker)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloTracker = helloTracker; // nullable — HelloTracker may be unavailable in some test setups
        }

        internal bool IsEspExitedForTest { get { lock (_stateLock) { return _espExitDetected; } } }
        internal bool IsWhiteGloveDetectedForTest { get { lock (_stateLock) { return _whiteGloveDetected; } } }

        public void Start()
        {
            try
            {
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=62404 or EventID=62407)]]");

                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;

                _logger.Info($"Started watching: {ShellCoreEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {ShellCoreEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start Shell-Core event log watcher", ex);
                // MON-D1: a dead Shell-Core watcher means the session never observes ESP exit /
                // WhiteGlove success — indistinguishable on the backend from a real no-signal
                // enrollment. Surface it as one-shot telemetry.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "ShellCoreTracker", reason: "watcher_arm_failed", ex: ex);
            }
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.EventRecordWritten -= OnEventRecordWritten;
                _watcher.Dispose();
            }
            catch (Exception ex) { _logger.Error("Error stopping Shell-Core event watcher", ex); }
            finally { _watcher = null; }
        }

        public void Dispose() => Stop();

        // =====================================================================
        // Live event handler
        // =====================================================================

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;

            try
            {
                var record = e.EventRecord;
                if (!TrackedShellCoreEventIds.Contains(record.Id)) return;

                var description = record.FormatDescription() ?? $"Event ID {record.Id}";
                var timestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();

                ProcessEvent(record.Id, description, timestamp, record.ProviderName ?? "", isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Shell-Core event record", ex);
            }
        }

        /// <summary>
        /// Core event-processing logic. Exposed as internal so tests can drive it without
        /// needing to synthesize an <see cref="EventRecord"/> (abstract + Windows-only).
        /// </summary>
        internal void ProcessEvent(int eventId, string description, DateTime timestamp, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity = EventSeverity.Info;
            string message;
            bool triggerFinalizingSetup = false;
            bool raiseHelloWizardStarted = false;
            string finalizingSetupReason = null;
            string detectedFailureType = null;

            switch (eventId)
            {
                case EventId_ShellCore_WebAppStarted: // 62404
                    if (description.Contains("AADHello") || description.Contains("'NGC'"))
                    {
                        eventType = Constants.EventTypes.HelloWizardStarted;
                        message = "Windows Hello wizard started (CloudExperienceHost)";
                        triggerFinalizingSetup = true;
                        raiseHelloWizardStarted = true;
                        finalizingSetupReason = "hello_wizard_started";

                        lock (_stateLock)
                        {
                            _helloWizardStartDetected = true;
                        }
                        _helloTracker?.NotifyHelloWizardStarted();

                        _logger.Info("Windows Hello wizard started - detected via Shell-Core event 62404");
                    }
                    else
                    {
                        return;
                    }
                    break;

                case EventId_ShellCore_WebAppEvent: // 62407
                    if (description.IndexOf("WhiteGlove_Success", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Fire-once guard
                        lock (_stateLock)
                        {
                            if (_whiteGloveDetected) return;
                            _whiteGloveDetected = true;
                        }

                        eventType = Constants.EventTypes.WhiteGloveComplete;
                        message = "WhiteGlove (Pre-Provisioning) completed successfully";
                        // No FinalizingSetup transition — WhiteGlove terminates pre-provisioning entirely

                        _logger.Info("WhiteGlove (Pre-Provisioning) success detected via Shell-Core event 62407");
                    }
                    else if (HasEspFailurePattern(description))
                    {
                        detectedFailureType = ExtractEspFailureType(description);
                        eventType = Constants.EventTypes.EspFailure;
                        severity = EventSeverity.Error;
                        message = $"ESP (Enrollment Status Page) reported a failure: {detectedFailureType}";
                        _logger.Warning($"ESP failure detected via Shell-Core event 62407: {detectedFailureType}");
                    }
                    else if (EspExitingPattern.IsMatch(description))
                    {
                        eventType = Constants.EventTypes.EspExiting;
                        message = "ESP (Enrollment Status Page) phase exiting";
                        triggerFinalizingSetup = true;
                        finalizingSetupReason = "esp_exiting";

                        lock (_stateLock)
                        {
                            _espExitDetected = true;
                            // Note: We do NOT start the Hello wait timer here!
                            // Event 62407 occurs at every ESP phase transition (Device->Account, Account->End)
                            // EnrollmentTracker will decide based on lastEspPhase whether to start the timer
                        }
                        _helloTracker?.NotifyEspExited();

                        _logger.Info("ESP phase exit detected - detected via Shell-Core event 62407");
                    }
                    else
                    {
                        return;
                    }
                    break;

                default:
                    return;
            }

            var eventData = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", description },
                { "eventLogChannel", ShellCoreEventLogChannel },
                { "eventTime", timestamp.ToString("o") }
            };

            if (eventType == Constants.EventTypes.EspFailure && detectedFailureType != null)
            {
                eventData["failureType"] = detectedFailureType;
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = timestamp,
                EventType = eventType,
                Severity = severity,
                Source = "EspAndHelloTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = eventData,
                ImmediateUpload = true
            });

            _logger.Info($"Shell-Core event detected: {eventType} (EventID {eventId})");

            // Set the source-event timestamp BEFORE each event raise so adapters / coordinators
            // can read it during their synchronous handler. Cleared in finally so a stale value
            // doesn't bleed across event types.
            LastEventOccurredAtUtc = timestamp;
            try
            {
                // Session 772fe502: raise HelloWizardStarted BEFORE FinalizingSetupPhaseTriggered
                // so the engine records the wizard fact (and runs the un-skip cure) before the
                // EspPhaseChanged(FinalizingSetup) signal is processed.
                if (raiseHelloWizardStarted)
                {
                    try { HelloWizardStarted?.Invoke(this, new HelloWizardStartedEventArgs(timestamp)); }
                    catch (Exception ex) { _logger.Error("HelloWizardStarted handler failed", ex); }
                }

                if (triggerFinalizingSetup)
                {
                    try { FinalizingSetupPhaseTriggered?.Invoke(this, finalizingSetupReason); }
                    catch (Exception ex) { _logger.Error("FinalizingSetupPhaseTriggered handler failed", ex); }
                }

                // Fire WhiteGloveCompleted AFTER event emission so the whiteglove_complete event
                // is in the spool before the agent exits.
                if (eventType == Constants.EventTypes.WhiteGloveComplete)
                {
                    try { WhiteGloveCompleted?.Invoke(this, EventArgs.Empty); }
                    catch (Exception ex) { _logger.Error("WhiteGloveCompleted handler failed", ex); }
                }

                // Fire EspFailureDetected AFTER event emission so the esp_failure event is in the
                // spool before the agent potentially shuts down.
                if (eventType == Constants.EventTypes.EspFailure && detectedFailureType != null)
                {
                    try { EspFailureDetected?.Invoke(this, detectedFailureType); }
                    catch (Exception ex) { _logger.Error($"EspFailureDetected handler failed for '{detectedFailureType}'", ex); }
                }

                // Fire EspExited AFTER event emission. The coordinator (EspAndHelloTracker) re-raises
                // this and EspAndHelloTrackerAdapter posts a DecisionSignalKind.EspExiting so the
                // engine can arm HelloSafety on the genuine post-AccountSetup exit. Engine-side guard
                // (ShouldTransitionToAwaitingHello) distinguishes intermediate exits from the real one.
                if (eventType == Constants.EventTypes.EspExiting)
                {
                    try { EspExited?.Invoke(this, new EspExitedEventArgs(timestamp)); }
                    catch (Exception ex) { _logger.Error("EspExited handler failed", ex); }
                }
            }
            finally
            {
                LastEventOccurredAtUtc = null;
            }
        }

        // =====================================================================
        // Backfill (public — called by coordinator)
        // =====================================================================

        /// <summary>Clamps a caller-supplied lookback into [1, <see cref="BackfillLookbackMaxMinutes"/>].</summary>
        internal static int ClampLookbackMinutes(int lookbackMinutes)
        {
            if (lookbackMinutes < 1) return 1;
            if (lookbackMinutes > BackfillLookbackMaxMinutes) return BackfillLookbackMaxMinutes;
            return lookbackMinutes;
        }

        public void BackfillRecentHelloWizardStart() => BackfillRecentHelloWizardStart(BackfillLookbackMinutes);

        /// <summary>
        /// Same recovery with a caller-chosen lookback, so a restart can reach back over its whole
        /// downtime (after a mid-ESP reboot the agent is gone from the forced restart until the
        /// post-reboot logon relaunches it; after a crash, until the next boot — the scheduled task
        /// has a BootTrigger only). Clamped to [1, <see cref="BackfillLookbackMaxMinutes"/>].
        /// <para>
        /// The 62407 records in the window are read but deliberately NOT replayed — see
        /// <see cref="ReplayBackfillRecords"/>. They are counted and reported once as an
        /// <c>agent_trace</c> so the gap stays visible in the timeline without becoming
        /// decision-relevant.
        /// </para>
        /// </summary>
        public void BackfillRecentHelloWizardStart(int lookbackMinutes)
        {
            try
            {
                var lookbackMs = ClampLookbackMinutes(lookbackMinutes) * 60 * 1000;
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=62404 or EventID=62407) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                var records = new List<(int Id, string Description, DateTime OccurredAtUtc)>();
                using (var reader = new EventLogReader(query))
                {
                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var description = record.FormatDescription() ?? "";
                            // Preserve the historical event time across backfill so subscribers
                            // (EspAndHelloTrackerAdapter) can stamp signals with the source time
                            // rather than collapsing to wall-clock-now.
                            var timestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();
                            records.Add((record.Id, description, timestamp));
                        }
                    }
                }

                ReplayBackfillRecords(records);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Shell-Core replay failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Replays a chronological batch of Shell-Core records. <b>Only the Hello-wizard start
        /// (62404) is replayed.</b> ESP exits and ESP failures (62407) are counted and reported,
        /// never re-raised.
        /// <para>
        /// Why the exit is excluded — the reasoning is the whole point of this method, so it lives
        /// here rather than in a commit message:
        /// </para>
        /// <list type="number">
        ///   <item>Windows writes the IDENTICAL description
        ///     (<c>CommercialOOBE_ESPProgress_Page_Exiting</c>) for the intermediate
        ///     DeviceSetup→AccountSetup transition and for the final post-AccountSetup exit, so a
        ///     replayed record carries no evidence of its own position.</item>
        ///   <item>Everything that could order it after the fact — the AccountSetup registry
        ///     probe, the settled-apps probe — reads state as it is NOW, not as it was at the
        ///     event's time.</item>
        ///   <item>The reducer orders exits by INGEST ORDINAL, not by timestamp
        ///     (<c>IsPostAccountSetupFinalExit</c>). A replayed historic exit is assigned a fresher
        ///     ordinal than reality, so it looks post-AccountSetup by construction.</item>
        ///   <item><c>HandleEspExitingV1</c> passes <c>espFinalExitInFlight: true</c> for every
        ///     arriving exit. With restored state (AccountSetupEntered + a genuine IME user
        ///     session + desktop arrived) arm C of <c>ShouldTransitionToAwaitingHello</c> then
        ///     opens on a historic intermediate exit — a completion built on a fact that never
        ///     happened.</item>
        /// </list>
        /// <para>
        /// There is therefore no honest way to classify a replayed exit, so it must not enter the
        /// decision stream at all. The same applies to a replayed ESP FAILURE, for the opposite
        /// reason: re-injecting a historic failure as fresh can fail a session that recovered
        /// (see ANALYZE-ESP-006, "ESP Failure Recovered After User Retry").
        /// </para>
        /// <para>
        /// The Hello-wizard start is different in kind and is the observation this replay was
        /// written for (session 772fe502): it is a CONSERVATIVE fact. It vetoes a premature
        /// "Hello is disabled" skip and can never by itself complete a session, so replaying it
        /// can only make the agent wait longer, never finish early.
        /// </para>
        /// </summary>
        internal void ReplayBackfillRecords(
            IReadOnlyList<(int Id, string Description, DateTime OccurredAtUtc)> records)
        {
            if (records == null || records.Count == 0) return;

            var skippedExits = 0;
            var skippedFailures = 0;
            DateTime? oldestSkipped = null;
            DateTime? newestSkipped = null;

            foreach (var record in records)
            {
                var description = record.Description ?? string.Empty;

                if (record.Id == EventId_ShellCore_WebAppStarted)
                {
                    HandleBackfillRecord(record.Id, description, record.OccurredAtUtc);
                    continue;
                }

                var isFailure = HasEspFailurePattern(description);
                var isExit = !isFailure && EspExitingPattern.IsMatch(description);
                if (!isFailure && !isExit) continue;

                if (isFailure) skippedFailures++; else skippedExits++;
                if (oldestSkipped == null || record.OccurredAtUtc < oldestSkipped.Value)
                    oldestSkipped = record.OccurredAtUtc;
                if (newestSkipped == null || record.OccurredAtUtc > newestSkipped.Value)
                    newestSkipped = record.OccurredAtUtc;
            }

            if (skippedExits > 0 || skippedFailures > 0)
                EmitSkippedShellCoreRecords(skippedExits, skippedFailures, oldestSkipped, newestSkipped);
        }

        /// <summary>
        /// One informational <c>agent_trace</c> naming the 62407 records the replay deliberately
        /// did not re-raise. Decision-neutral by construction (informational events are exempt
        /// from the dispatch guard) — its only job is to keep the blind window visible to whoever
        /// debugs a session later, instead of the replay silently dropping evidence.
        /// </summary>
        private void EmitSkippedShellCoreRecords(
            int skippedExits, int skippedFailures, DateTime? oldestUtc, DateTime? newestUtc)
        {
            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.AgentTrace,
                    Severity = EventSeverity.Info,
                    Source = "ShellCoreTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message =
                        $"Shell-Core replay skipped {skippedExits} ESP exit(s) and {skippedFailures} ESP failure(s) " +
                        "from the window in which no agent process was running — a replayed 62407 cannot be placed " +
                        "in time and must not reach the decision engine.",
                    Data = new Dictionary<string, object>
                    {
                        { "skippedEspExits", skippedExits },
                        { "skippedEspFailures", skippedFailures },
                        { "oldestSkippedUtc", oldestUtc?.ToString("o") ?? string.Empty },
                        { "newestSkippedUtc", newestUtc?.ToString("o") ?? string.Empty },
                        { "reason", "replayed_62407_not_orderable" },
                    },
                });
            }
            catch (Exception ex)
            {
                _logger.Debug($"ShellCoreTracker: skipped-records trace emit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal backfill record handler — extracted for testability and to keep the replay
        /// loop free of direct event-processing logic. Handles the Hello-wizard start ONLY; see
        /// <see cref="ReplayBackfillRecords"/> for why 62407 is never replayed. The
        /// <paramref name="occurredAtUtc"/> is the original Shell-Core event time
        /// (<c>record.TimeCreated</c>); subscribers read it via
        /// <see cref="LastEventOccurredAtUtc"/> during their synchronous event handler.
        /// </summary>
        internal void HandleBackfillRecord(int eventId, string description, DateTime occurredAtUtc)
        {
            if (eventId == EventId_ShellCore_WebAppStarted)
            {
                // 62404 — only the AADHello/NGC CXID is the Hello wizard; other web-app starts
                // are unrelated. Fire-once so a replayed log tail cannot re-raise (downstream is
                // idempotent anyway: HelloTracker once-guard, adapter dedup flag, engine
                // set-once fact — this guard just keeps the noise down).
                if (!description.Contains("AADHello") && !description.Contains("'NGC'")) return;

                bool shouldRaiseWizard;
                lock (_stateLock)
                {
                    shouldRaiseWizard = !_helloWizardStartDetected;
                    _helloWizardStartDetected = true;
                }
                if (!shouldRaiseWizard) return;

                _helloTracker?.NotifyHelloWizardStarted();
                _logger.Info($"Backfill: Hello wizard start found in recent Shell-Core logs (originalAt={occurredAtUtc:o})");
                LastEventOccurredAtUtc = occurredAtUtc;
                try
                {
                    try { HelloWizardStarted?.Invoke(this, new HelloWizardStartedEventArgs(occurredAtUtc)); }
                    catch (Exception ex) { _logger.Error("Backfill: HelloWizardStarted handler failed", ex); }
                    try { FinalizingSetupPhaseTriggered?.Invoke(this, "hello_wizard_started"); }
                    catch (Exception ex) { _logger.Error("Backfill: FinalizingSetupPhaseTriggered handler failed", ex); }
                }
                finally { LastEventOccurredAtUtc = null; }
                return;
            }

        }

        // =====================================================================
        // Pattern helpers
        // =====================================================================

        internal static bool HasEspFailurePattern(string description)
        {
            return description.IndexOf("ESPProgress_Failure", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("ESPProgress_Failed", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("ESPProgress_Timeout", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("ESPProgress_Abort", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("WhiteGlove_Failed", StringComparison.OrdinalIgnoreCase) >= 0
                || description.IndexOf("WhiteGlove_Failure", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Extracts a structured failure type from the Shell-Core event description.
        /// Returns e.g. "ESPProgress_Failure", "ESPProgress_Timeout", "WhiteGlove_Failed",
        /// or "Unknown_ESP_Failure" as a fallback.
        /// </summary>
        internal static string ExtractEspFailureType(string description)
        {
            string[] knownTypes = {
                "ESPProgress_Failure",
                "ESPProgress_Failed",
                "ESPProgress_Timeout",
                "ESPProgress_Abort",
                "WhiteGlove_Failed",
                "WhiteGlove_Failure"
            };

            foreach (var type in knownTypes)
            {
                if (description.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0)
                    return type;
            }

            return "Unknown_ESP_Failure";
        }
    }
}
