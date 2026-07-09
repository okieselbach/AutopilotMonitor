using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Partial: Event handlers for pattern matches (app state, scripts, ESP phases),
    /// telemetry processing, and utility methods.
    /// </summary>
    public partial class ImeLogTracker
    {
        // PR3-A2: dedup state for `IME impersonation` lines. ~24 identical per session at
        // DEBUG flooded the log; we now emit on user change + a 60s rollup with the count.
        private string _lastImpersonationUser;
        private int _impersonationRepeatCount;
        private DateTime _impersonationLastRollupUtc = DateTime.MinValue;
        private static readonly TimeSpan ImpersonationRollupInterval = TimeSpan.FromSeconds(60);

        private void HandleImeImpersonation(string user)
        {
            var current = string.IsNullOrEmpty(user) ? "(unknown)" : user;

            if (!string.Equals(current, _lastImpersonationUser, StringComparison.Ordinal))
            {
                if (_impersonationRepeatCount > 0)
                {
                    _logger.Debug($"IME impersonation: previous '{_lastImpersonationUser}' seen {_impersonationRepeatCount}x before change");
                }
                _logger.Debug($"IME impersonation: {current}");
                _lastImpersonationUser = current;
                _impersonationRepeatCount = 0;
                _impersonationLastRollupUtc = DateTime.UtcNow;
                return;
            }

            _impersonationRepeatCount++;
            var now = DateTime.UtcNow;
            if (now - _impersonationLastRollupUtc >= ImpersonationRollupInterval)
            {
                _logger.Debug($"IME impersonation: same as before (user='{current}', n={_impersonationRepeatCount} since last rollup)");
                _impersonationLastRollupUtc = now;
                _impersonationRepeatCount = 0;
            }
        }

        private void HandleDoTelemetry(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Extract app GUID from FileId
                string fileId = root.TryGetProperty("FileId", out var fileIdProp)
                    ? fileIdProp.GetString() : null;

                if (string.IsNullOrEmpty(fileId))
                {
                    _logger.Debug("ImeLogTracker: DO TEL entry has no FileId, skipping");
                    return;
                }

                string appId = ExtractAppIdFromDoFileId(fileId);
                if (string.IsNullOrEmpty(appId))
                {
                    appId = _packageStates.CurrentPackageId;
                    _logger.Debug($"ImeLogTracker: Could not extract app ID from DO FileId, using current app: {appId}");
                }

                if (string.IsNullOrEmpty(appId)) return;

                var pkg = _packageStates.GetPackage(appId);
                if (pkg == null)
                {
                    _logger.Debug($"ImeLogTracker: DO TEL for unknown app {appId}, skipping");
                    return;
                }

                // Extract all DO fields. Newer breakdown fields (LinkLocal/CacheServer/CacheHost)
                // may be absent in older IME log JSON — TryGetProperty falls back to defaults.
                long fileSize = root.TryGetProperty("FileSize", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long totalBytes = root.TryGetProperty("TotalBytesDownloaded", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesFromPeers = root.TryGetProperty("BytesFromPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                int peerCachingPct = root.TryGetProperty("PercentPeerCaching", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                long bytesLanPeers = root.TryGetProperty("BytesFromLanPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesGroupPeers = root.TryGetProperty("BytesFromGroupPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesInternetPeers = root.TryGetProperty("BytesFromInternetPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesLinkLocalPeers = root.TryGetProperty("BytesFromLinkLocalPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesFromCacheServer = root.TryGetProperty("BytesFromCacheServer", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                string cacheHost = root.TryGetProperty("CacheHost", out p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                int downloadMode = root.TryGetProperty("DownloadMode", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : -1;
                string downloadDuration = root.TryGetProperty("DownloadDuration", out p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                long bytesFromHttp = root.TryGetProperty("BytesFromHttp", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

                pkg.UpdateDoTelemetry(fileSize, totalBytes, bytesFromPeers, peerCachingPct,
                    bytesLanPeers, bytesGroupPeers, bytesInternetPeers,
                    downloadMode, downloadDuration, bytesFromHttp,
                    bytesFromLinkLocalPeers: bytesLinkLocalPeers,
                    bytesFromCacheServer: bytesFromCacheServer,
                    cacheHost: cacheHost);

                _logger.Info($"ImeLogTracker: DO TEL for {pkg.Name ?? appId}: " +
                    $"size={fileSize}, peers={bytesFromPeers} ({peerCachingPct}%), " +
                    $"http={bytesFromHttp}, cache={bytesFromCacheServer}, mode={downloadMode}, duration={downloadDuration}");

                OnDoTelemetryReceived?.Invoke(pkg);
                _stateDirty = true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: Failed to parse DO TEL JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts app GUID from DO FileId string.
        /// Format: ...intunewin-bin_{appGuid}_{number}
        /// Falls back to trying the second-to-last GUID-like segment.
        /// </summary>
        internal static string ExtractAppIdFromDoFileId(string fileId)
        {
            // Primary: look for "intunewin-bin_" marker
            const string marker = "intunewin-bin_";
            var idx = fileId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var afterMarker = fileId.Substring(idx + marker.Length);
                if (afterMarker.Length >= 36)
                {
                    var candidate = afterMarker.Substring(0, 36);
                    if (Guid.TryParse(candidate, out var guid))
                        return guid.ToString().ToLowerInvariant();
                }
            }

            // Fallback: split by underscore and find GUIDs, take the second-to-last one
            var segments = fileId.Split('_');
            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (Guid.TryParse(segments[i], out var guid))
                    return guid.ToString().ToLowerInvariant();
            }

            return null;
        }

        // -----------------------------------------------------------------------
        // PowerShell script tracking handlers
        // -----------------------------------------------------------------------

        // Returns the current platform-script accumulator, or null when no platform script is active.
        // Health-script (remediation) state is no longer tracked line-by-line — the HS-NEW-RESULT
        // pattern delivers the full pre-detection / remediation / post-detection JSON in one shot
        // via HandleHealthScriptResult. The platform branch keeps its line-by-line accumulator
        // because PS-* patterns (PS-SCRIPT-CONTEXT / PS-SCRIPT-EXITCODE / PS-AGENT-OUTPUT / …) still
        // arrive across multiple log lines.
        private ScriptExecutionState GetCurrentPlatformScript()
        {
            if (!string.IsNullOrEmpty(_lastPlatformScriptPolicyId) &&
                _pendingPlatformScripts.TryGetValue(_lastPlatformScriptPolicyId, out var state))
                return state;
            return null;
        }

        private void HandleScriptStarted(Match match, Dictionary<string, string> parameters)
        {
            var id = match.Groups["id"]?.Value;
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : "platform";
            var source = parameters != null && parameters.TryGetValue("source", out var src) ? src : null;

            if (string.IsNullOrEmpty(id))
            {
                _logger.Debug("ImeLogTracker: scriptStarted with no policyId captured, skipping");
                return;
            }

            if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
            {
                // Health script: emit a live "started" signal so the UI can show a running
                // indicator immediately. Also (re)init the per-policy slot that the
                // HS-RUN-CONTEXT / HS-EXITCODE / HS-STDOUT / HS-STDERR handlers fill, so the
                // early-signal HS-COMPLIANCE event carries real exit / stdout / stderr / context
                // instead of just the inferred compliance verdict. The consolidated outcome
                // arrives later via HS-NEW-RESULT → HandleHealthScriptResult.
                var policyType = match.Groups["policyType"]?.Value;
                _pendingHealthScript = new ScriptExecutionState
                {
                    PolicyId = id,
                    ScriptType = "remediation",
                };
                // Stamp the cycle start so HandleHealthScriptResultJson can compute the run
                // duration when the consolidated [HS] new result line arrives (the slot above is
                // cleared by the early-signal HS-COMPLIANCE handler, so timing lives in its own
                // map). Prefer the source CMTrace timestamp; latest start wins on a policy re-run.
                _healthScriptStartTimes[id] = LastMatchedLogTimestamp ?? DateTime.UtcNow;
                _logger.Info($"ImeLogTracker: health script started: {id}");
                try { OnScriptStarted?.Invoke(new ScriptStartedInfo { PolicyId = id, ScriptType = "remediation", PolicyType = policyType }); }
                catch (Exception ex) { _logger.Warning($"ImeLogTracker: OnScriptStarted handler threw: {ex.Message}"); }
            }
            else
            {
                // Platform script: AgentExecutor.log entries create/enrich, IME log entries also create
                if (!_pendingPlatformScripts.ContainsKey(id))
                {
                    // A fresh start for this policy (no pending entry) begins a NEW execution.
                    // Clear any emitted-marker from a prior run so IME re-evaluations / retries of
                    // the same platform-script policy within one agent lifetime are not silently
                    // deduped away. Within a single run the start always precedes exit/result, so
                    // this never clears the current run's own marker.
                    _platformScriptResultEmitted.Remove(id);
                    _pendingPlatformScripts[id] = new ScriptExecutionState
                    {
                        PolicyId = id,
                        ScriptType = "platform",
                        // Prefer the source CMTrace timestamp so a script that started before the
                        // agent launched (replayed log content) is dated to its real start, not now.
                        // Set only at slot creation: the start line fires twice (agentexecutor + ime
                        // source) but the earliest wins.
                        StartedAtUtc = LastMatchedLogTimestamp ?? DateTime.UtcNow,
                    };
                    // Live "running" indicator — same signal health scripts emit. Gated on slot
                    // creation so the duplicate start line (agentexecutor + ime source) doesn't
                    // double-emit for the same execution.
                    try { OnScriptStarted?.Invoke(new ScriptStartedInfo { PolicyId = id, ScriptType = "platform" }); }
                    catch (Exception ex) { _logger.Warning($"ImeLogTracker: OnScriptStarted handler threw: {ex.Message}"); }
                }
                _lastPlatformScriptPolicyId = id;
                // Started lines fire twice per script (agentexecutor + ime source) and carry no
                // outcome — the matching `platform script completed` line below carries result+exit
                // and stays on Info. Keep starts on Debug so Info reflects script outcomes only.
                _logger.Debug($"ImeLogTracker: platform script started: {id} (source: {source ?? "ime"})");
            }
        }

        // Routes line-by-line script-data handlers (context / exitCode / output) to the right
        // accumulator based on the pattern's scriptType parameter. Health-script lines feed the
        // single-slot _pendingHealthScript; platform-script lines feed _pendingPlatformScripts.
        private ScriptExecutionState GetCurrentScriptForLineUpdate(Dictionary<string, string> parameters)
        {
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : null;
            return string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase)
                ? _pendingHealthScript
                : GetCurrentPlatformScript();
        }

        // Session 6b4993e5 fix — platform-script stdout/exit cross-contamination.
        //
        // AgentExecutor.exe hosts BOTH platform scripts (…\Policies\Scripts\<id>.ps1) and
        // proactive-remediation scripts (…\IMECache\HealthScripts\<id>\detect.ps1), and writes
        // their lines interleaved into the shared AgentExecutor.log. The policyId-less
        // PS-AGENT-EXITCODE ("Powershell exit code is N") and PS-AGENT-OUTPUT ("write output
        // done. output = …") lines are routed to whichever platform script
        // _lastPlatformScriptPolicyId points at. A remediation invocation's start
        // ("Adding argument remediationScript …") does NOT match PS-AGENT-SCRIPT-START, so it
        // never moves the pointer — its exit/output then bleed into the last-started PLATFORM
        // script's slot (observed: platform c3e0124c emitted result=Failed but with exit 0 +
        // stdout "[Compliant] No Classic Teams found" — the Teams remediation's output).
        //
        // Each AgentExecutor process logs "ExecutorLog AgentExecutor gets invoked" first, so a
        // new banner means the previous invocation's line-capture context is over. Clearing the
        // pointer here scopes line-capture to a single invocation: the immediately following
        // "Adding argument powershell …\Policies\Scripts\<id>.ps1" (PS-AGENT-SCRIPT-START)
        // re-establishes it for a platform invocation, while a remediation invocation leaves it
        // null so its exit/output are dropped (the remediation's authoritative data still
        // arrives via HS-NEW-RESULT). The platform script's final result is unaffected — it is
        // keyed by policyId via PS-SCRIPT-RESULT, not by this pointer.
        private void HandleAgentExecutorInvocationBoundary()
        {
            if (_lastPlatformScriptPolicyId == null) return;
            _logger.Debug(
                $"ImeLogTracker: AgentExecutor invocation boundary — clearing platform-script line-capture pointer (was {_lastPlatformScriptPolicyId}).");
            _lastPlatformScriptPolicyId = null;
        }

        private void HandleScriptContext(Match match, Dictionary<string, string> parameters)
        {
            var context = match.Groups["context"]?.Value;
            if (string.IsNullOrEmpty(context)) return;

            var runContext = string.Equals(context, "machine", StringComparison.OrdinalIgnoreCase) ? "System" : "User";
            var script = GetCurrentScriptForLineUpdate(parameters);
            if (script != null)
            {
                script.RunContext = runContext;
                _logger.Debug($"ImeLogTracker: script context set to {runContext} for {script.PolicyId}");
            }
        }

        private void HandleScriptExitCode(Match match, Dictionary<string, string> parameters)
        {
            var exitCodeStr = match.Groups["exitCode"]?.Value;
            if (string.IsNullOrEmpty(exitCodeStr) || !int.TryParse(exitCodeStr, out var exitCode)) return;

            var script = GetCurrentScriptForLineUpdate(parameters);
            if (script != null)
            {
                script.ExitCode = exitCode;
                // Stamp when we learned the script's exit code so the deadline-based fallback
                // (FlushStalePlatformScriptResults) can fire if IME never logs its authoritative
                // PS-SCRIPT-RESULT line in time. Prefer the source CMTrace timestamp (already
                // UTC-normalized by the parser) so replayed log content is dated correctly.
                if (string.Equals(script.ScriptType, "platform", StringComparison.OrdinalIgnoreCase))
                    script.ExitObservedAtUtc = LastMatchedLogTimestamp ?? DateTime.UtcNow;
                _logger.Debug($"ImeLogTracker: script exit code {exitCode} for {script.PolicyId}");
            }
        }

        private void HandleScriptOutput(Match match, Dictionary<string, string> parameters)
        {
            var outputType = parameters != null && parameters.TryGetValue("outputType", out var ot) ? ot : null;
            var script = GetCurrentScriptForLineUpdate(parameters);
            if (script == null) return;

            // PS-AGENT-OUTPUT captures both stdout and stderr in one pattern
            var output = match.Groups["output"]?.Value;
            var error = match.Groups["error"]?.Value;

            if (!string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(error))
            {
                // Combined pattern (write output done. output = ..., error = ...)
                script.Stdout = TruncateOutput(output.Trim());
                script.Stderr = TruncateOutput(error.Trim());
            }
            else if (string.Equals(outputType, "stderr", StringComparison.OrdinalIgnoreCase))
            {
                script.Stderr = TruncateOutput(output?.Trim());
            }
            else
            {
                script.Stdout = TruncateOutput(output?.Trim());
            }

            _logger.Debug($"ImeLogTracker: script output captured for {script.PolicyId} (type: {outputType ?? "combined"})");
        }

        private void HandleScriptCompleted(Match match, Dictionary<string, string> parameters)
        {
            // Platform script only — the remediation path is now handled by HandleHealthScriptResult
            // (single-source via HS-NEW-RESULT JSON). Any caller passing scriptType=remediation
            // here is a stale pattern from a downgraded ruleset; ignore defensively.
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : "platform";
            if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("ImeLogTracker: ignoring remediation scriptCompleted from stale pattern (use HS-NEW-RESULT)");
                return;
            }

            // Platform script: final result from IntuneManagementExtension.log
            var id = match.Groups["id"]?.Value;
            var result = match.Groups["result"]?.Value;

            if (string.IsNullOrEmpty(id)) return;

            // Dedup: the deadline-based fallback (FlushStalePlatformScriptResults) may already have
            // emitted this script from its AgentExecutor exit code because IME's authoritative
            // PS-SCRIPT-RESULT line arrived late. The fallback carries the same exit code + stdout,
            // so re-emitting here would duplicate the timeline entry and inflate counts. Drop the
            // now-redundant pending entry and skip.
            if (_platformScriptResultEmitted.Contains(id))
            {
                _logger.Debug($"ImeLogTracker: platform script {id} PS-SCRIPT-RESULT arrived after fallback emit — skipping duplicate");
                _pendingPlatformScripts.Remove(id);
                if (string.Equals(_lastPlatformScriptPolicyId, id, StringComparison.OrdinalIgnoreCase))
                    _lastPlatformScriptPolicyId = null;
                return;
            }

            // Merge with pending AgentExecutor data if available
            if (_pendingPlatformScripts.TryGetValue(id, out var script))
            {
                // Stale-result guard: on an intra-lifetime re-run of the same policy, run 1's
                // late PS-SCRIPT-RESULT can arrive after run 2's start line already created a
                // fresh slot (the start clears the emitted-marker). Merging would pair run 2's
                // StartedAtUtc/state with run 1's result and then drop run 2's genuine result at
                // the marker check above. A result line always postdates its own run's start
                // line in the source log, so a result older than the pending slot's start
                // belongs to a previous run — drop it and keep the live slot intact.
                if (script.StartedAtUtc.HasValue
                    && LastMatchedLogTimestamp.HasValue
                    && LastMatchedLogTimestamp.Value < script.StartedAtUtc.Value)
                {
                    _logger.Debug($"ImeLogTracker: platform script {id} result predates the current run's start — stale result from a previous run, skipping");
                    return;
                }
            }
            else
            {
                script = new ScriptExecutionState
                {
                    PolicyId = id,
                    ScriptType = "platform"
                };
            }

            script.Result = result;
            script.ResultSource = "ime_policy_result";

            _logger.Info($"ImeLogTracker: platform script completed: {id}, result={result}, exit={script.ExitCode}");

            EmitScriptEvent(script);
            _platformScriptResultEmitted.Add(id);
            _pendingPlatformScripts.Remove(id);
            _stateDirty = true;
            if (string.Equals(_lastPlatformScriptPolicyId, id, StringComparison.OrdinalIgnoreCase))
                _lastPlatformScriptPolicyId = null;
        }

        /// <summary>
        /// Grace period after a platform script's AgentExecutor exit code is observed before
        /// <see cref="FlushStalePlatformScriptResults"/> emits a completion from that exit code.
        /// IME logs its authoritative <c>PS-SCRIPT-RESULT</c> line within ~1 s of exit on healthy
        /// reporting cycles, so 15 s lets the normal path win virtually always — the fallback is a
        /// true safety net for the case where IME has not flushed its batch-send to the Microsoft
        /// service before the (often short) Autopilot enrollment ends and the agent terminates.
        /// </summary>
        private static readonly TimeSpan PlatformScriptResultGrace = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Policy IDs of platform scripts already emitted (via either the authoritative
        /// PS-SCRIPT-RESULT path or the exit-code fallback). Guards against double emission when
        /// both fire for the same script.
        /// </summary>
        private readonly HashSet<string> _platformScriptResultEmitted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Emits a <c>script_completed</c>/<c>script_failed</c> for every pending platform script
        /// that has a known AgentExecutor exit code but whose IME <c>PS-SCRIPT-RESULT</c> line has
        /// not arrived within <see cref="PlatformScriptResultGrace"/>. Without this, scripts that
        /// completed shortly before the agent terminates are silently dropped (the
        /// <c>_pendingPlatformScripts</c> buffer is neither persisted nor flushed on dispose), so a
        /// device that ran N platform scripts could surface only the long-running one that happened
        /// to get its IME result logged in time. Result is derived from the exit code (0 → Success,
        /// else Failed) and tagged <c>resultSource=agentexecutor_fallback</c> so the data is honest
        /// about its provenance.
        /// <para>
        /// Runs on the single-threaded polling loop (no locking needed, same as the handlers).
        /// <paramref name="force"/> bypasses the grace check for the final pass on shutdown.
        /// </para>
        /// </summary>
        internal void FlushStalePlatformScriptResults(DateTime nowUtc, bool force = false)
        {
            if (_pendingPlatformScripts.Count == 0) return;

            List<string> toRemove = null;
            foreach (var kv in _pendingPlatformScripts)
            {
                var script = kv.Value;

                // Already emitted by the authoritative path — just drop the stale buffer entry.
                if (_platformScriptResultEmitted.Contains(script.PolicyId))
                {
                    (toRemove ??= new List<string>()).Add(kv.Key);
                    continue;
                }

                // No exit code yet → the script is still running; nothing to emit.
                if (!script.ExitCode.HasValue) continue;

                if (!force)
                {
                    if (!script.ExitObservedAtUtc.HasValue) continue;
                    if (nowUtc - script.ExitObservedAtUtc.Value < PlatformScriptResultGrace) continue;
                }

                script.Result = script.ExitCode.Value == 0 ? "Success" : "Failed";
                script.ResultSource = "agentexecutor_fallback";

                _logger.Info(
                    $"ImeLogTracker: platform script {script.PolicyId} result not seen within " +
                    $"{PlatformScriptResultGrace.TotalSeconds:F0}s{(force ? " (shutdown flush)" : "")} — " +
                    $"emitting from AgentExecutor exit code {script.ExitCode.Value} (fallback)");

                EmitScriptEvent(script);
                _platformScriptResultEmitted.Add(script.PolicyId);
                (toRemove ??= new List<string>()).Add(kv.Key);
            }

            if (toRemove != null)
            {
                // The flush can fire on a quiet polling cycle (grace expiry with no new log
                // lines), where nothing else marks state dirty — the emitted-markers must reach
                // the state file before a restart or the restarted tracker re-emits (H1).
                _stateDirty = true;
                foreach (var key in toRemove)
                {
                    _pendingPlatformScripts.Remove(key);
                    if (string.Equals(_lastPlatformScriptPolicyId, key, StringComparison.OrdinalIgnoreCase))
                        _lastPlatformScriptPolicyId = null;
                }
            }
        }

        /// <summary>Test seam: inject a pending platform script as if AgentExecutor.log had reported
        /// its start + exit code, without an IME PS-SCRIPT-RESULT line.</summary>
        internal void SeedPendingPlatformScriptForTesting(string policyId, int? exitCode, DateTime? exitObservedAtUtc, string stdout = null, DateTime? startedAtUtc = null)
        {
            // Mirrors a fresh execution start: a new run clears any prior emitted-marker (see
            // HandleScriptStarted) so re-runs of the same policy within one lifetime emit again.
            _platformScriptResultEmitted.Remove(policyId);
            _pendingPlatformScripts[policyId] = new ScriptExecutionState
            {
                PolicyId = policyId,
                ScriptType = "platform",
                ExitCode = exitCode,
                ExitObservedAtUtc = exitObservedAtUtc,
                Stdout = stdout,
                StartedAtUtc = startedAtUtc,
            };
            _lastPlatformScriptPolicyId = policyId;
        }

        /// <summary>Test seam: simulate the authoritative IME PS-SCRIPT-RESULT path for a platform
        /// script (same code as <see cref="HandleScriptCompleted"/> minus regex extraction).
        /// <paramref name="resultLineTimestampUtc"/> stands in for the CMTrace timestamp of the
        /// PS-SCRIPT-RESULT line (production reads it via <see cref="LastMatchedLogTimestamp"/>).</summary>
        internal void CompletePlatformScriptFromImeResultForTesting(string policyId, string result, DateTime? resultLineTimestampUtc = null)
        {
            if (string.IsNullOrEmpty(policyId)) return;

            if (_platformScriptResultEmitted.Contains(policyId))
            {
                _pendingPlatformScripts.Remove(policyId);
                if (string.Equals(_lastPlatformScriptPolicyId, policyId, StringComparison.OrdinalIgnoreCase))
                    _lastPlatformScriptPolicyId = null;
                return;
            }

            if (_pendingPlatformScripts.TryGetValue(policyId, out var script))
            {
                // Mirror of HandleScriptCompleted's stale-result guard: a result older than the
                // pending slot's start belongs to a previous run of the same policy.
                if (script.StartedAtUtc.HasValue
                    && resultLineTimestampUtc.HasValue
                    && resultLineTimestampUtc.Value < script.StartedAtUtc.Value)
                {
                    return;
                }
            }
            else
            {
                script = new ScriptExecutionState { PolicyId = policyId, ScriptType = "platform" };
            }

            script.Result = result;
            script.ResultSource = "ime_policy_result";
            EmitScriptEvent(script);
            _platformScriptResultEmitted.Add(policyId);
            _pendingPlatformScripts.Remove(policyId);
            if (string.Equals(_lastPlatformScriptPolicyId, policyId, StringComparison.OrdinalIgnoreCase))
                _lastPlatformScriptPolicyId = null;
            _stateDirty = true;
        }

        /// <summary>
        /// Parses the IME <c>[HS] new result = {…}</c> JSON and emits one to three
        /// <see cref="ScriptExecutionState"/> events covering the pre-detection,
        /// remediation, and post-detection phases of a health script run.
        /// All three field groups (<c>PreRemediationDetectScript*</c> /
        /// <c>RemediationScript*</c> / <c>PostRemediationDetectScript*</c>) are nullable —
        /// only the populated phases produce an event. Defensive against parse errors:
        /// invalid JSON is logged once at warning level and counted via
        /// <see cref="ImeLogTracker.HealthScriptResultParseFailures"/>.
        /// </summary>
        /// <summary>
        /// Early-signal fallback emit for health-script detection / post-detection.
        /// IME logs <c>[HS] the (pre|post)-remdiation detection script compliance result</c>
        /// immediately after each script runs, but the consolidated <c>[HS] new result = {…}</c>
        /// JSON line — the canonical source — only fires after IME's batch-send to the
        /// Microsoft service, typically 30-90 s later. Short Autopilot enrollments often end
        /// inside that gap, so this handler guarantees we have at least the compliance verdict
        /// for each detection. When HS-NEW-RESULT later fires with the full payload, the UI
        /// reducer's dataCompleteness scoring keeps the more complete entry.
        /// </summary>
        private void HandleHealthScriptDetectionResult(Match match, Dictionary<string, string> parameters)
        {
            var id = match.Groups["id"]?.Value;
            var compliance = match.Groups["compliance"]?.Value;
            var part = match.Groups["part"]?.Value; // "pre" or "post"

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(compliance))
            {
                _logger.Debug("ImeLogTracker: HS-COMPLIANCE missing id or compliance, skipping");
                return;
            }

            var isCompliant = string.Equals(compliance, "True", StringComparison.OrdinalIgnoreCase);
            var scriptPart = string.Equals(part, "post", StringComparison.OrdinalIgnoreCase) ? "post-detection" : "detection";

            // Pull line-by-line slot data accumulated since the matching HS-SCRIPT-START so the
            // early-signal event carries real exit / stdout / stderr / context — same fields V1
            // surfaced. The slot is keyed by the most recent script-start, so for the post-
            // detection compliance line it may be empty (lines for the remediation phase that
            // ran in between went into a different cycle's slot). That's fine: HS-NEW-RESULT
            // arrives later and fills any gaps via the UI dataCompleteness dedupe.
            int? exitCode = null;
            string runContext = null;
            string stdout = null;
            string stderr = null;
            if (_pendingHealthScript != null && string.Equals(_pendingHealthScript.PolicyId, id, StringComparison.OrdinalIgnoreCase))
            {
                exitCode = _pendingHealthScript.ExitCode;
                runContext = _pendingHealthScript.RunContext;
                stdout = _pendingHealthScript.Stdout;
                stderr = _pendingHealthScript.Stderr;
            }

            // If we don't have a real exit code from the line-by-line capture, fall back to
            // the inferred value: IME treats exit 0 = compliant, non-zero = non-compliant for
            // detection / post-detection scripts.
            if (exitCode == null) exitCode = isCompliant ? 0 : 1;

            var enriched = new ScriptExecutionState
            {
                PolicyId = id,
                ScriptType = "remediation",
                ScriptPart = scriptPart,
                ComplianceResult = isCompliant ? "True" : "False",
                ExitCode = exitCode,
                RunContext = runContext,
                Stdout = stdout,
                Stderr = stderr,
                // The compliance line is logged right after the (pre/post) detection script
                // exits, so cycle-start → this line is the actual execution time — unlike the
                // HS-NEW-RESULT path whose end stamp includes IME's batched reporting latency.
                // Peek only: HS-NEW-RESULT still consumes + removes the entry for cycle timing.
                StartedAtUtc = _healthScriptStartTimes.TryGetValue(id, out var earlyStart)
                    ? earlyStart
                    : (DateTime?)null,
                DurationBasis = "script_runtime",
            };

            _logger.Info($"ImeLogTracker: health-script {scriptPart} early-signal for {id}: " +
                         $"compliance={compliance}, exit={exitCode}, " +
                         $"stdout={(stdout == null ? "n/a" : stdout.Length + " chars")}, " +
                         $"stderr={(stderr == null ? "n/a" : stderr.Length + " chars")}");

            EmitScriptEvent(enriched);

            // Clear slot — next HS-SCRIPT-START reinitialises it for the next script.
            _pendingHealthScript = null;
        }

        /// <summary>
        /// Test seam: simulates the full HS-SCRIPT-START → HS-RUN-CONTEXT / HS-EXITCODE /
        /// HS-STDOUT / HS-STDERR → HS-COMPLIANCE sequence by directly populating the
        /// per-policy slot and then invoking the compliance handler. Lets unit tests verify
        /// the early-signal enrichment without driving the full regex pipeline.
        /// </summary>
        internal void HandleHealthScriptDetectionResultForTest(
            string id,
            string compliance,
            string part,
            int? exitCode = null,
            string runContext = null,
            string stdout = null,
            string stderr = null)
        {
            if (!string.IsNullOrEmpty(id) && (exitCode.HasValue || runContext != null || stdout != null || stderr != null))
            {
                _pendingHealthScript = new ScriptExecutionState
                {
                    PolicyId = id,
                    ScriptType = "remediation",
                    ExitCode = exitCode,
                    RunContext = runContext,
                    Stdout = stdout,
                    Stderr = stderr,
                };
            }

            // Build a synthetic Match with the named capture groups by running the regex on
            // a constructed line — far simpler than building a Match instance directly.
            var pcs = string.Equals(part, "post", StringComparison.OrdinalIgnoreCase) ? "post" : "pre";
            var sample = $"[HS] the {pcs}-remdiation detection script compliance result for {id} is {compliance}";
            var match = System.Text.RegularExpressions.Regex.Match(
                sample,
                @"\[HS\] the (?<part>pre|post)-remdiation detection script compliance result for (?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12}) is (?<compliance>True|False)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            HandleHealthScriptDetectionResult(match, new Dictionary<string, string>());
        }

        /// <summary>Test seam: seed the per-policy health-script cycle start timestamp as if an
        /// HS-SCRIPT-START line had been observed, so a subsequent
        /// <see cref="HandleHealthScriptResultJson"/> surfaces a run duration without driving the
        /// full regex pipeline.</summary>
        internal void SeedHealthScriptStartForTesting(string policyId, DateTime startedAtUtc)
        {
            if (string.IsNullOrEmpty(policyId)) return;
            _healthScriptStartTimes[policyId] = startedAtUtc;
        }

        private void HandleHealthScriptResult(Match match, Dictionary<string, string> parameters)
        {
            var json = match.Groups["json"]?.Value;
            if (string.IsNullOrEmpty(json))
            {
                _logger.Debug("ImeLogTracker: HS-NEW-RESULT match had empty json group, skipping");
                return;
            }
            HandleHealthScriptResultJson(json);
        }

        /// <summary>
        /// Test seam that bypasses the regex-match plumbing and feeds a raw <c>[HS] new result</c>
        /// JSON payload directly into the parser. Production code path runs through
        /// <see cref="HandleHealthScriptResult(Match, Dictionary{string, string})"/>; this method
        /// exists so unit tests can exercise the JSON parser + 1-3 phase emit logic without
        /// having to write CMTrace log files into a temp directory.
        /// </summary>
        internal void HandleHealthScriptResultJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                _logger.Debug("ImeLogTracker: HandleHealthScriptResultJson received empty json, skipping");
                return;
            }

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(json);
                // Clone so the JsonElement outlives the using-scope (we read it after dispose).
                root = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                HealthScriptResultParseFailures++;
                _logger.Warning($"ImeLogTracker: HS-NEW-RESULT JSON parse failed (failures so far: {HealthScriptResultParseFailures}): {ex.Message}");
                return;
            }

            var policyId = TryGetString(root, "PolicyId");
            if (string.IsNullOrEmpty(policyId))
            {
                _logger.Debug("ImeLogTracker: HS-NEW-RESULT JSON missing PolicyId, skipping");
                return;
            }

            // Cycle run-duration: start captured from HS-SCRIPT-START, end = this result line
            // (the adapter resolves the completion timestamp). Surfaced as durationSeconds on the
            // emitted phase events — the remediation analog of platform-script timing. Consumed +
            // removed so a later re-run of the same policy re-times from its own start. Null when
            // the start line was never seen in the agent's window (e.g. a replayed result).
            DateTime? cycleStartedAtUtc = _healthScriptStartTimes.TryGetValue(policyId, out var hsStart)
                ? hsStart
                : (DateTime?)null;
            _healthScriptStartTimes.Remove(policyId);

            // Common metadata — applied to every emitted phase event.
            var remediationStatus = TryGetInt(root, "RemediationStatus");
            var targetType = TryGetInt(root, "TargetType");
            var errorCode = TryGetInt(root, "ErrorCode");
            var runAsAccount = TryGetInt(root, "RunAsAccount"); // 1 = System, 2 = User
            var runContext = runAsAccount switch
            {
                1 => "System",
                2 => "User",
                _ => null
            };

            // Info sub-object holds the per-phase exit codes plus error details.
            int? firstDetectExit = null;
            int? remediationExit = null;
            int? lastDetectExit = null;
            string errorDetails = null;
            if (root.TryGetProperty("Info", out var infoElement) && infoElement.ValueKind == JsonValueKind.Object)
            {
                firstDetectExit = TryGetInt(infoElement, "FirstDetectExitCode");
                remediationExit = TryGetInt(infoElement, "RemediationExitCode");
                lastDetectExit = TryGetInt(infoElement, "LastDetectExitCode");
                errorDetails = TryGetString(infoElement, "ErrorDetails");
            }

            // Phase 1 (always): pre-detection — fires for every health-script run, including
            // detect-only policies (RemediationStatus = 4 / NoRemediation).
            var detection = BuildHealthScriptPhase(
                policyId,
                scriptPart: "detection",
                exitCode: firstDetectExit,
                stdout: TryGetString(root, "PreRemediationDetectScriptOutput"),
                stderr: TryGetString(root, "PreRemediationDetectScriptError"),
                runContext: runContext,
                remediationStatus: remediationStatus,
                targetType: targetType,
                errorCode: errorCode,
                errorDetails: errorDetails,
                startedAtUtc: cycleStartedAtUtc);
            // complianceResult mirrors the legacy HS-COMPLIANCE event semantics: True when the
            // pre-detection script reported compliant (exit 0), False otherwise.
            detection.ComplianceResult = firstDetectExit == 0 ? "True" : (firstDetectExit.HasValue ? "False" : null);
            EmitScriptEvent(detection);

            // Phase 2 (conditional): remediation script — only when the policy attached one and
            // detection returned non-compliant. Either Remediation* output/error is populated, or
            // RemediationExitCode is non-null (the latter is the canonical "remediation actually ran" signal).
            var remediationStdout = TryGetString(root, "RemediationScriptOutputDetails");
            var remediationStderr = TryGetString(root, "RemediationScriptErrorDetails");
            if (remediationExit.HasValue || !string.IsNullOrEmpty(remediationStdout) || !string.IsNullOrEmpty(remediationStderr))
            {
                var remediation = BuildHealthScriptPhase(
                    policyId,
                    scriptPart: "remediation",
                    exitCode: remediationExit,
                    stdout: remediationStdout,
                    stderr: remediationStderr,
                    runContext: runContext,
                    remediationStatus: remediationStatus,
                    targetType: targetType,
                    errorCode: errorCode,
                    errorDetails: errorDetails,
                    startedAtUtc: cycleStartedAtUtc);
                // No complianceResult on the remediation phase — there is nothing to be compliant about.
                EmitScriptEvent(remediation);
            }

            // Phase 3 (conditional): post-detection — IME's re-run of the detection script after
            // remediation completed, used to confirm the fix took effect. Same signal logic as phase 2.
            var postStdout = TryGetString(root, "PostRemediationDetectScriptOutput");
            var postStderr = TryGetString(root, "PostRemediationDetectScriptError");
            if (lastDetectExit.HasValue || !string.IsNullOrEmpty(postStdout) || !string.IsNullOrEmpty(postStderr))
            {
                var postDetection = BuildHealthScriptPhase(
                    policyId,
                    scriptPart: "post-detection",
                    exitCode: lastDetectExit,
                    stdout: postStdout,
                    stderr: postStderr,
                    runContext: runContext,
                    remediationStatus: remediationStatus,
                    targetType: targetType,
                    errorCode: errorCode,
                    errorDetails: errorDetails,
                    startedAtUtc: cycleStartedAtUtc);
                postDetection.ComplianceResult = lastDetectExit == 0 ? "True" : (lastDetectExit.HasValue ? "False" : null);
                EmitScriptEvent(postDetection);
            }

            _logger.Info($"ImeLogTracker: health-script result emitted for {policyId} " +
                         $"(status={remediationStatus?.ToString() ?? "?"}, " +
                         $"firstDetectExit={firstDetectExit?.ToString() ?? "n/a"}, " +
                         $"remediationExit={remediationExit?.ToString() ?? "n/a"}, " +
                         $"lastDetectExit={lastDetectExit?.ToString() ?? "n/a"})");
        }

        private static ScriptExecutionState BuildHealthScriptPhase(
            string policyId,
            string scriptPart,
            int? exitCode,
            string stdout,
            string stderr,
            string runContext,
            int? remediationStatus,
            int? targetType,
            int? errorCode,
            string errorDetails,
            DateTime? startedAtUtc) =>
            new ScriptExecutionState
            {
                PolicyId = policyId,
                ScriptType = "remediation",
                ScriptPart = scriptPart,
                ExitCode = exitCode,
                Stdout = TruncateOutput(stdout),
                Stderr = TruncateOutput(stderr),
                RunContext = runContext,
                RemediationStatus = remediationStatus,
                TargetType = targetType,
                ErrorCode = errorCode,
                ErrorDetails = errorDetails,
                // Cycle start (HS-SCRIPT-START). Every phase carries the same cycle start so the
                // adapter computes the total run duration; the Web surfaces it once at card level.
                StartedAtUtc = startedAtUtc,
                // End stamp is the HS-NEW-RESULT line — written only after IME's batched report
                // to the service, so this duration overstates the actual script execution time.
                DurationBasis = "cycle_including_reporting_latency"
            };

        private static string TryGetString(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var prop)) return null;
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString(),
                JsonValueKind.Null => null,
                _ => null
            };
        }

        private static int? TryGetInt(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var prop)) return null;
            if (prop.ValueKind != JsonValueKind.Number) return null;
            return prop.TryGetInt32(out var value) ? value : (int?)null;
        }

        private void EmitScriptEvent(ScriptExecutionState script)
        {
            if (script == null || string.IsNullOrEmpty(script.PolicyId)) return;
            OnScriptCompleted?.Invoke(script);
        }

        private static string TruncateOutput(string output)
        {
            if (string.IsNullOrEmpty(output) || output.Length <= MaxScriptOutputLength)
                return output;
            return output.Substring(0, MaxScriptOutputLength) + "...[truncated]";
        }

        // -----------------------------------------------------------------------
        // IME lifecycle handlers
        // -----------------------------------------------------------------------

        private void HandleImeStarted()
        {
            _logger.Info("ImeLogTracker: IME Agent Started detected");

            // Log any currently active app — it will be re-evaluated by new log entries after IME restart.
            // We intentionally do NOT mark it as Installed here because IME may retry the app.
            if (!string.IsNullOrEmpty(_packageStates.CurrentPackageId))
            {
                var currentPkg = _packageStates.GetPackage(_packageStates.CurrentPackageId);
                if (currentPkg?.IsActive == true)
                    _logger.Info($"ImeLogTracker: Active package {currentPkg.Name ?? currentPkg.Id} ({currentPkg.InstallationState}) will be re-evaluated after IME restart");
            }

            OnImeStarted?.Invoke();
        }

        private void HandleImeShutdown()
        {
            _logger.Info("ImeLogTracker: IME shutdown detected — marking all active packages as Postponed");

            foreach (var pkg in _packageStates.Where(p => p.IsActive).ToList())
            {
                _logger.Info($"ImeLogTracker: Postponing active package {pkg.Name ?? pkg.Id} ({pkg.InstallationState})");
                UpdateStateWithCallback(pkg.Id, AppInstallationState.Postponed);
            }
        }

        private string _lastEspPhaseDetected;

        /// <summary>
        /// Last ESP phase the tracker observed ("DeviceSetup" / "AccountSetup"), or null if none
        /// seen yet (e.g. no-ESP WDP v2). Read-only accessor for the adapter to tag
        /// <c>script_timeout_suspected</c> with the ESP context the hung script ran in.
        /// </summary>
        internal string LastEspPhaseDetected => _lastEspPhaseDetected;

        private void HandleEspPhaseDetected(string espPhaseString)
        {
            // Validate phase and get its ordinal for forward-only enforcement
            int phaseOrd;
            if (!EspPhaseOrder.TryGetValue(espPhaseString, out phaseOrd))
                return; // Not a recognized ESP phase

            bool logPhaseIsCurrentPhase = true; // Both DeviceSetup and AccountSetup are "current"

            // Forward-only phase progression: reject backward transitions.
            // During AccountSetup, IME may re-evaluate device apps and log "In EspPhase: DeviceSetup"
            // which would corrupt tracking if we allowed it to trigger a phase change.
            if (phaseOrd < _currentPhaseOrder)
            {
                _logger.Debug($"ImeLogTracker: ignoring backward phase transition to {espPhaseString} (current phase order: {_currentPhaseOrder})");
                return;
            }

            // If the ESP phase actually changed (e.g. DeviceSetup -> AccountSetup),
            // move ALL known app IDs into the ignore list so they won't emit events in the new phase.
            // We use both _packageStates (apps from policiesdiscovered) AND _seenAppIds (all IDs from
            // any pattern match) to ensure comprehensive coverage - apps seen via setcurrentapp,
            // esptrackstatus etc. that never entered _packageStates are also silenced.
            var isFirstOrChangedPhase = !string.Equals(_lastEspPhaseDetected, espPhaseString, StringComparison.OrdinalIgnoreCase);
            if (isFirstOrChangedPhase)
            {
                if (_lastEspPhaseDetected != null) // Not the first phase detection
                {
                    var ignoredFromStates = 0;
                    var ignoredFromSeen = 0;

                    foreach (var pkg in _packageStates)
                    {
                        if (_packageStates.AddToIgnoreList(pkg.Id))
                            ignoredFromStates++;
                    }
                    foreach (var appId in _seenAppIds)
                    {
                        if (_packageStates.AddToIgnoreList(appId))
                            ignoredFromSeen++;
                    }

                    _logger.Info($"ImeLogTracker: ESP phase changed from {_lastEspPhaseDetected} to {espPhaseString} - " +
                                 $"silenced {ignoredFromStates} packages + {ignoredFromSeen} additional seen IDs " +
                                 $"(total ignore list: {_packageStates.IgnoreList.Count})");

                    // Snapshot package states from the completed phase before clearing.
                    // We hold the actual AppPackageState references (cheap; List<T>.Clear() on
                    // _packageStates does not destroy them) so the termination summary path
                    // can iterate per-phase apps with their full typed state — see
                    // GetAllKnownPackageStates() in the partial Core class.
                    if (_packageStates.CountAll > 0)
                    {
                        _phasePackageSnapshots[_lastEspPhaseDetected] =
                            new List<AppPackageState>(_packageStates);
                        _logger.Info($"ImeLogTracker: Snapshotted {_packageStates.CountAll} package states from {_lastEspPhaseDetected} phase");
                    }

                    _packageStates.Clear();
                    _packageStates.SetCurrent(""); // Reset current package to avoid stale device-phase reference
                    _seenAppIds.Clear();
                    _allAppsCompletedFired = false;
                }
                _lastEspPhaseDetected = espPhaseString;
                _currentPhaseOrder = phaseOrd;
            }

            // First detection of any phase OR a real transition into a new phase: surface on Info
            // so phase boundaries are visible without enabling Debug. Re-matches of the same phase
            // (IME re-emits the phase string periodically as it re-evaluates app sets) stay on
            // Debug to avoid Info repetition.
            if (isFirstOrChangedPhase)
                _logger.Info($"ImeLogTracker: ESP phase detected: {espPhaseString}");
            else
                _logger.Debug($"ImeLogTracker: ESP phase detected: {espPhaseString}");
            ActivatePatterns(logPhaseIsCurrentPhase);

            OnEspPhaseChanged?.Invoke(espPhaseString);
        }

        private void HandleEspTrackStatus(Match match)
        {
            var from = match.Groups["from"]?.Value;
            var to = match.Groups["to"]?.Value;
            var id = match.Groups["id"]?.Value;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(to)) return;

            var pkg = _packageStates.GetPackage(id);
            var label = pkg?.Name ?? id;

            if (string.Equals(to, "InProgress", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> InProgress");
                _packageStates.SetCurrent(id);
                UpdateStateWithCallback(id, AppInstallationState.Installing);
            }
            else if (string.Equals(to, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> Completed");
                UpdateStateWithCallback(id, AppInstallationState.Installed);
            }
            else if (string.Equals(to, "Error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> Error");
                UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: "IME-ESP-TRACK-STATUS", errorDetail: $"ESP track status changed from {from} to {to}");
            }
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            try
            {
                var policies = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(policiesJson);
                if (policies != null)
                {
                    // Ignore user-targeted packages during device setup (we only track device phase in general)
                    _packageStates.AddUpdateFromJsonPolicies(policies, ignoreUserTargeted: false);
                    _logger.Info($"ImeLogTracker: discovered {policies.Count} policies, tracking {_packageStates.Count} packages");
                    OnPoliciesDiscovered?.Invoke(policiesJson);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: failed to parse policies JSON: {ex.Message}");
            }
        }

        private void HandleCancelStuckAndSetCurrent(string newId)
        {
            if (string.IsNullOrEmpty(newId)) return;

            // If current app is stuck in an active state, mark it as skipped
            var currentPkg = _packageStates.GetPackage(_packageStates.CurrentPackageId);
            if (currentPkg != null && (currentPkg.InstallationState == AppInstallationState.Installing ||
                                       currentPkg.InstallationState == AppInstallationState.InProgress ||
                                       currentPkg.InstallationState == AppInstallationState.Downloading))
            {
                _logger.Info($"ImeLogTracker: cancelling stuck app {currentPkg.Name ?? _packageStates.CurrentPackageId} ({currentPkg.InstallationState}), switching to {newId}");
                UpdateStateWithCallback(_packageStates.CurrentPackageId, AppInstallationState.Skipped);
            }

            _packageStates.SetCurrent(newId);
        }

        private void UpdateStateWithCallback(string id, AppInstallationState newState, int? progressPercent = null, string errorPatternId = null, string errorDetail = null, string errorCode = null)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

            // Set error context before state change so it's available in ToEventData()
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(errorPatternId))
                pkg.SetErrorContext(errorPatternId, errorDetail, errorCode);

            var oldState = pkg.InstallationState;
            var changed = _packageStates.UpdateState(id, newState, progressPercent);

            if (changed)
            {
                _logger.Info($"ImeLogTracker: {pkg.Name ?? id} state: {oldState} -> {newState}");
                OnAppStateChanged?.Invoke(pkg, oldState, newState);

                // Check if all apps are now completed - only fire once
                if (!_allAppsCompletedFired && _packageStates.CountAll > 0 && _packageStates.IsAllCompleted())
                {
                    _allAppsCompletedFired = true;
                    _logger.Info($"ImeLogTracker: all {_packageStates.CountAll} apps completed");
                    OnAllAppsCompleted?.Invoke();
                }
            }
        }

        private void UpdateDownloadingWithCallback(string id, string bytes, string ofbytes)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

            var oldState = pkg.InstallationState;
            var changed = _packageStates.UpdateStateToDownloading(id, bytes, ofbytes);

            if (changed)
            {
                _logger.Debug($"ImeLogTracker: {pkg.Name ?? id} downloading: {bytes}/{ofbytes}");
                OnAppStateChanged?.Invoke(pkg, oldState, AppInstallationState.Downloading);
            }
        }

        private void ActivatePatterns(bool logPhaseIsCurrentPhase, bool force = false)
        {
            if (!force && _logPhaseIsCurrentPhase == logPhaseIsCurrentPhase)
                return;

            var patterns = new List<CompiledPattern>(_patternsAlways);
            if (logPhaseIsCurrentPhase)
            {
                patterns.AddRange(_patternsCurrentPhase);
                _lastLogTimestamp = DateTime.MinValue; // Reset simulation delay
            }
            else
            {
                patterns.AddRange(_patternsOtherPhases);
            }

            _activePatterns = patterns;
            _packageStates.SetCurrent(""); // Reset current package
            _logPhaseIsCurrentPhase = logPhaseIsCurrentPhase;
        }

        private async Task ApplySimulationDelay(DateTime logTimestamp, CancellationToken token)
        {
            if (!SimulationMode || logTimestamp == DateTime.MinValue)
                return;

            if (_lastLogTimestamp != DateTime.MinValue)
            {
                var timeSpan = logTimestamp - _lastLogTimestamp;
                if (timeSpan.TotalMilliseconds > 0)
                {
                    var delayMs = (int)(timeSpan.TotalMilliseconds / SpeedFactor);
                    delayMs = Math.Max(0, Math.Min(delayMs, 5000)); // Cap at 5 seconds
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token);
                }
            }
            _lastLogTimestamp = logTimestamp;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        private void WriteMatchLog(string sourceFile, string rawLine, string patternId)
        {
            if (string.IsNullOrEmpty(_matchLogPath)) return;
            try
            {
                var entry = $"[{Path.GetFileName(sourceFile)}] [{patternId}] {rawLine}";
                lock (_matchLogLock)
                {
                    File.AppendAllText(_matchLogPath, entry + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>
        /// A compiled regex pattern with its action and parameters
        /// </summary>
        private class CompiledPattern
        {
            public string PatternId { get; set; }
            public Regex Regex { get; set; }
            public string Action { get; set; }
            public Dictionary<string, string> Parameters { get; set; }
        }
    }
}

