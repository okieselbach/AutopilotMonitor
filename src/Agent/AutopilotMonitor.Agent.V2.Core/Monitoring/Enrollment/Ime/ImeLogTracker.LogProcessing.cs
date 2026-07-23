using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Partial: Log file polling and pattern matching logic.
    /// </summary>
    public partial class ImeLogTracker
    {
        // M2: precompiled matchers for LogFilePatterns so CheckLogFilesAsync can filter a single
        // directory enumeration in-memory. Globs use '?' (single char) in the patterns; '*' is
        // supported too for forward-compatibility.
        private static readonly Regex[] LogFilePatternRegexes = BuildLogFilePatternRegexes();

        private static Regex[] BuildLogFilePatternRegexes()
        {
            var result = new Regex[LogFilePatterns.Length];
            for (var i = 0; i < LogFilePatterns.Length; i++)
            {
                var escaped = Regex.Escape(LogFilePatterns[i]).Replace("\\?", ".").Replace("\\*", ".*");
                result[i] = new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return result;
        }

        private static bool MatchesLogFilePattern(string fileName)
        {
            foreach (var rx in LogFilePatternRegexes)
            {
                if (rx.IsMatch(fileName)) return true;
            }
            return false;
        }

        private async Task CheckLogFilesAsync(CancellationToken token)
        {
            if (!Directory.Exists(_logFolder))
                return;

            // Get all matching log files, sorted by name (archived files come before current).
            // M2: a SINGLE directory enumeration filtered in-memory against the patterns, instead
            // of one Directory.GetFiles(pattern) per LogFilePattern every 100 ms poll (~90 folder
            // enumerations/s → 1). The in-memory regex match is anchored, so it is also immune to
            // the Win32 "*.ext" 8.3 search-pattern quirk.
            var files = new List<string>();
            try
            {
                foreach (var filePath in Directory.EnumerateFiles(_logFolder))
                {
                    if (MatchesLogFilePattern(Path.GetFileName(filePath)))
                        files.Add(filePath);
                }
            }
            catch (DirectoryNotFoundException) { }

            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in files)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists) continue;

                    var startPos = _positionTracker.GetSafePosition(filePath, fileInfo.Length);
                    if (startPos >= fileInfo.Length)
                    {
                        // M2: guard the interpolated string so it isn't built every 100 ms tick
                        // (per file) when Trace is off — which is the production default (Info).
                        if (_logger.LogLevel >= AgentLogLevel.Trace)
                            _logger.Trace($"ImeLogTracker: {Path.GetFileName(filePath)} — no new data (pos={startPos}, size={fileInfo.Length})");
                        continue;
                    }
                    if (_logger.LogLevel >= AgentLogLevel.Trace)
                        _logger.Trace($"ImeLogTracker: reading {Path.GetFileName(filePath)} from pos {startPos} (size={fileInfo.Length}, delta={fileInfo.Length - startPos})");

                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(startPos, SeekOrigin.Begin);

                        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                        {
                            // Buffer for multiline CMTrace entries (e.g. AgentExecutor.log
                            // "write output done. output = ..." spans many lines)
                            StringBuilder multiLineBuffer = null;
                            int multiLineCount = 0;

                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (token.IsCancellationRequested) break;

                                // --- Multiline CMTrace buffering ---
                                // CMTrace entries: <![LOG[message]LOG]!><time=...>
                                // When message contains newlines, the entry spans multiple lines.
                                // We buffer until we find the closing ]LOG]!> tag.
                                if (multiLineBuffer != null)
                                {
                                    // Continuing a multiline entry
                                    multiLineBuffer.Append('\n').Append(line);
                                    multiLineCount++;

                                    if (line.Contains("]LOG]!>"))
                                    {
                                        // Entry complete — use the assembled line
                                        line = multiLineBuffer.ToString();
                                        multiLineBuffer = null;
                                        multiLineCount = 0;
                                    }
                                    else if (multiLineCount >= MaxMultiLineBufferLines)
                                    {
                                        // Safety limit — discard to prevent unbounded memory usage
                                        _logger.Debug($"ImeLogTracker: discarding multiline CMTrace buffer after {multiLineCount} lines (corrupt entry?)");
                                        multiLineBuffer = null;
                                        multiLineCount = 0;
                                        continue;
                                    }
                                    else
                                    {
                                        // Still accumulating — read next line
                                        continue;
                                    }
                                }
                                else if (line.StartsWith("<![LOG[") && !line.Contains("]LOG]!>"))
                                {
                                    // Start of a multiline CMTrace entry
                                    multiLineBuffer = new StringBuilder(line);
                                    multiLineCount = 1;
                                    continue;
                                }

                                // --- Normal processing (single-line or completed multiline) ---
                                CmTraceLogEntry entry;
                                string messageToMatch;
                                if (CmTraceLogParser.TryParseLine(line, out entry))
                                {
                                    messageToMatch = entry.Message;
                                }
                                else
                                {
                                    // Non-CMTrace line - match raw
                                    messageToMatch = line;
                                    entry = null;
                                }

                                if (string.IsNullOrEmpty(messageToMatch)) continue;

                                // Simulation mode delay
                                if (SimulationMode && entry != null)
                                {
                                    await ApplySimulationDelay(entry.Timestamp, token);
                                }

                                // Match against active patterns
                                foreach (var pattern in _activePatterns)
                                {
                                    try
                                    {
                                        var match = pattern.Regex.Match(messageToMatch);
                                        if (match.Success)
                                        {
                                            WriteMatchLog(filePath, line, pattern.PatternId);
                                            HandlePatternMatch(pattern, match, messageToMatch, entry);
                                        }
                                    }
                                    catch (RegexMatchTimeoutException)
                                    {
                                        _logger.Debug($"ImeLogTracker: regex timeout for pattern '{pattern.PatternId}' — skipped to prevent ReDoS");
                                    }
                                }
                            }
                        }

                        _positionTracker.SetPosition(filePath, stream.Position);
                        _stateDirty = true;
                    }
                }
                catch (FileNotFoundException) { }
                catch (IOException ex)
                {
                    _logger.Debug($"ImeLogTracker: IO error reading {filePath}: {ex.Message}");
                }
            }
        }

        // Actions that mutate app/phase tracking state (_packageStates, _phasePackageSnapshots,
        // _currentPhaseOrder, DO telemetry) — the historic-replay guard skips these for source
        // lines from a previous enrollment so replayed apps never enter tracked state,
        // persistence, app_tracking_summary, culprit lists or final-status. Script actions are
        // deliberately NOT here: their tracker state is harmless (stale-slot hardening covers
        // leftovers) and the adapter suppresses their emissions. espphasedetected IS here — a
        // stale "In EspPhase: AccountSetup" would advance _currentPhaseOrder and make the fresh
        // enrollment's DeviceSetup lines bounce as "backward"; IME re-logs the phase constantly,
        // so fresh lines re-deliver it immediately.
        private static readonly HashSet<string> AppMutatingActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "imeshutdown", "espphasedetected", "setcurrentapp",
            "updatestateinstalled", "updatestatedownloading", "updatestateinstalling",
            "updatestateskipped", "updatestateerror", "updatestatepostponed",
            "captureexitcode", "capturehresult", "captureappversion",
            "captureapptypewinget", "captureapptypemsi", "captureattemptnumber",
            "capturedetectionresult", "esptrackstatus", "policiesdiscovered",
            "ignorecompletedapp", "updatename", "updatewin32appstate",
            "cancelstuckandsetcurrent", "updatedotelemetry",
        };

        private void HandlePatternMatch(CompiledPattern pattern, Match match, string message, CmTraceLogEntry entry)
        {
            LastMatchedPatternId = pattern.PatternId;
            LastMatchedLogTimestamp = entry?.Timestamp;

            // Generic pattern-match hook (M4.4.4). Invoked before action-specific callbacks so
            // subscribers (e.g. ImeLogTrackerAdapter emitting WhiteGloveSealingPatternDetected)
            // see the match with LastMatchedPatternId already set. Wrapped to isolate subscriber
            // exceptions from the action-dispatch that follows.
            try { OnPatternMatched?.Invoke(pattern.PatternId); }
            catch (Exception ex) { _logger?.Warning($"OnPatternMatched handler threw: {ex.Message}"); }

            // Historic-replay guard (session eaf3d8c4): a source line > 24 h older than now is
            // content from a previous enrollment whose IME log survived on disk. App-mutating
            // actions are skipped BEFORE _seenAppIds so replayed apps poison neither the tracked
            // state nor the phase-change ignore list. SimulationMode (--replay-log-dir dev tool)
            // replays historic logs on purpose and bypasses the guard.
            var isStaleReplayLine = !SimulationMode && entry != null
                && (UtcNowProvider() - NormalizeUtc(entry.Timestamp)) > HistoricReplayThreshold;
            if (isStaleReplayLine && AppMutatingActions.Contains(pattern.Action?.ToLower() ?? string.Empty))
            {
                _logger.Debug($"ImeLogTracker: skipped app action '{pattern.Action}' for historic line ({entry.Timestamp:o})");
                return;
            }

            try
            {
                var id = match.Groups["id"]?.Value;
                var useCurrentApp = pattern.Parameters.ContainsKey("useCurrentApp") &&
                                    pattern.Parameters["useCurrentApp"] == "true";

                if (useCurrentApp && string.IsNullOrEmpty(id))
                    id = _packageStates.CurrentPackageId;

                // Track every app ID seen during the current phase for comprehensive ignore on phase change
                if (!string.IsNullOrEmpty(id))
                    _seenAppIds.Add(id);

                switch (pattern.Action?.ToLower())
                {
                    case "imestarted":
                        HandleImeStarted();
                        break;

                    case "imeshutdown":
                        HandleImeShutdown();
                        break;

                    case "imesessionchange":
                        var sessionChange = match.Groups["change"]?.Value;
                        // PR3-A3: lift sessionId + user from match if the regex captures them, so the
                        // line carries enough context to correlate without cross-referencing.
                        var sessionChangeSid = match.Groups["sessionId"]?.Value;
                        var sessionChangeUser = match.Groups["user"]?.Value;
                        var sessionChangeContext = (!string.IsNullOrEmpty(sessionChangeSid) || !string.IsNullOrEmpty(sessionChangeUser))
                            ? $" (sessionId={sessionChangeSid ?? "?"}, user={sessionChangeUser ?? "?"})"
                            : string.Empty;
                        _logger.Debug($"IME session change: {sessionChange}{sessionChangeContext}");
                        OnImeSessionChange?.Invoke(sessionChange);
                        break;

                    case "espphasedetected":
                        var phase = match.Groups["espPhase"]?.Value;
                        if (string.IsNullOrEmpty(phase) && pattern.Parameters.ContainsKey("phase"))
                            phase = pattern.Parameters["phase"];
                        if (!string.IsNullOrEmpty(phase))
                            HandleEspPhaseDetected(phase);
                        break;

                    case "setcurrentapp":
                        if (!string.IsNullOrEmpty(id))
                            _packageStates.SetCurrent(id);
                        break;

                    case "imeagentversion":
                        var version = match.Groups["agentVersion"]?.Value;
                        if (!string.IsNullOrEmpty(version))
                            OnImeAgentVersion?.Invoke(version);
                        break;

                    case "imeimpersonation":
                        // PR3-A2: dedup. The same user triggers ~24 identical lines per session;
                        // log on first/changed user, otherwise count and emit a single rollup
                        // every 60s ("same as before (n=…)") so the log stays readable but the
                        // sequence stays reconstructible.
                        HandleImeImpersonation(match.Groups["user"]?.Value);
                        break;

                    case "enrollmentcompleted":
                        _logger.Info("ImeLogTracker: User session completed detected");
                        OnUserSessionCompleted?.Invoke();
                        break;

                    case "updatestateinstalled":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Installed);
                        break;

                    case "updatestatedownloading":
                        if (!string.IsNullOrEmpty(id))
                        {
                            var bytes = match.Groups["bytes"]?.Value;
                            var ofbytes = match.Groups["ofbytes"]?.Value;
                            if (!string.IsNullOrEmpty(bytes) && !string.IsNullOrEmpty(ofbytes))
                                UpdateDownloadingWithCallback(id, bytes, ofbytes);
                            else
                                UpdateStateWithCallback(id, AppInstallationState.Downloading);
                        }
                        break;

                    case "updatestateinstalling":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Installing);
                        break;

                    case "updatestateskipped":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Skipped);
                        break;

                    case "updatestateerror":
                        // Extract structured error code from named capture groups (exitCode, hresult, errorCode)
                        var extractedErrorCode = match.Groups["exitCode"]?.Value;
                        if (string.IsNullOrEmpty(extractedErrorCode))
                            extractedErrorCode = match.Groups["hresult"]?.Value;
                        if (string.IsNullOrEmpty(extractedErrorCode))
                            extractedErrorCode = match.Groups["errorCode"]?.Value;

                        if (pattern.Parameters.ContainsKey("checkTo") && pattern.Parameters["checkTo"] == "true")
                        {
                            // Only apply error if the "to" value is "Error"
                            var toValue = match.Groups["to"]?.Value;
                            if (string.Equals(toValue, "Error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id))
                                UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message, errorCode: extractedErrorCode);
                        }
                        else if (!string.IsNullOrEmpty(id))
                        {
                            UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message, errorCode: extractedErrorCode);
                        }
                        break;

                    case "captureexitcode":
                        var exitCodeVal = match.Groups["exitCode"]?.Value;
                        if (!string.IsNullOrEmpty(exitCodeVal) && !string.IsNullOrEmpty(_packageStates.CurrentPackageId))
                            _packageStates.GetPackage(_packageStates.CurrentPackageId)?.UpdateExitCode(exitCodeVal);
                        break;

                    case "capturehresult":
                        var hresultVal = match.Groups["hresult"]?.Value;
                        if (!string.IsNullOrEmpty(hresultVal) && !string.IsNullOrEmpty(_packageStates.CurrentPackageId))
                            _packageStates.GetPackage(_packageStates.CurrentPackageId)?.UpdateHResult(hresultVal);
                        break;

                    case "captureappversion":
                        var appVersionVal = match.Groups["appVersion"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(appVersionVal))
                            _packageStates.GetPackage(id)?.UpdateAppVersion(appVersionVal);
                        break;

                    case "captureapptypewinget":
                        if (!string.IsNullOrEmpty(id))
                            _packageStates.GetPackage(id)?.UpdateAppType("WinGet");
                        break;

                    case "captureapptypemsi":
                        if (!string.IsNullOrEmpty(id))
                            _packageStates.GetPackage(id)?.UpdateAppType("MSI");
                        break;

                    case "captureattemptnumber":
                        // IME logs "Execute retry 0" for the first attempt. We report attempt+1 so
                        // the human-friendly value starts at 1 (first attempt).
                        var attemptVal = match.Groups["attempt"]?.Value;
                        if (!string.IsNullOrEmpty(attemptVal) && int.TryParse(attemptVal, out var attemptIdx)
                            && !string.IsNullOrEmpty(_packageStates.CurrentPackageId))
                        {
                            _packageStates.GetPackage(_packageStates.CurrentPackageId)?.UpdateAttemptNumber(attemptIdx + 1);
                        }
                        break;

                    case "capturedetectionresult":
                        var detectionVal = match.Groups["detection"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(detectionVal))
                            _packageStates.GetPackage(id)?.UpdateDetectionResult(detectionVal);
                        break;

                    case "updatestatepostponed":
                        if (!string.IsNullOrEmpty(id))
                        {
                            // Only postpone if not already in a terminal state
                            var pkg = _packageStates.GetPackage(id);
                            if (pkg != null && pkg.InstallationState != AppInstallationState.Installed &&
                                pkg.InstallationState != AppInstallationState.Error)
                            {
                                UpdateStateWithCallback(id, AppInstallationState.Postponed);
                            }
                        }
                        break;

                    case "esptrackstatus":
                        HandleEspTrackStatus(match);
                        break;

                    case "policiesdiscovered":
                        var policiesJson = match.Groups["policies"]?.Value;
                        if (!string.IsNullOrEmpty(policiesJson))
                            HandlePoliciesDiscovered(policiesJson);
                        break;

                    case "ignorecompletedapp":
                        _packageStates.AddToIgnoreList(_packageStates.CurrentPackageId);
                        break;

                    case "updatename":
                        var name = match.Groups["name"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                            _packageStates.UpdateName(id, name);
                        break;

                    case "updatewin32appstate":
                        var state = match.Groups["state"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(state))
                            _packageStates.UpdateStateFromWin32AppState(id, state);
                        break;

                    case "cancelstuckandsetcurrent":
                        HandleCancelStuckAndSetCurrent(id);
                        break;

                    case "updatedotelemetry":
                        var doTelJson = match.Groups["doTelJson"]?.Value;
                        if (!string.IsNullOrEmpty(doTelJson))
                            HandleDoTelemetry(doTelJson);
                        break;

                    // PowerShell script tracking actions
                    case "scriptstarted":
                        HandleScriptStarted(match, pattern.Parameters);
                        break;

                    case "scriptcontext":
                        HandleScriptContext(match, pattern.Parameters);
                        break;

                    case "scriptexitcode":
                        HandleScriptExitCode(match, pattern.Parameters);
                        break;

                    case "scriptoutput":
                        HandleScriptOutput(match, pattern.Parameters);
                        break;

                    case "scriptcompleted":
                        HandleScriptCompleted(match, pattern.Parameters);
                        break;

                    case "resetplatformscriptcontext":
                        // Session 6b4993e5 fix: a fresh AgentExecutor invocation banner
                        // ("ExecutorLog AgentExecutor gets invoked") ends the previous
                        // invocation's line-capture context. See HandleAgentExecutorInvocationBoundary.
                        HandleAgentExecutorInvocationBoundary();
                        break;

                    case "healthscriptresult":
                        HandleHealthScriptResult(match, pattern.Parameters);
                        break;

                    case "healthscriptdetectionresult":
                        HandleHealthScriptDetectionResult(match, pattern.Parameters);
                        break;

                    default:
                        _logger.Debug($"ImeLogTracker: unhandled action '{pattern.Action}' for pattern {pattern.PatternId}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: error handling match for {pattern.PatternId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Test seam: run a single already-assembled log message through the active-pattern
        /// pipeline exactly as <see cref="CheckLogFilesAsync"/> does per line (match against
        /// <c>_activePatterns</c> → <see cref="HandlePatternMatch"/>). Lets unit tests drive the
        /// script / app handlers deterministically without writing CMTrace files or spinning the
        /// poller. <paramref name="sourceTimestampUtc"/> populates the entry timestamp some
        /// handlers read via <see cref="LastMatchedLogTimestamp"/>.
        /// </summary>
        internal void ProcessLogMessageForTest(string message, DateTime? sourceTimestampUtc = null)
        {
            if (string.IsNullOrEmpty(message)) return;
            var entry = sourceTimestampUtc.HasValue
                ? new CmTraceLogEntry { Timestamp = sourceTimestampUtc.Value, Message = message }
                : null;
            foreach (var pattern in _activePatterns)
            {
                try
                {
                    var match = pattern.Regex.Match(message);
                    if (match.Success)
                        HandlePatternMatch(pattern, match, message, entry);
                }
                catch (RegexMatchTimeoutException) { }
            }
        }

        /// <summary>
        /// Parses [DO TEL] JSON and links it to the correct app via FileId.
        /// The FileId contains the app GUID in the format: ...intunewin-bin_{appGuid}_{number}
        /// </summary>
    }
}
