#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Surfaces the REAL Microsoft 365 Apps (Office Click-to-Run / C2R) background install lifecycle
    /// — even when the Intune "integrated" Office app reports done to IME within 1-2 minutes while C2R
    /// keeps streaming/laying down the product for many more minutes.
    /// <para>
    /// <b>Event-driven, no idle polling</b> (Rev 2). This class is the pure decision core; the
    /// <c>OfficeInstallDetectorHost</c> owns the OS watchers and drives it:
    /// <list type="bullet">
    ///   <item><see cref="OnWorkerStarted"/> — Office C2R worker (OfficeC2RClient.exe) started
    ///     (WMI <c>Win32_ProcessStartTrace</c> push or startup probe) → emit
    ///     <see cref="Constants.EventTypes.OfficeInstallStarted"/>.</item>
    ///   <item><see cref="OnRegistryChanged"/> — <c>RegNotifyChangeKeyValue</c> push on the ClickToRun
    ///     key (Configuration value / Scenario subkey churn) → emit
    ///     <see cref="Constants.EventTypes.OfficeInstallProgress"/> on real change only (no heartbeat).</item>
    ///   <item><see cref="OnOfficeDoSample"/> — aggregated Delivery-Optimization stats for Office's CDN
    ///     download (from the extended DeliveryOptimizationCollector). DO has no push API, so it is
    ///     sampled while the worker is alive; a byte advance folds a real download-% into progress.</item>
    ///   <item><see cref="OnWorkerStopped"/> — last worker exited (<see cref="Process.Exited"/>) →
    ///     emit <see cref="Constants.EventTypes.OfficeInstallCompleted"/> /
    ///     <see cref="Constants.EventTypes.OfficeInstallFailed"/> with version + duration + DO summary.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Registry reads use the forced Registry64 view (AnyCPU net48 may otherwise read the stale
    /// WOW6432Node mirror). One install lifecycle per detector (latches Terminal); a later C2R update
    /// in the same session is out of scope for v1.
    /// </para>
    /// </summary>
    public class OfficeInstallDetector
    {
        internal const string SourceName = "OfficeInstallDetector";
        internal const string AppName = "Microsoft 365 Apps";

        private const string ConfigurationSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        private const string ScenarioSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Scenario";

        // Value-name substrings scanned (case-insensitive) for a best-effort failure code in the
        // undocumented Scenario value set. Deliberately NARROW (no bare "result" — that would match a
        // benign "Result=Success"); a hit is only treated as a failure when the VALUE is a non-zero
        // numeric/hex code (see IsNonZeroNumericCode). TODO(office-followup): validate the real value
        // names against a field enrollment and widen only if needed.
        private static readonly string[] ErrorValueHints = { "errorcode", "hresult", "exitcode", "lasterror", "failurecode" };

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;
        private readonly object _lock = new object();

        // Test seam: when set, used instead of reading the live registry/process state. Production
        // never assigns it. Surfaced to the test assembly via InternalsVisibleTo.
        internal Func<OfficeC2RSnapshot>? SnapshotProvider { get; set; }

        private enum DetectorState { Idle, Active, Terminal }

        private DetectorState _state = DetectorState.Idle;
        private DateTime? _startedAtUtc;
        private string? _lastProgressSignature;
        private OfficeDoSample? _lastDo;

        public OfficeInstallDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock clock)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        // -----------------------------------------------------------------------
        // Event entry points (driven by the host's process + registry watchers)
        // -----------------------------------------------------------------------

        /// <summary>Office C2R worker started — open the install window.</summary>
        public void OnWorkerStarted()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Idle) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;

                _state = DetectorState.Active;
                _startedAtUtc = _clock.UtcNow;
                _lastProgressSignature = BuildProgressSignature(snap, _lastDo);
                EmitLifecycle(Constants.EventTypes.OfficeInstallStarted, snap, EventSeverity.Info, PhaseOf(snap), isTerminal: false);
            }
        }

        /// <summary>ClickToRun registry changed — progress or a discovered error code.</summary>
        public void OnRegistryChanged()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Active) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;
                EvaluateActive(snap);
            }
        }

        /// <summary>Latest aggregated Office DO stats — folds a real download-% into progress.</summary>
        public void OnOfficeDoSample(OfficeDoSample sample)
        {
            if (sample == null) return;
            lock (_lock)
            {
                _lastDo = sample;
                if (_state != DetectorState.Active) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;
                EvaluateActive(snap);
            }
        }

        /// <summary>Last Office C2R worker exited — resolve terminal completed / failed.</summary>
        public void OnWorkerStopped()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Active) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) snap = new OfficeC2RSnapshot();
                Finalize(snap);
            }
        }

        // -----------------------------------------------------------------------
        // State machine
        // -----------------------------------------------------------------------

        private void EvaluateActive(OfficeC2RSnapshot snap)
        {
            if (!string.IsNullOrEmpty(snap.ErrorCode))
            {
                Finalize(snap);
                return;
            }

            var sig = BuildProgressSignature(snap, _lastDo);
            if (!string.Equals(sig, _lastProgressSignature, StringComparison.Ordinal))
            {
                _lastProgressSignature = sig;
                EmitLifecycle(Constants.EventTypes.OfficeInstallProgress, snap, EventSeverity.Info, PhaseOf(snap), isTerminal: false);
            }
        }

        private void Finalize(OfficeC2RSnapshot snap)
        {
            if (_state == DetectorState.Terminal) return;
            _state = DetectorState.Terminal;

            string eventType;
            EventSeverity severity;
            string phase;
            if (!string.IsNullOrEmpty(snap.ErrorCode))
            {
                eventType = Constants.EventTypes.OfficeInstallFailed; severity = EventSeverity.Error; phase = "Failed";
            }
            else if (snap.StreamingFinished)
            {
                eventType = Constants.EventTypes.OfficeInstallCompleted; severity = EventSeverity.Info; phase = "Completed";
            }
            else
            {
                // Worker gone without StreamingFinished → abandoned install.
                eventType = Constants.EventTypes.OfficeInstallFailed; severity = EventSeverity.Warning; phase = "Failed";
            }

            EmitLifecycle(eventType, snap, severity, phase, isTerminal: true);
        }

        private static string PhaseOf(OfficeC2RSnapshot snap)
            => snap.StreamingFinished ? "Finalizing" : "Streaming";

        /// <summary>
        /// Stable signature of everything we treat as "progress". A change in any of these (registry
        /// phase/version/scenario-value, or a DO byte advance) emits one progress event; an unchanged
        /// signature emits nothing — this keeps the detector heartbeat-free.
        /// </summary>
        private static string BuildProgressSignature(OfficeC2RSnapshot snap, OfficeDoSample? doSample)
        {
            var parts = new List<string>
            {
                "phase=" + PhaseOf(snap),
                "streaming=" + snap.StreamingFinished,
                "version=" + (snap.VersionToReport ?? string.Empty),
                "scenario=" + (snap.ActiveScenarioName ?? string.Empty),
            };
            if (snap.ScenarioValues != null && snap.ScenarioValues.Count > 0)
            {
                foreach (var kv in snap.ScenarioValues.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    parts.Add(kv.Key + "=" + kv.Value);
            }
            if (doSample != null)
            {
                parts.Add("doBytes=" + doSample.TotalBytesDownloaded);
                parts.Add("doPct=" + doSample.DownloadPercent);
            }
            return string.Join("|", parts);
        }

        // -----------------------------------------------------------------------
        // Payload
        // -----------------------------------------------------------------------

        private void EmitLifecycle(string eventType, OfficeC2RSnapshot snap, EventSeverity severity, string phase, bool isTerminal)
        {
            var data = BuildPayload(snap, phase, isTerminal, _lastDo);
            var message = BuildMessage(eventType, snap, phase, _lastDo);

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = true,
            });
        }

        internal Dictionary<string, object> BuildPayload(OfficeC2RSnapshot snap, string phase, bool isTerminal, OfficeDoSample? doSample)
        {
            var elapsedSeconds = _startedAtUtc.HasValue
                ? Math.Max(0, (int)(_clock.UtcNow - _startedAtUtc.Value).TotalSeconds)
                : 0;

            var data = new Dictionary<string, object>
            {
                { "appName", AppName },
                { "products", snap.Products ?? new List<string>() },
                { "channel", snap.Channel ?? string.Empty },
                { "platform", snap.Platform ?? string.Empty },
                { "scenario", snap.ActiveScenarioName ?? string.Empty },
                { "phase", phase },
                { "versionReached", snap.VersionToReport ?? string.Empty },
                { "streamingFinished", snap.StreamingFinished },
                { "officeC2RClientRunning", snap.OfficeC2RClientRunning },
                { "errorCode", (object?)snap.ErrorCode ?? string.Empty },
                { "scanErrors", (snap.Errors ?? new List<string>()).ToList() },
            };

            if (doSample != null)
            {
                // Delivery Optimization download telemetry for Office's CDN content (folded in to
                // avoid creating a phantom app in the backend AppInstallSummary — Office is not an IME app).
                data["doJobCount"] = doSample.JobCount;
                data["doFileSize"] = doSample.FileSize;
                data["doTotalBytesDownloaded"] = doSample.TotalBytesDownloaded;
                data["doBytesFromPeers"] = doSample.BytesFromPeers;
                data["doBytesFromHttp"] = doSample.BytesFromHttp;
                data["doBytesFromCacheServer"] = doSample.BytesFromCacheServer;
                data["doPercentPeerCaching"] = doSample.PercentPeerCaching;
                data["doDownloadMode"] = doSample.DownloadMode;
                if (doSample.DownloadPercent.HasValue)
                    data["downloadPercent"] = doSample.DownloadPercent.Value;
            }

            if (isTerminal)
                data["durationSeconds"] = elapsedSeconds;
            else
                data["elapsedSeconds"] = elapsedSeconds;

            return data;
        }

        private static string BuildMessage(string eventType, OfficeC2RSnapshot snap, string phase, OfficeDoSample? doSample)
        {
            var product = snap.Products != null && snap.Products.Count > 0 ? string.Join(",", snap.Products) : AppName;
            var pct = doSample?.DownloadPercent;
            if (eventType == Constants.EventTypes.OfficeInstallStarted)
                return $"{SourceName}: {product} install detected (phase={phase})";
            if (eventType == Constants.EventTypes.OfficeInstallProgress)
                return pct.HasValue
                    ? $"{SourceName}: {product} install progress (phase={phase}, download {pct.Value}%)"
                    : $"{SourceName}: {product} install progress (phase={phase})";
            if (eventType == Constants.EventTypes.OfficeInstallCompleted)
                return $"{SourceName}: {product} install completed{(string.IsNullOrEmpty(snap.VersionToReport) ? string.Empty : $" (v{snap.VersionToReport})")}";
            return $"{SourceName}: {product} install failed{(string.IsNullOrEmpty(snap.ErrorCode) ? string.Empty : $" (code {snap.ErrorCode})")}";
        }

        // -----------------------------------------------------------------------
        // Registry + process read. Fail-soft — errors captured, never thrown.
        // -----------------------------------------------------------------------

        private OfficeC2RSnapshot? ReadSnapshotSafe()
        {
            try { return SnapshotProvider != null ? SnapshotProvider() : ReadSnapshot(); }
            catch (Exception ex) { _logger.Warning($"[{SourceName}] snapshot read failed: {ex.Message}"); return null; }
        }

        internal virtual OfficeC2RSnapshot ReadSnapshot()
        {
            var snap = new OfficeC2RSnapshot();
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    ReadConfiguration(hklm, snap);
                    ReadScenarios(hklm, snap);
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"registry:{ex.GetType().Name}: {ex.Message}");
            }

            snap.OfficeC2RClientRunning = IsOfficeC2RClientRunning(snap);
            return snap;
        }

        private static void ReadConfiguration(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var key = hklm.OpenSubKey(ConfigurationSubKey, writable: false))
                {
                    if (key == null) return;
                    snap.ConfigurationKeyPresent = true;

                    var products = ReadString(key, "ProductReleaseIds");
                    if (!string.IsNullOrEmpty(products))
                    {
                        snap.Products = products!
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();
                    }

                    snap.Platform = ReadString(key, "Platform");
                    snap.Channel = ReadString(key, "UpdateChannel") ?? ReadString(key, "CDNBaseUrl");
                    snap.VersionToReport = ReadString(key, "VersionToReport") ?? ReadString(key, "ClientVersionToReport");
                    snap.StreamingFinished = IsTruthy(ReadString(key, "StreamingFinished"));
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"configuration:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ReadScenarios(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var scenarioRoot = hklm.OpenSubKey(ScenarioSubKey, writable: false))
                {
                    if (scenarioRoot == null) return;

                    var scenarioNames = scenarioRoot.GetSubKeyNames();
                    if (scenarioNames.Length == 0) return;

                    snap.ActiveScenarioPresent = true;
                    snap.ActiveScenarioName = scenarioNames[0]; // primary scenario (INSTALL / FIRSTRUN / UPDATE …)

                    foreach (var scenarioName in scenarioNames)
                    {
                        try
                        {
                            using (var scenarioKey = scenarioRoot.OpenSubKey(scenarioName, writable: false))
                            {
                                if (scenarioKey == null) continue;
                                foreach (var valueName in scenarioKey.GetValueNames())
                                {
                                    var value = ReadString(scenarioKey, valueName);
                                    if (value == null) continue;
                                    snap.ScenarioValues[$"{scenarioName}\\{valueName}"] = value;

                                    if (snap.ErrorCode == null
                                        && ErrorValueHints.Any(h => valueName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                                        && IsNonZeroNumericCode(value))
                                    {
                                        snap.ErrorCode = value;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            snap.Errors.Add($"scenario[{scenarioName}]:{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"scenario:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsOfficeC2RClientRunning(OfficeC2RSnapshot snap)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName("OfficeC2RClient");
                return procs.Length > 0;
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"process:{ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
        }

        private static string? ReadString(RegistryKey key, string valueName)
        {
            try
            {
                var v = key.GetValue(valueName);
                if (v == null) return null;
                var s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        /// <summary>StreamingFinished is REG_SZ "True"/"False" or REG_DWORD 0/1 across versions.</summary>
        private static bool IsTruthy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value!.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True only when <paramref name="value"/> is a non-zero numeric or hex (0x…) code. Textual
        /// values such as "Success" / "Completed" / "InProgress" are NOT error codes and return false,
        /// so a benign "Result=Success" never masquerades as a failure.
        /// </summary>
        internal static bool IsNonZeroNumericCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex != 0;
            }
            // Decimal (HRESULTs can be large or negative); reject anything non-numeric.
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec != 0;
        }
    }

    // --------------------------------------------------------------------------------------
    // Raw scan model (surfaced to the test assembly via InternalsVisibleTo).
    // --------------------------------------------------------------------------------------

    /// <summary>One read's worth of Office C2R facts (registry + process).</summary>
    public sealed class OfficeC2RSnapshot
    {
        public bool ConfigurationKeyPresent { get; set; }
        public List<string> Products { get; set; } = new List<string>();
        public string? Channel { get; set; }
        public string? Platform { get; set; }
        public string? VersionToReport { get; set; }
        public bool StreamingFinished { get; set; }
        public bool ActiveScenarioPresent { get; set; }
        public string? ActiveScenarioName { get; set; }
        public Dictionary<string, string> ScenarioValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? ErrorCode { get; set; }
        public bool OfficeC2RClientRunning { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Aggregated Delivery-Optimization stats across all Office CDN download jobs in one DO poll,
    /// produced by the extended DeliveryOptimizationCollector and folded into the office_install_* events.
    /// </summary>
    public sealed class OfficeDoSample
    {
        public int JobCount { get; set; }
        public long FileSize { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long BytesFromPeers { get; set; }
        public long BytesFromHttp { get; set; }
        public long BytesFromCacheServer { get; set; }
        public int PercentPeerCaching { get; set; }
        public int DownloadMode { get; set; }

        /// <summary>Whole-percent download progress, or null when total file size is unknown.</summary>
        public int? DownloadPercent => FileSize > 0 ? (int?)Math.Min(100, (int)((TotalBytesDownloaded * 100) / FileSize)) : null;
    }
}
