using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Logging;
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

        /// <remarks>internal (not private) as a test seam: the offset calibration depends on the
        /// pass structure — first observation versus a later pass over a grown file — which only
        /// a real read cycle exercises. Production still reaches it solely from the poll loop.</remarks>
        internal async Task CheckLogFilesAsync(CancellationToken token)
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

            // Σ(file length − bookmark) after this pass — the tracker's queue depth.
            long backlogBytes = 0;

            foreach (var filePath in files)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists) continue;

                    // Captured BEFORE MarkChecked below stamps this pass. Growth measured
                    // against a previously observed state is what makes this pass's lines valid
                    // "written now" anchors — for the per-file measurement AND per-line anchoring.
                    var hadPreviousObservation = _positionTracker.HasSeen(filePath);
                    var lastCheckedUtc = _positionTracker.GetLastCheckedUtc(filePath);
                    var passNowUtc = UtcNowProvider();

                    var startPos = _positionTracker.GetSafePosition(filePath, fileInfo.Length);

                    // Every look counts, including "no new data" and an empty first sight: the
                    // NEXT pass's freshness window is measured from here. Restored bookmarks
                    // deliberately carry no LastCheckedUtc — the first pass after a restart reads
                    // downtime backlog and must never count as fresh.
                    _positionTracker.MarkChecked(filePath, passNowUtc);

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

                    _currentSourceFileName = Path.GetFileName(filePath);
                    _currentPassLinesAreFresh = hadPreviousObservation
                        && lastCheckedUtc > DateTime.MinValue
                        && (passNowUtc - lastCheckedUtc) <= FreshLineMaxAge;

                    // Newest bias-less line of this pass — the calibration anchor. Bias-carrying
                    // lines are skipped: they already state the writer's offset, so they need no
                    // measurement and must not overwrite one.
                    CmTraceLogEntry calibrationAnchor = null;

                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(startPos, SeekOrigin.Begin);

                        var reader = new BoundedLineReader(stream, MaxEntryBytes);
                        var stats = new PassStats();

                        // Buffer for multiline CMTrace entries (e.g. AgentExecutor.log
                        // "write output done. output = ..." spans many lines)
                        StringBuilder multiLineBuffer = null;
                        int multiLineCount = 0;
                        // Set after a capped entry is dropped: its remaining physical lines
                        // are skipped instead of being matched as raw text, until the entry
                        // closes or a new entry begins (so no real entry is lost).
                        bool skippingDroppedEntry = false;
                        // File offset where the entry currently being assembled/processed
                        // begins — the bookmark to fall back to when that entry is NOT
                        // consumed (cancellation, or a held-back unterminated tail).
                        long entryStart = startPos;
                        // Where the next pass resumes. EOF unless an entry is left unconsumed.
                        long resumePos;

                        while (true)
                        {
                            // CancellationToken.None: cancellation is honoured by the explicit
                            // check below (exact bookmark) rather than by an exception out of
                            // the read, which the poll loop would log as an error.
                            var line = await reader.ReadLineAsync(CancellationToken.None);
                            if (line == null)
                            {
                                // EOF. An in-flight multiline entry here is an entry the writer
                                // has not finished — hold it back (see HoldBackTail) instead of
                                // dropping it and raw-matching its remaining lines next pass.
                                if (multiLineBuffer != null && HoldBackTail(filePath, entryStart, fileInfo.Length, passNowUtc))
                                {
                                    _heldTailCount++;
                                    resumePos = entryStart;
                                    break;
                                }
                                resumePos = reader.Position;
                                break;
                            }

                            if (token.IsCancellationRequested)
                            {
                                // Exact bookmark: nothing of this line (or of the multiline
                                // entry it belongs to) has been handled.
                                resumePos = multiLineBuffer != null ? entryStart : reader.LastLineStart;
                                break;
                            }

                            if (multiLineBuffer == null)
                                entryStart = reader.LastLineStart;

                            _linesRead++;

                            // A physical line over the cap is never matched: its captures would be
                            // cut and the regexes would run over attacker-sized input. The capped
                            // prefix is enough to tell whether it opened an entry whose remaining
                            // lines must be skipped rather than raw-matched.
                            if (reader.LastLineTruncated)
                            {
                                stats.OversizedLines++;
                                _oversizedLines++;
                                if (multiLineBuffer != null)
                                {
                                    multiLineBuffer = null;
                                    multiLineCount = 0;
                                    skippingDroppedEntry = true;
                                }
                                else if (line.StartsWith("<![LOG[") && !line.Contains("]LOG]!>"))
                                {
                                    skippingDroppedEntry = true;
                                }
                                continue;
                            }

                            if (!reader.LastLineTerminated && multiLineBuffer == null
                                && HoldBackTail(filePath, reader.LastLineStart, fileInfo.Length, passNowUtc))
                            {
                                // Unterminated single line at EOF: the writer is mid-line.
                                _heldTailCount++;
                                resumePos = reader.LastLineStart;
                                break;
                            }

                            if (skippingDroppedEntry)
                            {
                                // A line opening a new entry ends the skip and is processed
                                // itself — checked BEFORE the close-tag test, because a complete
                                // single-line entry contains both and must not be consumed as
                                // the dropped entry's close.
                                if (line.StartsWith("<![LOG["))
                                    skippingDroppedEntry = false;
                                else
                                {
                                    if (line.Contains("]LOG]!>")) skippingDroppedEntry = false;
                                    continue;
                                }
                            }

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
                                else if (multiLineCount >= MaxMultiLineBufferLines || multiLineBuffer.Length >= MaxEntryBytes)
                                {
                                    // Safety limit — discard to bound memory and parser work. Warning
                                    // (not Debug) so a capped entry is visible in the client log at the
                                    // default level: a real IME entry this large would be news.
                                    _logger.Warning($"ImeLogTracker: discarding multiline CMTrace buffer in {Path.GetFileName(filePath)} after {multiLineCount} lines / {multiLineBuffer.Length} chars (cap {MaxMultiLineBufferLines} lines / {MaxEntryBytes} chars) — entry dropped");
                                    multiLineBuffer = null;
                                    multiLineCount = 0;
                                    skippingDroppedEntry = true;
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
                                if (entry.HasTimestamp && !entry.BiasMinutes.HasValue)
                                    calibrationAnchor = entry;
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
                                await ApplySimulationDelay(ResolveEntryUtc(entry), token);
                            }

                            MatchLine(filePath, line, messageToMatch, entry, stats);
                        }

                        ClearHeldTailIfConsumed(filePath, resumePos);
                        _positionTracker.SetPosition(filePath, resumePos);
                        _stateDirty = true;
                        backlogBytes += Math.Max(0, fileInfo.Length - resumePos);

                        // One line per pass and file, never per hostile line: the counters are
                        // the operator's only trace that matching was skipped or cut short.
                        if (stats.RegexTimeouts > 0 || stats.BudgetBreaks > 0 || stats.OversizedLines > 0)
                        {
                            _logger.Warning(
                                $"ImeLogTracker: {Path.GetFileName(filePath)} pass skipped work — " +
                                $"oversizedLines={stats.OversizedLines} (cap {MaxEntryBytes} bytes), " +
                                $"regexTimeouts={stats.RegexTimeouts}, lineBudgetBreaks={stats.BudgetBreaks}" +
                                (stats.FirstSkippedPatternId != null ? $", firstSkippedPattern={stats.FirstSkippedPatternId}" : string.Empty));
                            RaiseTrackerDegradedOnce(Path.GetFileName(filePath), stats.FirstSkippedPatternId);
                        }

                        // Calibrate AFTER the pass: this pass's lines were resolved with the
                        // offset established previously, at most one poll (100 ms) old. Buffering
                        // the pass to calibrate first is not an option — the first pass of
                        // AppWorkload.log can be hundreds of MB. The cost is a warm-up of one
                        // growing pass, during which lines fall back to the reader zone and are
                        // flagged as such.
                        if (hadPreviousObservation && calibrationAnchor != null)
                            CalibrateFrom(_currentSourceFileName, Path.GetFileName(filePath), calibrationAnchor);
                    }
                }
                catch (FileNotFoundException) { }
                catch (IOException ex)
                {
                    _logger.Debug($"ImeLogTracker: IO error reading {filePath}: {ex.Message}");
                }
            }

            lock (_healthLock)
            {
                _filesTailed = files.Count;
                _backlogBytes = backlogBytes;
            }
        }

        private sealed class PassStats
        {
            public int RegexTimeouts;
            public int BudgetBreaks;
            public int OversizedLines;
            public string FirstSkippedPatternId;
        }

        /// <summary>
        /// Matches one message against every active pattern under the per-line budget. A
        /// pattern that times out is skipped (counted); once the aggregate budget is spent the
        /// remaining patterns are skipped for this line (counted, first skipped ID recorded) —
        /// matches already handled on this line stand.
        /// <para>
        /// The budget is the SUM of the time spent inside <c>Regex.Match</c> on this line — the
        /// cost the budget exists to bound (hostile input × unanchored custom patterns). It is
        /// deliberately not the wall-clock from the first to the last pattern: that window also
        /// contains the match handlers (signal ingress, match-log append, state writes) and
        /// whatever the scheduler does to this thread in between. Session 946ccbd6 (2026-08-30)
        /// recorded two budget breaks on a 1 MB `AppWorkload.log` with no line over 5 KB — the
        /// Hyper-V guest had stalled the whole process for 5–10 s mid-line — and each break
        /// silently skipped the remaining patterns of a genuine line.
        /// </para>
        /// </summary>
        private void MatchLine(string filePath, string line, string messageToMatch, CmTraceLogEntry entry, PassStats stats)
        {
            long matchTicks = 0;
            for (var i = 0; i < _activePatterns.Count; i++)
            {
                var pattern = _activePatterns[i];
                if (matchTicks > LineMatchBudgetTicks)
                {
                    stats.BudgetBreaks++;
                    _budgetBreaks++;
                    if (stats.FirstSkippedPatternId == null) stats.FirstSkippedPatternId = pattern.PatternId;
                    return;
                }

                Match match;
                var matchStarted = Stopwatch.GetTimestamp();
                try
                {
                    match = pattern.Regex.Match(messageToMatch);
                }
                catch (RegexMatchTimeoutException)
                {
                    matchTicks += Stopwatch.GetTimestamp() - matchStarted;
                    stats.RegexTimeouts++;
                    _regexTimeouts++;
                    if (_timeoutLoggedPatternIds.Add(pattern.PatternId))
                        _logger.Debug($"ImeLogTracker: regex timeout for pattern '{pattern.PatternId}' (message length {messageToMatch.Length}) — skipped; further timeouts of this pattern are only counted");
                    continue;
                }
                matchTicks += Stopwatch.GetTimestamp() - matchStarted;

                if (match.Success)
                {
                    RecordPatternHit(pattern.PatternId);
                    WriteMatchLog(filePath, line, pattern.PatternId);
                    HandlePatternMatch(pattern, match, messageToMatch, entry);
                }
            }
        }

        /// <summary>
        /// Decides whether an unterminated tail starting at <paramref name="tailStart"/> is held
        /// back for the writer to finish. True = do not process, resume from the tail start next
        /// pass. False = process it now. A tail longer than <see cref="HeldTailMaxBytes"/> is
        /// never held (re-reading it each poll is the cost to bound); a tail at the same start seen for longer than
        /// <see cref="HeldTailSettle"/> is released so a never-completing tail is bounded to a
        /// handful of re-reads.
        /// </summary>
        private bool HoldBackTail(string filePath, long tailStart, long fileLength, DateTime nowUtc)
        {
            if (fileLength - tailStart > HeldTailMaxBytes)
            {
                _heldTails.Remove(filePath);
                return false;
            }

            HeldTail held;
            if (_heldTails.TryGetValue(filePath, out held) && held.Start == tailStart)
            {
                if (nowUtc - held.FirstHeldUtc >= HeldTailSettle)
                {
                    _heldTails.Remove(filePath);
                    return false;
                }
                return true;
            }

            _heldTails[filePath] = new HeldTail { Start = tailStart, FirstHeldUtc = nowUtc };
            return true;
        }

        private void ClearHeldTailIfConsumed(string filePath, long resumePos)
        {
            HeldTail held;
            if (_heldTails.TryGetValue(filePath, out held) && held.Start != resumePos)
                _heldTails.Remove(filePath);
        }

        /// <summary>
        /// One-shot per session (persisted): the first pass that skipped work raises
        /// <see cref="OnTrackerDegraded"/> so the condition reaches the timeline — the client
        /// log Warning alone is invisible until someone pulls diagnostics.
        /// </summary>
        private void RaiseTrackerDegradedOnce(string fileName, string firstSkippedPatternId)
        {
            lock (_healthLock)
            {
                if (_trackerDegradedFired) return;
                _trackerDegradedFired = true;
            }
            _stateDirty = true;
            try { OnTrackerDegraded?.Invoke(GetHealthSnapshot(), fileName, firstSkippedPatternId); }
            catch (Exception ex) { _logger.Warning($"ImeLogTracker: OnTrackerDegraded handler failed: {ex.Message}"); }
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
            // Not app-mutating, but a stale token-failure replayed from a previous
            // enrollment's log would emit a misleading Warning for THIS session.
            "imetokenfailure",
        };

        private void HandlePatternMatch(CompiledPattern pattern, Match match, string message, CmTraceLogEntry entry)
        {
            LastMatchedPatternId = pattern.PatternId;
            if (entry == null)
            {
                LastMatchedLogTimestamp = null;
                LastMatchedSourceLocalTimestamp = null;
                LastMatchedSourceOffsetMinutes = null;
                LastMatchedSourceOffsetOrigin = CmTraceOffsetOrigin.None;
                LastMatchedMeasuredWriterOffsetMinutes = null;
            }
            else
            {
                CmTraceOffsetOrigin origin;
                int? offsetMinutes;
                LastMatchedLogTimestamp = ResolveEntryUtc(entry, out origin, out offsetMinutes);
                LastMatchedSourceLocalTimestamp = entry.HasTimestamp ? entry.LocalTimestamp : (DateTime?)null;
                LastMatchedSourceOffsetMinutes = offsetMinutes;
                LastMatchedSourceOffsetOrigin = origin;

                // Observational: what the calibrator measured for this file, distinct from what
                // was applied above.
                TimeSpan measuredWriterOffset;
                LastMatchedMeasuredWriterOffsetMinutes =
                    _currentSourceFileName != null
                    && OffsetCalibrator.TryGetOffset(_currentSourceFileName, out measuredWriterOffset)
                        ? (int)measuredWriterOffset.TotalMinutes
                        : (int?)null;
            }

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
                && (UtcNowProvider() - NormalizeUtc(ResolveEntryUtc(entry))) > HistoricReplayThreshold;
            if (isStaleReplayLine && AppMutatingActions.Contains(pattern.Action?.ToLower() ?? string.Empty))
            {
                _logger.Debug($"ImeLogTracker: skipped app action '{pattern.Action}' for historic line ({ResolveEntryUtc(entry):o})");
                return;
            }

            // A token success resolves any pending token failure (grace-window model, see
            // HandleImeTokenFailure). Matched on our own stable pattern ID — the line also
            // drives espPhaseDetected, whose handler must stay unaware of token semantics.
            // Placed after the staleness gate so a success replayed from a previous
            // enrollment's log cannot clear a genuine current outage.
            if (!isStaleReplayLine && string.Equals(pattern.PatternId, "IME-TOKEN-SUCCESS", StringComparison.OrdinalIgnoreCase))
                ClearPendingTokenFailure();

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
                        {
                            lock (_healthLock) { _imeAgentVersionSeen = version; }
                            OnImeAgentVersion?.Invoke(version);
                        }
                        break;

                    case "imetokenfailure":
                        HandleImeTokenFailure(match.Groups["errorCode"]?.Value, message);
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

                    case "usersessionzeroapps":
                        // IME found no targeted user apps — its zero-app branch skips the
                        // "Completed user session" line, so this is the equivalent evidence.
                        HandleUserSessionZeroApps();
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
            // The test seam hands in an already-resolved UTC instant, so it takes the same route a
            // writer-declared bias does: TimestampUtc set, no zone left to guess.
            var entry = sourceTimestampUtc.HasValue
                ? new CmTraceLogEntry
                {
                    TimestampUtc = sourceTimestampUtc.Value,
                    LocalTimestamp = DateTime.SpecifyKind(sourceTimestampUtc.Value, DateTimeKind.Unspecified),
                    HasTimestamp = true,
                    Message = message,
                }
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

        /// <summary>
        /// Resolve a parsed CMTrace entry to UTC.
        ///
        /// <para>
        /// Order of preference: a writer-declared bias (authoritative) &gt; this process's own zone
        /// (a fallback carrying a known defect, see
        /// <see cref="CmTraceLogParser.ResolveUtcAssumingReaderZone"/>) &gt; the agent clock for a
        /// line with no parseable timestamp.
        /// </para>
        ///
        /// <para>
        /// The reader-zone step is where the tracker is still wrong when IME's process holds a
        /// different zone belief than ours. It is preserved verbatim here so that splitting the
        /// parser changes no behaviour; the measured offset from <c>CmTraceOffsetCalibrator</c>
        /// slots in at exactly this point in the follow-up change.
        /// </para>
        /// </summary>
        private DateTime ResolveEntryUtc(CmTraceLogEntry entry)
        {
            CmTraceOffsetOrigin origin;
            int? offsetMinutes;
            return ResolveEntryUtc(entry, out origin, out offsetMinutes);
        }

        /// <summary>
        /// Resolve a parsed entry to UTC and report HOW the offset was obtained, so the emitting
        /// side can attach the evidence (P8). See <see cref="CmTraceOffsetOrigin"/>.
        /// </summary>
        private DateTime ResolveEntryUtc(CmTraceLogEntry entry, out CmTraceOffsetOrigin origin, out int? offsetMinutes)
        {
            origin = CmTraceOffsetOrigin.None;
            offsetMinutes = null;

            if (entry == null) return UtcNowProvider();

            // Writer declared its own offset — nothing left to measure.
            if (entry.TimestampUtc.HasValue)
            {
                origin = CmTraceOffsetOrigin.Bias;
                // Bias uses the GetTimeZoneInformation convention (UTC = local + bias); report the
                // value in the same sense as a measured offset (local = UTC + offset).
                offsetMinutes = entry.BiasMinutes.HasValue ? -entry.BiasMinutes.Value : (int?)null;
                return entry.TimestampUtc.Value;
            }

            if (!entry.HasTimestamp) return UtcNowProvider();

            // ERA-AWARE RESOLUTION (2026-08-20, second attempt): a FRESH line anchors ITSELF.
            //
            // The first attempt (e9dba11b, reverted in 04b1a7c6) applied one measured offset per
            // FILE — wrong, because a single log file holds lines from multiple writer eras:
            // AgentExecutor.log is written by short-lived child processes whose zone belief
            // flips per process, interleaved in one file (fixture
            // tests/fixtures/cmtrace-logs/agentexecutor-two-writer-eras-v1.cmtrace). Any
            // cross-line anchor — per file, or nearest-in-write-order — inherits that trap.
            //
            // Per-line self-anchoring does not: at a 100 ms poll every line of a growing pass
            // was written essentially "now", so its own distance to the agent clock IS its
            // writer's offset, era by era. The line's timestamp still contributes the sub-poll
            // precision and ordering (two lines 6 ms apart stay 6 ms apart — a plain
            // occurredAt=now could not do that), while the offset grid absorbs poll latency.
            // Freshness is load-bearing (see FreshLineMaxAge) — backlog passes, restart
            // catch-up and replay logs must never anchor, because an old line's AGE can round
            // onto the grid.
            if (_currentPassLinesAreFresh)
            {
                TimeSpan lineOffset;
                if (CmTraceOffsetCalibrator.TryMeasureOffset(entry.LocalTimestamp, UtcNowProvider(), out lineOffset))
                {
                    origin = CmTraceOffsetOrigin.LineAnchored;
                    offsetMinutes = (int)lineOffset.TotalMinutes;
                    return DateTime.SpecifyKind(entry.LocalTimestamp - lineOffset, DateTimeKind.Utc);
                }
            }

            // Fallback for everything not provably fresh: assume the reader's zone, UNIFORMLY.
            // Wrong in absolute terms whenever the writer held a different belief, but
            // self-consistent — both ends of a duration are wrong by the same amount, so derived
            // durations stay right. That asymmetry is the lesson of the 04b1a7c6 revert:
            // partially corrected is strictly worse than uniformly wrong. Origin stays None so
            // the emitted event says so.
            var readerZoneOffset = TimeZoneInfo.Local.GetUtcOffset(entry.LocalTimestamp);
            offsetMinutes = (int)readerZoneOffset.TotalMinutes;
            return CmTraceLogParser.ResolveUtcAssumingReaderZone(entry.LocalTimestamp);
        }

        // Field-forensics (tripwire, 2026-08-20): sessions e9753578 and 067797d8 both logged a
        // measurement whose LABEL and ANCHOR could not have come from the same file iteration —
        // 067797d8's anchor value (19:51:26.948) does not even exist in ANY log file on the
        // device, and IntuneManagementExtension.log never produced its expected first
        // measurement at all. Mechanism unexplained; resolution is immune (per-line anchoring),
        // but the observational layer must convict the culprit on the next occurrence. Hence:
        // the stream's file name travels alongside the label (a mismatch is THE smoking gun),
        // and the first rejected anchor per file becomes visible instead of silent.
        private readonly HashSet<string> _calibrationRejectLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Feed the pass's newest bias-less line to the calibrator and report a measured offset
        /// that disagrees with this process's own zone — that disagreement is precisely the
        /// condition that used to corrupt every IME-derived timestamp silently.
        /// </summary>
        /// <param name="sourceFileName">The calibration key (<c>_currentSourceFileName</c>).</param>
        /// <param name="streamFileName">The file the enclosing iteration actually read. Must equal
        /// <paramref name="sourceFileName"/> — logged loudly when it does not (see tripwire note).</param>
        private void CalibrateFrom(string sourceFileName, string streamFileName, CmTraceLogEntry anchor)
        {
            if (!string.Equals(sourceFileName, streamFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warning(
                    $"ImeLogTracker: CALIBRATION LABEL MISMATCH — key='{sourceFileName}' but the iteration read '{streamFileName}' " +
                    $"(anchor local={anchor.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fffffff}). Skipping this anchor; see tripwire note.");
                return;
            }

            TimeSpan previous;
            var hadOffset = OffsetCalibrator.TryGetOffset(sourceFileName, out previous);

            if (!OffsetCalibrator.TryCalibrate(sourceFileName, anchor.LocalTimestamp, UtcNowProvider()))
            {
                // Once per file: why a file that visibly grows never produces a measurement.
                // 067797d8 ran 24 s of growing IME.log passes with no measurement at all — this
                // silence is exactly what made the phenomenon undiagnosable from the log.
                if (_calibrationRejectLogged.Add(sourceFileName))
                {
                    _logger?.Debug(
                        $"ImeLogTracker: {sourceFileName} anchor rejected by calibrator " +
                        $"(anchor local={anchor.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fffffff}, now={UtcNowProvider():HH:mm:ss.fff}Z) — first rejection for this file, further ones stay silent.");
                }
                return;
            }

            TimeSpan measured;
            if (!OffsetCalibrator.TryGetOffset(sourceFileName, out measured)) return;
            if (hadOffset && measured == previous) return;

            // The anchor's local timestamp is part of the record ON PURPOSE (tripwire, 2026-08-20):
            // session e9753578 logged "+02:00 measured for IntuneManagementExtension.log" although
            // that file provably held no +2-era line — an anchor/label pairing the committed code
            // could not be shown to produce. Should it ever happen again, the logged anchor value
            // itself will prove (or refute) the crossing without needing the device's log files.
            var readerZoneOffset = TimeZoneInfo.Local.GetUtcOffset(UtcNowProvider());
            if (measured == readerZoneOffset)
            {
                _logger?.Debug(
                    $"ImeLogTracker: {sourceFileName} writer offset measured {measured} (matches this process, anchor local={anchor.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff}).");
                return;
            }

            _logger?.Info(
                $"ImeLogTracker: {sourceFileName} writer offset measured {measured}, this process believes {readerZoneOffset} " +
                $"(anchor local={anchor.LocalTimestamp:yyyy-MM-ddTHH:mm:ss.fff}). Measurement is observational — resolution anchors per line.");
        }

    }
}
