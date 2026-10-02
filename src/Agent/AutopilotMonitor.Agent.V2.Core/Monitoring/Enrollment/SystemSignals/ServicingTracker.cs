using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Xml.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches the CBS servicing steps of OS updates in the <c>Setup</c> log
    /// (<c>Microsoft-Windows-Servicing</c>, D-310): 1 initiating changes (current → target state),
    /// 2 target state reached, 3 failed, 4 a restart is needed to finish, 6 failed and partially
    /// installed. The Windows Update client logs only the start of an installation; these steps
    /// show the long part of it — staging, installing, "reboot required" right before the restart.
    /// <para>
    /// Only packages of the OS update clients (<c>UpdateAgentLCU</c> for the cumulative update,
    /// <c>WindowsUpdateAgent</c>) are emitted as <c>windows_update_servicing</c> and reported to
    /// the decision engine; language packs, features on demand and DISM operations started by
    /// apps are counted into the host's <see cref="UpdateActivityTally"/>.
    /// </para>
    /// </summary>
    internal sealed class ServicingTracker : IDisposable
    {
        internal const string Channel = "Setup";
        internal const string ProviderName = "Microsoft-Windows-Servicing";

        internal const int EventId_InitiatingChanges = 1;
        internal const int EventId_StateReached = 2;
        internal const int EventId_ChangeFailed = 3;
        internal const int EventId_RebootRequired = 4;
        internal const int EventId_PartiallyInstalled = 6;

        internal static readonly int[] TargetedEventIds =
        {
            EventId_InitiatingChanges, EventId_StateReached, EventId_ChangeFailed, EventId_RebootRequired, EventId_PartiallyInstalled,
        };

        internal const string WatermarkStateFileName = "servicing-watermark.json";

        // CBS client ids of the Windows Update installers (seen on 24H2/25H2: UpdateAgentLCU).
        private static readonly HashSet<string> OsUpdateClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UpdateAgentLCU",
            "WindowsUpdateAgent",
        };

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly UpdateActivityTally _tally;
        private readonly Action<string, string, DateTime> _onOsUpdateActivity;
        private readonly EventRecordWatermark _watermark;
        private EventLogWatcher _watcher;

        public ServicingTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 60,
            string stateDirectory = null,
            UpdateActivityTally tally = null,
            Action<string, string, DateTime> onOsUpdateActivity = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _tally = tally;
            _onOsUpdateActivity = onOsUpdateActivity;
            _watermark = new EventRecordWatermark(stateDirectory, WatermarkStateFileName, logger, "Servicing");
        }

        public void Start()
        {
            _watermark.Load();
            StartWatcher();

            if (_backfillEnabled && _backfillLookbackMinutes > 0)
                BackfillRecentEvents();
            else
                _logger.Info("Servicing backfill disabled by config");
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
                _logger.Info("Servicing watcher stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping Servicing watcher", ex);
            }
            finally
            {
                _watcher = null;
            }
        }

        public void Dispose() => Stop();

        internal void LoadWatermark() => _watermark.Load();

        private static HashSet<int> TargetedSet => new HashSet<int>(TargetedEventIds);

        private void StartWatcher()
        {
            try
            {
                var query = new EventLogQuery(Channel, PathType.LogName,
                    WindowsUpdateTracker.BuildXPath(TargetedSet, ProviderName));
                _watcher = new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEventRecordWritten;
                _watcher.Enabled = true;
                _logger.Info($"Servicing watcher started: {Channel} ({ProviderName}, targetedIds={string.Join(",", TargetedEventIds)})");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Servicing event log not found: {Channel}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"Servicing watcher access denied for {Channel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start Servicing watcher for {Channel}", ex);
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "ServicingTracker", reason: $"watcher_arm_failed:{Channel}", ex: ex);
            }
        }

        private void OnEventRecordWritten(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;
            try
            {
                ProcessRecord(e.EventRecord, isBackfill: false);
            }
            catch (Exception ex)
            {
                _logger.Error("Error processing Servicing event", ex);
            }
        }

        private void ProcessRecord(EventRecord record, bool isBackfill)
        {
            var recordId = record.RecordId ?? -1;
            if (_watermark.IsAlreadyProcessed(recordId)) return;

            string xml = null;
            try { xml = record.ToXml(); }
            catch { }

            var fields = ParseUserData(xml);
            fields.TryGetValue("PackageIdentifier", out var package);
            fields.TryGetValue("InitialPackageStateTextized", out var initialState);
            fields.TryGetValue("IntendedPackageStateTextized", out var targetState);
            fields.TryGetValue("ErrorCode", out var errorCode);
            fields.TryGetValue("Client", out var client);

            ProcessEvent(
                eventId: record.Id,
                recordId: recordId,
                timeCreatedUtc: record.TimeCreated?.ToUniversalTime(),
                package: package,
                initialState: initialState,
                targetState: targetState,
                errorCode: errorCode,
                client: client,
                isBackfill: isBackfill);
        }

        /// <summary>Test seam — drives the processing without a Windows-only <see cref="EventRecord"/>.</summary>
        internal void ProcessEvent(
            int eventId,
            long recordId,
            DateTime? timeCreatedUtc,
            string package,
            string initialState,
            string targetState,
            string errorCode,
            string client,
            bool isBackfill)
        {
            if (!_watermark.TryClaim(recordId)) return;

            if (string.IsNullOrEmpty(client) || !OsUpdateClients.Contains(client))
            {
                _tally?.Increment($"servicing_other_{eventId}");
                return;
            }

            var pkg = string.IsNullOrEmpty(package) ? "(unknown package)" : package;
            var target = string.IsNullOrEmpty(targetState) ? "?" : targetState;

            string step;
            string osStep;
            string message;
            var severity = EventSeverity.Info;
            string hresultHex = null;
            string hresultSymbol = null;
            if (WindowsUpdateTracker.TryNormalizeHResult(errorCode, out var hresultValue, out var hex) && hresultValue != 0)
            {
                hresultHex = hex;
                hresultSymbol = WindowsUpdateTracker.ResolveHResultSymbol(hresultValue);
            }

            switch (eventId)
            {
                case EventId_InitiatingChanges:
                    step = "initiating";
                    osStep = IsState(targetState, "Installed") ? OsUpdateSteps.InstallStarted
                        : IsState(targetState, "Staged") ? OsUpdateSteps.StagingStarted
                        : null;
                    message = $"Servicing started for {pkg}: {(string.IsNullOrEmpty(initialState) ? "?" : initialState)} → {target}";
                    break;
                case EventId_StateReached:
                    step = "state_reached";
                    osStep = IsState(targetState, "Installed") ? OsUpdateSteps.Installed
                        : IsState(targetState, "Staged") ? OsUpdateSteps.Staged
                        : null;
                    message = $"Servicing finished for {pkg}: now {target}";
                    break;
                case EventId_RebootRequired:
                    step = "reboot_required";
                    osStep = OsUpdateSteps.RebootRequired;
                    message = $"Servicing needs a restart to finish {pkg} → {target}";
                    break;
                case EventId_ChangeFailed:
                case EventId_PartiallyInstalled:
                    step = "failed";
                    osStep = OsUpdateSteps.Failed;
                    severity = EventSeverity.Warning;
                    message = $"Servicing FAILED for {pkg} → {target}" +
                        (hresultHex != null ? $" ({hresultHex} {hresultSymbol})" : string.Empty) +
                        (eventId == EventId_PartiallyInstalled ? ", package is partially installed" : string.Empty);
                    break;
                default:
                    return;
            }

            var data = new Dictionary<string, object>
            {
                { "servicingEventId", eventId },
                { "step", step },
                { "package", pkg },
                { "client", client },
                { "backfilled", isBackfill },
            };
            if (!string.IsNullOrEmpty(initialState)) data["initialState"] = initialState;
            if (!string.IsNullOrEmpty(targetState)) data["targetState"] = targetState;
            if (hresultHex != null)
            {
                data["hresult"] = hresultHex;
                data["hresultSymbol"] = hresultSymbol;
            }
            if (recordId >= 0) data["recordId"] = recordId;
            if (timeCreatedUtc.HasValue) data["timeCreated"] = timeCreatedUtc.Value.ToString("o");

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = Constants.EventTypes.WindowsUpdateServicing,
                Severity = severity,
                Source = "ServicingWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = false,
            });

            if (osStep != null && _onOsUpdateActivity != null)
            {
                try { _onOsUpdateActivity(osStep, pkg, timeCreatedUtc ?? DateTime.UtcNow); }
                catch (Exception ex) { _logger.Warning($"Servicing: OS update activity callback failed: {ex.Message}"); }
            }

            // Last: everything this record produced has been handed to the ingress first.
            _watermark.MarkEmitted(recordId);
        }

        private static bool IsState(string state, string expected) =>
            string.Equals(state, expected, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Child elements of the first element under <c>UserData</c>
        /// (<c>CbsPackageInitiateChanges</c> / <c>CbsPackageChangeState</c>), keyed by local name,
        /// case-insensitively. Empty on null/malformed XML — never throws.
        /// </summary>
        internal static Dictionary<string, string> ParseUserData(string xml)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(xml)) return result;

            try
            {
                var doc = XDocument.Parse(xml);
                var userData = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "UserData");
                var payload = userData?.Elements().FirstOrDefault();
                if (payload == null) return result;

                foreach (var el in payload.Elements())
                {
                    if (!result.ContainsKey(el.Name.LocalName))
                        result[el.Name.LocalName] = el.Value;
                }
            }
            catch
            {
                // Malformed / unexpected XML — best effort.
            }

            return result;
        }

        private void BackfillRecentEvents()
        {
            try
            {
                var lookbackMs = (long)_backfillLookbackMinutes * 60 * 1000;
                var xpath = WindowsUpdateTracker.BuildXPath(TargetedSet, ProviderName, lookbackMs);
                _logger.Info($"Servicing backfill: scanning {Channel} (lookback={_backfillLookbackMinutes}min, restartWatermark={_watermark.RestartWatermark})");

                int processed = 0;
                using (var reader = new EventLogReader(new EventLogQuery(Channel, PathType.LogName, xpath)))
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

                if (processed > 0)
                    _logger.Info($"Servicing backfill: scanned {processed} event(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"Servicing event log not found during backfill: {Channel}");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"Servicing backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Servicing backfill failed: {ex.Message}");
            }
        }
    }
}
