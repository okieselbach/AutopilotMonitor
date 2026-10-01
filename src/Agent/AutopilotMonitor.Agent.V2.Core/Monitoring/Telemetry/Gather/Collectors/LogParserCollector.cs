using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;

using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Shared.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class LogParserCollector : IGatherRuleCollector
    {
        public string CollectorType => "logparser";

        /// <summary>A wildcard target reads at most this many files per run, the newest first.</summary>
        internal const int MaxFilesPerRun = 20;

        /// <summary>Lines one run reads from one file unless the rule sets <c>maxLines</c>.</summary>
        internal const int DefaultMaxLines = 1000;

        /// <summary>The IME tracker's line bound: a longer physical line is skipped, never matched.</summary>
        internal const int MaxLineBytes = 32 * 1024 * 1024;

        /// <summary>
        /// Executes the log parser rule. Returns null because logparser emits events directly
        /// via context.OnEventCollected rather than returning a single result dictionary.
        /// </summary>
        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var filePath = rule.Target;
            if (string.IsNullOrEmpty(filePath))
            {
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser, "empty target — nothing to parse");
                return null;
            }

            // Expand custom tokens (%LOGGED_ON_USER_PROFILE%) and standard environment variables
            var userProfilePath = UserProfileResolver.ContainsUserProfileToken(filePath)
                ? UserProfileResolver.GetLoggedOnUserProfilePath() : null;
            filePath = UserProfileResolver.ExpandCustomTokens(filePath, userProfilePath);
            if (filePath == null)
            {
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    "target contains %LOGGED_ON_USER_PROFILE% but no user is logged on — skipped this run");
                return null; // Token present but no user logged on — skip silently
            }

            // Apply IME log path override if set. The override comes from the local
            // --ime-log-path flag only (never from remote config), so the operator
            // running it is already a local admin — it relaxes the allowlist the same
            // way unrestricted mode does, while the hard blocks below still apply.
            var localOverride = false;
            if (!string.IsNullOrEmpty(context.ImeLogPathOverride))
            {
                var fileName = Path.GetFileName(filePath);
                filePath = Path.Combine(context.ImeLogPathOverride, fileName);
                localOverride = true;
            }

            var relaxAllowlist = context.UnrestrictedMode || localOverride;

            // Guard: only allow enrollment-relevant file paths. Checked before resolution
            // so a blocked target is reported even when no file matches it. A wildcard
            // target is checked by its directory — Path.GetFullPath rejects '*' and '?'.
            var guardPath = HasWildcard(Path.GetFileName(filePath))
                ? Path.GetDirectoryName(filePath)
                : filePath;
            if (!GatherRuleGuards.IsFilePathAllowed(guardPath, relaxAllowlist, userProfilePath))
            {
                context.EmitSecurityWarning(rule, "logparser", filePath);
                return null;
            }

            // Get parameters
            string patternStr;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("pattern", out patternStr) ||
                string.IsNullOrEmpty(patternStr))
            {
                context.Logger.Warning($"LogParser rule {rule.RuleId} has no 'pattern' parameter");
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageError,
                    "missing 'pattern' parameter — rule can never match");
                return null;
            }

            string trackPositionStr;
            bool trackPosition = true;
            if (rule.Parameters.TryGetValue("trackPosition", out trackPositionStr))
                bool.TryParse(trackPositionStr, out trackPosition);

            // A value that is no positive number keeps the default: a run capped at 0 lines would
            // never advance its position, and the rule would read nothing for the whole session.
            string maxLinesStr;
            int maxLines = DefaultMaxLines;
            if (rule.Parameters.TryGetValue("maxLines", out maxLinesStr))
            {
                int parsedMaxLines;
                if (int.TryParse(maxLinesStr, out parsedMaxLines) && parsedMaxLines > 0)
                    maxLines = parsedMaxLines;
                else
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"maxLines '{maxLinesStr}' is not a positive number — using {DefaultMaxLines}");
            }

            // Determine format: "cmtrace" (default) or "text" for plain text logs
            string formatStr;
            bool isTextMode = false;
            if (rule.Parameters.TryGetValue("format", out formatStr))
                isTextMode = string.Equals(formatStr, "text", StringComparison.OrdinalIgnoreCase);

            Regex pattern;
            try
            {
                pattern = new Regex(patternStr, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                context.Logger.Warning($"LogParser rule {rule.RuleId} has invalid regex: {ex.Message}");
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageError,
                    $"invalid regex pattern '{patternStr}' — rule can never match: {ex.Message}");
                return null;
            }

            // Resolve file paths — supports wildcards (* and ?) in the filename portion
            var resolvedPaths = ResolveLogPaths(filePath, rule.RuleId, context);
            if (resolvedPaths.Count == 0)
            {
                context.Logger.Debug($"LogParser rule {rule.RuleId}: no files found for: {filePath}");
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    $"no files matched target {filePath}" + (HasWildcard(Path.GetFileName(filePath)) ? " (wildcard)" : "") + " — nothing to parse");
                return null;
            }

            foreach (var resolvedPath in resolvedPaths)
            {
                // Re-check each resolved path: wildcard expansion can surface files
                // reached through a junction that the directory check did not cover.
                if (!GatherRuleGuards.IsFilePathAllowed(resolvedPath, relaxAllowlist, userProfilePath))
                {
                    context.EmitSecurityWarning(rule, "logparser", resolvedPath);
                    continue;
                }

                ProcessLogFile(resolvedPath, rule, pattern, trackPosition, maxLines, isTextMode, context);
            }

            return null;
        }

        // Per-match trace lines are capped per file per run — the per-file summary line
        // still carries the full match count, this only bounds the per-match detail.
        private const int MaxTracedMatchesPerFile = 10;

        /// <summary>
        /// Writes one debug-trace line per regex match: the line number, the matched text,
        /// and every capture group that will land in the emitted event (same "0" exclusion
        /// as the event data). Stops after <see cref="MaxTracedMatchesPerFile"/> matches.
        /// </summary>
        private static void TraceMatch(GatherRuleContext context, GatherRule rule, string fileName,
            long lineNumber, Match match, Regex pattern, int matchNumber)
        {
            if (matchNumber > MaxTracedMatchesPerFile)
            {
                if (matchNumber == MaxTracedMatchesPerFile + 1)
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"{fileName}: further matches not traced individually (cap {MaxTracedMatchesPerFile}/file/run) — see the per-file summary for the total");
                return;
            }

            var sb = new StringBuilder();
            sb.Append(fileName).Append(": line ").Append(lineNumber)
              .Append(" matched \"").Append(TruncateMessage(match.Value, 200)).Append('"');

            var first = true;
            foreach (var groupName in pattern.GetGroupNames())
            {
                if (groupName == "0") continue;
                var group = match.Groups[groupName];
                if (!group.Success) continue;
                sb.Append(first ? " — groups: " : ", ");
                first = false;
                sb.Append(groupName).Append("=\"").Append(TruncateMessage(group.Value, 100)).Append('"');
            }

            context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser, sb.ToString());
        }

        private static bool HasWildcard(string fileNamePart)
            => !string.IsNullOrEmpty(fileNamePart) &&
               (fileNamePart.Contains("*") || fileNamePart.Contains("?"));

        private static List<string> ResolveLogPaths(string filePath, string ruleId, GatherRuleContext context)
        {
            var fileNamePart = Path.GetFileName(filePath);

            // No wildcards — single file
            if (!HasWildcard(fileNamePart))
            {
                if (File.Exists(filePath))
                    return new List<string> { filePath };
                return new List<string>();
            }

            // Wildcard expansion
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return new List<string>();

            try
            {
                // Return matched files sorted by last write time (newest first), capped at MaxFilesPerRun
                var allMatches = Directory.GetFiles(directory, fileNamePart);
                if (allMatches.Length > MaxFilesPerRun)
                    context.DebugLog(ruleId, GatherRuleDebugLog.StageLogParser,
                        $"wildcard matched {allMatches.Length} files — capped to the {MaxFilesPerRun} newest");
                return allMatches
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .Take(MaxFilesPerRun)
                    .ToList();
            }
            catch (Exception ex)
            {
                context.Logger.Warning($"LogParser rule {ruleId}: wildcard expansion failed for {filePath}: {ex.Message}");
                context.DebugLog(ruleId, GatherRuleDebugLog.StageError, $"wildcard expansion failed for {filePath}: {ex.Message}");
                return new List<string>();
            }
        }

        private static void ProcessLogFile(string filePath, GatherRule rule, Regex pattern,
            bool trackPosition, int maxLines, bool isTextMode, GatherRuleContext context)
        {
            var fileName = Path.GetFileName(filePath);
            try
            {
                var state = context.SessionState;
                var known = trackPosition ? state.PeekLogPosition(rule.RuleId, filePath) : null;

                // Nothing new since the last run — decided from the length alone, without opening the file.
                var length = new FileInfo(filePath).Length;
                if (known != null && known.Position == length)
                {
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"{fileName}: no new content (position {known.Position} >= length {length}) — trackPosition=true, nothing re-read" +
                        (known.FromPreviousRun ? "; position saved by a previous agent run" : ""));
                    return;
                }

                // A last line without a line end is usually one its writer is still in. An interval
                // rule leaves it for its next run, once. A one-shot trigger may have no next run, so it
                // reads the line now but saves the position before it: the next run reads it again,
                // whole — at worst a match repeats, it is never split.
                var isInterval = string.Equals(rule.Trigger, "interval", StringComparison.OrdinalIgnoreCase);
                var mayHoldTail = trackPosition && isInterval;
                var tailReadAgainNextRun = false;

                int matchCount = 0;
                int linesRead = 0;
                int committedLines = 0;
                int parseFailCount = 0;
                int timeoutCount = 0;
                int oversizedCount = 0;
                long startPosition;
                long endPosition;
                long lineNumberBase;
                long fileLength;
                long heldTailStart = -1;
                LogFileHead head;
                LogTextEncoding encoding;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fileLength = stream.Length;
                    head = LogFileHead.Read(stream);
                    encoding = head.Encoding;

                    LogReadStart start;
                    startPosition = encoding.AlignDown(GatherRuleSessionState.ResolveStart(known, fileLength, head, out start));
                    lineNumberBase = start == LogReadStart.Continue ? known.LineNumber : 0;
                    TraceReadStart(context, rule, fileName, known, start, fileLength);

                    stream.Seek(startPosition, SeekOrigin.Begin);
                    var reader = new LogLineReader(stream, encoding, MaxLineBytes);
                    endPosition = startPosition;

                    while (linesRead < maxLines)
                    {
                        var line = reader.ReadLine();
                        if (line == null)
                        {
                            // End of file. A "\n" the reader skipped behind the "\r" the previous run
                            // ended on is consumed too — unless a kept tail must be read again.
                            if (!tailReadAgainNextRun)
                                endPosition = reader.Position;
                            break;
                        }

                        var unfinished = !reader.LastLineTerminated;
                        if (unfinished && mayHoldTail
                            && (known == null || known.HeldTailStart != reader.LastLineStart))
                        {
                            heldTailStart = reader.LastLineStart;
                            break;
                        }

                        linesRead++;
                        var lineNumber = lineNumberBase + linesRead;
                        if (unfinished && trackPosition && !isInterval)
                        {
                            tailReadAgainNextRun = true;
                        }
                        else
                        {
                            endPosition = reader.Position;
                            committedLines = linesRead;
                        }

                        if (reader.LastLineTruncated)
                        {
                            oversizedCount++;
                            continue;
                        }

                        if (isTextMode)
                        {
                            // Text mode: match regex directly against the raw line
                            Match match;
                            try
                            {
                                match = pattern.Match(line);
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                timeoutCount++;
                                continue;
                            }
                            if (!match.Success)
                                continue;

                            var data = new Dictionary<string, object>();
                            foreach (var groupName in pattern.GetGroupNames())
                            {
                                if (groupName == "0") continue;
                                var group = match.Groups[groupName];
                                if (group.Success)
                                    data[groupName] = group.Value;
                            }

                            data["logLine"] = TruncateMessage(line, 500);
                            data["logLineNumber"] = lineNumber;
                            data["logFile"] = fileName;
                            data["ruleId"] = rule.RuleId;
                            data["ruleTitle"] = rule.Title;
                            GatherRuleExecutor.StampRuleMarkers(rule, data);

                            var eventType = !string.IsNullOrEmpty(rule.OutputEventType)
                                ? rule.OutputEventType
                                : "logparser_match";
                            var severity = GatherRuleContext.ParseSeverity(rule.OutputSeverity);

                            context.OnEventCollected(new EnrollmentEvent
                            {
                                SessionId = context.SessionId,
                                TenantId = context.TenantId,
                                Timestamp = DateTime.UtcNow,
                                EventType = eventType,
                                Severity = severity,
                                Source = GatherRuleExecutor.SourceName,
                                Message = $"Gather: {rule.Title}",
                                Data = data
                            });

                            matchCount++;
                            TraceMatch(context, rule, fileName, lineNumber, match, pattern, matchCount);
                        }
                        else
                        {
                            // CMTrace mode: parse line as CMTrace format, match regex against message
                            CmTraceLogEntry entry;
                            if (!CmTraceLogParser.TryParseLine(line, out entry))
                            {
                                parseFailCount++;
                                continue;
                            }

                            Match match;
                            try
                            {
                                match = pattern.Match(entry.Message);
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                timeoutCount++;
                                continue;
                            }
                            if (!match.Success)
                                continue;

                            var data = new Dictionary<string, object>();
                            foreach (var groupName in pattern.GetGroupNames())
                            {
                                if (groupName == "0") continue;
                                var group = match.Groups[groupName];
                                if (group.Success)
                                    data[groupName] = group.Value;
                            }

                            // Gather-rule logs are arbitrary files this collector does not
                            // tail, so no writer offset can be measured for them; the
                            // reader-zone fallback carries its known defect (see
                            // ResolveUtcAssumingReaderZone). Behaviour unchanged from before
                            // the parser split.
                            var entryUtc = entry.TimestampUtc
                                ?? (entry.HasTimestamp
                                    ? CmTraceLogParser.ResolveUtcAssumingReaderZone(entry.LocalTimestamp)
                                    : DateTime.UtcNow);
                            data["logTimestamp"] = entryUtc.ToString("o");
                            data["logComponent"] = entry.Component;
                            data["logType"] = entry.Type;
                            data["logMessage"] = TruncateMessage(entry.Message, 500);
                            data["logFile"] = fileName;
                            data["ruleId"] = rule.RuleId;
                            data["ruleTitle"] = rule.Title;
                            GatherRuleExecutor.StampRuleMarkers(rule, data);

                            var eventType = !string.IsNullOrEmpty(rule.OutputEventType)
                                ? rule.OutputEventType
                                : "logparser_match";
                            var severity = MapCmTraceTypeToSeverity(entry.Type, rule.OutputSeverity);

                            context.OnEventCollected(new EnrollmentEvent
                            {
                                SessionId = context.SessionId,
                                TenantId = context.TenantId,
                                Timestamp = entryUtc,
                                EventType = eventType,
                                Severity = severity,
                                Source = GatherRuleExecutor.SourceName,
                                Message = $"Gather: {rule.Title}",
                                Data = data
                            });

                            matchCount++;
                            TraceMatch(context, rule, fileName, lineNumber, match, pattern, matchCount);
                        }
                    }
                }

                // Saved only now, after every event of this file went out: a process that dies
                // before this line repeats the file's new lines after the restart, never skips them.
                if (trackPosition)
                    state.CommitLogPosition(rule.RuleId, filePath, endPosition, lineNumberBase + committedLines, head, heldTailStart);

                if (matchCount > 0)
                    context.Logger.Debug($"LogParser rule {rule.RuleId}: {matchCount} matches from {linesRead} lines in {fileName}");

                // Per-file outcome — always written so a "matched 0" run is visible too.
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    $"{fileName}: read {linesRead} lines from position {startPosition}->{endPosition}, " +
                    $"matched {matchCount}, parseFailures={parseFailCount}, regexTimeouts={timeoutCount}, mode={(isTextMode ? "text" : "cmtrace")}, encoding={encoding.Name}" +
                    (oversizedCount > 0 ? $", skipped {oversizedCount} line(s) over {MaxLineBytes / (1024 * 1024)} MB" : ""));

                if (heldTailStart >= 0)
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"{fileName}: last line has no line end yet — left for the next run, read then even if still unfinished");
                else if (tailReadAgainNextRun)
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"{fileName}: last line has no line end yet — read now and again by the next run, position kept before it");

                if (!isTextMode && linesRead > 0 && parseFailCount == linesRead)
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        "every line failed CMTrace parsing — if this is a plain-text log, set parameter format=text");

                if (linesRead == maxLines && endPosition < fileLength)
                    context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                        $"stopped at maxLines={maxLines} — remaining content is deferred to the next run");
            }
            catch (Exception ex)
            {
                context.Logger.Warning($"LogParser rule {rule.RuleId} failed reading {filePath}: {ex.Message}");
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageError, $"failed reading {filePath}: {ex}");
            }
        }

        private static void TraceReadStart(GatherRuleContext context, GatherRule rule, string fileName,
            LogPositionSnapshot known, LogReadStart start, long fileLength)
        {
            if (start == LogReadStart.Shrunk)
            {
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    $"{fileName}: file is shorter than the saved position (length {fileLength} < {known.Position}) — truncated or recreated, reading from the beginning");
            }
            else if (start == LogReadStart.Replaced)
            {
                context.Logger.Info($"LogParser rule {rule.RuleId}: {fileName} was replaced since the last read — reading it from the beginning");
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    $"{fileName}: file start changed since the last read (replaced or rewritten) — saved position {known.Position} discarded, reading from the beginning");
            }
            else if (start == LogReadStart.Continue && known.FromPreviousRun)
            {
                context.DebugLog(rule.RuleId, GatherRuleDebugLog.StageLogParser,
                    $"{fileName}: continuing at position {known.Position} (after line {known.LineNumber}) saved by a previous agent run");
            }
        }

        private static EventSeverity MapCmTraceTypeToSeverity(int cmTraceType, string ruleOverride)
        {
            // If the rule specifies a severity, use it
            if (!string.IsNullOrEmpty(ruleOverride))
            {
                switch (ruleOverride.ToLower())
                {
                    case "debug": return EventSeverity.Debug;
                    case "info": return EventSeverity.Info;
                    case "warning": return EventSeverity.Warning;
                    case "error": return EventSeverity.Error;
                    case "critical": return EventSeverity.Critical;
                }
            }

            // Otherwise derive from CMTrace log type
            switch (cmTraceType)
            {
                case 2: return EventSeverity.Warning;
                case 3: return EventSeverity.Error;
                default: return EventSeverity.Info;
            }
        }

        private static string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message;
            return message.Substring(0, maxLength) + "...";
        }
    }
}
