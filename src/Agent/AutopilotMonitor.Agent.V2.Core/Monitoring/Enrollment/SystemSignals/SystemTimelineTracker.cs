using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches the <c>System</c> event log for two enrollment blind spots and forwards them as
    /// timeline ground truth:
    /// <list type="bullet">
    ///   <item><b>System clock steps</b> (Kernel-General EventID 1) → <c>system_clock_changed</c>.
    ///         The payload carries <c>oldTime</c>/<c>newTime</c>/<c>timeDeltaMs</c> plus the
    ///         setting process — the "clock set from X to Y" fact that today has to be inferred
    ///         from timestamp discontinuities. w32time also logs frequent 1 ms micro-slews on the
    ///         same EventID, so emissions are gated on <see cref="MinClockDeltaMs"/>.</item>
    ///   <item><b>Completed sleep episodes</b> → <c>system_sleep_episode</c>. Classic S3/S4 comes
    ///         from Power-Troubleshooter EventID 1 (one event per episode at wake, carrying
    ///         SleepTime + WakeTime). Modern Standby comes from Kernel-Power EventID 507 (exit),
    ///         whose payload carries the scenario duration and the actual sleep residency; the
    ///         enter time is derived as exit − duration. Kernel-Power 506 (enter) is deliberately
    ///         NOT consumed: 507 already carries everything, and pairing 506/507 would need an
    ///         orphan-tolerant state machine across agent restarts for zero information gain —
    ///         an episode without a 507 never completed and would not be emitted under any design.</item>
    /// </list>
    /// <para>
    /// <b>Payload times are authoritative.</b> The decisive clock correction (w32time fixing a
    /// wrong BIOS clock) happens exactly when the device clock is untrustworthy, and the backend
    /// clamps implausible event timestamps to server time. All semantic instants therefore travel
    /// as explicit ISO-8601 payload fields, never only as the event's own <c>Timestamp</c>.
    /// </para>
    /// <para>
    /// <b>Backfill + cross-restart dedup</b> mirror <see cref="WindowsUpdateTracker"/>: the System
    /// .evtx persists across the OOBE reboots, so a startup scan recovers pre-agent clock steps
    /// and standby episodes; a persisted RecordId watermark plus an intra-run seen-set (a HashSet,
    /// NOT a high-water mark — see the field comment) keep restarts from re-emitting.
    /// </para>
    /// </summary>
    internal sealed class SystemTimelineTracker : IDisposable
    {
        internal const string Channel = "System";

        internal const string ProviderKernelGeneral       = "Microsoft-Windows-Kernel-General";       // EventID 1: system time changed
        internal const string ProviderPowerTroubleshooter = "Microsoft-Windows-Power-Troubleshooter"; // EventID 1: resumed from classic sleep/hibernate
        internal const string ProviderKernelPower         = "Microsoft-Windows-Kernel-Power";         // EventID 507: Modern Standby exit

        internal const int EventId_ClockChange       = 1;
        internal const int EventId_ClassicResume     = 1;
        internal const int EventId_ModernStandbyExit = 507;

        internal const string WatermarkStateFileName = "system-timeline-watermark.json";

        // w32time logs Kernel-General 1 for routine 1 ms slew corrections (verified live: several
        // per hour). Only genuine steps are timeline-relevant; anything below this is claimed in
        // the dedup set but never emitted.
        internal const long MinClockDeltaMs = 2000;

        // A step of at least 5 minutes can invalidate token/cert validation windows and visibly
        // reorders the timeline (matches the web's TimeJumpBadge threshold) — surface it fast.
        internal const long ClockDeltaWarningMs = 5 * 60 * 1000;

        // Modern Standby logs a 506/507 pair for every screen-off scenario, including seconds-long
        // ones where the device never actually slept (SleepDurationInUs 0). Only episodes with
        // real sleep residency are emitted. Classic Power-Troubleshooter episodes have no floor:
        // they only exist for genuinely completed S3/S4 transitions.
        internal const long MinSleepDurationSeconds = 60;

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly string _stateDirectory;

        private EventLogWatcher _watcher;

        // Dedup. Guarded because EventRecordWritten callbacks run concurrently with the backfill scan.
        //
        // Cross-restart boundary loaded from disk: the highest RecordId emitted in a PRIOR run.
        // Immutable during this run — records at/below it were already emitted before this process
        // started and must not be re-emitted by the backfill scan.
        private long _restartWatermark = -1;
        // RecordIds claimed in THIS run — the intra-run dedup set. Deliberately a HashSet, NOT a
        // high-water mark: the live watcher is armed before the backfill runs, so a live record with
        // a HIGHER RecordId can be processed before/during backfill. A high-water mark would then
        // make the dedup skip every older, never-emitted backfill record — silently dropping exactly
        // the pre-agent clock corrections and standby episodes this tracker exists for. The targeted
        // records are low-volume, so the set stays tiny.
        private readonly HashSet<long> _seenThisRun = new HashSet<long>();
        // Highest RecordId claimed so far (max of restart watermark + this run). Persisted so the
        // next run's restart watermark is correct.
        private long _maxEmittedRecordId = -1;
        private readonly object _watermarkLock = new object();

        public SystemTimelineTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 1440,
            string stateDirectory = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
        }

        public void Start()
        {
            LoadWatermark();
            StartWatcher();

            if (_backfillEnabled && _backfillLookbackMinutes > 0)
                BackfillRecentEvents();
            else
                _logger.Info("SystemTimeline backfill disabled by config");
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
                _logger.Info("SystemTimeline watcher stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping SystemTimeline watcher", ex);
            }
            finally
            {
                _watcher = null;
            }
        }

        public void Dispose() => Stop();

        // -----------------------------------------------------------------------
        // Watcher lifecycle
        // -----------------------------------------------------------------------

        private void StartWatcher()
        {
            try
            {
                var query = new EventLogQuery(Channel, PathType.LogName, BuildXPath());
                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;
                _logger.Info($"SystemTimeline watcher started: {Channel} (clock changes + sleep episodes)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"SystemTimeline event log not found: {Channel} (normal on non-Windows test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"SystemTimeline watcher access denied for {Channel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start SystemTimeline watcher for {Channel}", ex);
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "SystemTimelineTracker", reason: $"watcher_arm_failed:{Channel}", ex: ex);
            }
        }

        /// <summary>
        /// Provider-qualified XPath for the three targeted signals. Provider filtering is mandatory
        /// on the System channel — EventID 1 alone matches many unrelated providers.
        /// Exposed as internal for tests.
        /// </summary>
        internal static string BuildXPath() =>
            "*[System[" + ProviderIdClauses() + "]]";

        /// <summary>Backfill variant of <see cref="BuildXPath"/> with the lookback window applied.</summary>
        internal static string BuildBackfillXPath(long lookbackMs) =>
            "*[System[(" + ProviderIdClauses() + $") and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

        private static string ProviderIdClauses() =>
            $"(Provider[@Name='{ProviderKernelGeneral}'] and EventID={EventId_ClockChange})" +
            $" or (Provider[@Name='{ProviderPowerTroubleshooter}'] and EventID={EventId_ClassicResume})" +
            $" or (Provider[@Name='{ProviderKernelPower}'] and EventID={EventId_ModernStandbyExit})";

        // -----------------------------------------------------------------------
        // Event processing
        // -----------------------------------------------------------------------

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;
            try
            {
                ProcessRecord(e.EventRecord, isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing SystemTimeline event", ex);
            }
        }

        private void ProcessRecord(EventRecord record, bool isBackfill)
        {
            var recordId = record.RecordId ?? -1;

            // Cheap dedup gate BEFORE the XML rendering work.
            if (IsAlreadyProcessed(recordId))
                return;

            string xml = null;
            try { xml = record.ToXml(); }
            catch { /* fall through with empty EventData */ }

            ProcessEvent(
                providerName: record.ProviderName,
                eventId: record.Id,
                recordId: recordId,
                timeCreatedUtc: record.TimeCreated?.ToUniversalTime(),
                eventData: ParseEventData(xml),
                isBackfill: isBackfill);
        }

        /// <summary>
        /// Core processing extracted to primitive inputs so tests can drive it without synthesizing
        /// an abstract, Windows-only <see cref="EventRecord"/> (WindowsUpdateTracker test-seam
        /// pattern; a plain dictionary instead of named args because three providers with ~10
        /// payload fields each make a fixed signature impractical).
        /// </summary>
        internal void ProcessEvent(
            string providerName,
            int eventId,
            long recordId,
            DateTime? timeCreatedUtc,
            Dictionary<string, string> eventData,
            bool isBackfill)
        {
            if (!MarkProcessed(recordId))
                return; // already emitted (cross-restart or duplicate delivery)

            eventData = eventData ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.Equals(providerName, ProviderKernelGeneral, StringComparison.OrdinalIgnoreCase) && eventId == EventId_ClockChange)
                HandleClockChange(recordId, timeCreatedUtc, eventData, isBackfill);
            else if (string.Equals(providerName, ProviderPowerTroubleshooter, StringComparison.OrdinalIgnoreCase) && eventId == EventId_ClassicResume)
                HandleClassicResume(recordId, timeCreatedUtc, eventData, isBackfill);
            else if (string.Equals(providerName, ProviderKernelPower, StringComparison.OrdinalIgnoreCase) && eventId == EventId_ModernStandbyExit)
                HandleModernStandbyExit(recordId, timeCreatedUtc, eventData, isBackfill);
            // Anything else slipped past the XPath filter — ignore.
        }

        // -----------------------------------------------------------------------
        // system_clock_changed (Kernel-General EventID 1)
        // -----------------------------------------------------------------------

        private void HandleClockChange(long recordId, DateTime? timeCreatedUtc, Dictionary<string, string> eventData, bool isBackfill)
        {
            eventData.TryGetValue("NewTime", out var newTimeRaw);
            eventData.TryGetValue("OldTime", out var oldTimeRaw);
            var newTime = TryParseUtc(newTimeRaw);
            var oldTime = TryParseUtc(oldTimeRaw);

            // Signed delta preferably from the authoritative instants (NewTime − OldTime carries
            // the direction unambiguously); TimeDeltaInMs is the fallback when either fails to
            // parse. Records with neither are unusable and skipped (already claimed by dedup).
            long deltaMs;
            if (newTime.HasValue && oldTime.HasValue)
            {
                deltaMs = (long)Math.Round((newTime.Value - oldTime.Value).TotalMilliseconds);
            }
            else if (eventData.TryGetValue("TimeDeltaInMs", out var deltaRaw) &&
                long.TryParse(deltaRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDelta))
            {
                deltaMs = parsedDelta;
            }
            else
            {
                _logger.Debug($"SystemTimeline: clock-change record {recordId} has no usable times/delta — skipped");
                return;
            }

            if (Math.Abs(deltaMs) < MinClockDeltaMs)
                return; // w32time micro-slew — claimed (MarkProcessed) but never emitted

            eventData.TryGetValue("Reason", out var reasonRaw);
            int.TryParse(reasonRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reason);
            var reasonText = reason == 1 ? "application_set"
                : reason == 2 ? "hardware_clock_sync"
                : "unknown";

            eventData.TryGetValue("ProcessName", out var processName);

            var data = new Dictionary<string, object>
            {
                { "timeDeltaMs", deltaMs },
                { "reason", reason },
                { "reasonText", reasonText },
                { "backfilled", isBackfill },
            };
            // Authoritative instants: re-normalized ISO-8601 when parseable, raw pass-through
            // otherwise — the payload must survive even a malformed provider rendering.
            data["newTime"] = newTime.HasValue ? newTime.Value.ToString("o") : (object)(newTimeRaw ?? string.Empty);
            data["oldTime"] = oldTime.HasValue ? oldTime.Value.ToString("o") : (object)(oldTimeRaw ?? string.Empty);
            if (recordId >= 0) data["recordId"] = recordId;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");
            if (!string.IsNullOrEmpty(processName)) data["processName"] = processName;
            if (eventData.TryGetValue("ProcessID", out var pidRaw) &&
                int.TryParse(pidRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                data["processId"] = pid;
            }

            var direction = deltaMs >= 0 ? "forward" : "backward";
            var processLeaf = ProcessLeafName(processName);
            var message = $"System clock stepped {direction} {FormatDuration(TimeSpan.FromMilliseconds(Math.Abs(deltaMs)))}" +
                $" (reason: {reasonText}" + (processLeaf != null ? $", process: {processLeaf})" : ")");

            var isLargeStep = Math.Abs(deltaMs) >= ClockDeltaWarningMs;
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // The record's own creation time, not UtcNow: backfilled pre-agent clock steps must
                // land on the timeline where they happened (WindowsUpdateTracker convention).
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = Constants.EventTypes.SystemClockChanged,
                Severity = isLargeStep ? EventSeverity.Warning : EventSeverity.Info,
                Source = "SystemTimelineWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = isLargeStep,
            });
        }

        // -----------------------------------------------------------------------
        // system_sleep_episode — classic sleep/hibernate (Power-Troubleshooter EventID 1)
        // -----------------------------------------------------------------------

        private void HandleClassicResume(long recordId, DateTime? timeCreatedUtc, Dictionary<string, string> eventData, bool isBackfill)
        {
            eventData.TryGetValue("SleepTime", out var sleepRaw);
            eventData.TryGetValue("WakeTime", out var wakeRaw);
            var enteredAt = TryParseUtc(sleepRaw);
            var exitedAt = TryParseUtc(wakeRaw);

            // EffectiveState 5 = hibernate (S4); TargetState is the fallback when the effective
            // state is missing (older event versions).
            eventData.TryGetValue("EffectiveState", out var effectiveState);
            eventData.TryGetValue("TargetState", out var targetState);
            var stateValue = !string.IsNullOrEmpty(effectiveState) ? effectiveState : targetState;
            var kind = stateValue == "5" ? "hibernate" : "sleep";

            long? durationSeconds = null;
            if (enteredAt.HasValue && exitedAt.HasValue && exitedAt.Value > enteredAt.Value)
                durationSeconds = (long)Math.Round((exitedAt.Value - enteredAt.Value).TotalSeconds);

            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "backfilled", isBackfill },
            };
            data["enteredAt"] = enteredAt.HasValue ? enteredAt.Value.ToString("o") : (object)(sleepRaw ?? string.Empty);
            data["exitedAt"] = exitedAt.HasValue ? exitedAt.Value.ToString("o") : (object)(wakeRaw ?? string.Empty);
            if (durationSeconds.HasValue) data["durationSeconds"] = durationSeconds.Value;
            if (recordId >= 0) data["recordId"] = recordId;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");
            if (eventData.TryGetValue("WakeSourceType", out var wakeTypeRaw) &&
                int.TryParse(wakeTypeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wakeType))
            {
                data["wakeSourceType"] = wakeType;
            }
            eventData.TryGetValue("WakeSourceText", out var wakeText);
            if (!string.IsNullOrEmpty(wakeText)) data["wakeSourceText"] = wakeText;

            var durationLabel = durationSeconds.HasValue
                ? " after " + FormatDuration(TimeSpan.FromSeconds(durationSeconds.Value))
                : string.Empty;
            var wakeLabel = !string.IsNullOrEmpty(wakeText) ? $" (wake source: {wakeText})" : string.Empty;

            EmitSleepEpisode(timeCreatedUtc, data,
                $"Device resumed from {kind}{durationLabel}{wakeLabel}");
        }

        // -----------------------------------------------------------------------
        // system_sleep_episode — Modern Standby (Kernel-Power EventID 507)
        // -----------------------------------------------------------------------

        private void HandleModernStandbyExit(long recordId, DateTime? timeCreatedUtc, Dictionary<string, string> eventData, bool isBackfill)
        {
            eventData.TryGetValue("SleepDurationInUs", out var sleepUsRaw);
            long.TryParse(sleepUsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sleepUs);
            var sleepDurationSeconds = sleepUs / 1_000_000;

            // Every screen-off scenario logs a 507, including seconds-long ones where the device
            // never slept (SleepDurationInUs 0, incl. SleepEntered=false) — one gate covers both.
            if (sleepDurationSeconds < MinSleepDurationSeconds)
                return; // claimed but not an episode worth the timeline

            eventData.TryGetValue("DurationInUs", out var durationUsRaw);
            long.TryParse(durationUsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationUs);
            if (durationUs < sleepUs) durationUs = sleepUs; // scenario duration can never undercut its sleep residency

            var durationSeconds = durationUs / 1_000_000;
            var exitedAt = timeCreatedUtc; // 507 is written at scenario exit
            DateTime? enteredAt = exitedAt.HasValue
                ? exitedAt.Value.AddMilliseconds(-(durationUs / 1000.0))
                : (DateTime?)null;

            var data = new Dictionary<string, object>
            {
                { "kind", "modern_standby" },
                { "durationSeconds", durationSeconds },
                { "sleepDurationSeconds", sleepDurationSeconds },
                { "backfilled", isBackfill },
            };
            if (enteredAt.HasValue) data["enteredAt"] = enteredAt.Value.ToString("o");
            if (exitedAt.HasValue) data["exitedAt"] = exitedAt.Value.ToString("o");
            if (recordId >= 0) data["recordId"] = recordId;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");
            if (eventData.TryGetValue("Reason", out var reasonRaw) &&
                int.TryParse(reasonRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var reason))
            {
                data["reason"] = reason;
            }
            bool? onAcPower = null;
            if (eventData.TryGetValue("PowerStateAc", out var acRaw) && bool.TryParse(acRaw, out var ac))
            {
                onAcPower = ac;
                data["onAcPower"] = ac;
            }
            if (eventData.TryGetValue("BatteryRemainingCapacityOnExit", out var battRaw) &&
                long.TryParse(battRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var batt))
            {
                data["batteryRemainingCapacityOnExit"] = batt;
            }

            var powerLabel = onAcPower.HasValue ? (onAcPower.Value ? ", on AC" : ", on battery") : string.Empty;
            EmitSleepEpisode(timeCreatedUtc, data,
                $"Device exited Modern Standby after {FormatDuration(TimeSpan.FromSeconds(durationSeconds))}" +
                $" (slept {FormatDuration(TimeSpan.FromSeconds(sleepDurationSeconds))}{powerLabel})");
        }

        private void EmitSleepEpisode(DateTime? timeCreatedUtc, Dictionary<string, object> data, string message)
        {
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // Wake/exit moment (the record's creation time) — backfilled pre-agent episodes
                // must land on the timeline where the device actually woke.
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = Constants.EventTypes.SystemSleepEpisode,
                Severity = EventSeverity.Info,
                Source = "SystemTimelineWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = false,
            });
        }

        // -----------------------------------------------------------------------
        // Parsing helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Extracts <c>&lt;Data Name="..."&gt;value&lt;/Data&gt;</c> pairs from an event's rendered
        /// XML, keyed case-insensitively. Namespace-agnostic (matches on local name). Returns an
        /// empty map on null/malformed XML — never throws.
        /// </summary>
        internal static Dictionary<string, string> ParseEventData(string xml)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(xml)) return result;

            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Data"))
                {
                    var nameAttr = el.Attribute("Name");
                    if (nameAttr == null || string.IsNullOrEmpty(nameAttr.Value)) continue;
                    if (!result.ContainsKey(nameAttr.Value))
                        result[nameAttr.Value] = el.Value;
                }
            }
            catch
            {
                // Malformed / unexpected XML — best effort, return what we have.
            }

            return result;
        }

        internal static DateTime? TryParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        /// <summary>Compact human duration for messages: "2h 05m", "56m 12s", "45s".</summary>
        internal static string FormatDuration(TimeSpan span)
        {
            if (span.TotalHours >= 1)
                return $"{(long)span.TotalHours}h {span.Minutes:D2}m";
            if (span.TotalMinutes >= 1)
                return $"{span.Minutes}m {span.Seconds:D2}s";
            return $"{(long)Math.Ceiling(span.TotalSeconds)}s";
        }

        /// <summary>Leaf file name of a kernel device path like <c>\Device\HarddiskVolume3\Windows\System32\svchost.exe</c>.</summary>
        internal static string ProcessLeafName(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            var idx = devicePath.LastIndexOf('\\');
            var leaf = idx >= 0 ? devicePath.Substring(idx + 1) : devicePath;
            return string.IsNullOrEmpty(leaf) ? null : leaf;
        }

        // -----------------------------------------------------------------------
        // Backfill
        // -----------------------------------------------------------------------

        private void BackfillRecentEvents()
        {
            try
            {
                var lookbackMs = (long)_backfillLookbackMinutes * 60 * 1000;
                _logger.Info($"SystemTimeline backfill: scanning {Channel} " +
                    $"(lookback={_backfillLookbackMinutes}min, restartWatermark={_restartWatermark})");

                var query = new EventLogQuery(Channel, PathType.LogName, BuildBackfillXPath(lookbackMs));

                int processed = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            ProcessRecord(record, isBackfill: true);
                            processed++;
                        }
                    }
                }

                if (processed == 0)
                    _logger.Debug($"SystemTimeline backfill: no targeted events in last {_backfillLookbackMinutes} minutes");
                else
                    _logger.Info($"SystemTimeline backfill: scanned {processed} record(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"SystemTimeline event log not found during backfill: {Channel} (normal on non-Windows test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"SystemTimeline backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"SystemTimeline backfill failed: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Watermark dedup (cross-restart)
        // -----------------------------------------------------------------------

        private bool IsAlreadyProcessed(long recordId)
        {
            if (recordId < 0) return false; // no RecordId → cannot dedup, let it through
            lock (_watermarkLock)
            {
                return recordId <= _restartWatermark || _seenThisRun.Contains(recordId);
            }
        }

        /// <summary>
        /// Atomically claims <paramref name="recordId"/>. Returns true if this is the first time we
        /// see it (caller should evaluate/emit), false if it was already processed — either in a
        /// PRIOR run (at/below the restart watermark) or already this run (in the seen-set). Only
        /// advances + persists the max on a genuinely-new, higher RecordId. Records without a
        /// RecordId (-1) are always processed, never tracked or persisted. Note: threshold-suppressed
        /// records are claimed too, so a restart's backfill does not re-evaluate them.
        /// </summary>
        private bool MarkProcessed(long recordId)
        {
            if (recordId < 0) return true;

            long toPersist = -1;
            lock (_watermarkLock)
            {
                if (recordId <= _restartWatermark || !_seenThisRun.Add(recordId))
                    return false;

                if (recordId > _maxEmittedRecordId)
                {
                    _maxEmittedRecordId = recordId;
                    toPersist = _maxEmittedRecordId;
                }
            }
            if (toPersist >= 0) PersistWatermark(toPersist);
            return true;
        }

        internal void LoadWatermark()
        {
            if (string.IsNullOrEmpty(_stateDirectory)) return;

            var filePath = Path.Combine(_stateDirectory, WatermarkStateFileName);
            if (!File.Exists(filePath)) return;

            try
            {
                var json = File.ReadAllText(filePath);
                var state = JsonConvert.DeserializeObject<WatermarkState>(json);
                if (state != null)
                {
                    lock (_watermarkLock)
                    {
                        _restartWatermark = state.LastRecordId;
                        _maxEmittedRecordId = state.LastRecordId;
                    }
                    _logger.Info($"SystemTimeline watermark loaded: lastRecordId={state.LastRecordId} (persisted {state.PersistedUtc:O})");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load SystemTimeline watermark: {ex.Message}");
            }
        }

        private void PersistWatermark(long recordId)
        {
            if (string.IsNullOrEmpty(_stateDirectory)) return;

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var filePath = Path.Combine(_stateDirectory, WatermarkStateFileName);
                var state = new WatermarkState { LastRecordId = recordId, PersistedUtc = DateTime.UtcNow };
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Copy(tempPath, filePath, overwrite: true);
                try { File.Delete(tempPath); } catch { }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to persist SystemTimeline watermark: {ex.Message}");
            }
        }

        internal sealed class WatermarkState
        {
            public long LastRecordId { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}
