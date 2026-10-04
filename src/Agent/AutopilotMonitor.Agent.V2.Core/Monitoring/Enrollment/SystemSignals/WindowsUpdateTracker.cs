using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Services;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches ONE channel of the <c>Microsoft-Windows-WindowsUpdateClient</c> provider for update
    /// activity during OOBE / ESP and forwards it as <c>windows_update_started</c> /
    /// <c>windows_update_succeeded</c> / <c>windows_update_failed</c> events.
    /// <para>
    /// <b>Two channels (D-310).</b> The provider manifest writes the install events — 19 success,
    /// 20 failure (HRESULT), 43 install started, 44 download started — to the <b>System</b> log and
    /// the scan/download events — 25 scan failed, 26 scan found N, 31 download failed, 41
    /// downloaded — to the Operational channel. The host runs one instance per channel; RecordIds
    /// are per channel, so each instance keeps its own watermark. The System log is shared with
    /// every other provider, hence the provider filter in its XPath.
    /// </para>
    /// <para>
    /// <b>Classification.</b> Only Windows/.NET updates (<see cref="WindowsUpdateClassifier.Os"/>)
    /// are emitted one by one and reported to the decision engine. Store, Defender and other
    /// updates arrive every few minutes on a fresh device and are only counted into the host's
    /// <see cref="UpdateActivityTally"/>. Repeated scan results (26) and scan failures (25) are
    /// deduplicated; their repeats are counted the same way.
    /// </para>
    /// <para>
    /// <b>Backfill.</b> Updates can start before the agent does; the .evtx survives the reboot, so
    /// a startup backfill with a generous lookback catches them. Backfilled events keep their own
    /// event time on the timeline.
    /// </para>
    /// </summary>
    internal sealed class WindowsUpdateTracker : IDisposable
    {
        internal const string Channel = "Microsoft-Windows-WindowsUpdateClient/Operational";
        internal const string SystemChannel = "System";
        internal const string ProviderName = "Microsoft-Windows-WindowsUpdateClient";
        internal const string OrchestratorChannel = "Microsoft-Windows-UpdateOrchestrator/Operational";

        // System channel (provider manifest).
        internal const int EventId_InstallSuccess   = 19;
        internal const int EventId_InstallFailure   = 20;
        internal const int EventId_InstallStarted   = 43;
        internal const int EventId_DownloadStarted  = 44;
        // Operational channel.
        internal const int EventId_ScanFailed       = 25;
        internal const int EventId_ScanFound        = 26;
        internal const int EventId_DownloadFailed   = 31;
        internal const int EventId_Downloaded       = 41;

        internal const string WatermarkStateFileName = "windows-update-watermark.json";
        internal const string SystemWatermarkStateFileName = "windows-update-system-watermark.json";

        // Hard bound for the unfiltered census scan — during OOBE both channels carry at most a
        // few hundred records in the lookback window; the cap only guards against a pathological
        // log. A truncated census says so in its payload (no silent caps).
        internal const int CensusRecordCap = 5000;

        // Scan results repeat every few minutes; only changes are worth a timeline row.
        internal const int MaxScanFoundEmissions = 10;
        internal const int MaxScanFailedEmissions = 5;

        private static readonly int[] DefaultSystemEventIds = { EventId_InstallSuccess, EventId_InstallFailure, EventId_InstallStarted, EventId_DownloadStarted };
        private static readonly int[] DefaultOperationalEventIds = { EventId_ScanFailed, EventId_ScanFound, EventId_DownloadFailed, EventId_Downloaded };

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly string _channel;
        private readonly bool _isSystemChannel;
        private readonly HashSet<int> _targetedEventIds;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly bool _channelCensusEnabled;
        private readonly Func<bool> _osBuildChangedProvider;
        private readonly UpdateActivityTally _tally;
        private readonly Action<string, string, DateTime> _onOsUpdateActivity;
        private readonly EventRecordWatermark _watermark;

        // Targeted events processed THIS run (any class). Zero after the backfill while the OS
        // build provably changed = the watcher is blind to the update's channel/IDs.
        private int _emittedThisRun;

        private readonly object _throttleLock = new object();
        private readonly HashSet<string> _scanFailureCodesReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _lastScanFoundCount;
        private int _scanFoundEmitted;

        private EventLogWatcher _watcher;

        /// <param name="channel">Event log channel; defaults to the Operational channel.</param>
        /// <param name="targetedEventIds">IDs to watch on <paramref name="channel"/>; defaults to the manifest set of that channel.</param>
        /// <param name="tally">Counter for activity that is counted instead of emitted; null drops the counts.</param>
        /// <param name="onOsUpdateActivity">Called for every Windows/.NET update step with (step, update title, event time).</param>
        public WindowsUpdateTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int[] targetedEventIds = null,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 60,
            string stateDirectory = null,
            bool channelCensusEnabled = true,
            Func<bool> osBuildChangedProvider = null,
            string channel = null,
            UpdateActivityTally tally = null,
            Action<string, string, DateTime> onOsUpdateActivity = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _channel = string.IsNullOrEmpty(channel) ? Channel : channel;
            _isSystemChannel = string.Equals(_channel, SystemChannel, StringComparison.OrdinalIgnoreCase);
            _targetedEventIds = targetedEventIds != null && targetedEventIds.Length > 0
                ? new HashSet<int>(targetedEventIds)
                : new HashSet<int>(_isSystemChannel ? DefaultSystemEventIds : DefaultOperationalEventIds);
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _channelCensusEnabled = channelCensusEnabled;
            _osBuildChangedProvider = osBuildChangedProvider;
            _tally = tally;
            _onOsUpdateActivity = onOsUpdateActivity;
            _watermark = new EventRecordWatermark(
                stateDirectory,
                _isSystemChannel ? SystemWatermarkStateFileName : WatermarkStateFileName,
                logger,
                _isSystemChannel ? "WindowsUpdate (System)" : "WindowsUpdate");
        }

        /// <summary>Channel this instance watches.</summary>
        internal string WatchedChannel => _channel;

        /// <summary>Targeted events processed this run, any class.</summary>
        internal int EmittedThisRun => Volatile.Read(ref _emittedThisRun);

        /// <summary>EventIDs this instance watches on <see cref="WatchedChannel"/>.</summary>
        internal IReadOnlyCollection<int> TargetedEventIds => _targetedEventIds;

        /// <summary>
        /// True when the provider manifest writes <paramref name="eventId"/> to the System log
        /// (16–24, 27, 28, 32, 33, 43, 44, 212–218); all other IDs live in the Operational channel.
        /// </summary>
        internal static bool IsSystemChannelEventId(int eventId) =>
            (eventId >= 16 && eventId <= 24)
            || eventId == 27 || eventId == 28
            || eventId == 32 || eventId == 33
            || eventId == 43 || eventId == 44
            || (eventId >= 212 && eventId <= 218);

        /// <summary>
        /// Watches the channel and backfills it. The blind-spot census is NOT run here: it needs
        /// the emissions of every channel, so the host runs it after all its trackers started.
        /// </summary>
        public void Start()
        {
            _watermark.Load();
            StartWatcher();

            if (_backfillEnabled && _backfillLookbackMinutes > 0)
            {
                BackfillRecentEvents();
            }
            else
            {
                _logger.Info($"WindowsUpdate backfill disabled by config ({_channel})");
            }
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
                _logger.Info($"WindowsUpdate watcher stopped ({_channel})");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error stopping WindowsUpdate watcher ({_channel})", ex);
            }
            finally
            {
                _watcher = null;
            }
        }

        public void Dispose() => Stop();

        internal void LoadWatermark() => _watermark.Load();

        // -----------------------------------------------------------------------
        // Watcher lifecycle
        // -----------------------------------------------------------------------

        private string ProviderFilter => _isSystemChannel ? ProviderName : null;

        private void StartWatcher()
        {
            if (_targetedEventIds.Count == 0)
            {
                _logger.Warning($"WindowsUpdate watcher not started: no targeted EventIDs configured ({_channel})");
                return;
            }

            try
            {
                var query = new EventLogQuery(_channel, PathType.LogName, BuildXPath(_targetedEventIds, ProviderFilter));
                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;
                _logger.Info($"WindowsUpdate watcher started: {_channel} (targetedIds={string.Join(",", _targetedEventIds.OrderBy(id => id))})");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"WindowsUpdate event log not found: {_channel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"WindowsUpdate watcher access denied for {_channel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start WindowsUpdate watcher for {_channel}", ex);
                // MON-D1: surface a dead watcher as one-shot telemetry, not just a local log line.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "WindowsUpdateTracker", reason: $"watcher_arm_failed:{_channel}", ex: ex);
            }
        }

        /// <summary>Targeted-EventID XPath filter without a provider clause. Exposed for tests.</summary>
        internal static string BuildXPath(HashSet<int> targetedEventIds) =>
            BuildXPath(targetedEventIds, providerName: null);

        /// <summary>
        /// Targeted-EventID XPath filter, optionally restricted to <paramref name="providerName"/>
        /// (mandatory on the shared System log) and to the last <paramref name="lookbackMs"/>.
        /// </summary>
        internal static string BuildXPath(HashSet<int> targetedEventIds, string providerName, long? lookbackMs = null)
        {
            var clauses = new List<string>(3);
            if (!string.IsNullOrEmpty(providerName))
                clauses.Add($"Provider[@Name='{providerName}']");
            clauses.Add("(" + string.Join(" or ", targetedEventIds.OrderBy(id => id).Select(id => $"EventID={id}")) + ")");
            if (lookbackMs.HasValue)
                clauses.Add($"TimeCreated[timediff(@SystemTime) <= {lookbackMs.Value}]");
            return $"*[System[{string.Join(" and ", clauses)}]]";
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
            if (_watermark.IsAlreadyProcessed(recordId))
                return;

            string xml = null;
            try { xml = record.ToXml(); }
            catch { /* fall back to positional-less parse below */ }

            var eventData = ParseEventData(xml);
            eventData.TryGetValue("updateTitle", out var updateTitle);
            eventData.TryGetValue("updateGuid", out var updateGuid);
            eventData.TryGetValue("updateRevisionNumber", out var updateRevision);
            eventData.TryGetValue("errorCode", out var errorCode);
            eventData.TryGetValue("serviceGuid", out var serviceGuid);
            eventData.TryGetValue("updateCount", out var updateCount);

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
                isBackfill: isBackfill,
                serviceGuid: serviceGuid,
                updateCount: updateCount);
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
            bool isBackfill,
            string serviceGuid = null,
            string updateCount = null)
        {
            if (!_watermark.TryClaim(recordId))
                return; // already emitted (cross-restart or duplicate delivery)

            Interlocked.Increment(ref _emittedThisRun);

            var shape = ShapeFor(eventId);
            var title = string.IsNullOrEmpty(updateTitle) ? "(unknown update)" : updateTitle;

            // Scan-level events name no update: deduplicate instead of classifying. Every other
            // event is classified; only Windows/.NET updates are listed, the rest is counted —
            // so windows_update_succeeded/failed always mean an OS update (ANALYZE-DEV-004/005).
            string updateClass = null;
            if (eventId == EventId_ScanFound)
            {
                if (!ClaimScanFoundEmission(updateCount)) { _tally?.Increment("scan_found_repeat"); return; }
            }
            else if (eventId == EventId_ScanFailed)
            {
                if (!ClaimScanFailedEmission(errorCode)) { _tally?.Increment("scan_failed_repeat"); return; }
            }
            else
            {
                updateClass = WindowsUpdateClassifier.Classify(updateTitle, serviceGuid);
                if (updateClass != WindowsUpdateClassifier.Os)
                {
                    _tally?.Increment($"{updateClass}_{eventId}");
                    return;
                }
            }

            var data = new Dictionary<string, object>
            {
                { "wuEventId", eventId },
                { "wuChannel", _isSystemChannel ? "system" : "operational" },
                { "wuPhase", shape.Phase },
                { "backfilled", isBackfill },
            };
            if (updateClass != null) data["updateClass"] = updateClass;
            if (recordId >= 0) data["recordId"] = recordId;
            if (level.HasValue) data["level"] = level.Value;
            if (!string.IsNullOrEmpty(updateTitle)) data["updateTitle"] = updateTitle;
            if (!string.IsNullOrEmpty(updateGuid)) data["updateGuid"] = updateGuid;
            if (!string.IsNullOrEmpty(updateRevisionNumber)) data["updateRevisionNumber"] = updateRevisionNumber;
            if (!string.IsNullOrEmpty(updateCount)) data["updateCount"] = updateCount;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");

            // Decode the HRESULT for failure events (and any event that carries a non-empty errorCode).
            if (TryNormalizeHResult(errorCode, out var hresultValue, out var hresultHex))
            {
                data["hresult"] = hresultHex;
                data["hresultSymbol"] = ResolveHResultSymbol(hresultValue);
            }

            if (!string.IsNullOrEmpty(formattedDescription))
            {
                data["description"] = formattedDescription.Length > 1000
                    ? formattedDescription.Substring(0, 1000) + "…"
                    : formattedDescription;
            }

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // Use the WU event's own time, not the default UtcNow the ctor stamps: backfilled
                // pre-agent OOBE updates must land on the timeline at when the update actually
                // happened, not at agent start. InformationalEventPost forwards this as occurredAtUtc,
                // which drives the timeline entry's timestamp (EventTimelineEmitter).
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = shape.EventType,
                Severity = shape.Severity,
                Source = Constants.EventSources.WindowsUpdateWatcher,
                Phase = EnrollmentPhase.Unknown,
                Message = BuildMessage(eventId, title, updateCount, data),
                Data = data,
                ImmediateUpload = shape.Immediate,
            });

            if (updateClass == WindowsUpdateClassifier.Os && shape.OsStep != null && _onOsUpdateActivity != null)
            {
                try { _onOsUpdateActivity(shape.OsStep, updateTitle, timeCreatedUtc ?? DateTime.UtcNow); }
                catch (Exception ex) { _logger.Warning($"WindowsUpdate: OS update activity callback failed: {ex.Message}"); }
            }

            // Last: everything this record produced has been handed to the ingress first.
            _watermark.MarkEmitted(recordId);
        }

        private struct EventShape
        {
            public string EventType;
            public EventSeverity Severity;
            public bool Immediate;
            public string Phase;
            /// <summary>Step reported to the decision engine for OS updates; null = not a step.</summary>
            public string OsStep;
        }

        private static EventShape ShapeFor(int eventId)
        {
            switch (eventId)
            {
                case EventId_InstallSuccess:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateSucceeded, Severity = EventSeverity.Info, Phase = "installed", OsStep = OsUpdateSteps.Installed };
                case EventId_InstallFailure:
                    // A mid-OOBE install failure is high-signal — surface it fast.
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateFailed, Severity = EventSeverity.Error, Immediate = true, Phase = "install", OsStep = OsUpdateSteps.Failed };
                case EventId_InstallStarted:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateStarted, Severity = EventSeverity.Info, Phase = "install", OsStep = OsUpdateSteps.InstallStarted };
                case EventId_DownloadStarted:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateStarted, Severity = EventSeverity.Info, Phase = "download", OsStep = OsUpdateSteps.DownloadStarted };
                case EventId_Downloaded:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateStarted, Severity = EventSeverity.Info, Phase = "downloaded", OsStep = OsUpdateSteps.Downloaded };
                case EventId_DownloadFailed:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateFailed, Severity = EventSeverity.Warning, Phase = "download", OsStep = OsUpdateSteps.Failed };
                case EventId_ScanFailed:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateFailed, Severity = EventSeverity.Warning, Phase = "scan" };
                case EventId_ScanFound:
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateStarted, Severity = EventSeverity.Debug, Phase = "scan" };
                default:
                    // An EventID enabled via config whose meaning has not been mapped: keep it
                    // visible (Info, not Debug) so it can be assessed on real traces.
                    return new EventShape { EventType = Constants.EventTypes.WindowsUpdateStarted, Severity = EventSeverity.Info, Phase = "activity" };
            }
        }

        private static string BuildMessage(int eventId, string title, string updateCount, Dictionary<string, object> data)
        {
            var hresultSuffix = data.TryGetValue("hresultSymbol", out var sym)
                ? $" ({data["hresult"]} {sym})"
                : string.Empty;

            switch (eventId)
            {
                case EventId_InstallSuccess:  return $"Windows Update installed during enrollment: {title}";
                case EventId_InstallFailure:  return $"Windows Update FAILED during enrollment: {title}{hresultSuffix}";
                case EventId_InstallStarted:  return $"Windows Update install started during enrollment: {title}";
                case EventId_DownloadStarted: return $"Windows Update download started during enrollment: {title}";
                case EventId_Downloaded:      return $"Windows Update downloaded during enrollment: {title}";
                case EventId_DownloadFailed:  return $"Windows Update download FAILED during enrollment: {title}{hresultSuffix}";
                case EventId_ScanFailed:      return $"Windows Update scan failed during enrollment{hresultSuffix}";
                case EventId_ScanFound:
                    return string.IsNullOrEmpty(updateCount)
                        ? "Windows Update scan completed during enrollment"
                        : $"Windows Update scan found {updateCount} update(s) during enrollment";
                default:                      return $"Windows Update activity (EventID {eventId}) during enrollment: {title}";
            }
        }

        private bool ClaimScanFoundEmission(string updateCount)
        {
            lock (_throttleLock)
            {
                if (_scanFoundEmitted >= MaxScanFoundEmissions) return false;
                if (string.Equals(_lastScanFoundCount, updateCount ?? string.Empty, StringComparison.Ordinal)) return false;
                _lastScanFoundCount = updateCount ?? string.Empty;
                _scanFoundEmitted++;
                return true;
            }
        }

        private bool ClaimScanFailedEmission(string errorCode)
        {
            lock (_throttleLock)
            {
                if (_scanFailureCodesReported.Count >= MaxScanFailedEmissions) return false;
                return _scanFailureCodesReported.Add(errorCode ?? string.Empty);
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
        /// Symbolic name of a WU HRESULT from the shared error-code catalog (WU, CBS, Win32 and
        /// COM families). <c>S_OK</c> for zero, <c>WU_E_UNKNOWN</c> when the catalog has no symbol,
        /// so the raw <c>hresult</c> hex still carries the value. A catalog load failure is
        /// swallowed the same way: decoding is decoration and must never cost the event.
        /// </summary>
        internal static string ResolveHResultSymbol(uint hresult)
            => ResolveHResultSymbol(hresult, code => ErrorCodeCatalog.TryLookup(code));

        internal static string ResolveHResultSymbol(uint hresult, Func<string, ErrorCodeEntry> lookup)
        {
            if (hresult == 0) return "S_OK";
            try
            {
                var symbol = lookup("0x" + hresult.ToString("x8", CultureInfo.InvariantCulture))?.Symbol;
                return string.IsNullOrEmpty(symbol) ? "WU_E_UNKNOWN" : symbol;
            }
            catch
            {
                return "WU_E_UNKNOWN";
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
                var xpath = BuildXPath(_targetedEventIds, ProviderFilter, lookbackMs);

                _logger.Info($"WindowsUpdate backfill: scanning {_channel} " +
                    $"(lookback={_backfillLookbackMinutes}min, targetedIds={_targetedEventIds.Count}, restartWatermark={_watermark.RestartWatermark})");

                var query = new EventLogQuery(_channel, PathType.LogName, xpath);

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
                    _logger.Debug($"WindowsUpdate backfill: no targeted events in last {_backfillLookbackMinutes} minutes ({_channel})");
                else
                    _logger.Info($"WindowsUpdate backfill: scanned {processed} event(s) ({_channel})");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"WindowsUpdate event log not found during backfill: {_channel} (normal on non-Windows 10/11 test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"WindowsUpdate backfill access denied ({_channel}): {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"WindowsUpdate backfill failed ({_channel}): {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Blind-spot channel census (session 7443317c)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Self-evidencing blind spot diagnosis. Session 7443317c: an OOBE quality update
        /// provably installed (OS build changed across the mid-enrollment reboot) yet the watcher
        /// captured nothing — it read the install IDs from the Operational channel, while the
        /// manifest writes them to the System log (D-310). When the blind spot occurs again
        /// (build changed AND zero targeted events on every channel this run), emit a one-shot
        /// unfiltered EventID histogram of the Operational and UpdateOrchestrator channels.
        /// </summary>
        /// <param name="otherChannelsEmitted">Targeted events the host's other update watchers processed this run.</param>
        /// <param name="otherTargetedEventIds">EventIDs the host's other update watchers target, listed with this instance's own.</param>
        internal void EmitChannelCensusIfBlind(int otherChannelsEmitted = 0, IEnumerable<int> otherTargetedEventIds = null)
        {
            if (!_channelCensusEnabled) return;

            try
            {
                if (_osBuildChangedProvider == null || !_osBuildChangedProvider())
                    return;
                if (EmittedThisRun + otherChannelsEmitted > 0)
                    return;

                var lookback = _backfillLookbackMinutes > 0 ? _backfillLookbackMinutes : 60;
                var scanner = CensusScannerOverride ?? ScanChannel;
                var wuClient = scanner(Channel, lookback);
                var orchestrator = scanner(OrchestratorChannel, lookback);

                var wuClientHistogram = FormatHistogram(wuClient.Histogram);
                var orchestratorHistogram = FormatHistogram(orchestrator.Histogram);
                _logger.Info(
                    $"WindowsUpdate channel census (blind spot): wuClient=[{wuClientHistogram}] " +
                    $"updateOrchestrator=[{orchestratorHistogram}] lookback={lookback}min");

                var data = new Dictionary<string, object>
                {
                    { "wuClientCensus", wuClientHistogram },
                    { "updateOrchestratorCensus", orchestratorHistogram },
                    { "lookbackMinutes", lookback },
                    { "restartWatermark", _watermark.RestartWatermark },
                    { "targetedEventIds", string.Join(",", _targetedEventIds.Concat(otherTargetedEventIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(id => id)) },
                };
                if (wuClient.Truncated || orchestrator.Truncated)
                    data["censusTruncated"] = true;

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.WindowsUpdateChannelCensus,
                    Severity = EventSeverity.Debug,
                    Source = Constants.EventSources.WindowsUpdateWatcher,
                    Phase = EnrollmentPhase.Unknown,
                    Message = "Windows Update channel census: OS build changed across restart but no targeted WU events were captured — EventID histogram attached",
                    Data = data,
                    ImmediateUpload = false,
                });
            }
            catch (Exception ex)
            {
                // Diagnosis must never break the watcher.
                _logger.Warning($"WindowsUpdate channel census failed: {ex.Message}");
            }
        }

        /// <summary>Test seam — replaces the real per-channel scan (Windows event log access).</summary>
        internal Func<string, int, ChannelCensusScan> CensusScannerOverride { get; set; }

        internal sealed class ChannelCensusScan
        {
            public Dictionary<int, int> Histogram { get; }
            public bool Truncated { get; }

            public ChannelCensusScan(Dictionary<int, int> histogram, bool truncated)
            {
                Histogram = histogram ?? new Dictionary<int, int>();
                Truncated = truncated;
            }
        }

        /// <summary>
        /// Unfiltered EventID→count histogram of <paramref name="channel"/> over the lookback
        /// window. Fail-soft per channel (a missing UpdateOrchestrator channel returns an empty
        /// histogram). Bounded by <see cref="CensusRecordCap"/>.
        /// </summary>
        internal static ChannelCensusScan ScanChannel(string channel, int lookbackMinutes)
        {
            var truncated = false;
            var histogram = new Dictionary<int, int>();
            try
            {
                var lookbackMs = (long)lookbackMinutes * 60 * 1000;
                var xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";
                var query = new EventLogQuery(channel, PathType.LogName, xpath);

                var scanned = 0;
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            histogram.TryGetValue(record.Id, out var count);
                            histogram[record.Id] = count + 1;
                            if (++scanned >= CensusRecordCap)
                            {
                                truncated = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                // Channel absent (older builds / test environments) — empty histogram says so.
            }
            return new ChannelCensusScan(histogram, truncated);
        }

        /// <summary>Compact "id=count" form ordered by EventID, e.g. "19=2,25=1,43=2".</summary>
        internal static string FormatHistogram(Dictionary<int, int> histogram) =>
            string.Join(",", histogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
