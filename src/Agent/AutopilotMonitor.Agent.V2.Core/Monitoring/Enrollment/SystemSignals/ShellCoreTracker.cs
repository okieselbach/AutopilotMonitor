using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
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
        internal const int EventId_ShellCore_WebAppStopped = 62405;
        internal const int EventId_ShellCore_WebAppEventName = 62406;
        internal const int EventId_ShellCore_WebAppEvent = 62407;
        internal const int BackfillLookbackMinutes = 5;

        // 62405 (page stopped) and 62406 (event with a name only) feed the CloudExperienceHost
        // navigation breadcrumbs in agent.log and the OOBE update page telemetry (D-310), nothing else.
        private const string WatchedEventIdsXPath =
            "(EventID=62404 or EventID=62405 or EventID=62406 or EventID=62407)";

        /// <summary>
        /// Upper bound for the caller-supplied backfill lookback. Matches the agent's max
        /// lifetime (360 min) — a window wider than the agent can ever live would only widen
        /// the blast radius of a stale Shell-Core record without recovering anything the
        /// current enrollment could still act on.
        /// </summary>
        internal const int BackfillLookbackMaxMinutes = 360;

        /// <summary>
        /// Lookback of the first run's update page backfill (<see cref="BackfillUpdatePageTelemetry"/>)
        /// — the hour the servicing watcher reads back, so the page's records and the update's
        /// servicing steps cover the same time.
        /// </summary>
        internal const int UpdatePageBackfillLookbackMinutes = 60;

        private static readonly HashSet<int> TrackedShellCoreEventIds = new HashSet<int>
        {
            EventId_ShellCore_WebAppStarted,
            EventId_ShellCore_WebAppStopped,
            EventId_ShellCore_WebAppEventName,
            EventId_ShellCore_WebAppEvent
        };

        private static readonly Regex EspExitingPattern = new Regex(
            @"OOBE_ESP.*Exiting", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Manifest templates: 62404 "... started. CXID: '%1'.", 62405 "... stopped. Result: '%1'.",
        // 62406 "... Event 1. Name: '%1'.", 62407 "... Event 2. Name: '%1', Value: '%2'.". The
        // fields are read from the event PROPERTIES (%1/%2): the formatted message is localized
        // ("Ergebnis: '…'", "Nom : « … »"), so these patterns are only the fallback for a record
        // without properties and accept « » and a space before the colon. A 62407 value can carry
        // URLs with tenant ids — a value leaves the tracker only in a form that cannot
        // (SafeValuePattern: a boolean, an integer, an HRESULT).
        private static readonly Regex CxidPattern = new Regex(
            @"CXID\s*:\s*['\u00AB]\s*([^'\u00BB]*?)\s*['\u00BB]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex ResultPattern = new Regex(
            @"Result\s*:\s*['\u00AB]\s*([^'\u00BB]*?)\s*['\u00BB]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex NamePattern = new Regex(
            @"Name\s*:\s*['\u00AB]\s*([^'\u00BB]*?)\s*['\u00BB]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex ValuePattern = new Regex(
            @"Value\s*:\s*['\u00AB]\s*([^'\u00BB]*?)\s*['\u00BB]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex SafeValuePattern = new Regex(
            @"^(?:true|false|-?[0-9]{1,10}|0x[0-9a-f]{1,8})$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private const int MaxBreadcrumbTokenLength = 128;

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly HelloTracker _helloTracker;
        private readonly OobeUpdateTelemetry _oobeUpdate;
        // Shell-Core RecordIds of the update-page records already reported: the restart backfill
        // must not report a record an earlier run already sent (D-310 field data: the same 20
        // records came twice after a restart).
        private readonly EventRecordWatermark _pageWatermark;
        internal const string PageWatermarkStateFileName = "shellcore-page-watermark.json";

        private EventLogWatcher _watcher;
        private int _started;
        private int _stopReported;
        private bool _espExitDetected;
        private bool _whiteGloveDetected;
        private bool _helloWizardStartDetected;
        // 62405 names no page, it ends the last one started. The live watcher's page; a backfill
        // moves it only with a newer record (see ObserveCxhRecord).
        private string _lastCxhPage;
        private long _lastCxhPageRecordId = -1;
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
            HelloTracker helloTracker,
            Func<IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState>> oobeUpdateRegistryReader = null,
            string stateDirectory = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _helloTracker = helloTracker; // nullable — HelloTracker may be unavailable in some test setups
            // Test seam: a fake registry reader; production reads the real registry.
            _oobeUpdate = new OobeUpdateTelemetry(sessionId, tenantId, post, logger,
                oobeUpdateRegistryReader ?? OobeUpdateRegistrySnapshot.Read);
            _pageWatermark = new EventRecordWatermark(stateDirectory, PageWatermarkStateFileName, logger, "Shell-Core update page");
        }

        internal bool IsEspExitedForTest { get { lock (_stateLock) { return _espExitDetected; } } }
        internal bool IsWhiteGloveDetectedForTest { get { lock (_stateLock) { return _whiteGloveDetected; } } }

        /// <summary>Loads the update-page watermark as <see cref="Start"/> does, without arming the watcher.</summary>
        internal void LoadPageWatermark() => _pageWatermark.Load();

        public void Start()
        {
            // The OOBE update registry state at every agent start — so also right after the
            // update's restart (D-310).
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _oobeUpdate.ReportState(OobeUpdateTelemetry.Moments.AgentStart);
                _pageWatermark.Load();
            }

            try
            {
                var query = new EventLogQuery(
                    ShellCoreEventLogChannel,
                    PathType.LogName,
                    $"*[System[{WatchedEventIdsXPath}]]");

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
                    collectorName: Constants.EventSources.ShellCoreTracker, reason: "watcher_arm_failed", ex: ex);
            }
        }

        public void Stop()
        {
            // Stop runs again from Dispose — one snapshot, and only for a tracker that started.
            if (Volatile.Read(ref _started) == 1 && Interlocked.Exchange(ref _stopReported, 1) == 0)
            {
                _oobeUpdate.ReportState(OobeUpdateTelemetry.Moments.AgentStop);
            }

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

                ProcessEvent(record.Id, description, timestamp, record.ProviderName ?? "", isBackfill: false,
                    recordId: record.RecordId ?? -1, properties: ReadProperties(record));
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
        internal void ProcessEvent(
            int eventId,
            string description,
            DateTime timestamp,
            string providerName,
            bool isBackfill,
            long recordId = -1,
            IReadOnlyList<string> properties = null)
        {
            var fields = ReadCxhFields(eventId, description, properties);
            ObserveCxhRecord(eventId, fields, timestamp, isBackfill, recordId, backfillPage: null);
            if (eventId == EventId_ShellCore_WebAppStopped || eventId == EventId_ShellCore_WebAppEventName) return;

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
                    if (IsHelloWizardStart(fields.Primary, description))
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
                ReplayBackfillRecords(ReadRecentRecords(lookbackMinutes));
            }
            catch (Exception ex)
            {
                _logger.Warning($"Shell-Core replay failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The FIRST run's counterpart of the restart replay, for telemetry only: the update
        /// page's records of the last <paramref name="lookbackMinutes"/> minutes (clamped like
        /// the replay) become <c>oobe_update_page</c> rows. The agent is installed during the ESP
        /// and can start after the update page began; without this its time attribution has no
        /// page start to begin the update with (field: a first run 7 min after the update's
        /// restart saw neither the page start nor the update's first half).
        /// <para>
        /// Nothing else is looked at: no Hello-wizard replay, no ESP exit or failure count, no
        /// <c>agent_trace</c>. The first run keeps its decision behaviour exactly — see
        /// <see cref="ReplayBackfillRecords"/> for why no replayed record may reach the engine and
        /// <c>DefaultComponentFactory.ResolveEspExitBackfillLookbackMinutes</c> for why the first
        /// run has no replay. No boot bound: an agent that first starts after the update's restart
        /// is exactly the case to cover; the RecordId watermark keeps it from re-sending.
        /// </para>
        /// </summary>
        public void BackfillUpdatePageTelemetry(int lookbackMinutes)
        {
            try
            {
                ReplayUpdatePageTelemetry(ReadRecentRecords(lookbackMinutes));
            }
            catch (Exception ex)
            {
                _logger.Warning($"Shell-Core update page backfill failed: {ex.Message}");
            }
        }

        /// <summary>The update page telemetry of a chronological batch of Shell-Core records — nothing else.</summary>
        internal void ReplayUpdatePageTelemetry(IReadOnlyList<ShellCoreRecord> records)
        {
            if (records == null || records.Count == 0) return;

            var page = new BackfillPageCursor();
            foreach (var record in records)
            {
                var fields = ReadCxhFields(record.Id, record.Description ?? string.Empty, record.Properties);
                ObserveCxhRecord(record.Id, fields, record.OccurredAtUtc, isBackfill: true, recordId: record.RecordId, backfillPage: page);
            }
        }

        /// <summary>The watched Shell-Core records of the last minutes, oldest first, with their original event time.</summary>
        private List<ShellCoreRecord> ReadRecentRecords(int lookbackMinutes)
        {
            var lookbackMs = ClampLookbackMinutes(lookbackMinutes) * 60 * 1000;
            var query = new EventLogQuery(
                ShellCoreEventLogChannel,
                PathType.LogName,
                $"*[System[{WatchedEventIdsXPath} and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]");

            var records = new List<ShellCoreRecord>();
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
                        records.Add(new ShellCoreRecord(record.Id, description, timestamp,
                            record.RecordId ?? -1, ReadProperties(record)));
                    }
                }
            }
            return records;
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
            if (records == null) return;
            var converted = new List<ShellCoreRecord>(records.Count);
            foreach (var r in records) converted.Add(new ShellCoreRecord(r.Id, r.Description, r.OccurredAtUtc));
            ReplayBackfillRecords(converted);
        }

        internal void ReplayBackfillRecords(IReadOnlyList<ShellCoreRecord> records)
        {
            if (records == null || records.Count == 0) return;

            var skippedExits = 0;
            var skippedFailures = 0;
            DateTime? oldestSkipped = null;
            DateTime? newestSkipped = null;
            var page = new BackfillPageCursor();

            foreach (var record in records)
            {
                var description = record.Description ?? string.Empty;
                var fields = ReadCxhFields(record.Id, description, record.Properties);
                ObserveCxhRecord(record.Id, fields, record.OccurredAtUtc, isBackfill: true, recordId: record.RecordId, backfillPage: page);

                if (record.Id == EventId_ShellCore_WebAppStarted)
                {
                    HandleBackfillRecord(record.Id, description, record.OccurredAtUtc, fields.Primary);
                    continue;
                }

                // 62405/62406 only feed the breadcrumbs and the update page telemetry above.
                if (record.Id != EventId_ShellCore_WebAppEvent) continue;

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
                    Source = Constants.EventSources.ShellCoreTracker,
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
        internal void HandleBackfillRecord(int eventId, string description, DateTime occurredAtUtc, string cxid = null)
        {
            if (eventId == EventId_ShellCore_WebAppStarted)
            {
                // 62404 — only the AADHello/NGC CXID is the Hello wizard; other web-app starts
                // are unrelated. Fire-once so a replayed log tail cannot re-raise (downstream is
                // idempotent anyway: HelloTracker once-guard, adapter dedup flag, engine
                // set-once fact — this guard just keeps the noise down).
                if (!IsHelloWizardStart(cxid ?? ReadCxhFields(eventId, description, null).Primary, description)) return;

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
        // CloudExperienceHost navigation: breadcrumbs and OOBE update page telemetry (D-310)
        // =====================================================================

        /// <summary>The last page started within one backfill batch.</summary>
        private sealed class BackfillPageCursor
        {
            public string Page;
        }

        /// <summary>
        /// Writes one agent.log line per CloudExperienceHost page start/stop and per update-page
        /// event name, so a diagnostics package shows the OOBE navigation. Records of the update
        /// page itself (<c>OobeNDUP</c>, <c>RebootNDUP</c> and its <c>ExpeditedUpdate_*</c> names)
        /// are also reported as <c>oobe_update_page</c> — once across agent runs, by RecordId — and
        /// at the page's live start and stop the registry state behind it as
        /// <c>oobe_update_state</c>. No decision.
        /// <para>
        /// A stop (62405) belongs to the page started last. A backfill batch
        /// (<paramref name="backfillPage"/>) reads records older than the live watcher's, which is
        /// armed first, so it keeps its own last page; it moves the live watcher's only with a
        /// record of a higher RecordId — a replayed older start must not take over the next live
        /// stop, and a first run's live stop still finds the page that started before the agent.
        /// </para>
        /// </summary>
        private void ObserveCxhRecord(int eventId, CxhFields fields, DateTime occurredAtUtc, bool isBackfill, long recordId,
            BackfillPageCursor backfillPage)
        {
            try
            {
                string page;
                lock (_stateLock)
                {
                    if (eventId == EventId_ShellCore_WebAppStarted && fields.Primary != null)
                    {
                        if (backfillPage != null) backfillPage.Page = fields.Primary;
                        if (backfillPage == null || (recordId >= 0 && recordId > _lastCxhPageRecordId))
                        {
                            _lastCxhPage = fields.Primary;
                            if (recordId >= 0) _lastCxhPageRecordId = recordId;
                        }
                    }
                    page = backfillPage != null ? backfillPage.Page : _lastCxhPage;
                }

                var crumb = FormatCxhBreadcrumb(eventId, fields, page);
                if (crumb == null) return;
                _logger.Info(isBackfill ? $"{crumb} (backfill, at {occurredAtUtc:o})" : crumb);

                var record = ParseUpdatePageRecord(eventId, fields, page);
                if (record == null) return;
                // A record an earlier run (or this one) already reported — the restart backfill
                // re-reads the minutes before the restart.
                if (!_pageWatermark.TryClaim(recordId)) return;

                // Only a reported record moves the state file — at most the run budget plus the
                // page boundaries; one over the budget stays claimed for this run only.
                if (_oobeUpdate.ReportPage(record, occurredAtUtc, isBackfill))
                    _pageWatermark.MarkEmitted(recordId);

                // The registry is read now — only a live page boundary is the page's moment.
                if (!isBackfill && record.CxhEvent != OobeUpdatePageRecord.EventName)
                {
                    _oobeUpdate.ReportState(
                        record.CxhEvent == OobeUpdatePageRecord.PageStarted
                            ? OobeUpdateTelemetry.Moments.UpdatePageStarted
                            : OobeUpdateTelemetry.Moments.UpdatePageStopped,
                        record.Page);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"ShellCoreTracker: CXH record observation failed: {ex.Message}");
            }
        }

        /// <summary>The own fields of a CloudExperienceHost record: CXID, result or name (%1) and the 62407 value (%2).</summary>
        internal readonly struct CxhFields
        {
            public CxhFields(string primary, string value)
            {
                Primary = primary;
                Value = value;
            }

            /// <summary>62404: CXID; 62405: result; 62406/62407: event name. Null when not readable.</summary>
            public string Primary { get; }

            /// <summary>62407: the raw value (never logged or sent unless it is of a safe form).</summary>
            public string Value { get; }
        }

        /// <summary>
        /// The record's fields from its event properties, which carry them verbatim in every
        /// display language. Without properties (a test, a record that failed to render them) the
        /// formatted message is parsed — English punctuation and « » only, so a localized label
        /// ("Ergebnis", "Nom") yields null there.
        /// </summary>
        internal static CxhFields ReadCxhFields(int eventId, string description, IReadOnlyList<string> properties)
        {
            if (properties != null && properties.Count > 0)
            {
                return new CxhFields(
                    Bound(properties[0]),
                    eventId == EventId_ShellCore_WebAppEvent && properties.Count > 1 ? Bound(properties[1]) : null);
            }

            switch (eventId)
            {
                case EventId_ShellCore_WebAppStarted:
                    return new CxhFields(ExtractToken(CxidPattern, description), null);
                case EventId_ShellCore_WebAppStopped:
                    return new CxhFields(ExtractToken(ResultPattern, description), null);
                case EventId_ShellCore_WebAppEventName:
                    return new CxhFields(ExtractToken(NamePattern, description), null);
                case EventId_ShellCore_WebAppEvent:
                    return new CxhFields(ExtractToken(NamePattern, description), ExtractToken(ValuePattern, description));
                default:
                    return default;
            }
        }

        /// <summary>
        /// The breadcrumb for one Shell-Core record, or null when it is not one: a page start
        /// (62404, its CXID), a page stop (62405, its result, attributed to the last started
        /// page), or an update-page event name (62406/62407 — a 62407 value only in a safe form).
        /// </summary>
        internal static string FormatCxhBreadcrumb(int eventId, string description, string lastPage) =>
            FormatCxhBreadcrumb(eventId, ReadCxhFields(eventId, description, null), lastPage);

        internal static string FormatCxhBreadcrumb(int eventId, CxhFields fields, string lastPage)
        {
            switch (eventId)
            {
                case EventId_ShellCore_WebAppStarted:
                    return fields.Primary == null ? null : $"CXH page started: {fields.Primary}";
                case EventId_ShellCore_WebAppStopped:
                    return $"CXH page stopped: {lastPage ?? "?"} (result={fields.Primary ?? "?"})";
                case EventId_ShellCore_WebAppEventName:
                case EventId_ShellCore_WebAppEvent:
                {
                    if (!IsUpdatePageToken(fields.Primary)) return null;
                    var value = eventId == EventId_ShellCore_WebAppEvent ? SafeValue(fields.Value) : null;
                    return value == null ? $"CXH event: {fields.Primary}" : $"CXH event: {fields.Primary} (value={value})";
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// The record of the OOBE update page, or null for any other CloudExperienceHost record:
        /// the start of a page whose CXID names NDUP, the stop of such a page (62405, attributed
        /// to the last started page), or an update-page event name from 62406/62407.
        /// </summary>
        internal static OobeUpdatePageRecord ParseUpdatePageRecord(int eventId, string description, string lastPage) =>
            ParseUpdatePageRecord(eventId, ReadCxhFields(eventId, description, null), lastPage);

        internal static OobeUpdatePageRecord ParseUpdatePageRecord(int eventId, CxhFields fields, string lastPage)
        {
            switch (eventId)
            {
                case EventId_ShellCore_WebAppStarted:
                    return IsUpdatePageToken(fields.Primary)
                        ? new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStarted, eventId, page: fields.Primary)
                        : null;
                case EventId_ShellCore_WebAppStopped:
                    return IsUpdatePageToken(lastPage)
                        ? new OobeUpdatePageRecord(OobeUpdatePageRecord.PageStopped, eventId,
                            page: lastPage, result: fields.Primary ?? "?")
                        : null;
                case EventId_ShellCore_WebAppEventName:
                case EventId_ShellCore_WebAppEvent:
                {
                    if (!IsUpdatePageToken(fields.Primary)) return null;
                    var value = eventId == EventId_ShellCore_WebAppEvent ? SafeValue(fields.Value) : null;
                    return new OobeUpdatePageRecord(OobeUpdatePageRecord.EventName, eventId, name: fields.Primary, value: value);
                }
                default:
                    return null;
            }
        }

        internal static bool IsUpdatePageToken(string token) =>
            token != null
            && (token.IndexOf("NDUP", StringComparison.OrdinalIgnoreCase) >= 0
                || token.IndexOf("ExpeditedUpdate", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// The Hello wizard's CloudExperienceHost start: CXID <c>AADHello</c> or <c>NGC</c>. With
        /// no readable CXID the formatted message decides, as before (an English or German
        /// message quotes the CXID as <c>'NGC'</c>).
        /// </summary>
        internal static bool IsHelloWizardStart(string cxid, string description) =>
            cxid != null
                ? cxid.Contains("AADHello") || string.Equals(cxid, "NGC", StringComparison.Ordinal)
                : description != null && (description.Contains("AADHello") || description.Contains("'NGC'"));

        /// <summary>
        /// The 62407 value when it is a boolean, an integer of up to ten digits or an HRESULT —
        /// forms that cannot carry a URL or a tenant id (the CSP gate, error codes). Null for
        /// anything else.
        /// </summary>
        internal static string ExtractSafeValue(string description) =>
            SafeValue(ReadCxhFields(EventId_ShellCore_WebAppEvent, description, null).Value);

        internal static string SafeValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                return SafeValuePattern.IsMatch(value) ? value : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        private static string ExtractToken(Regex pattern, string description)
        {
            if (string.IsNullOrEmpty(description)) return null;
            try
            {
                var match = pattern.Match(description);
                return match.Success ? Bound(match.Groups[1].Value) : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        private static string Bound(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            return token.Length > MaxBreadcrumbTokenLength ? token.Substring(0, MaxBreadcrumbTokenLength) + "…" : token;
        }

        /// <summary>The event's property values as strings (the manifest's %1, %2, …); null when unreadable.</summary>
        private static IReadOnlyList<string> ReadProperties(EventRecord record)
        {
            try
            {
                var properties = record.Properties;
                if (properties == null || properties.Count == 0) return null;
                var values = new string[properties.Count];
                for (var i = 0; i < properties.Count; i++)
                    values[i] = Convert.ToString(properties[i]?.Value, CultureInfo.InvariantCulture);
                return values;
            }
            catch (Exception)
            {
                return null;
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

    /// <summary>One Shell-Core record read by the restart backfill.</summary>
    internal readonly struct ShellCoreRecord
    {
        public ShellCoreRecord(int id, string description, DateTime occurredAtUtc, long recordId = -1, IReadOnlyList<string> properties = null)
        {
            Id = id;
            Description = description;
            OccurredAtUtc = occurredAtUtc;
            RecordId = recordId;
            Properties = properties;
        }

        public int Id { get; }
        public string Description { get; }
        public DateTime OccurredAtUtc { get; }

        /// <summary>The channel's RecordId; -1 when unknown (no cross-run dedup).</summary>
        public long RecordId { get; }

        /// <summary>The event's property values (%1, %2, …); null when unknown.</summary>
        public IReadOnlyList<string> Properties { get; }
    }
}
