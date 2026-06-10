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
                        finalizingSetupReason = "hello_wizard_started";

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

        /// <summary>
        /// Backfills recent ESP exit and failure events from Shell-Core log on startup.
        /// Secondary recovery mechanism when state persistence is unavailable.
        /// </summary>
        public void BackfillRecentEspExitEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=62407) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

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
                            HandleBackfillRecord(description, timestamp);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ESP exit/failure event backfill failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal backfill record handler — extracted for testability and to keep the
        /// backfill loop free of direct event-processing logic. The
        /// <paramref name="occurredAtUtc"/> is the original Shell-Core event time
        /// (<c>record.TimeCreated</c>); subscribers read it via
        /// <see cref="LastEventOccurredAtUtc"/> during their synchronous event handler.
        /// </summary>
        internal void HandleBackfillRecord(string description, DateTime occurredAtUtc)
        {
            if (EspExitingPattern.IsMatch(description))
            {
                bool shouldNotify = false;
                lock (_stateLock)
                {
                    if (!_espExitDetected)
                    {
                        _espExitDetected = true;
                        shouldNotify = true;
                    }
                }
                if (shouldNotify)
                {
                    _helloTracker?.NotifyEspExited();
                    _logger.Info($"Backfill: ESP exit event found in recent Shell-Core logs (originalAt={occurredAtUtc:o})");
                    LastEventOccurredAtUtc = occurredAtUtc;
                    try
                    {
                        try { FinalizingSetupPhaseTriggered?.Invoke(this, "esp_exiting"); }
                        catch (Exception ex) { _logger.Error("Backfill: FinalizingSetupPhaseTriggered handler failed", ex); }
                        try { EspExited?.Invoke(this, new EspExitedEventArgs(occurredAtUtc)); }
                        catch (Exception ex) { _logger.Error("Backfill: EspExited handler failed", ex); }
                    }
                    finally { LastEventOccurredAtUtc = null; }
                }
            }

            if (HasEspFailurePattern(description))
            {
                var failureType = ExtractEspFailureType(description);
                _logger.Info($"Backfill: ESP failure event found in recent Shell-Core logs: {failureType} (originalAt={occurredAtUtc:o})");
                LastEventOccurredAtUtc = occurredAtUtc;
                try { EspFailureDetected?.Invoke(this, failureType); }
                catch (Exception ex) { _logger.Error($"Backfill: EspFailureDetected handler failed for '{failureType}'", ex); }
                finally { LastEventOccurredAtUtc = null; }
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
