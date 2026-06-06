using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Ebene 2 — Defensive deep probes that run only when enrollment has been silent for a while.
    ///
    /// The collector is NOT a timer of its own. Instead, it is invoked from
    /// <see cref="Core.MonitoringService.IdleCheckCallback"/> (which runs every 60 s for the
    /// entire agent lifetime). On each tick the collector inspects the idle duration and fires
    /// a full scan at the configured thresholds (default: 2, 15, 30, 60, 180 min).
    ///
    /// Each probe scans up to four read-only sources (all wrapped in try/catch + hard timeout):
    /// 1. Provisioning registry  (HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings)
    /// 2. Diagnostics registry   (HKLM\SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot)
    /// 3. Windows event logs     (ModernDeployment + Shell-Core + Application)
    /// 4. AppWorkload.log tail   (IME ESP app install state)
    ///
    /// The probe classifies the outcome as:
    ///   - anomaly        → <c>stall_probe_result</c> (Warning) + optional <c>EspFailureDetected</c>
    ///   - activeInstalls → positive signal (healthy-in-progress), no alarm
    ///   - quiet          → nothing found in any source
    ///
    /// Output policy (configurable via StallProbeTraceIndices, default = [2]):
    ///   - Trace-indexed probes: emit a <c>stall_probe_check</c> Trace event on quiet/activeInstalls.
    ///   - Non-trace probes: emit nothing when no anomaly is found (agent log only).
    ///   - Anomaly found: always emit <c>stall_probe_result</c> Warning (plus EspFailureDetected if terminal).
    ///   - Probe index configured as "session-stalled": emit fire-once <c>session_stalled</c> Warning.
    /// </summary>
    public class StallProbeCollector
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly int[] _thresholdsMinutes;
        private readonly HashSet<int> _traceIndices;
        private readonly HashSet<string> _sources;
        private readonly int _sessionStalledAfterProbeIndex;
        private readonly HashSet<int> _harmlessModernDeploymentEventIds;

        private readonly object _stateLock = new object();
        private readonly HashSet<int> _firedProbeIndices = new HashSet<int>();
        private bool _sessionStalledFired;

        public event EventHandler<string> EspFailureDetected;

        // Per-source hard timeouts (ms)
        private const int RegistryScanTimeoutMs = 500;
        private const int DiagnosticsRegistryScanTimeoutMs = 500;
        private const int EventLogScanTimeoutMs = 2000;
        private const int AppWorkloadTailTimeoutMs = 1000;

        // Cross-source caps for the aggregated probe result (enforced during merge).
        private const int MaxRawSamples = 5;
        private const int MaxActiveInstalls = 10;

        // Terminal ESP failure EventIDs in ModernDeployment-Diagnostics-Provider channels.
        // Conservative list — we prefer to NOT auto-terminate on unknown IDs and let the user decide.
        //
        // TODO(stall-probe-classification): This is stellschraube #1 for promoting stall-probe findings
        // to terminal failures. Currently empty by design: the ModernDeployment watcher (Ebene 1) runs
        // in live-capture mode and forwards raw events so we can gather real production EventIDs first.
        // Once we have 1–2 weeks of production data showing which EventIDs reliably correspond to
        // terminal ESP failures (e.g. "app install failed terminally", "ESP category Error"), add them
        // here. From that point on, Probe 1 (2 min) will detect them and fire EspFailureDetected →
        // the session goes terminal-Failed within ~2 min instead of waiting for the 5h backend sweep.
        // Cross-reference: the matching terminalReason should go into result.TerminalReason when you
        // add entries (see ScanEventLogs around the TerminalModernDeploymentEventIds.Contains check).
        private static readonly HashSet<int> TerminalModernDeploymentEventIds = new HashSet<int>
        {
            // Populated as real failure EventIDs are observed in production.
            // Kept empty intentionally — live-capture mode (Etappe 3) collects the raw events first.
        };

        // Fallback list of ModernDeployment EventIDs that are known to be harmless and should NOT
        // count as anomalies in the stall probe scan. The runtime-configurable list from the backend
        // is injected via the constructor (CollectorConfiguration.ModernDeploymentHarmlessEventIds);
        // this fallback is used only when no list is provided.
        private static readonly int[] DefaultHarmlessModernDeploymentEventIds = new[] { 100, 1005, 1010 };

        // AppWorkload.log active-install markers (positive signals)
        private static readonly Regex ActiveInstallRegex = new Regex(
            @"EnforcementState\s*[:=]\s*(Downloading|Installing|InProgress)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // AppWorkload.log failure markers (negative signals — but scanner only reports, no terminal decision).
        //
        // TODO(stall-probe-classification): This is stellschraube #2 for promoting stall-probe findings
        // to terminal failures. Today any match of this regex produces a Warning-severity
        // stall_probe_result event but does NOT set result.IsTerminal or fire EspFailureDetected —
        // on purpose, because IME writes transient "Failed" lines that recover (retry, re-detect) and
        // we do not want false-terminal verdicts during the live-capture phase. Once we can
        // distinguish "retryable IME hiccup" from "terminal app install failure" based on real
        // production data, tighten the regex (e.g. require a specific error code range or a second
        // confirming line) and set result.IsTerminal = true + result.TerminalReason in ScanAppWorkloadTail.
        // Ideas for stronger terminal signals to add here once validated:
        //   - EnforcementState: Failed + specific HRESULT ranges (0x87D1... Win32 app failures)
        //   - DetectionState: Error that persists across multiple tail samples
        //   - Explicit "Required app failed" lines from the ESP required-app enforcer
        //
        // TODO(stall-probe-imelog-scan): Also scan IntuneManagementExtension.log (not just
        // AppWorkload.log). Learning from the Oriflame session b4b5d37e-a993-453e-b16b-9b75098022e4
        // (MS Intune AAD token service outage 2026-04-08): the ESP blocker was
        //   "Failed to get AAD token. errorCode = 3399548929"  (= 0xCAA90001 AADSTS_SERVER_ERROR)
        // and this string lives in IntuneManagementExtension.log, NOT AppWorkload.log. Add a third
        // tail-scan source for `C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\IntuneManagementExtension.log`
        // with a regex that catches repeated "Failed to get AAD token.*errorCode" lines (decimal or
        // hex) as a strong "IME is blocked by service issue" signal. Watch out: IME retries every
        // ~1h, so requiring at least 2 occurrences within the 15-min probe window dedup's single
        // transient blips. The errorCode is DECIMAL in IME logs (3399548929), NOT hex, so the regex
        // must handle both formats. A non-terminal Warning is probably the right severity — a
        // service outage is recoverable and Windows ESP will eventually time out on its own if the
        // outage persists, and at that point the ModernDeployment watcher (Ebene 1) should pick it up.
        private static readonly Regex FailureRegex = new Regex(
            @"EnforcementState\s*[:=]\s*Failed|Error\s+0x[0-9A-Fa-f]+|DetectionState\s*[:=]\s*Error",
            RegexOptions.Compiled);

        public StallProbeCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int[] thresholdsMinutes,
            int[] traceIndices,
            string[] sources,
            int sessionStalledAfterProbeIndex,
            int[] harmlessModernDeploymentEventIds = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _thresholdsMinutes = thresholdsMinutes ?? throw new ArgumentNullException(nameof(thresholdsMinutes));
            _traceIndices = new HashSet<int>(traceIndices ?? new int[0]);
            _sources = new HashSet<string>(sources ?? new string[0], StringComparer.OrdinalIgnoreCase);
            _sessionStalledAfterProbeIndex = sessionStalledAfterProbeIndex;
            _harmlessModernDeploymentEventIds = new HashSet<int>(
                harmlessModernDeploymentEventIds != null && harmlessModernDeploymentEventIds.Length > 0
                    ? harmlessModernDeploymentEventIds
                    : DefaultHarmlessModernDeploymentEventIds);
        }

        /// <summary>
        /// Resets all per-probe fire-once state. Called when a new real (non-periodic) event arrives.
        /// session_stalled stays fire-once across the whole session — we don't re-warn if the
        /// session stalls a second time; the backend maintenance sweep handles late stalls.
        /// </summary>
        public void ResetProbes()
        {
            lock (_stateLock)
            {
                if (_firedProbeIndices.Count > 0)
                {
                    _logger.Trace($"StallProbeCollector: resetting {_firedProbeIndices.Count} fired probes");
                    _firedProbeIndices.Clear();
                }
            }
        }

        /// <summary>
        /// Called from the 60-s idle check tick. Decides whether any probe threshold has been
        /// newly crossed and runs the corresponding probe.
        /// </summary>
        public void CheckAndRunProbes(double idleMinutes)
        {
            // Iterate thresholds from lowest to highest so probes fire in order.
            for (int i = 0; i < _thresholdsMinutes.Length; i++)
            {
                var thresholdMinutes = _thresholdsMinutes[i];
                if (idleMinutes < thresholdMinutes)
                    return;

                int probeIndex = i + 1; // 1-based for user-facing output

                bool alreadyFired;
                lock (_stateLock)
                {
                    alreadyFired = _firedProbeIndices.Contains(probeIndex);
                }
                if (alreadyFired)
                    continue;

                RunProbe(probeIndex, thresholdMinutes, idleMinutes);

                lock (_stateLock)
                {
                    _firedProbeIndices.Add(probeIndex);
                }
            }
        }

        private void RunProbe(int probeIndex, int thresholdMinutes, double idleMinutes)
        {
            var started = DateTime.UtcNow;
            var result = new ProbeResult { ProbeIndex = probeIndex, IdleMinutes = Math.Round(idleMinutes, 1) };

            // Run each source best-effort; one failure does not skip the others.
            if (_sources.Contains("provisioning_registry"))
                RunWithTimeout(ScanProvisioningRegistry, RegistryScanTimeoutMs, "provisioning_registry", result);

            if (_sources.Contains("diagnostics_registry"))
                RunWithTimeout(ScanDiagnosticsRegistry, DiagnosticsRegistryScanTimeoutMs, "diagnostics_registry", result);

            if (_sources.Contains("eventlog"))
                RunWithTimeout(ScanEventLogs, EventLogScanTimeoutMs, "eventlog", result);

            if (_sources.Contains("appworkload_log"))
                RunWithTimeout(ScanAppWorkloadTail, AppWorkloadTailTimeoutMs, "appworkload_log", result);

            result.DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;

            // Always log locally for diagnostics — the agent_YYYYMMDD.log is the always-on observability.
            var anomaliesStr = result.Anomalies.Count > 0
                ? $"anomalies=[{string.Join(",", result.Anomalies)}]"
                : "quiet";
            var activeStr = result.ActiveInstalls.Count > 0
                ? $" activeInstalls=[{string.Join(",", result.ActiveInstalls)}]"
                : string.Empty;
            var failedStr = result.SourcesFailed.Count > 0
                ? $" sources_failed=[{string.Join(",", result.SourcesFailed)}]"
                : string.Empty;
            _logger.Info($"StallProbeCollector: Probe {probeIndex}/{_thresholdsMinutes.Length} ran at idle={result.IdleMinutes}min, {anomaliesStr}{activeStr}{failedStr} (duration={result.DurationMs}ms)");

            bool hasAnomaly = result.Anomalies.Count > 0;
            bool hasActiveInstalls = result.ActiveInstalls.Count > 0;

            // Emit stall_probe_result Warning on anomaly — always, regardless of probe index.
            if (hasAnomaly)
            {
                EmitProbeResultEvent(result, severity: EventSeverity.Warning);

                if (result.IsTerminal)
                {
                    _logger.Warning($"StallProbeCollector: terminal ESP failure detected in Probe {probeIndex} — firing EspFailureDetected");
                    try { EspFailureDetected?.Invoke(this, result.TerminalReason ?? "stall_probe_terminal"); }
                    catch (Exception ex) { _logger.Error("EspFailureDetected handler threw", ex); }
                }
            }
            else if (_traceIndices.Contains(probeIndex))
            {
                // Quiet or activeInstalls — emit a Trace heartbeat at trace-indexed probes.
                EmitProbeCheckTraceEvent(result);
            }
            // else: non-trace probe without anomaly → silent except for agent log.

            // session_stalled is fire-once across the whole session. It fires at the configured
            // probe index UNLESS activeInstalls is observed (activeInstalls counts as progress).
            if (probeIndex == _sessionStalledAfterProbeIndex && !hasActiveInstalls)
            {
                lock (_stateLock)
                {
                    if (_sessionStalledFired)
                        return;
                    _sessionStalledFired = true;
                }
                EmitSessionStalledEvent(result);
            }
        }

        private void RunWithTimeout(Action<ProbeResult> scan, int timeoutMs, string sourceName, ProbeResult result)
        {
            // Enforce a real hard timeout: run the source on a dedicated background thread and
            // join with the cap. A wedged source (e.g. an EventLogReader query that never returns,
            // or a registry/file handle that blocks) would otherwise hang the IdleCheckCallback
            // worker for the entire agent lifetime — and .NET has no safe way to abort the
            // in-flight read. The scan writes into an isolated scratch result; we merge it into the
            // live result only on in-time, successful completion. On timeout we abandon the worker
            // (read-only + IsBackground, so it dies with the process and never touches the live
            // result → no data race) and mark the source failed. The probe fires at most a handful
            // of times per session (at the idle thresholds), so the per-source thread cost is
            // negligible.
            var scratch = new ProbeResult { ProbeIndex = result.ProbeIndex, IdleMinutes = result.IdleMinutes };
            Exception captured = null;

            var worker = new Thread(() =>
            {
                try { scan(scratch); }
                catch (Exception ex) { captured = ex; }
            })
            {
                IsBackground = true,
                Name = $"StallProbe-{sourceName}"
            };

            worker.Start();
            bool completed = worker.Join(timeoutMs);

            if (!completed)
            {
                _logger.Warning($"StallProbeCollector: {sourceName} scan exceeded hard timeout ({timeoutMs}ms) — abandoned");
                result.SourcesFailed.Add(sourceName);
                return;
            }

            if (captured != null)
            {
                _logger.Warning($"StallProbeCollector: {sourceName} scan failed: {captured.Message}");
                result.SourcesFailed.Add(sourceName);
                return;
            }

            MergeProbeResult(result, scratch);
        }

        // Folds a single source's scratch result into the aggregated probe result, enforcing the
        // cross-source caps that the individual scans can only enforce within their own scope.
        private static void MergeProbeResult(ProbeResult target, ProbeResult source)
        {
            target.Anomalies.AddRange(source.Anomalies);
            target.SourcesFailed.AddRange(source.SourcesFailed);

            foreach (var install in source.ActiveInstalls)
            {
                if (target.ActiveInstalls.Count >= MaxActiveInstalls)
                    break;
                if (!target.ActiveInstalls.Contains(install))
                    target.ActiveInstalls.Add(install);
            }

            foreach (var sample in source.RawSamples)
            {
                if (target.RawSamples.Count >= MaxRawSamples)
                    break;
                target.RawSamples.Add(sample);
            }

            if (source.IsTerminal && !target.IsTerminal)
            {
                target.IsTerminal = true;
                target.TerminalReason = source.TerminalReason;
            }
        }

        private void ScanProvisioningRegistry(ProbeResult result)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotSettings", writable: false))
            {
                if (key == null)
                    return;

                foreach (var valueName in new[] { "DevicePreparationCategory.Status", "DeviceSetupCategory.Status", "AccountSetupCategory.Status" })
                {
                    var json = key.GetValue(valueName)?.ToString();
                    if (string.IsNullOrEmpty(json))
                        continue;

                    // Rudimentary scan — only flag patterns that are definitively bad.
                    if (json.IndexOf("\"categorySucceeded\":false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        json.IndexOf("\"subcategoryState\":\"failed\"", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Anomalies.Add($"provisioning_registry:{valueName}");
                        if (result.RawSamples.Count < MaxRawSamples)
                            result.RawSamples.Add($"{valueName}: {Truncate(json, 400)}");
                    }
                }
            }
        }

        private void ScanDiagnosticsRegistry(ProbeResult result)
        {
            using (var diagRoot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot", writable: false))
            {
                if (diagRoot == null)
                    return;

                object deployErrorCode = diagRoot.GetValue("DeploymentErrorCode");
                object deployErrorReason = diagRoot.GetValue("DeploymentErrorReason");

                if (deployErrorCode != null)
                {
                    // DeploymentErrorCode is typically a DWORD. 0 = no error.
                    var asString = deployErrorCode.ToString();
                    if (!string.IsNullOrEmpty(asString) && asString != "0" && asString != "0x0")
                    {
                        result.Anomalies.Add("diagnostics_registry:DeploymentErrorCode");
                        result.RawSamples.Add($"DeploymentErrorCode={asString}; DeploymentErrorReason={deployErrorReason ?? "(null)"}");
                        result.IsTerminal = true;
                        result.TerminalReason = $"DeploymentErrorCode={asString}";
                    }
                }
            }
        }

        private void ScanEventLogs(ProbeResult result)
        {
            // Scan a small window (last 15 min) of Autopilot/ESP-related channels at Level ≤ 3.
            var since = DateTime.UtcNow.AddMinutes(-15);
            var sinceStr = since.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var xpath = $"*[System[(Level >= 1 and Level <= 3) and TimeCreated[@SystemTime >= '{sinceStr}']]]";

            var channels = new[]
            {
                "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot",
                "Microsoft-Windows-ModernDeployment-Diagnostics-Provider/ManagementService",
                "Microsoft-Windows-Shell-Core/Operational"
            };

            int foundCount = 0;
            foreach (var channel in channels)
            {
                try
                {
                    var query = new EventLogQuery(channel, PathType.LogName, xpath) { ReverseDirection = true };
                    using (var reader = new EventLogReader(query))
                    {
                        int perChannelCap = 20;
                        EventRecord record;
                        while ((record = reader.ReadEvent()) != null && perChannelCap-- > 0)
                        {
                            using (record)
                            {
                                // Skip known harmless events — they are normal operational noise
                                // and should not be counted as stall anomalies.
                                if (_harmlessModernDeploymentEventIds.Contains(record.Id))
                                    continue;

                                foundCount++;
                                if (result.RawSamples.Count < MaxRawSamples)
                                {
                                    string desc = null;
                                    try { desc = record.FormatDescription(); }
                                    catch { }
                                    desc = Truncate(desc ?? $"EventID {record.Id}", 400);
                                    result.RawSamples.Add($"[{channel.Split('/').Last()}] ID={record.Id} Level={record.Level}: {desc}");
                                }

                                // Check for known terminal ModernDeployment failure event IDs.
                                if (TerminalModernDeploymentEventIds.Contains(record.Id))
                                {
                                    result.IsTerminal = true;
                                    result.TerminalReason = $"ModernDeployment EventID {record.Id}";
                                }
                            }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    // Channel not available on this OS — ignore silently.
                }
                catch (Exception ex)
                {
                    _logger.Trace($"StallProbeCollector: eventlog scan of {channel} failed: {ex.Message}");
                }
            }

            if (foundCount > 0)
            {
                result.Anomalies.Add($"eventlog:{foundCount}_events");
            }
        }

        private void ScanAppWorkloadTail(ProbeResult result)
        {
            var path = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log";
            if (!File.Exists(path))
                return;

            // Read the last ~200 KB. AppWorkload.log can be huge; we never read the whole file.
            const int tailBytes = 200 * 1024;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                {
                    string content = reader.ReadToEnd();

                    // Cut at the first newline to avoid starting mid-line.
                    var firstNewline = content.IndexOf('\n');
                    if (firstNewline > 0 && firstNewline < content.Length - 1)
                        content = content.Substring(firstNewline + 1);

                    // Positive-signal scan: active installations recently logged.
                    foreach (Match m in ActiveInstallRegex.Matches(content))
                    {
                        if (result.ActiveInstalls.Count < MaxActiveInstalls && !result.ActiveInstalls.Contains(m.Value))
                            result.ActiveInstalls.Add(m.Value);
                    }

                    // Negative-signal scan: failures.
                    var failureMatches = FailureRegex.Matches(content);
                    if (failureMatches.Count > 0)
                    {
                        result.Anomalies.Add($"appworkload_log:{failureMatches.Count}_errors");
                        // Sample up to 3 failure lines for context.
                        int sampled = 0;
                        foreach (Match m in failureMatches)
                        {
                            if (sampled >= 3 || result.RawSamples.Count >= MaxRawSamples)
                                break;
                            // Extract the enclosing line for context.
                            int lineStart = content.LastIndexOf('\n', Math.Max(0, m.Index));
                            int lineEnd = content.IndexOf('\n', m.Index);
                            if (lineStart < 0) lineStart = 0;
                            if (lineEnd < 0) lineEnd = content.Length;
                            var line = content.Substring(lineStart, Math.Min(400, lineEnd - lineStart)).Trim();
                            result.RawSamples.Add($"AppWorkload: {line}");
                            sampled++;
                        }
                    }
                }
            }
        }

        private void EmitProbeResultEvent(ProbeResult result, EventSeverity severity)
        {
            var data = BuildDataJson(result);
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.StallProbeResult,
                Severity = severity,
                Source = "StallProbeCollector",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Stall probe {result.ProbeIndex}/{_thresholdsMinutes.Length}: {result.Anomalies.Count} anomaly/ies after {result.IdleMinutes}min idle",
                Data = data
            });
        }

        private void EmitProbeCheckTraceEvent(ProbeResult result)
        {
            var data = BuildDataJson(result);
            var summary = result.ActiveInstalls.Count > 0
                ? $"activeInstalls={result.ActiveInstalls.Count}"
                : "quiet";
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.StallProbeCheck,
                Severity = EventSeverity.Trace,
                Source = "StallProbeCollector",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Stall probe {result.ProbeIndex}/{_thresholdsMinutes.Length} heartbeat: {summary} after {result.IdleMinutes}min idle",
                Data = data
            });
        }

        private void EmitSessionStalledEvent(ProbeResult result)
        {
            var data = BuildDataJson(result);
            data["probeIndexTriggered"] = result.ProbeIndex;
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.SessionStalled,
                Severity = EventSeverity.Warning,
                Source = "StallProbeCollector",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Session stalled: no enrollment progress for {result.IdleMinutes}min (stall probe {result.ProbeIndex})",
                Data = data
            });
        }

        private static Dictionary<string, object> BuildDataJson(ProbeResult r)
        {
            return new Dictionary<string, object>
            {
                { "probeIndex", r.ProbeIndex },
                { "idleMinutes", r.IdleMinutes },
                { "probeDurationMs", r.DurationMs },
                { "anomalyTypes", r.Anomalies.ToArray() },
                { "activeInstalls", r.ActiveInstalls.ToArray() },
                { "sources_failed", r.SourcesFailed.ToArray() },
                { "rawSamples", r.RawSamples.ToArray() },
                { "isTerminal", r.IsTerminal },
                { "terminalReason", r.TerminalReason ?? string.Empty }
            };
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private class ProbeResult
        {
            public int ProbeIndex;
            public double IdleMinutes;
            public int DurationMs;
            public readonly List<string> Anomalies = new List<string>();
            public readonly List<string> ActiveInstalls = new List<string>();
            public readonly List<string> SourcesFailed = new List<string>();
            public readonly List<string> RawSamples = new List<string>();
            public bool IsTerminal;
            public string TerminalReason;
        }
    }
}
