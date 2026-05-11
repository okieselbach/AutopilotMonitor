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
                // indicator immediately. The consolidated outcome arrives later via the
                // HS-NEW-RESULT pattern → HandleHealthScriptResult, typically 30 s – 3 min later.
                var policyType = match.Groups["policyType"]?.Value;
                _logger.Info($"ImeLogTracker: health script started: {id}");
                try { OnScriptStarted?.Invoke(new ScriptStartedInfo { PolicyId = id, ScriptType = "remediation", PolicyType = policyType }); }
                catch (Exception ex) { _logger.Warning($"ImeLogTracker: OnScriptStarted handler threw: {ex.Message}"); }
            }
            else
            {
                // Platform script: AgentExecutor.log entries create/enrich, IME log entries also create
                if (!_pendingPlatformScripts.ContainsKey(id))
                {
                    _pendingPlatformScripts[id] = new ScriptExecutionState
                    {
                        PolicyId = id,
                        ScriptType = "platform"
                    };
                }
                _lastPlatformScriptPolicyId = id;
                // Started lines fire twice per script (agentexecutor + ime source) and carry no
                // outcome — the matching `platform script completed` line below carries result+exit
                // and stays on Info. Keep starts on Debug so Info reflects script outcomes only.
                _logger.Debug($"ImeLogTracker: platform script started: {id} (source: {source ?? "ime"})");
            }
        }

        private void HandleScriptContext(Match match, Dictionary<string, string> parameters)
        {
            var context = match.Groups["context"]?.Value;
            if (string.IsNullOrEmpty(context)) return;

            var runContext = string.Equals(context, "machine", StringComparison.OrdinalIgnoreCase) ? "System" : "User";
            var script = GetCurrentPlatformScript();
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

            var script = GetCurrentPlatformScript();
            if (script != null)
            {
                script.ExitCode = exitCode;
                _logger.Debug($"ImeLogTracker: script exit code {exitCode} for {script.PolicyId}");
            }
        }

        private void HandleScriptOutput(Match match, Dictionary<string, string> parameters)
        {
            var outputType = parameters != null && parameters.TryGetValue("outputType", out var ot) ? ot : null;
            var script = GetCurrentPlatformScript();
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

            // Merge with pending AgentExecutor data if available
            if (!_pendingPlatformScripts.TryGetValue(id, out var script))
            {
                script = new ScriptExecutionState
                {
                    PolicyId = id,
                    ScriptType = "platform"
                };
            }

            script.Result = result;

            _logger.Info($"ImeLogTracker: platform script completed: {id}, result={result}, exit={script.ExitCode}");

            EmitScriptEvent(script);
            _pendingPlatformScripts.Remove(id);
            if (string.Equals(_lastPlatformScriptPolicyId, id, StringComparison.OrdinalIgnoreCase))
                _lastPlatformScriptPolicyId = null;
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
                errorDetails: errorDetails);
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
                    errorDetails: errorDetails);
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
                    errorDetails: errorDetails);
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
            string errorDetails) =>
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
                ErrorDetails = errorDetails
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

