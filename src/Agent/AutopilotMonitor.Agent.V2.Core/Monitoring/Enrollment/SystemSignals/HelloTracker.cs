using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Tracks Windows Hello for Business (WHfB) provisioning during Autopilot enrollment.
    ///
    /// Signals:
    ///   - User Device Registration/Admin: 300/301/358/360/362/376 (NGC key lifecycle)
    ///   - HelloForBusiness/Operational: 3024/6045 (processing start/stop + skip HRESULT)
    ///   - Registry (CSP + GPO): PassportForWork policy detection
    ///   - External "wizard started" signal from ESP (Shell-Core 62404)
    ///   - External "ESP exited" signal from ESP (Shell-Core 62407)
    ///
    /// Completion outcomes: "completed", "skipped", "timeout", "not_configured", "wizard_not_started".
    /// </summary>
    internal sealed class HelloTracker : IDisposable
    {
        internal const string UdrEventLogChannel = "Microsoft-Windows-User Device Registration/Admin";
        internal const string HelloForBusinessEventLogChannel = "Microsoft-Windows-HelloForBusiness/Operational";

        internal const string CspPolicyBasePath = @"SOFTWARE\Microsoft\Policies\PassportForWork";
        internal const string GpoPolicyPath = @"SOFTWARE\Policies\Microsoft\PassportForWork";

        internal const int EventId_NgcKeyRegistered = 300;
        internal const int EventId_NgcKeyRegistrationFailed = 301;
        internal const int EventId_ProvisioningWillLaunch = 358;
        internal const int EventId_ProvisioningWillNotLaunch = 360;
        internal const int EventId_ProvisioningBlocked = 362;
        internal const int EventId_PinStatus = 376;

        internal const int EventId_HelloForBusiness_ProcessingStarted = 3024;
        internal const int EventId_HelloForBusiness_ProcessingStopped = 6045;

        internal const string HResult_UserSkippedHello = "0x801C044F";

        internal const int HelloCompletionTimeoutSeconds = 300;
        internal const int BackfillLookbackMinutes = 5;

        private static readonly HashSet<int> TrackedUdrEventIds = new HashSet<int>
        {
            EventId_NgcKeyRegistered,
            EventId_NgcKeyRegistrationFailed,
            EventId_ProvisioningWillLaunch,
            EventId_ProvisioningWillNotLaunch,
            EventId_ProvisioningBlocked,
            EventId_PinStatus
        };

        private static readonly HashSet<int> TrackedHelloForBusinessEventIds = new HashSet<int>
        {
            EventId_HelloForBusiness_ProcessingStarted,
            EventId_HelloForBusiness_ProcessingStopped
        };

        private static readonly Regex HResultPattern = new Regex(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

        // PR4 (882fef64 debrief) — when policy is explicitly DISABLED, only wait briefly
        // for a Hello wizard. The grace exists solely as a sanity check against a buggy
        // policy detector — if a Hello terminal arrives anyway, the mismatch event flags it.
        internal const int HelloWaitTimeoutDisabledSeconds = 10;

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly int _helloWaitTimeoutSeconds;

        private EventLogWatcher _udrWatcher;
        private EventLogWatcher _helloForBusinessWatcher;
        private System.Threading.Timer _policyCheckTimer;
        private System.Threading.Timer _helloWaitTimer;
        private System.Threading.Timer _helloCompletionTimer;

        private bool _isPolicyConfigured;
        private bool _isHelloPolicyEnabled;
        private bool _isHelloCompleted;
        private bool _helloWizardStarted;
        private bool _espExitSeen;
        // MON-C2 — one-shot guard so the "policy not yet detected" grace re-arm of the wait
        // timer can fire at most once before falling through to the not-configured resolution.
        private bool _helloUnknownGraceUsed;
        private readonly object _stateLock = new object();

        public event EventHandler HelloCompleted;

        // PR4 (882fef64 debrief) — fires once when the Hello policy is first detected.
        // Subscribers (EspAndHelloTrackerAdapter) post a DecisionSignalKind.HelloPolicyDetected
        // signal so the engine state reflects the fact. Args: (helloEnabled, source).
        public event Action<bool, string> HelloPolicyDetected;

        public string HelloOutcome { get; private set; }

        public bool IsPolicyConfigured { get { lock (_stateLock) { return _isPolicyConfigured; } } }

        public bool IsHelloCompleted { get { lock (_stateLock) { return _isHelloCompleted; } } }

        public HelloTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int helloWaitTimeoutSeconds = 30)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloWaitTimeoutSeconds = helloWaitTimeoutSeconds;
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Start()
        {
            CheckHelloPolicy();

            _policyCheckTimer = new System.Threading.Timer(
                _ => CheckHelloPolicy(),
                null,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10));

            StartUdrEventLogWatcher();
            StartHelloForBusinessEventLogWatcher();

            BackfillRecentTerminalHelloEvents();
            BackfillRecentHelloForBusinessEvents();
        }

        public void Stop()
        {
            DisposeTimer(ref _policyCheckTimer, "policy check");
            DisposeTimer(ref _helloWaitTimer, "Hello wait");
            DisposeTimer(ref _helloCompletionTimer, "Hello completion");

            if (_udrWatcher != null)
            {
                try
                {
                    _udrWatcher.Enabled = false;
                    _udrWatcher.EventRecordWritten -= OnUdrEventRecordWritten;
                    _udrWatcher.Dispose();
                    _udrWatcher = null;
                }
                catch (Exception ex) { _logger.Error("Error stopping UDR watcher", ex); }
            }

            if (_helloForBusinessWatcher != null)
            {
                try
                {
                    _helloForBusinessWatcher.Enabled = false;
                    _helloForBusinessWatcher.EventRecordWritten -= OnHelloForBusinessEventRecordWritten;
                    _helloForBusinessWatcher.Dispose();
                    _helloForBusinessWatcher = null;
                }
                catch (Exception ex) { _logger.Error("Error stopping HelloForBusiness watcher", ex); }
            }
        }

        public void Dispose() => Stop();

        private void DisposeTimer(ref System.Threading.Timer timer, string name)
        {
            if (timer == null) return;
            try { timer.Dispose(); }
            catch (Exception ex) { _logger.Error($"Error stopping {name} timer", ex); }
            timer = null;
        }

        // =====================================================================
        // External coordination API (called by EspTracker / coordinator)
        // =====================================================================

        /// <summary>
        /// Force-marks Hello as completed from an external caller (e.g. safety timeout in EnrollmentTracker).
        /// Does NOT invoke the HelloCompleted event — the caller handles completion logic directly.
        /// </summary>
        public void ForceMarkHelloCompleted(string reason)
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted) return;
                _isHelloCompleted = true;
                HelloOutcome = reason;
                StopHelloCompletionTimerLocked();
                _logger.Warning($"Hello force-completed by external caller: {reason}");
            }
        }

        /// <summary>
        /// Resets Hello tracking state when ESP resumes after a mid-enrollment reboot (hybrid join).
        /// Stops all running timers and clears Hello state so the timer chain restarts fresh when
        /// ESP exits again for real.
        /// </summary>
        public void ResetForEspResumption()
        {
            lock (_stateLock)
            {
                _helloWaitTimer?.Dispose();
                _helloWaitTimer = null;
                _helloCompletionTimer?.Dispose();
                _helloCompletionTimer = null;

                _isHelloCompleted = false;
                _helloWizardStarted = false;
                _espExitSeen = false;
                HelloOutcome = null;
                // MON-C2 — clear the one-shot unknown-policy grace flag too, otherwise a fresh
                // post-resume wait that hits an undetected policy would skip its grace re-arm and
                // resolve straight to not_configured.
                _helloUnknownGraceUsed = false;
            }
            _logger.Info("HelloTracker: Reset for ESP resumption — Hello tracking restarted");
        }

        /// <summary>
        /// Starts the Hello wait timer. Called by EnrollmentTracker when AccountSetup phase exits.
        /// Waits for Hello wizard to start (Shell-Core event 62404, reported via
        /// <see cref="NotifyHelloWizardStarted"/>) within the configured timeout.
        /// If timeout expires without Hello wizard, marks Hello as completed so enrollment can proceed.
        /// </summary>
        public void StartHelloWaitTimer()
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted)
                {
                    _logger.Debug("StartHelloWaitTimer called but Hello wizard already started - skipping");
                    return;
                }

                if (_isHelloCompleted)
                {
                    _logger.Debug("StartHelloWaitTimer called but Hello already completed - skipping");
                    return;
                }

                if (_helloWaitTimer != null)
                {
                    _logger.Debug("StartHelloWaitTimer called but timer already running - skipping");
                    return;
                }

                // PR4 (882fef64 debrief) — when policy is explicitly disabled, only wait
                // briefly. The grace exists purely as a sanity check against a buggy
                // detector — if a Hello terminal arrives anyway, the mismatch event flags it.
                // Policy=enabled or unknown keeps the default wait (30s) so a slow wizard
                // start still has its full window.
                var waitSeconds = (_isPolicyConfigured && !_isHelloPolicyEnabled)
                    ? HelloWaitTimeoutDisabledSeconds
                    : _helloWaitTimeoutSeconds;

                _logger.Info($"Starting Hello wait timer ({waitSeconds}s) - waiting for Hello wizard to start" +
                             $" (policy={(_isPolicyConfigured ? (_isHelloPolicyEnabled ? "enabled" : "disabled") : "unknown")})");
                _helloWaitTimer = new System.Threading.Timer(
                    OnHelloWaitTimeout,
                    null,
                    TimeSpan.FromSeconds(waitSeconds),
                    TimeSpan.FromMilliseconds(-1));
            }
        }

        /// <summary>
        /// Called by the ESP tracker when Shell-Core event 62404 fires with AADHello/NGC context.
        /// Stops the wait timer (if running) and arms the long completion timer to catch cases
        /// where the wizard appeared but no terminal Hello event arrives.
        /// </summary>
        public void NotifyHelloWizardStarted()
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted) return;
                _helloWizardStarted = true;

                if (_helloWaitTimer != null)
                {
                    _helloWaitTimer.Dispose();
                    _helloWaitTimer = null;
                    _logger.Info($"Hello wizard started within {_helloWaitTimeoutSeconds}s timeout - stopping wait timer");
                }

                StartHelloCompletionTimerLocked();
            }
        }

        /// <summary>
        /// Called by the ESP tracker when the ESP exits (Shell-Core 62407 with OOBE_ESP*Exiting).
        /// Used only for informational data fields in wait/completion timeout events.
        /// </summary>
        public void NotifyEspExited()
        {
            lock (_stateLock)
            {
                _espExitSeen = true;
            }
        }

        // =====================================================================
        // Policy detection (CSP + GPO)
        // =====================================================================

        private void CheckHelloPolicy()
        {
            try
            {
                var (isEnabled, source) = DetectHelloPolicy();

                bool justDetected = false;
                bool detectedValue = false;
                string detectedSource = null;

                lock (_stateLock)
                {
                    if (!_isPolicyConfigured && isEnabled.HasValue)
                    {
                        _isPolicyConfigured = true;
                        _isHelloPolicyEnabled = isEnabled.Value;
                        var status = isEnabled.Value ? "enabled" : "disabled";

                        _post.Emit(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = Constants.EventTypes.HelloPolicyDetected,
                            Severity = EventSeverity.Info,
                            Source = "EspAndHelloTracker",
                            Phase = EnrollmentPhase.Unknown,
                            Message = $"Windows Hello for Business policy detected: {status} (via {source})",
                            Data = new Dictionary<string, object>
                            {
                                { "helloEnabled", isEnabled.Value },
                                { "policySource", source }
                            },
                            // First lifecycle signal that pins down Hello policy — flush immediately
                            // so the UI timeline reflects "WHfB enabled/disabled" within seconds.
                            ImmediateUpload = true
                        });

                        _logger.Info($"WHfB policy detected: {status} (source: {source})");

                        if (_policyCheckTimer != null)
                        {
                            _policyCheckTimer.Dispose();
                            _policyCheckTimer = null;
                            _logger.Debug("Stopped periodic Hello policy check - policy has been detected");
                        }

                        justDetected = true;
                        detectedValue = isEnabled.Value;
                        detectedSource = source;
                    }
                    else if (!_isPolicyConfigured && !isEnabled.HasValue)
                    {
                        _logger.Debug("Periodic Hello policy check: No WHfB policy found yet - will check again");
                    }
                }

                // PR4 (882fef64 debrief) — invoke OUTSIDE the state lock so a subscriber that
                // re-enters the tracker (or anything that already holds another lock) can't
                // deadlock. Subscribers post a DecisionSignalKind.HelloPolicyDetected signal
                // so DecisionState.HelloPolicyEnabled reflects the value across replays.
                if (justDetected)
                {
                    var subscribers = HelloPolicyDetected;
                    if (subscribers != null)
                    {
                        try { subscribers.Invoke(detectedValue, detectedSource); }
                        catch (Exception ex)
                        {
                            _logger.Warning($"HelloPolicyDetected subscriber threw: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error checking WHfB policy: {ex.Message}");
            }
        }

        private (bool? isEnabled, string source) DetectHelloPolicy()
        {
            try
            {
                using (var baseCspKey = Registry.LocalMachine.OpenSubKey(CspPolicyBasePath, false))
                {
                    if (baseCspKey != null)
                    {
                        foreach (var tenantSubKey in baseCspKey.GetSubKeyNames())
                        {
                            using (var tenantKey = baseCspKey.OpenSubKey(tenantSubKey, false))
                            {
                                if (tenantKey != null)
                                {
                                    foreach (var scopeSubKey in tenantKey.GetSubKeyNames())
                                    {
                                        using (var policiesKey = tenantKey.OpenSubKey($@"{scopeSubKey}\Policies", false))
                                        {
                                            if (policiesKey != null)
                                            {
                                                var value = policiesKey.GetValue("UsePassportForWork");
                                                if (value != null)
                                                {
                                                    var scope = scopeSubKey.Equals("Device", StringComparison.OrdinalIgnoreCase)
                                                        ? "device"
                                                        : "user";
                                                    return (Convert.ToInt32(value) == 1, $"CSP/Intune ({scope}-scoped)");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var gpoKey = Registry.LocalMachine.OpenSubKey(GpoPolicyPath, false))
                {
                    if (gpoKey != null)
                    {
                        var value = gpoKey.GetValue("Enabled");
                        if (value != null)
                        {
                            return (Convert.ToInt32(value) == 1, "GPO");
                        }
                    }
                }
            }
            catch { }

            return (null, null);
        }

        // =====================================================================
        // UDR event log watcher (events 300/301/358/360/362/376)
        // =====================================================================

        private void StartUdrEventLogWatcher()
        {
            try
            {
                var query = new EventLogQuery(
                    UdrEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=300 or EventID=301 or EventID=358 or EventID=360 or EventID=362 or EventID=376)]]");

                _udrWatcher = new EventLogWatcher(query);
                _udrWatcher.EventRecordWritten += OnUdrEventRecordWritten;
                _udrWatcher.Enabled = true;

                _logger.Info($"Started watching: {UdrEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {UdrEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start Hello event log watcher", ex);
                // MON-D1: a dead UDR watcher silently drops Hello provisioning evidence.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "HelloTracker", reason: "udr_watcher_arm_failed", ex: ex);
            }
        }

        private void OnUdrEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedUdrEventIds.Contains(eventId)) return;

                ProcessHelloEvent(
                    eventId,
                    (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(),
                    record.ProviderName ?? "",
                    isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Hello event record", ex);
            }
        }

        // PR3-A3: when 358/360 are suppressed below, include eventTime + provider so log readers
        // can still tell which exact occurrence was dropped without cross-referencing the eventlog.
        private void LogSuppressedSnapshotEvent(int eventId, DateTime timestamp, string providerName, string label)
        {
            _logger.Debug($"Hello EventID {eventId} ({label}) observed — snapshot only, suppressed (eventTime={timestamp:O}, provider={providerName ?? "(unknown)"})");
        }

        internal void ProcessHelloEvent(int eventId, DateTime timestamp, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity;
            string message;
            bool shouldTriggerHelloCompleted = false;

            switch (eventId)
            {
                case EventId_ProvisioningWillLaunch: // 358
                    // EventID 358 is a prerequisites-passed SNAPSHOT that flips multiple times
                    // during a single enrollment (see session 9ed7021e: 358 fired 6×, including
                    // three times within 232 ms around desktop arrival). Per project memory
                    // `project_hello_willlaunch_unreliable` this must NEVER be used as Hello
                    // resolution evidence, and the DecisionEngine already ignores it — the
                    // event only fueled UI-timeline noise. Keep the debug log line so the
                    // sequence is still reconstructible from agent-logs during diagnostics,
                    // but do not emit a backend event.
                    LogSuppressedSnapshotEvent(eventId, timestamp, providerName, "willlaunch");
                    return;

                case EventId_NgcKeyRegistered: // 300
                    eventType = Constants.EventTypes.HelloProvisioningCompleted;
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business provisioned successfully - NGC key registered";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED successfully");
                    break;

                case EventId_NgcKeyRegistrationFailed: // 301
                    eventType = Constants.EventTypes.HelloProvisioningFailed;
                    severity = EventSeverity.Error;
                    message = "Windows Hello for Business provisioning failed - NGC key registration error";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED with failure");
                    break;

                case EventId_ProvisioningWillNotLaunch: // 360
                    // Sibling of 358 — also a snapshot flag. DecisionEngine does not treat it
                    // as terminal either, so suppress for the same noise-reduction reason.
                    LogSuppressedSnapshotEvent(eventId, timestamp, providerName, "willnotlaunch");
                    return;

                case EventId_ProvisioningBlocked: // 362
                    eventType = Constants.EventTypes.HelloProvisioningBlocked;
                    severity = EventSeverity.Warning;
                    message = "Windows Hello for Business provisioning blocked";
                    shouldTriggerHelloCompleted = MarkHelloCompleted();
                    _logger.Info("Windows Hello provisioning COMPLETED (blocked)");
                    break;

                case EventId_PinStatus: // 376
                    eventType = Constants.EventTypes.HelloPinStatus;
                    severity = EventSeverity.Info;
                    message = "Windows Hello PIN status update";
                    break;

                default:
                    return;
            }

            // Keep payload minimal — full descriptions may contain sensitive data
            // (server responses with key IDs, UPNs, tokens).
            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", "truncated" },
                { "eventTime", timestamp.ToString("o") }
            };

            if (isBackfill)
            {
                data["backfill"] = true;
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
                Data = data,
                // Flush only events that finalize Hello provisioning or signal a terminal
                // failure: 300/301/362 (completion/failure/blocked), 6045-skip, and the
                // explicit `hello_provisioning_failed` case. Snapshot-type events
                // (willlaunch, willnotlaunch, pin_status) are not decision-relevant and can
                // flip state multiple times — keep batched.
                ImmediateUpload = shouldTriggerHelloCompleted
                    || eventType == Constants.EventTypes.HelloProvisioningFailed
            });

            _logger.Info($"Hello event detected: {eventType} (EventID {eventId}{(isBackfill ? ", backfill" : "")})");

            if (shouldTriggerHelloCompleted)
            {
                EmitHelloPolicyDetectionMismatch(eventType, eventId, timestamp);
                try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        // PR4 (882fef64 debrief) — emit a warning when a Hello-terminal event arrives while
        // policy is explicitly disabled. This indicates a bug in the CSP/GPO detector — the
        // device clearly DOES have Hello-for-Business in some form, despite our reader saying
        // otherwise. Severity Warning + Phase Unknown so the event surfaces in dashboards but
        // doesn't claim to be a lifecycle marker.
        private void EmitHelloPolicyDetectionMismatch(string actualEventType, int? windowsEventId, DateTime timestamp)
        {
            string source;
            lock (_stateLock)
            {
                if (!(_isPolicyConfigured && !_isHelloPolicyEnabled)) return;
                source = "EspAndHelloTracker";
            }

            try
            {
                var data = new Dictionary<string, object>
                {
                    { "expected", "no_hello" },
                    { "actual", actualEventType ?? string.Empty },
                    { "policySource", "tracker_state" },
                };
                if (windowsEventId.HasValue) data["windowsEventId"] = windowsEventId.Value;

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = timestamp,
                    EventType = Constants.EventTypes.HelloPolicyDetectionMismatch,
                    Severity = EventSeverity.Warning,
                    Source = source,
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Hello policy detected as DISABLED but Hello terminal event '{actualEventType}' arrived — detector bug suspected.",
                    Data = data,
                    ImmediateUpload = true,
                });
                _logger.Warning($"hello_policy_detection_mismatch: actual={actualEventType} (eventId={windowsEventId})");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to emit hello_policy_detection_mismatch: {ex.Message}");
            }
        }

        private bool MarkHelloCompleted()
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello terminal event received but Hello is already marked completed");
                    return false;
                }

                _isHelloCompleted = true;
                HelloOutcome = "completed";
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        private void BackfillRecentTerminalHelloEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    UdrEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=300 or EventID=301 or EventID=362) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    DateTime latestTimestamp = DateTime.MinValue;
                    int? latestEventId = null;
                    string latestProvider = string.Empty;

                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var eventId = record.Id;
                            if (eventId != EventId_NgcKeyRegistered && eventId != EventId_NgcKeyRegistrationFailed && eventId != EventId_ProvisioningBlocked)
                                continue;

                            var timestamp = record.TimeCreated ?? DateTime.MinValue;
                            if (timestamp < latestTimestamp) continue;

                            latestTimestamp = timestamp;
                            latestEventId = eventId;
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestEventId.HasValue)
                    {
                        _logger.Info($"Backfill found recent Hello terminal event: EventID {latestEventId.Value} at {latestTimestamp:O}");
                        ProcessHelloEvent(
                            latestEventId.Value,
                            (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(),
                            latestProvider,
                            isBackfill: true);
                    }
                    else
                    {
                        _logger.Debug($"Backfill found no terminal Hello events in last {BackfillLookbackMinutes} minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Hello terminal event backfill failed: {ex.Message}");
            }
        }

        // =====================================================================
        // HelloForBusiness/Operational watcher (events 3024/6045)
        // =====================================================================

        private void StartHelloForBusinessEventLogWatcher()
        {
            try
            {
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    "*[System[(EventID=3024 or EventID=6045)]]");

                _helloForBusinessWatcher = new EventLogWatcher(query);
                _helloForBusinessWatcher.EventRecordWritten += OnHelloForBusinessEventRecordWritten;
                _helloForBusinessWatcher.Enabled = true;

                _logger.Info($"Started watching: {HelloForBusinessEventLogChannel}");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Event log not found: {HelloForBusinessEventLogChannel} (normal if not on a real device)");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to start HelloForBusiness event log watcher", ex);
                // MON-D1: a dead HelloForBusiness watcher silently drops Hello completion evidence.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "HelloTracker", reason: "hellobusiness_watcher_arm_failed", ex: ex);
            }
        }

        private void OnHelloForBusinessEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;

            try
            {
                var record = e.EventRecord;
                var eventId = record.Id;
                if (!TrackedHelloForBusinessEventIds.Contains(eventId)) return;

                var description = record.FormatDescription() ?? $"Event ID {eventId}";
                var timestamp = (record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime();

                ProcessHelloForBusinessEvent(eventId, timestamp, description, record.ProviderName ?? "", isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing HelloForBusiness event record", ex);
            }
        }

        internal void ProcessHelloForBusinessEvent(int eventId, DateTime timestamp, string description, string providerName, bool isBackfill)
        {
            string eventType;
            EventSeverity severity;
            string message;
            bool shouldTriggerHelloCompleted = false;
            string hresult = null;

            switch (eventId)
            {
                case EventId_HelloForBusiness_ProcessingStarted: // 3024
                    eventType = Constants.EventTypes.HelloProcessingStarted;
                    severity = EventSeverity.Info;
                    message = "Windows Hello for Business processing started";
                    _logger.Info("Hello for Business processing started (event 3024)");
                    break;

                case EventId_HelloForBusiness_ProcessingStopped: // 6045
                    hresult = ExtractHResultFromDescription(description);

                    if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                    {
                        eventType = Constants.EventTypes.HelloSkipped;
                        severity = EventSeverity.Warning;
                        message = $"Windows Hello for Business skipped by user ({HResult_UserSkippedHello})";
                        shouldTriggerHelloCompleted = MarkHelloSkipped();
                        _logger.Info($"Hello for Business SKIPPED by user (event 6045, HRESULT {hresult})");
                    }
                    else
                    {
                        eventType = Constants.EventTypes.HelloProcessingStopped;
                        severity = EventSeverity.Info;
                        message = $"Windows Hello for Business processing stopped (HRESULT: {hresult ?? "unknown"})";
                        _logger.Info($"Hello for Business processing stopped (event 6045, HRESULT {hresult ?? "unknown"}) - not treated as terminal");
                    }
                    break;

                default:
                    return;
            }

            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "providerName", providerName ?? "" },
                { "description", description },
                { "eventLogChannel", HelloForBusinessEventLogChannel },
                { "eventTime", timestamp.ToString("o") }
            };

            if (hresult != null) data["hresult"] = hresult;
            if (isBackfill) data["backfill"] = true;

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
                Data = data,
                ImmediateUpload = shouldTriggerHelloCompleted
            });

            _logger.Info($"HelloForBusiness event detected: {eventType} (EventID {eventId}{(isBackfill ? ", backfill" : "")})");

            if (shouldTriggerHelloCompleted)
            {
                EmitHelloPolicyDetectionMismatch(eventType, eventId, timestamp);
                try { HelloCompleted?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        private bool MarkHelloSkipped()
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello skip event received but Hello is already marked completed");
                    return false;
                }

                _isHelloCompleted = true;
                HelloOutcome = "skipped";
                StopHelloCompletionTimerLocked();
                return true;
            }
        }

        internal static string ExtractHResultFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            var match = HResultPattern.Match(description);
            return match.Success ? match.Value : null;
        }

        private void BackfillRecentHelloForBusinessEvents()
        {
            try
            {
                var lookbackMs = BackfillLookbackMinutes * 60 * 1000;
                var query = new EventLogQuery(
                    HelloForBusinessEventLogChannel,
                    PathType.LogName,
                    $"*[System[(EventID=6045) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

                using (var reader = new EventLogReader(query))
                {
                    DateTime latestTimestamp = DateTime.MinValue;
                    string latestDescription = null;
                    string latestProvider = string.Empty;

                    for (EventRecord record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
                    {
                        using (record)
                        {
                            var timestamp = record.TimeCreated ?? DateTime.MinValue;
                            if (timestamp < latestTimestamp) continue;

                            latestTimestamp = timestamp;
                            latestDescription = record.FormatDescription() ?? $"Event ID {record.Id}";
                            latestProvider = record.ProviderName ?? "";
                        }
                    }

                    if (latestDescription != null)
                    {
                        var hresult = ExtractHResultFromDescription(latestDescription);
                        if (string.Equals(hresult, HResult_UserSkippedHello, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.Info($"Backfill found recent Hello skip event: HRESULT {hresult} at {latestTimestamp:O}");
                            ProcessHelloForBusinessEvent(
                                EventId_HelloForBusiness_ProcessingStopped,
                                (latestTimestamp == DateTime.MinValue ? DateTime.UtcNow : latestTimestamp).ToUniversalTime(),
                                latestDescription,
                                latestProvider,
                                isBackfill: true);
                        }
                        else
                        {
                            _logger.Debug($"Backfill found HelloForBusiness event 6045 but HRESULT {hresult ?? "unknown"} is not a known terminal code - skipping");
                        }
                    }
                    else
                    {
                        _logger.Debug($"Backfill found no HelloForBusiness 6045 events in last {BackfillLookbackMinutes} minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"HelloForBusiness event backfill failed: {ex.Message}");
            }
        }

        // =====================================================================
        // Timers: wait → completion
        // =====================================================================

        private void OnHelloWaitTimeout(object state)
        {
            lock (_stateLock)
            {
                if (_helloWizardStarted)
                {
                    _logger.Debug("Hello wait timeout fired but wizard already started - ignoring");
                    return;
                }

                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello wait timeout fired but Hello already completed - ignoring");
                    return;
                }

                // Timeout expired without the Hello wizard starting. Resolve into one of three
                // cases — only a *positively detected DISABLED* policy is a real "Hello not
                // configured" signal (MON-C2):
                //
                //   * policy known ENABLED  — wizard simply hasn't appeared yet; keep waiting on
                //                             the long completion timer.
                //   * policy NOT YET DETECTED — a slow MDM/CSP sync can land the policy read after
                //                             this wait; force-completing to "not_configured" now
                //                             would prematurely resolve Hello on a device that is
                //                             actually configured. Grant one bounded grace re-arm
                //                             for detection to catch up.
                //   * policy known DISABLED (or still unknown after the grace) — genuinely not
                //                             configured / skipped; resolve completion.
                var policyKnownEnabled = _isPolicyConfigured && _isHelloPolicyEnabled;
                var policyUnknown = !_isPolicyConfigured;

                if (policyKnownEnabled)
                {
                    _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired but Hello policy is enabled — " +
                                 $"wizard not yet visible, starting long completion timer ({HelloCompletionTimeoutSeconds}s)");

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.HelloWaitTimeout,
                        Severity = EventSeverity.Info,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Hello wizard did not start within {_helloWaitTimeoutSeconds}s after ESP exit — " +
                                  $"Hello policy is enabled, waiting up to {HelloCompletionTimeoutSeconds}s for wizard",
                        Data = new Dictionary<string, object>
                        {
                            { "timeoutSeconds", _helloWaitTimeoutSeconds },
                            { "espExitDetected", _espExitSeen },
                            { "helloPolicyEnabled", true },
                            { "policyDetected", true },
                            { "action", "extended_wait" }
                        },
                        ImmediateUpload = true
                    });

                    StartHelloCompletionTimerLocked();
                    return;
                }

                if (policyUnknown && !_helloUnknownGraceUsed)
                {
                    _helloUnknownGraceUsed = true;
                    _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired with Hello policy not yet detected — " +
                                 $"granting a {_helloWaitTimeoutSeconds}s grace for a slow policy sync before assuming not configured");

                    _post.Emit(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = Constants.EventTypes.HelloWaitTimeout,
                        Severity = EventSeverity.Info,
                        Source = "EspAndHelloTracker",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Hello wizard did not start within {_helloWaitTimeoutSeconds}s after ESP exit and Hello policy " +
                                  $"is not yet detected — waiting a further {_helloWaitTimeoutSeconds}s for a slow policy sync",
                        Data = new Dictionary<string, object>
                        {
                            { "timeoutSeconds", _helloWaitTimeoutSeconds },
                            { "espExitDetected", _espExitSeen },
                            { "helloPolicyEnabled", false },
                            { "policyDetected", false },
                            { "action", "unknown_policy_grace" }
                        },
                        ImmediateUpload = true
                    });

                    // Re-arm the existing one-shot wait timer for a single bounded grace window.
                    // The 10s-interval policy check timer keeps running and may flip the policy to
                    // enabled/disabled within this window; otherwise the next fire resolves it.
                    // L2 (delta review 2026-07-02): Stop() disposes the timers WITHOUT taking
                    // _stateLock, so it can race this callback between the null-check and Change —
                    // an unhandled ObjectDisposedException on a threadpool timer thread kills the
                    // process on net48. Shutdown is winning anyway; swallow and bail.
                    try
                    {
                        _helloWaitTimer?.Change(TimeSpan.FromSeconds(_helloWaitTimeoutSeconds), TimeSpan.FromMilliseconds(-1));
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.Debug("HelloTracker: wait-timer re-arm lost the race against Stop() — shutting down, grace window skipped");
                    }
                    return;
                }

                _logger.Info($"Hello wait timeout ({_helloWaitTimeoutSeconds}s) expired without Hello wizard starting");
                _logger.Info(_isPolicyConfigured
                    ? "Hello policy detected as disabled — assuming Hello is not configured or was skipped"
                    : "Hello policy still not detected after grace — assuming Hello is not configured or was skipped");

                _isHelloCompleted = true;
                HelloOutcome = "not_configured";
                StopHelloCompletionTimerLocked();

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.HelloWaitTimeout,
                    Severity = EventSeverity.Info,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Hello wizard did not start within {_helloWaitTimeoutSeconds}s after ESP exit - assuming not configured",
                    Data = new Dictionary<string, object>
                    {
                        { "timeoutSeconds", _helloWaitTimeoutSeconds },
                        { "espExitDetected", _espExitSeen },
                        { "helloPolicyEnabled", false },
                        { "policyDetected", _isPolicyConfigured },
                        { "action", "enrollment_complete" }
                    },
                    ImmediateUpload = true
                });

                try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Error("Error invoking HelloCompleted from timeout", ex); }
            }
        }

        private void StartHelloCompletionTimerLocked()
        {
            if (_isHelloCompleted) return;
            if (_helloCompletionTimer != null) return;

            _logger.Info($"Starting Hello completion timer ({HelloCompletionTimeoutSeconds}s) - waiting for terminal Hello event (300/301/362)");
            _helloCompletionTimer = new System.Threading.Timer(
                OnHelloCompletionTimeout,
                null,
                TimeSpan.FromSeconds(HelloCompletionTimeoutSeconds),
                TimeSpan.FromMilliseconds(-1));
        }

        private void StopHelloCompletionTimerLocked()
        {
            if (_helloCompletionTimer == null) return;
            try { _helloCompletionTimer.Dispose(); } catch { }
            finally { _helloCompletionTimer = null; }
        }

        private void OnHelloCompletionTimeout(object state)
        {
            lock (_stateLock)
            {
                if (_isHelloCompleted)
                {
                    _logger.Debug("Hello completion timeout fired but Hello already completed - ignoring");
                    return;
                }

                _logger.Warning($"Hello completion timeout ({HelloCompletionTimeoutSeconds}s) expired after wizard start without terminal event");
                _isHelloCompleted = true;
                HelloOutcome = _helloWizardStarted ? "timeout" : "wizard_not_started";
                StopHelloCompletionTimerLocked();

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.HelloCompletionTimeout,
                    Severity = EventSeverity.Warning,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Hello wizard started but no terminal event (300/301/362) arrived within {HelloCompletionTimeoutSeconds}s",
                    Data = new Dictionary<string, object>
                    {
                        { "timeoutSeconds", HelloCompletionTimeoutSeconds },
                        { "helloWizardStarted", _helloWizardStarted }
                    },
                    ImmediateUpload = true
                });

                try { HelloCompleted?.Invoke(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.Error("Error invoking HelloCompleted from completion timeout", ex); }
            }
        }

        // =====================================================================
        // Test seams — allow tests to drive timer logic deterministically
        // =====================================================================

        /// <summary>Test-only: simulate policy detection without a real registry read.</summary>
        internal void SetPolicyForTest(bool helloEnabled, string source)
        {
            lock (_stateLock)
            {
                _isPolicyConfigured = true;
                _isHelloPolicyEnabled = helloEnabled;

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.HelloPolicyDetected,
                    Severity = EventSeverity.Info,
                    Source = "EspAndHelloTracker",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Windows Hello for Business policy detected: {(helloEnabled ? "enabled" : "disabled")} (via {source})",
                    Data = new Dictionary<string, object>
                    {
                        { "helloEnabled", helloEnabled },
                        { "policySource", source }
                    },
                    ImmediateUpload = true
                });
            }

            // PR4 (882fef64 debrief) — fire the policy event outside the state lock so tests
            // exercise the same notification path as production.
            var subscribers = HelloPolicyDetected;
            if (subscribers != null)
            {
                try { subscribers.Invoke(helloEnabled, source); }
                catch (Exception ex)
                {
                    _logger.Warning($"HelloPolicyDetected subscriber threw (test seam): {ex.Message}");
                }
            }
        }

        internal void TriggerWaitTimeoutForTest() => OnHelloWaitTimeout(null);
        internal void TriggerCompletionTimeoutForTest() => OnHelloCompletionTimeout(null);

        internal bool IsWaitTimerActiveForTest { get { lock (_stateLock) { return _helloWaitTimer != null; } } }
        internal bool IsCompletionTimerActiveForTest { get { lock (_stateLock) { return _helloCompletionTimer != null; } } }
        internal bool IsHelloWizardStartedForTest { get { lock (_stateLock) { return _helloWizardStarted; } } }
    }
}
