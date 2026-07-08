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
    /// Watches <c>Microsoft-Windows-WindowsUpdateClient/Operational</c> for quality/cumulative
    /// update install activity that happens DURING Autopilot OOBE / ESP and forwards it as
    /// structured <c>windows_update_succeeded</c> / <c>windows_update_failed</c> /
    /// <c>windows_update_started</c> events.
    /// <para>
    /// This is a blind spot no other tool surfaces well: a cumulative update installing mid-OOBE
    /// can break the enrollment (r/Intune KB5095189), and the ESP "Install Windows quality updates
    /// during OOBE" feature makes it increasingly common. The Intune console shows none of it — it
    /// is otherwise deep manual digging on the device.
    /// </para>
    /// <para>
    /// <b>Backfill.</b> OOBE updates run at the early "Getting updates" screen, frequently BEFORE the
    /// agent starts. The WindowsUpdateClient/Operational .evtx persists across the mid-OOBE reboot,
    /// so a startup backfill with a generous lookback catches those pre-agent events.
    /// </para>
    /// <para>
    /// <b>Cross-restart dedup.</b> Unlike the single-latch WhiteGlove backfill, WU events are many
    /// distinct records, so an agent restart would re-read (and re-emit) events already reported in
    /// the prior run. We persist the highest processed <see cref="EventRecord.RecordId"/> as a
    /// watermark and skip records at/below it. Live records always carry a higher RecordId and pass.
    /// </para>
    /// <para>
    /// <b>Verified EventIDs (build against these):</b> 19=install success, 20=install failure
    /// (carries HRESULT in <c>errorCode</c>), 43=install started, 44=download started. IDs whose
    /// semantics are unverified across sources (25/26/31, restart-required 21 vs 41) are excluded by
    /// default and can be added via <c>CollectorConfiguration.WindowsUpdateTargetedEventIds</c>
    /// after validation on a real device trace — no redeploy needed.
    /// </para>
    /// </summary>
    internal sealed class WindowsUpdateTracker : IDisposable
    {
        internal const string Channel = "Microsoft-Windows-WindowsUpdateClient/Operational";

        internal const int EventId_InstallSuccess  = 19;
        internal const int EventId_InstallFailure  = 20;
        internal const int EventId_InstallStarted   = 43;
        internal const int EventId_DownloadStarted  = 44;

        internal const string WatermarkStateFileName = "windows-update-watermark.json";

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly HashSet<int> _targetedEventIds;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly string _stateDirectory;

        private EventLogWatcher _watcher;

        // Dedup. Guarded because EventRecordWritten callbacks run concurrently with the backfill scan.
        //
        // Cross-restart boundary loaded from disk: the highest RecordId emitted in a PRIOR run.
        // Immutable during this run — events at/below it were already emitted before this process
        // started and must not be re-emitted by the backfill scan.
        private long _restartWatermark = -1;
        // RecordIds emitted in THIS run — the intra-run dedup set. Deliberately a HashSet, NOT a
        // high-water mark: the live watcher is armed before the backfill runs, so a live event with a
        // HIGHER RecordId can be processed before/during backfill. A high-water mark would then make
        // the dedup skip every older, never-emitted backfill event (recordId <= max) — silently
        // dropping exactly the early pre-agent OOBE updates that are this feature's whole point.
        // WU install events are low-volume (a handful per enrollment), so the set stays tiny.
        private readonly HashSet<long> _seenThisRun = new HashSet<long>();
        // Highest RecordId emitted so far (max of restart watermark + this run). Persisted so the next
        // run's restart watermark is correct.
        private long _maxEmittedRecordId = -1;
        private readonly object _watermarkLock = new object();

        public WindowsUpdateTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int[] targetedEventIds = null,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 60,
            string stateDirectory = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _targetedEventIds = targetedEventIds != null && targetedEventIds.Length > 0
                ? new HashSet<int>(targetedEventIds)
                : new HashSet<int> { EventId_InstallSuccess, EventId_InstallFailure, EventId_InstallStarted, EventId_DownloadStarted };
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
        }

        public void Start()
        {
            LoadWatermark();
            StartWatcher();

            if (_backfillEnabled && _backfillLookbackMinutes > 0)
            {
                BackfillRecentEvents();
            }
            else
            {
                _logger.Info("WindowsUpdate backfill disabled by config");
            }
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
                _logger.Info("WindowsUpdate watcher stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping WindowsUpdate watcher", ex);
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
            if (_targetedEventIds.Count == 0)
            {
                _logger.Warning("WindowsUpdate watcher not started: no targeted EventIDs configured");
                return;
            }

            try
            {
                var xpath = BuildXPath(_targetedEventIds);
                var query = new EventLogQuery(Channel, PathType.LogName, xpath);
                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;
                _logger.Info($"WindowsUpdate watcher started: {Channel} (targetedIds={_targetedEventIds.Count})");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"WindowsUpdate event log not found: {Channel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"WindowsUpdate watcher access denied for {Channel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start WindowsUpdate watcher for {Channel}", ex);
                // MON-D1: surface a dead watcher as one-shot telemetry, not just a local log line.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "WindowsUpdateTracker", reason: $"watcher_arm_failed:{Channel}", ex: ex);
            }
        }

        /// <summary>
        /// Builds the targeted-EventID XPath filter. Exposed as internal for tests.
        /// </summary>
        internal static string BuildXPath(HashSet<int> targetedEventIds)
        {
            var idClauses = string.Join(" or ",
                targetedEventIds.OrderBy(id => id).Select(id => $"EventID={id}"));
            return $"*[System[({idClauses})]]";
        }

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
                _logger.Error("Error processing WindowsUpdate event", ex);
            }
        }

        private void ProcessRecord(EventRecord record, bool isBackfill)
        {
            var recordId = record.RecordId ?? -1;

            // Cheap dedup gate BEFORE the expensive XML/FormatDescription work.
            if (IsAlreadyProcessed(recordId))
                return;

            string xml = null;
            try { xml = record.ToXml(); }
            catch { /* fall back to positional-less parse below */ }

            var eventData = ParseEventData(xml);
            eventData.TryGetValue("updateTitle", out var updateTitle);
            eventData.TryGetValue("updateGuid", out var updateGuid);
            eventData.TryGetValue("updateRevisionNumber", out var updateRevision);
            eventData.TryGetValue("errorCode", out var errorCode);

            string description = null;
            try { description = record.FormatDescription(); }
            catch { /* some events lack formatting resources */ }

            ProcessEvent(
                eventId: record.Id,
                level: record.Level,
                recordId: recordId,
                timeCreatedUtc: record.TimeCreated?.ToUniversalTime(),
                updateTitle: updateTitle,
                updateGuid: updateGuid,
                updateRevisionNumber: updateRevision,
                errorCode: errorCode,
                formattedDescription: description,
                isBackfill: isBackfill);
        }

        /// <summary>
        /// Core processing extracted to primitive inputs so tests can drive it without synthesizing
        /// an abstract, Windows-only <see cref="EventRecord"/>. Mirrors the ModernDeploymentTracker
        /// test-seam pattern.
        /// </summary>
        internal void ProcessEvent(
            int eventId,
            int? level,
            long recordId,
            DateTime? timeCreatedUtc,
            string updateTitle,
            string updateGuid,
            string updateRevisionNumber,
            string errorCode,
            string formattedDescription,
            bool isBackfill)
        {
            if (!MarkProcessed(recordId))
                return; // already emitted (cross-restart or duplicate delivery)

            string eventType;
            EventSeverity severity;
            bool immediateUpload;
            ClassifyEventId(eventId, out eventType, out severity, out immediateUpload);

            var data = new Dictionary<string, object>
            {
                { "wuEventId", eventId },
                { "backfilled", isBackfill },
            };
            if (recordId >= 0) data["recordId"] = recordId;
            if (level.HasValue) data["level"] = level.Value;
            if (!string.IsNullOrEmpty(updateTitle)) data["updateTitle"] = updateTitle;
            if (!string.IsNullOrEmpty(updateGuid)) data["updateGuid"] = updateGuid;
            if (!string.IsNullOrEmpty(updateRevisionNumber)) data["updateRevisionNumber"] = updateRevisionNumber;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");

            // Decode the HRESULT for failure events (and any event that carries a non-empty errorCode).
            string hresultHex;
            if (TryNormalizeHResult(errorCode, out var hresultValue, out hresultHex))
            {
                data["hresult"] = hresultHex;
                data["hresultSymbol"] = DecodeHResult(hresultValue);
            }

            var title = string.IsNullOrEmpty(updateTitle) ? "(unknown update)" : updateTitle;
            string message;
            switch (eventId)
            {
                case EventId_InstallSuccess:
                    message = $"Windows Update installed during enrollment: {title}";
                    break;
                case EventId_InstallFailure:
                    message = data.TryGetValue("hresultSymbol", out var sym)
                        ? $"Windows Update FAILED during enrollment: {title} ({data["hresult"]} {sym})"
                        : $"Windows Update FAILED during enrollment: {title}";
                    break;
                case EventId_DownloadStarted:
                    message = $"Windows Update download started during enrollment: {title}";
                    break;
                case EventId_InstallStarted:
                    message = $"Windows Update install started during enrollment: {title}";
                    break;
                default:
                    message = $"Windows Update activity (EventID {eventId}) during enrollment: {title}";
                    break;
            }
            if (!string.IsNullOrEmpty(formattedDescription))
            {
                var truncated = formattedDescription.Length > 1000
                    ? formattedDescription.Substring(0, 1000) + "…"
                    : formattedDescription;
                data["description"] = truncated;
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // Use the WU event's own time, not the default UtcNow the ctor stamps: backfilled
                // pre-agent OOBE updates must land on the timeline at when the update actually
                // installed, not at agent start. InformationalEventPost forwards this as occurredAtUtc,
                // which drives the timeline entry's timestamp (EventTimelineEmitter).
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = eventType,
                Severity = severity,
                Source = "WindowsUpdateWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = immediateUpload,
            });
        }

        private static void ClassifyEventId(int eventId, out string eventType, out EventSeverity severity, out bool immediateUpload)
        {
            switch (eventId)
            {
                case EventId_InstallFailure:
                    eventType = Constants.EventTypes.WindowsUpdateFailed;
                    severity = EventSeverity.Error;
                    immediateUpload = true; // a mid-OOBE update failure is high-signal — surface it fast
                    break;
                case EventId_InstallSuccess:
                    eventType = Constants.EventTypes.WindowsUpdateSucceeded;
                    severity = EventSeverity.Info;
                    immediateUpload = false;
                    break;
                case EventId_InstallStarted:
                case EventId_DownloadStarted:
                    eventType = Constants.EventTypes.WindowsUpdateStarted;
                    severity = EventSeverity.Debug; // timeline context only
                    immediateUpload = false;
                    break;
                default:
                    // A newly-enabled (still-being-validated) EventID: keep it visible (Info, not Debug)
                    // so it can be assessed on real traces.
                    eventType = Constants.EventTypes.WindowsUpdateStarted;
                    severity = EventSeverity.Info;
                    immediateUpload = false;
                    break;
            }
        }

        // -----------------------------------------------------------------------
        // EventData XML parsing
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

        // -----------------------------------------------------------------------
        // HRESULT decoding
        // -----------------------------------------------------------------------

        /// <summary>
        /// Normalizes a raw WU <c>errorCode</c> string (hex "0x...", signed or unsigned decimal) to a
        /// 32-bit value and its canonical <c>0x{X8}</c> form. Returns false for null/empty/unparseable.
        /// </summary>
        internal static bool TryNormalizeHResult(string errorCode, out uint value, out string hex)
        {
            value = 0;
            hex = null;
            if (string.IsNullOrWhiteSpace(errorCode)) return false;

            var s = errorCode.Trim();
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToUInt32(s.Substring(2), 16);
                }
                else if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
                {
                    value = unchecked((uint)signed);
                }
                else if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal))
                {
                    value = hexVal;
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            hex = "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Maps common Windows Update HRESULTs to their symbolic name. Not exhaustive — the full
        /// catalog lives at
        /// https://learn.microsoft.com/windows/deployment/update/windows-update-error-reference.
        /// Unknown codes return "WU_E_UNKNOWN" so the raw <c>hresult</c> hex still carries the value.
        /// </summary>
        internal static string DecodeHResult(uint hresult)
        {
            switch (hresult)
            {
                case 0x00000000: return "S_OK";
                // WU agent codes (0x8024xxxx)
                case 0x80240022: return "WU_E_ALL_UPDATES_FAILED";
                case 0x8024200B: return "WU_E_UH_INSTALLERFAILURE";
                case 0x80240020: return "WU_E_NO_INTERACTIVE_USER";
                case 0x80240016: return "WU_E_INSTALL_NOT_ALLOWED";
                case 0x8024000B: return "WU_E_CALL_CANCELLED";
                case 0x8024000C: return "WU_E_NOOP";
                case 0x80240FFF: return "WU_E_UNEXPECTED";
                case 0x80248007: return "WU_E_DS_NODATA";
                case 0x8024402C: return "WU_E_PT_WINHTTP_NAME_NOT_RESOLVED";
                // Generic Win32 / HRESULT
                case 0x80070005: return "E_ACCESSDENIED";
                case 0x80070002: return "ERROR_FILE_NOT_FOUND";
                case 0x80070003: return "ERROR_PATH_NOT_FOUND";
                case 0x800705B4: return "ERROR_TIMEOUT";
                // CBS / servicing (LCU install failures)
                case 0x800F0831: return "CBS_E_STORE_CORRUPTION";
                case 0x800F0922: return "CBS_E_INSTALLERS_FAILED";
                case 0x800F0991: return "PSFX_E_MISSING_PAYLOAD_FILE";
                default: return "WU_E_UNKNOWN";
            }
        }

        // -----------------------------------------------------------------------
        // Backfill
        // -----------------------------------------------------------------------

        private void BackfillRecentEvents()
        {
            if (_targetedEventIds.Count == 0) return;

            try
            {
                var lookbackMs = (long)_backfillLookbackMinutes * 60 * 1000;
                var idClauses = string.Join(" or ",
                    _targetedEventIds.OrderBy(id => id).Select(id => $"EventID={id}"));
                var xpath = $"*[System[({idClauses}) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

                _logger.Info($"WindowsUpdate backfill: scanning {Channel} " +
                    $"(lookback={_backfillLookbackMinutes}min, targetedIds={_targetedEventIds.Count}, restartWatermark={_restartWatermark})");

                var query = new EventLogQuery(Channel, PathType.LogName, xpath);

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
                    _logger.Debug($"WindowsUpdate backfill: no targeted events in last {_backfillLookbackMinutes} minutes");
                else
                    _logger.Info($"WindowsUpdate backfill: scanned {processed} event(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"WindowsUpdate event log not found during backfill: {Channel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"WindowsUpdate backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"WindowsUpdate backfill failed: {ex.Message}");
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
        /// see it (caller should emit), false if it was already processed — either in a PRIOR run
        /// (at/below the restart watermark) or already this run (in the seen-set). Only advances +
        /// persists the max on a genuinely-new, higher RecordId. Records without a RecordId (-1) are
        /// always emitted, never tracked or persisted.
        /// </summary>
        private bool MarkProcessed(long recordId)
        {
            if (recordId < 0) return true;

            long toPersist = -1;
            lock (_watermarkLock)
            {
                // Prior run, or already emitted this run. Note: a lower RecordId that was never
                // emitted this run (e.g. a backfill record read after a higher live record) is NOT
                // in _seenThisRun and is above the restart watermark, so it is correctly emitted.
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
                    _logger.Info($"WindowsUpdate watermark loaded: lastRecordId={state.LastRecordId} (persisted {state.PersistedUtc:O})");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load WindowsUpdate watermark: {ex.Message}");
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
                _logger.Warning($"Failed to persist WindowsUpdate watermark: {ex.Message}");
            }
        }

        internal sealed class WatermarkState
        {
            public long LastRecordId { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}
