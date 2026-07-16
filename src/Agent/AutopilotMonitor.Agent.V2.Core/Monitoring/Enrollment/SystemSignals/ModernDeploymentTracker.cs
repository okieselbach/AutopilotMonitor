using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches Microsoft-Windows-ModernDeployment-Diagnostics-Provider channels
    /// (Autopilot + ManagementService) for Autopilot / ESP events and forwards them
    /// to the event sink as modern_deployment_log/warning/error events.
    ///
    /// Additionally detects WhiteGlove (Pre-Provisioning) initiation via
    /// ManagementService Event 509 with cross-restart dedup via disk persistence.
    ///
    /// The Autopilot channel's documented event-ID semantics (100 waiting-for-profile,
    /// 153/160/161/163/164 profile-download flow, 171/172 TPM attestation, 807/809/815/908
    /// ZTD registration/assignment errors) are catalogued with sources in
    /// docs/agent/autopilot-ztd-diagnostics.md — re-check that doc's sources periodically.
    /// The continuous watcher here filters to Level ≤ 3; <see cref="Telemetry.DeviceInfo.ZtdEvidence"/>
    /// runs a targeted all-level one-shot query on the profile-missing path instead.
    /// </summary>
    internal sealed class ModernDeploymentTracker : IDisposable
    {
        internal const string AutopilotChannel = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot";
        internal const string ManagementChannel = "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService";

        /// <summary>
        /// ManagementService Event ID 509: fires when a technician actually initiates
        /// pre-provisioning (Win 5x → Provision). Level 4 (Informational) — bypasses
        /// the default Level≤3 filter via a targeted OR clause in the XPath.
        /// </summary>
        internal const int EventId_WhiteGloveStart = 509;

        internal static readonly HashSet<int> TargetedManagementServiceEventIds = new HashSet<int>
        {
            EventId_WhiteGloveStart
        };

        internal const string WhiteGloveBackfillStateFileName = "whiteglove-backfill.json";

        /// <summary>
        /// Per-EventId emission caps for harmless-downgraded events (session 8bc1180f,
        /// 2026-06-12). Windows can dump hundreds of identical EventID-100 "Autopilot policy
        /// not found" records in a single minute (observed: 689/min → signal-ingress queue
        /// saturated at 256/256). Forwarding each one as a Debug event has zero diagnostic
        /// value beyond a count, so per EventId the first
        /// <see cref="HarmlessRollupIndividualLimit"/> occurrences are forwarded individually
        /// and afterwards only every <see cref="HarmlessRollupEmitEvery"/>th occurrence is
        /// emitted, carrying the cumulative <c>occurrenceCount</c>. Counter-based (no timers)
        /// so the suppression is deterministic and replay-safe; the tail below the next
        /// multiple is intentionally absorbed — the last emitted rollup carries the running
        /// total, which bounds the loss to &lt; one bucket.
        /// </summary>
        internal const int HarmlessRollupIndividualLimit = 3;

        /// <summary>See <see cref="HarmlessRollupIndividualLimit"/>.</summary>
        internal const int HarmlessRollupEmitEvery = 100;

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly int _logLevelMax;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly string _stateDirectory;
        private readonly HashSet<int> _harmlessEventIds;

        private EventLogWatcher _autopilotWatcher;
        private EventLogWatcher _managementWatcher;
        private bool _whiteGloveStartDetected;
        private readonly object _stateLock = new object();

        // Cumulative per-EventId occurrence counters for harmless-downgraded events.
        // EventRecordWritten callbacks can run concurrently → guarded by its own lock.
        private readonly Dictionary<int, int> _harmlessOccurrenceCounts = new Dictionary<int, int>();
        private readonly object _harmlessCountLock = new object();

#if DEBUG
        // Call-once invariant guard for Classify() (see its doc-comment): Classify mutates the
        // harmless-rollup counter, so a second call for the SAME record would double-count. The
        // two legitimate callers (ProcessRecord / ProcessEvent) set this permit immediately before
        // their single Classify call; Classify asserts it is set and consumes it, so a second call
        // within the same record's processing trips the assert — and a NEW caller that forgets to
        // set the permit is caught too. [ThreadStatic] keeps it per-processing-thread so the
        // concurrent EventRecordWritten callbacks (distinct records) never falsely collide.
        [ThreadStatic]
        private static bool s_classifyPermitted;
#endif

        public ModernDeploymentTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int logLevelMax = 3,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 30,
            string stateDirectory = null,
            int[] harmlessEventIds = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logLevelMax = logLevelMax;
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
            _harmlessEventIds = harmlessEventIds != null && harmlessEventIds.Length > 0
                ? new HashSet<int>(harmlessEventIds)
                : new HashSet<int> { 100, 1005, 1010 };
        }

        /// <summary>
        /// True once WhiteGlove start has been detected (either live Event 509 or persisted from prior run).
        /// Exposed primarily for diagnostics/tests.
        /// </summary>
        internal bool IsWhiteGloveStartDetected
        {
            get { lock (_stateLock) { return _whiteGloveStartDetected; } }
        }

        public void Start()
        {
            StartWatchers();

            if (_backfillEnabled)
            {
                BackfillTargetedEvents();
            }
            else
            {
                _logger.Info("ModernDeployment backfill disabled by config");
            }
        }

        public void Stop()
        {
            StopWatcher(ref _autopilotWatcher, "Autopilot");
            StopWatcher(ref _managementWatcher, "ManagementService");
        }

        public void Dispose() => Stop();

        // -----------------------------------------------------------------------
        // Watcher lifecycle
        // -----------------------------------------------------------------------

        private void StartWatchers()
        {
            _autopilotWatcher = TryStartWatcher(AutopilotChannel, "Autopilot", null);
            _managementWatcher = TryStartWatcher(ManagementChannel, "ManagementService", TargetedManagementServiceEventIds);
        }

        private EventLogWatcher TryStartWatcher(string channelName, string shortName, HashSet<int> targetedEventIds)
        {
            try
            {
                var xpath = BuildXPath(_logLevelMax, targetedEventIds);
                var query = new EventLogQuery(channelName, PathType.LogName, xpath);
                var watcher = new EventLogWatcher(query);
                watcher.EventRecordWritten += (sender, args) => OnEventRecordWritten(args, shortName, channelName);
                watcher.Enabled = true;
                _logger.Info($"ModernDeployment watcher started: {channelName} (levelMax={Math.Max(1, Math.Min(5, _logLevelMax))}, targetedIds={targetedEventIds?.Count ?? 0})");
                return watcher;
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"ModernDeployment event log not found: {channelName} (normal on non-Windows 10/11 test environments)");
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"ModernDeployment watcher access denied for {channelName}: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start ModernDeployment watcher for {channelName}", ex);
                // MON-D1: surface a dead ModernDeployment channel watcher as one-shot telemetry.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "ModernDeploymentTracker", reason: $"watcher_arm_failed:{channelName}", ex: ex);
                return null;
            }
        }

        /// <summary>
        /// Builds the XPath filter for the watcher. Exposed as internal for tests.
        /// </summary>
        internal static string BuildXPath(int logLevelMax, HashSet<int> targetedEventIds)
        {
            var levelMax = Math.Max(1, Math.Min(5, logLevelMax));
            var levelFilter = $"Level=0 or (Level >= 1 and Level <= {levelMax})";
            if (targetedEventIds != null && targetedEventIds.Count > 0)
            {
                var idClauses = string.Join(" or ", targetedEventIds.Select(id => $"EventID={id}"));
                levelFilter += $" or ({idClauses})";
            }
            return $"*[System[{levelFilter}]]";
        }

        private void StopWatcher(ref EventLogWatcher watcher, string shortName)
        {
            if (watcher == null) return;
            try
            {
                watcher.Enabled = false;
                watcher.Dispose();
                _logger.Info($"ModernDeployment watcher stopped: {shortName}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error stopping ModernDeployment watcher ({shortName})", ex);
            }
            finally
            {
                watcher = null;
            }
        }

        // -----------------------------------------------------------------------
        // Event processing
        // -----------------------------------------------------------------------

        private void OnEventRecordWritten(EventRecordWrittenEventArgs e, string shortName, string channelName)
        {
            if (e.EventRecord == null) return;
            try
            {
                ProcessRecord(e.EventRecord, shortName, channelName, isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Error processing ModernDeployment event from {shortName}", ex);
            }
        }

        private void ProcessRecord(EventRecord record, string shortName, string channelName, bool isBackfill)
        {
            // Classify BEFORE formatting. record.FormatDescription() loads the provider's
            // message-resource DLL (the expensive part), and the Autopilot channel watcher has
            // no EventID filter, so harmless-ID bursts (session 8bc1180f: ~689 EventID-100
            // records/minute) would otherwise pay full formatting cost only to be discarded by
            // the rollup gate. Skip formatting entirely for rollup-suppressed records.
#if DEBUG
            s_classifyPermitted = true; // single Classify permit for this record — see Classify().
#endif
            var verdict = Classify(record.Id, record.Level, shortName,
                out var eventType, out var severity, out var harmlessDowngraded, out var occurrenceCount);
            if (verdict == EventVerdict.Suppress)
            {
                return;
            }

            string description = null;
            try { description = record.FormatDescription(); }
            catch { /* some events lack formatting resources */ }

            var timeCreatedUtc = record.TimeCreated?.ToUniversalTime();
            if (verdict == EventVerdict.WhiteGlove)
            {
                HandleWhiteGloveStart(record.Id, record.Level, timeCreatedUtc, description, isBackfill);
                return;
            }

            EmitClassified(record.Id, record.Level, record.LevelDisplayName, record.ProviderName,
                timeCreatedUtc, description, shortName, channelName, isBackfill,
                eventType, severity, harmlessDowngraded, occurrenceCount);
        }

        /// <summary>
        /// Core event-processing logic, extracted to primitive inputs so tests can drive it
        /// without needing to synthesize an EventRecord (which is abstract + Windows-only).
        /// </summary>
        internal void ProcessEvent(
            int eventId,
            int? level,
            string levelDisplayName,
            string providerName,
            DateTime? timeCreatedUtc,
            string formattedDescription,
            string shortName,
            string channelName,
            bool isBackfill)
        {
#if DEBUG
            s_classifyPermitted = true; // single Classify permit for this record — see Classify().
#endif
            var verdict = Classify(eventId, level, shortName,
                out var eventType, out var severity, out var harmlessDowngraded, out var occurrenceCount);
            if (verdict == EventVerdict.WhiteGlove)
            {
                HandleWhiteGloveStart(eventId, level, timeCreatedUtc, formattedDescription, isBackfill);
                return;
            }
            if (verdict == EventVerdict.Suppress)
            {
                return;
            }

            EmitClassified(eventId, level, levelDisplayName, providerName, timeCreatedUtc,
                formattedDescription, shortName, channelName, isBackfill,
                eventType, severity, harmlessDowngraded, occurrenceCount);
        }

        private enum EventVerdict { Emit, Suppress, WhiteGlove }

        /// <summary>
        /// Cheap classification + rollup accounting from the EventID/level/channel alone — never
        /// touches the formatted description, so the EventRecord path can decide to skip the
        /// expensive <see cref="EventRecord.FormatDescription"/> for records that will be
        /// rollup-suppressed. MUST be called exactly once per record: it mutates the harmless
        /// occurrence counter.
        /// </summary>
        private EventVerdict Classify(
            int eventId,
            int? level,
            string shortName,
            out string eventType,
            out EventSeverity severity,
            out bool harmlessDowngraded,
            out int occurrenceCount)
        {
#if DEBUG
            // Enforce the call-once invariant: the caller (ProcessRecord/ProcessEvent) must have
            // just issued a single permit. Consume it so a second Classify for the same record trips.
            System.Diagnostics.Debug.Assert(
                s_classifyPermitted,
                "ModernDeploymentTracker.Classify() called without a fresh per-record permit — it " +
                "mutates the harmless-rollup counter and MUST be called exactly once per record " +
                "(set s_classifyPermitted in ProcessRecord/ProcessEvent immediately before the call).");
            s_classifyPermitted = false;
#endif

            eventType = null;
            severity = EventSeverity.Info;
            harmlessDowngraded = false;
            occurrenceCount = 0;

            if (eventId == EventId_WhiteGloveStart && shortName == "ManagementService")
            {
                return EventVerdict.WhiteGlove;
            }

            var effectiveLevel = level ?? 4;
            switch (effectiveLevel)
            {
                case 1:
                case 2:
                    eventType = Constants.EventTypes.ModernDeploymentError;
                    severity = EventSeverity.Error;
                    break;
                case 3:
                    eventType = Constants.EventTypes.ModernDeploymentWarning;
                    severity = EventSeverity.Warning;
                    break;
                default:
                    eventType = Constants.EventTypes.ModernDeploymentLog;
                    severity = EventSeverity.Info;
                    break;
            }

            // Downgrade configurable harmless EventIDs to Debug.
            // Level 1 (Critical) is NEVER downgraded — too risky to silence. Only Level 2
            // (Error) and Level 3 (Warning) are considered. The list is delivered by the
            // backend via CollectorConfiguration.ModernDeploymentHarmlessEventIds and
            // pre-seeded with known-harmless IDs (e.g. 100 "Autopilot policy not found",
            // 1005, 1010 "Autopilot.dll WIL hardwareinfo.cpp HRESULT 0x80070002" — no real
            // enrollment impact). Events stay visible in the timeline (Debug severity) for
            // troubleshooting.
            harmlessDowngraded = (effectiveLevel == 2 || effectiveLevel == 3) && _harmlessEventIds.Contains(eventId);
            if (harmlessDowngraded)
            {
                eventType = Constants.EventTypes.ModernDeploymentLog;
                severity = EventSeverity.Debug;

                // Burst rollup (session 8bc1180f): forward the first N occurrences per
                // EventId individually, then only every Kth with the cumulative count.
                lock (_harmlessCountLock)
                {
                    _harmlessOccurrenceCounts.TryGetValue(eventId, out occurrenceCount);
                    occurrenceCount++;
                    _harmlessOccurrenceCounts[eventId] = occurrenceCount;
                }
                if (occurrenceCount > HarmlessRollupIndividualLimit
                    && occurrenceCount % HarmlessRollupEmitEvery != 0)
                {
                    return EventVerdict.Suppress; // counted toward the next rollup emission
                }
            }

            return EventVerdict.Emit;
        }

        private void EmitClassified(
            int eventId,
            int? level,
            string levelDisplayName,
            string providerName,
            DateTime? timeCreatedUtc,
            string formattedDescription,
            string shortName,
            string channelName,
            bool isBackfill,
            string eventType,
            EventSeverity severity,
            bool harmlessDowngraded,
            int occurrenceCount)
        {
            var effectiveLevel = level ?? 4;
            var description = string.IsNullOrEmpty(formattedDescription)
                ? $"Event ID {eventId} (no formatted description)"
                : formattedDescription;

            var truncated = description.Length > 1000 ? description.Substring(0, 1000) + "…" : description;

            var data = new Dictionary<string, object>
            {
                { "channel", shortName },
                { "channelFullName", channelName },
                { "eventId", eventId },
                { "level", effectiveLevel },
                { "levelName", levelDisplayName ?? string.Empty },
                { "providerName", providerName ?? string.Empty },
                { "timeCreated", timeCreatedUtc?.ToString("o") ?? string.Empty },
                { "backfilled", isBackfill }
            };

            var message = $"[{shortName}] EventID {eventId}: {truncated}";
            if (harmlessDowngraded)
            {
                data["occurrenceCount"] = occurrenceCount;
                if (occurrenceCount > HarmlessRollupIndividualLimit)
                {
                    data["rollup"] = true;
                    message = $"[{shortName}] EventID {eventId}: {occurrenceCount} occurrences so far " +
                        $"(harmless-ID rollup, intermediate occurrences suppressed). Last: {truncated}";
                }
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "ModernDeploymentWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });
        }

        private void HandleWhiteGloveStart(int eventId, int? level, DateTime? timeCreatedUtc, string formattedDescription, bool isBackfill)
        {
            var description = string.IsNullOrEmpty(formattedDescription)
                ? $"Event ID {eventId} (no formatted description)"
                : formattedDescription;

            if (description.IndexOf("WhiteGlove", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _logger.Trace($"ManagementService Event 509 without WhiteGlove keyword — ignoring: {description}");
                return;
            }

            lock (_stateLock)
            {
                if (_whiteGloveStartDetected) return;
                _whiteGloveStartDetected = true;
            }

            _logger.Info($"WhiteGlove start detected via ManagementService Event 509 (backfill={isBackfill}): {description}");

            PersistWhiteGloveBackfillState(timeCreatedUtc);

            var truncated = description.Length > 500 ? description.Substring(0, 500) + "…" : description;

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.WhiteGloveStarted,
                Severity = EventSeverity.Info,
                Source = "ModernDeploymentWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"First WhiteGlove (Pre-Provisioning) hint — ManagementService EventID {eventId} (soft signal, not yet confirmed)",
                Data = new Dictionary<string, object>
                {
                    { "channel", "ManagementService" },
                    { "channelFullName", ManagementChannel },
                    { "eventId", eventId },
                    { "level", level ?? 4 },
                    { "description", truncated },
                    { "timeCreated", timeCreatedUtc?.ToString("o") ?? string.Empty },
                    { "backfilled", isBackfill }
                },
                ImmediateUpload = true
            });
        }

        // -----------------------------------------------------------------------
        // Backfill
        // -----------------------------------------------------------------------

        private void BackfillTargetedEvents()
        {
            if (TargetedManagementServiceEventIds.Count == 0) return;

            var persistedState = LoadWhiteGloveBackfillState();
            if (persistedState != null && persistedState.WhiteGloveStartSeen)
            {
                _logger.Info($"WhiteGlove start already persisted from prior run (seen at {persistedState.SeenUtc:O}) — skipping backfill");
                lock (_stateLock) { _whiteGloveStartDetected = true; }
                return;
            }

            try
            {
                var lookbackMs = _backfillLookbackMinutes * 60 * 1000;
                var idClauses = string.Join(" or ",
                    TargetedManagementServiceEventIds.Select(id => $"EventID={id}"));
                var xpath = $"*[System[({idClauses}) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

                _logger.Info($"ModernDeployment backfill: scanning {ManagementChannel} " +
                    $"(lookback={_backfillLookbackMinutes}min, targetedIds={TargetedManagementServiceEventIds.Count})");

                var query = new EventLogQuery(ManagementChannel, PathType.LogName, xpath)
                {
                    ReverseDirection = true
                };

                int found = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            found++;
                            _logger.Info($"Backfill found ManagementService Event {record.Id} at {record.TimeCreated:O}");
                            ProcessRecord(record, "ManagementService", ManagementChannel, isBackfill: true);
                        }
                    }
                }

                if (found == 0)
                    _logger.Debug($"ModernDeployment backfill: no targeted events found in last {_backfillLookbackMinutes} minutes");
                else
                    _logger.Info($"ModernDeployment backfill: processed {found} event(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"ModernDeployment event log not found during backfill: {ManagementChannel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"ModernDeployment backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"ModernDeployment backfill failed: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Backfill state persistence
        // -----------------------------------------------------------------------

        internal void PersistWhiteGloveBackfillState(DateTime? eventTimeUtc)
        {
            if (string.IsNullOrEmpty(_stateDirectory)) return;

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var filePath = Path.Combine(_stateDirectory, WhiteGloveBackfillStateFileName);
                var state = new WhiteGloveBackfillState
                {
                    WhiteGloveStartSeen = true,
                    SeenUtc = eventTimeUtc ?? DateTime.UtcNow,
                    PersistedUtc = DateTime.UtcNow
                };
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, filePath, overwrite: true);
                try { File.Delete(tempPath); } catch { }
                _logger.Info($"WhiteGlove backfill state persisted to {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to persist WhiteGlove backfill state: {ex.Message}");
            }
        }

        internal WhiteGloveBackfillState LoadWhiteGloveBackfillState()
        {
            if (string.IsNullOrEmpty(_stateDirectory)) return null;

            var filePath = Path.Combine(_stateDirectory, WhiteGloveBackfillStateFileName);
            if (!File.Exists(filePath)) return null;

            try
            {
                var json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<WhiteGloveBackfillState>(json);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load WhiteGlove backfill state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Test-only: force the in-memory WhiteGlove-seen guard (simulates successful backfill-dedup).
        /// </summary>
        internal void MarkWhiteGloveStartDetectedForTest()
        {
            lock (_stateLock) { _whiteGloveStartDetected = true; }
        }

        internal class WhiteGloveBackfillState
        {
            public bool WhiteGloveStartSeen { get; set; }
            public DateTime SeenUtc { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}
