using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
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
    /// EventID 2800 — logged once per MDM policy URI that matches the OS reboot-required catalog —
    /// and emits ONE aggregated <c>mdm_policy_reboot_required</c> event per burst carrying the URI
    /// list (<c>rebootUris</c>, <c>uriCount</c>, <c>firstRebootUri</c>).
    /// <para>
    /// Verified against session b2e890c1 (2026-07-20): the 2800 description reads
    /// <c>The following URI has triggered a reboot: (./Device/...).</c> and records arrive in
    /// sub-second bursts during policy sync — hence the debounce aggregation instead of per-record
    /// events. CRITICAL semantics: only URIs applied DURING ESP DeviceSetup cause the coalesced
    /// mid-ESP reboot ("second sign-in", PMPC research); later syncs (AccountSetup and beyond)
    /// merely flag the requirement with no forced reboot. The event is therefore a NEUTRAL
    /// observation (Info) — the reboot claim lives in ANALYZE-ESP-005, which requires an
    /// actually-observed <c>system_reboot_detected</c> before attributing anything.
    /// </para>
    /// <para>
    /// <b>Backfill + watermark.</b> Device policies can apply before the agent starts, and a
    /// genuine coalesced reboot kills the agent BEFORE the debounced emit — in both cases the
    /// post-(re)start backfill re-reads the .evtx and emits the aggregate then (historical
    /// timestamps). The cross-restart RecordId watermark is persisted only at successful flush, so
    /// a pre-emit death loses nothing and a completed emit is never repeated.
    /// </para>
    /// </summary>
    internal sealed class MdmRebootPolicyTracker : IDisposable
    {
        internal const string Channel = "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin";
        internal const int EventId_PolicyRebootRequired = 2800;

        internal const string WatermarkStateFileName = "mdm-reboot-watermark.json";

        /// <summary>Cap for the emitted <c>rebootUris</c> list — <c>uriCount</c> always carries the uncapped distinct total.</summary>
        internal const int MaxUrisPerEvent = 20;

        /// <summary>Default quiet-period before a burst of 2800 records is flushed as one event.</summary>
        internal const int DefaultDebounceMilliseconds = 10000;

        /// <summary>Registry key whose <c>RebootRequired</c> value/subkey flags a pending OMA-DM coalesced reboot.</summary>
        internal const string OmadmSyncMlKeyPath = @"SOFTWARE\Microsoft\Provisioning\OMADM\SyncML";

        // Permissive OMA-DM URI shape ("./Device/Vendor/MSFT/...", "./User/..."). The verified 2800
        // text wraps the URI in parentheses — ')' is deliberately outside the class so the match
        // stops cleanly; trailing sentence punctuation is trimmed after the match.
        private static readonly Regex UriPattern = new Regex(
            @"\./[A-Za-z0-9_\-./\[\]{}%~+]+", RegexOptions.Compiled);

        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly bool _backfillEnabled;
        private readonly int _backfillLookbackMinutes;
        private readonly string _stateDirectory;
        private readonly int _debounceMilliseconds;

        private EventLogWatcher _watcher;
        private Timer _debounceTimer;

        private sealed class PendingRecord
        {
            public long RecordId;
            public DateTime TimeUtc;
            public string Uri;          // null when extraction failed
            public string Description;  // may be null
            public bool IsBackfill;
        }

        // Pending burst buffer + dedup state, all guarded by _stateLock. Same claim contract as
        // WindowsUpdateTracker (immutable cross-restart boundary + intra-run HashSet), with ONE
        // deliberate difference: the watermark is persisted at FLUSH time, not at claim time —
        // records buffered but never flushed (process killed by the very reboot they announce)
        // must be re-read and emitted by the post-restart backfill.
        private readonly List<PendingRecord> _pending = new List<PendingRecord>();
        private long _restartWatermark = -1;
        private readonly HashSet<long> _seenThisRun = new HashSet<long>();
        private long _maxPersistedRecordId = -1;
        private readonly object _stateLock = new object();

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
            string stateDirectory = null,
            int debounceMilliseconds = DefaultDebounceMilliseconds)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _backfillEnabled = backfillEnabled;
            _backfillLookbackMinutes = backfillLookbackMinutes;
            _stateDirectory = stateDirectory != null ? Environment.ExpandEnvironmentVariables(stateDirectory) : null;
            _debounceMilliseconds = debounceMilliseconds;
        }

        public void Start()
        {
            LoadWatermark();
            StartWatcher();

            if (_backfillEnabled && _backfillLookbackMinutes > 0)
            {
                BackfillRecentEvents();
                // The backfill batch is complete — flush immediately instead of waiting out the
                // debounce (live records that raced in are simply included).
                FlushPending();
            }
            else
            {
                _logger.Info("MdmRebootPolicy backfill disabled by config");
            }
        }

        public void Stop()
        {
            // Flush any un-emitted burst before tearing down (normal agent shutdown path).
            try { FlushPending(); }
            catch (Exception ex) { _logger.Warning($"MdmRebootPolicy flush on stop failed: {ex.Message}"); }

            var timer = Interlocked.Exchange(ref _debounceTimer, null);
            if (timer != null)
            {
                try { timer.Dispose(); } catch { }
            }

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
        // Event processing — buffer + debounce
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
        /// Buffers one 2800 record into the pending burst (primitive-input test seam, mirroring the
        /// WindowsUpdateTracker pattern). The aggregated event is emitted by <see cref="FlushPending"/>
        /// — after the debounce quiet-period, at backfill completion, or at <see cref="Stop"/>.
        /// </summary>
        internal void ProcessEvent(
            int eventId,
            long recordId,
            DateTime? timeCreatedUtc,
            string rebootUri,
            string formattedDescription,
            bool isBackfill)
        {
            lock (_stateLock)
            {
                if (recordId >= 0)
                {
                    // Already emitted in a prior run, or already buffered/emitted this run.
                    if (recordId <= _restartWatermark || !_seenThisRun.Add(recordId))
                        return;
                }

                _pending.Add(new PendingRecord
                {
                    RecordId = recordId,
                    TimeUtc = timeCreatedUtc ?? DateTime.UtcNow,
                    Uri = string.IsNullOrEmpty(rebootUri) ? null : rebootUri,
                    Description = formattedDescription,
                    IsBackfill = isBackfill,
                });
            }

            ArmDebounce();
        }

        private void ArmDebounce()
        {
            if (_debounceMilliseconds <= 0) return; // manual-flush mode (tests; backfill/Stop flush explicitly)

            var timer = _debounceTimer;
            if (timer == null)
            {
                var created = new Timer(_ =>
                {
                    try { FlushPending(); }
                    catch (Exception ex) { _logger.Warning($"MdmRebootPolicy debounce flush failed: {ex.Message}"); }
                }, null, Timeout.Infinite, Timeout.Infinite);

                timer = Interlocked.CompareExchange(ref _debounceTimer, created, null) ?? created;
                if (!ReferenceEquals(timer, created))
                {
                    try { created.Dispose(); } catch { }
                }
            }

            try { timer.Change(_debounceMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* raced Stop() — Stop's flush covers the buffer */ }
        }

        /// <summary>
        /// Emits the pending burst as ONE aggregated <c>mdm_policy_reboot_required</c> event and
        /// persists the watermark. No-op when nothing is pending. Internal for tests.
        /// </summary>
        internal void FlushPending()
        {
            List<PendingRecord> batch;
            long watermarkToPersist = -1;

            lock (_stateLock)
            {
                if (_pending.Count == 0) return;
                batch = new List<PendingRecord>(_pending);
                _pending.Clear();

                foreach (var r in batch)
                {
                    if (r.RecordId > _maxPersistedRecordId)
                        _maxPersistedRecordId = r.RecordId;
                }
                watermarkToPersist = _maxPersistedRecordId;
            }

            if (watermarkToPersist >= 0) PersistWatermark(watermarkToPersist);

            batch.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));

            var distinctUris = batch
                .Where(r => r.Uri != null)
                .Select(r => r.Uri)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var firstUri = batch.FirstOrDefault(r => r.Uri != null)?.Uri;
            var unparsedCount = batch.Count(r => r.Uri == null);
            var allBackfill = batch.All(r => r.IsBackfill);
            var earliest = batch[0].TimeUtc;

            var data = new Dictionary<string, object>
            {
                { "windowsEventId", EventId_PolicyRebootRequired },
                { "eventLogChannel", Channel },
                { "recordCount", batch.Count },
                { "uriCount", distinctUris.Count },
                { "backfilled", allBackfill },
            };
            if (distinctUris.Count > 0)
            {
                data["rebootUris"] = distinctUris
                    .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxUrisPerEvent)
                    .ToList();
                data["firstRebootUri"] = firstUri;
            }
            if (unparsedCount > 0)
            {
                data["unparsedCount"] = unparsedCount;
                var sample = batch.FirstOrDefault(r => r.Uri == null && !string.IsNullOrEmpty(r.Description))?.Description;
                if (sample != null)
                    data["sampleDescription"] = sample.Length > 500 ? sample.Substring(0, 500) + "…" : sample;
            }

            var flag = SafeProbeRebootRequiredFlag();
            if (flag.HasValue) data["omadmRebootRequiredFlag"] = flag.Value;

            string message;
            if (distinctUris.Count == 1)
                message = $"Windows flagged an MDM policy URI as reboot-required: {firstUri}";
            else if (distinctUris.Count > 1)
                message = $"Windows flagged {distinctUris.Count} MDM policy URIs as reboot-required: {firstUri} (+{distinctUris.Count - 1} more)";
            else
                message = $"Windows flagged {batch.Count} MDM policy record(s) as reboot-required (URI not parsed — see sampleDescription)";

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                // Earliest record time, not UtcNow: backfilled (and post-reboot re-read) bursts
                // must land on the timeline at when the policies were actually flagged.
                Timestamp = earliest,
                EventType = Constants.EventTypes.MdmPolicyRebootRequired,
                // Neutral observation — reboot-required flags outside ESP DeviceSetup have no
                // immediate effect. The reboot CLAIM is ANALYZE-ESP-005's job, gated on an
                // actually-observed system_reboot_detected.
                Severity = EventSeverity.Info,
                Source = "MdmRebootPolicyWatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                // Cheap (one event per burst) and still time-critical: a burst DURING DeviceSetup
                // precedes a coalesced reboot that kills the agent.
                ImmediateUpload = true,
            });

            _logger.Info($"MdmRebootPolicy: flushed {batch.Count} record(s) as one event " +
                $"(uris={distinctUris.Count}, unparsed={unparsedCount}, backfilled={allBackfill})");
        }

        /// <summary>
        /// Extracts the reboot-forcing OMA-DM URI. Preference order: (1) a structured EventData
        /// value that starts with <c>./</c>, (2) a permissive regex over the formatted description
        /// (verified shape: <c>The following URI has triggered a reboot: (./Device/...).</c>).
        /// Returns null when nothing URI-like is found — the record still counts into the
        /// aggregate (<c>unparsedCount</c> + <c>sampleDescription</c>).
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
                    // The permissive pattern can swallow sentence punctuation trailing the URI.
                    // OMA-DM URI segments never end in punctuation — trim it.
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
        /// Reads the pending-reboot flag under <see cref="OmadmSyncMlKeyPath"/>. Both a value and a
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
        // Watermark dedup (cross-restart) — persisted at flush, not at claim
        // -----------------------------------------------------------------------

        private bool IsAlreadyProcessed(long recordId)
        {
            if (recordId < 0) return false; // no RecordId → cannot dedup, let it through
            lock (_stateLock)
            {
                return recordId <= _restartWatermark || _seenThisRun.Contains(recordId);
            }
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
                    lock (_stateLock)
                    {
                        _restartWatermark = state.LastRecordId;
                        _maxPersistedRecordId = state.LastRecordId;
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
