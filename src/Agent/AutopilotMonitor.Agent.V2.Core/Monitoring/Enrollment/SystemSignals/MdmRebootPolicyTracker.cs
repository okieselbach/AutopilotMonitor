using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Watches <c>Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin</c> for
    /// EventID 2800 — logged once per device-assigned MDM policy whose CSP URI matches the OS
    /// reboot-required catalog — and forwards each as a <c>mdm_policy_reboot_required</c> event
    /// carrying the reboot-forcing URI.
    /// <para>
    /// This attributes the "unexpected reboot + second sign-in screen" pattern (PMPC research):
    /// device-assigned policies applied during ESP DeviceSetup can demand a reboot; Windows
    /// coalesces them into one restart at the end of DeviceSetup, forcing the user through a second
    /// sign-in before AccountSetup. Admins are blind to this in the Intune console — the attributed
    /// URIs make it actionable (reassign the profiles to user groups and the reboot disappears).
    /// </para>
    /// <para>
    /// <b>Backfill.</b> Device policies can apply before the agent starts, so a startup backfill
    /// scans the lookback window. The .evtx persists across the coalesced reboot, so the
    /// post-reboot agent restart re-reads the same records — which is why the
    /// <b>cross-restart RecordId watermark</b> (WindowsUpdateTracker pattern) is mandatory here:
    /// without it every reboot-forcing URI would be re-emitted after the very reboot it caused.
    /// </para>
    /// <para>
    /// <b>Tolerant matching.</b> The exact 2800 message text is unverified (validated post-deploy on
    /// the E2E VM): emission is gated on the EventID only, never on message wording. URI extraction
    /// prefers structured EventData values that look like an OMA-DM URI (<c>./…</c>) and falls back
    /// to a permissive regex over the formatted description. Events with an unparseable URI are
    /// still emitted (with the captured description) so the timeline shows them and the captured
    /// text tells us how to fix the extraction — the analyze rule keys on <c>rebootUri exists</c>
    /// and simply stays silent for those.
    /// </para>
    /// </summary>
    internal sealed class MdmRebootPolicyTracker : IDisposable
    {
        internal const string Channel = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
        internal const int EventId_PolicyRebootRequired = 2800;

        internal const string WatermarkStateFileName = "mdm-reboot-watermark.json";

        /// <summary>Registry key whose <c>RebootRequired</c> value/subkey flags a pending OMA-DM coalesced reboot.</summary>
        internal const string OmadmSyncMlKeyPath = @"SOFTWARE\Microsoft\Provisioning\OMADM\SyncML";

        // Permissive OMA-DM URI shape: "./Device/Vendor/MSFT/...", "./User/...", "./Vendor/...".
        // Kept deliberately broad — the exact 2800 text is unverified; post-deploy E2E validates
        // and tightens this against real records if needed.
        private static readonly Regex UriPattern = new Regex(
            @"\./[A-Za-z0-9_\-./\[\]{}%~+]+", RegexOptions.Compiled);

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly string _stateDirectory;

        private EventLogWatcher _watcher;

        // Dedup — same contract as WindowsUpdateTracker (see its field docs): immutable
        // cross-restart boundary from disk + intra-run HashSet (NOT a high-water mark, because the
        // live watcher is armed before the backfill scan and can deliver higher RecordIds first).
        private long _restartWatermark = -1;
        private readonly HashSet<long> _seenThisRun = new HashSet<long>();
        private long _maxEmittedRecordId = -1;
        private readonly object _watermarkLock = new object();

        /// <summary>
        /// Fail-soft corroboration probe for the OMA-DM RebootRequired flag — null = unknown
        /// (key absent or unreadable). Settable test seam; the default reads the live registry.
        /// </summary>
        internal Func<bool?> RebootRequiredFlagProbe { get; set; } = ReadOmadmRebootRequiredFlag;

        public MdmRebootPolicyTracker(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            bool backfillEnabled = true,
            int backfillLookbackMinutes = 60,
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
                _logger.Info("MdmRebootPolicy backfill disabled by config");
        }

        public void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.Enabled = false;
                _watcher.Dispose();
                _logger.Info("MdmRebootPolicy watcher stopped");
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping MdmRebootPolicy watcher", ex);
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
                _logger.Info($"MdmRebootPolicy watcher started: {Channel} (EventID {EventId_PolicyRebootRequired})");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"MdmRebootPolicy event log not found: {Channel} (normal on non-MDM-enrolled test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"MdmRebootPolicy watcher access denied for {Channel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start MdmRebootPolicy watcher for {Channel}", ex);
                // MON-D1: surface a dead watcher as one-shot telemetry, not just a local log line.
                CollectorDegradationReporter.Report(_post, _sessionId, _tenantId,
                    collectorName: "MdmRebootPolicyTracker", reason: $"watcher_arm_failed:{Channel}", ex: ex);
            }
        }

        /// <summary>Targeted-EventID XPath filter. Exposed as internal for tests.</summary>
        internal static string BuildXPath() =>
            $"*[System[(EventID={EventId_PolicyRebootRequired})]]";

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
                _logger.Error("Error processing MdmRebootPolicy event", ex);
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
            catch { /* fall back to description-only parse below */ }

            string description = null;
            try { description = record.FormatDescription(); }
            catch { /* some events lack formatting resources */ }

            var rebootUri = ExtractRebootUri(ParseEventData(xml), description);

            ProcessEvent(
                eventId: record.Id,
                recordId: recordId,
                timeCreatedUtc: record.TimeCreated?.ToUniversalTime(),
                rebootUri: rebootUri,
                formattedDescription: description,
                isBackfill: isBackfill);
        }

        /// <summary>
        /// Core processing extracted to primitive inputs so tests can drive it without synthesizing
        /// an abstract, Windows-only <see cref="EventRecord"/>. Mirrors the WindowsUpdateTracker
        /// test-seam pattern.
        /// </summary>
        internal void ProcessEvent(
            int eventId,
            long recordId,
            DateTime? timeCreatedUtc,
            string rebootUri,
            string formattedDescription,
            bool isBackfill)
        {
            if (!MarkProcessed(recordId))
                return; // already emitted (cross-restart or duplicate delivery)

            var data = new Dictionary<string, object>
            {
                { "windowsEventId", eventId },
                { "backfilled", isBackfill },
                { "eventLogChannel", Channel },
            };
            if (recordId >= 0) data["recordId"] = recordId;
            if (timeCreatedUtc.HasValue) data["eventTime"] = timeCreatedUtc.Value.ToString("o");
            if (!string.IsNullOrEmpty(rebootUri)) data["rebootUri"] = rebootUri;
            if (!string.IsNullOrEmpty(formattedDescription))
            {
                data["description"] = formattedDescription.Length > 1000
                    ? formattedDescription.Substring(0, 1000) + "…"
                    : formattedDescription;
            }

            var flag = SafeProbeRebootRequiredFlag();
            if (flag.HasValue) data["omadmRebootRequiredFlag"] = flag.Value;

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // The record's own time, not UtcNow: backfilled pre-agent (and post-reboot re-read)
                // records must land on the timeline at when the policy actually demanded the reboot.
                Timestamp = timeCreatedUtc ?? DateTime.UtcNow,
                EventType = Constants.EventTypes.MdmPolicyRebootRequired,
                Severity = EventSeverity.Warning,
                Source = "MdmRebootPolicyWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = !string.IsNullOrEmpty(rebootUri)
                    ? $"MDM policy requires a reboot during ESP: {rebootUri}"
                    : "MDM policy requires a reboot during ESP (URI not parsed from event — see description)",
                Data = data,
                // The reboot this event announces will kill the agent — flush now.
                ImmediateUpload = true,
            });
        }

        /// <summary>
        /// Extracts the reboot-forcing OMA-DM URI. Preference order: (1) a structured EventData
        /// value that starts with <c>./</c> (the CSP-URI shape), (2) a permissive regex over the
        /// formatted description. Returns null when nothing URI-like is found — the caller still
        /// emits, just without the <c>rebootUri</c> field.
        /// </summary>
        internal static string ExtractRebootUri(Dictionary<string, string> eventData, string description)
        {
            if (eventData != null)
            {
                foreach (var value in eventData.Values)
                {
                    var trimmed = value?.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && trimmed.StartsWith("./", StringComparison.Ordinal))
                        return trimmed;
                }
            }

            if (!string.IsNullOrEmpty(description))
            {
                var match = UriPattern.Match(description);
                if (match.Success)
                {
                    // The permissive pattern can swallow sentence punctuation trailing the URI
                    // ("...Security."). OMA-DM URI segments never end in punctuation — trim it.
                    var candidate = match.Value.TrimEnd('.', ',', ';', ':', '\'', '"', ')');
                    if (candidate.Length > 2) return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts <c>&lt;Data Name="..."&gt;value&lt;/Data&gt;</c> pairs from an event's rendered
        /// XML, keyed case-insensitively. Also collects UNNAMED Data elements under positional keys
        /// (<c>Data0</c>, <c>Data1</c>, …) — DM-Enterprise events frequently carry positional-only
        /// payloads. Namespace-agnostic; returns an empty map on null/malformed XML — never throws.
        /// </summary>
        internal static Dictionary<string, string> ParseEventData(string xml)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(xml)) return result;

            try
            {
                var doc = XDocument.Parse(xml);
                var positionalIndex = 0;
                foreach (var el in doc.Descendants().Where(e => e.Name.LocalName == "Data"))
                {
                    var nameAttr = el.Attribute("Name");
                    var key = nameAttr != null && !string.IsNullOrEmpty(nameAttr.Value)
                        ? nameAttr.Value
                        : "Data" + positionalIndex++;
                    if (!result.ContainsKey(key))
                        result[key] = el.Value;
                }
            }
            catch
            {
                // Malformed / unexpected XML — best effort, return what we have.
            }

            return result;
        }

        // -----------------------------------------------------------------------
        // OMA-DM RebootRequired flag probe (fail-soft corroboration)
        // -----------------------------------------------------------------------

        private bool? SafeProbeRebootRequiredFlag()
        {
            try
            {
                return RebootRequiredFlagProbe?.Invoke();
            }
            catch
            {
                return null; // corroboration only — never let the probe break the emit
            }
        }

        /// <summary>
        /// Reads the pending-reboot flag under <see cref="OmadmSyncMlKeyPath"/>. The exact shape is
        /// unverified (PMPC research names the key, not the value type), so both a value and a
        /// subkey named <c>RebootRequired</c> count as "pending". Fail-soft: null = unknown
        /// (key absent / unreadable), matching the EspSkipConfigurationProbe convention.
        /// </summary>
        internal static bool? ReadOmadmRebootRequiredFlag()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(OmadmSyncMlKeyPath))
                {
                    if (key == null) return null;
                    if (key.GetValue("RebootRequired") != null) return true;
                    using (var sub = key.OpenSubKey("RebootRequired"))
                    {
                        return sub != null ? (bool?)true : false;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // Backfill
        // -----------------------------------------------------------------------

        private void BackfillRecentEvents()
        {
            try
            {
                var lookbackMs = (long)_backfillLookbackMinutes * 60 * 1000;
                var xpath = $"*[System[(EventID={EventId_PolicyRebootRequired}) and TimeCreated[timediff(@SystemTime) <= {lookbackMs}]]]";

                _logger.Info($"MdmRebootPolicy backfill: scanning {Channel} " +
                    $"(lookback={_backfillLookbackMinutes}min, restartWatermark={_restartWatermark})");

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
                    _logger.Debug($"MdmRebootPolicy backfill: no EventID {EventId_PolicyRebootRequired} records in last {_backfillLookbackMinutes} minutes");
                else
                    _logger.Info($"MdmRebootPolicy backfill: scanned {processed} record(s)");
            }
            catch (EventLogNotFoundException)
            {
                _logger.Warning($"MdmRebootPolicy event log not found during backfill: {Channel} (normal on non-MDM-enrolled test environments)");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning($"MdmRebootPolicy backfill access denied: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"MdmRebootPolicy backfill failed: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Watermark dedup (cross-restart) — WindowsUpdateTracker pattern verbatim
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
        /// (at/below the restart watermark) or already this run (in the seen-set). Records without
        /// a RecordId (-1) are always emitted, never tracked or persisted.
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
                    _logger.Info($"MdmRebootPolicy watermark loaded: lastRecordId={state.LastRecordId} (persisted {state.PersistedUtc:O})");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load MdmRebootPolicy watermark: {ex.Message}");
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
                _logger.Warning($"Failed to persist MdmRebootPolicy watermark: {ex.Message}");
            }
        }

        internal sealed class WatermarkState
        {
            public long LastRecordId { get; set; }
            public DateTime PersistedUtc { get; set; }
        }
    }
}
