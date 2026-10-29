using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Tests a logparser gather-rule regex against pasted sample log lines with the AGENT's
    /// exact matching semantics — the ".NET dry-run" for logparser rules, which cannot be
    /// dry-run against a session (they execute on devices).
    ///
    /// Parity is the entire point: authors test regexes in whatever engine their AI client
    /// has (JS, PHP, Python) and .NET behaves subtly differently (this endpoint exists
    /// because exactly that happened in the field). Mirrored semantics, keep in lock-step
    /// with LogParserCollector (agent):
    /// - Regex options: 1s match timeout, NO IgnoreCase — logparser matching is
    ///   case-SENSITIVE (unlike analyze-rule regex conditions). The agent adds
    ///   RegexOptions.Compiled; that flag changes speed only, never a match result, so the
    ///   one-shot pattern here runs interpreted (see <see cref="BuildAgentRegex"/>).
    /// - First match per line (Regex.Match, not Matches).
    /// - format "cmtrace" (default): CmTraceLogParser.TryParseLine first (shared code, the
    ///   same implementation the agent runs), regex against the parsed MESSAGE only.
    /// - format "text": regex against the raw line.
    /// - Captured data: every named/numbered group except "0", unsuccessful groups omitted.
    ///
    /// No tenant data is touched; the endpoint is pure compute on the request body — and that
    /// compute is caller-controlled regex work on the shared Function App (ingest and every
    /// tenant's portal), so it is bounded three ways (D-379): the first match timeout or the
    /// <see cref="EvaluationBudget"/> ends the test, the invocation token (client gone, host
    /// stopping) ends it between two lines, and <see cref="TestLogPatternGate"/> caps how many
    /// tests run per instance. Each bound closes one carrier of the reported abuse; none of
    /// them changes a match result.
    /// </summary>
    public class TestLogPatternFunction
    {
        private readonly ILogger<TestLogPatternFunction> _logger;
        private readonly TestLogPatternGate _gate;

        public TestLogPatternFunction(ILogger<TestLogPatternFunction> logger, TestLogPatternGate gate)
        {
            _logger = logger;
            _gate = gate;
        }

        internal const int MaxSampleLines = 200;
        internal const int MaxLineLength = 8192;

        /// <summary>Same cap the MCP schema enforces (rules.ts); checked before any Regex is built.</summary>
        internal const int MaxPatternLength = 2000;

        /// <summary>The agent's per-match timeout (LogParserCollector) — parity, not a tuning knob.</summary>
        internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Wall-clock budget for one whole test. A legitimate pattern finishes 200 lines in
        /// milliseconds; the worst case per request is this budget plus one running match
        /// (≤ <see cref="MatchTimeout"/>), which is what <see cref="RetryAfterSeconds"/> promises.
        /// </summary>
        internal static readonly TimeSpan EvaluationBudget = TimeSpan.FromSeconds(2);

        private const int RetryAfterSeconds = 3;

        internal const string StopReasonRegexTimeout = "regex_timeout";
        internal const string StopReasonTimeBudget = "time_budget";
        internal const string StopReasonCancelled = "cancelled";

        [Function("TestLogPattern")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/gather/test-pattern")] HttpRequestData req,
            CancellationToken cancellationToken)
        {
            // Authentication + TenantAdminOrGlobalReader authorization enforced by PolicyEnforcementMiddleware.

            var read = await req.ReadAsync<TestLogPatternRequest>(1_048_576);
            if (read.Error != null) return read.Error;
            var request = read.Value!;

            var validationError = ValidateRequest(request);
            if (validationError != null)
            {
                return await req.BadRequestAsync(validationError);
            }

            var isTextMode = string.Equals(request!.Format, "text", StringComparison.OrdinalIgnoreCase);

            // The slot is taken BEFORE the regex is built: parsing a hostile pattern is work too.
            var slot = _gate.TryEnter();
            if (slot == null)
            {
                _logger.LogWarning(
                    "Log-pattern test refused: {Limit} evaluations already running on this instance",
                    _gate.Limit);
                return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
                    "too many pattern tests are running right now — retry in a few seconds",
                    retryAfterSeconds: RetryAfterSeconds);
            }

            using (slot)
            {
                Regex pattern;
                try
                {
                    pattern = BuildAgentRegex(request.Pattern!);
                }
                catch (ArgumentException ex)
                {
                    return await req.BadRequestAsync($"Invalid regex pattern (this is the agent's .NET engine speaking): {ex.Message}");
                }

                var clock = Stopwatch.StartNew();
                var result = EvaluatePattern(pattern, isTextMode, request.SampleLines!, EvaluationBudget, cancellationToken);
                clock.Stop();

                if (result.StopReason != null)
                {
                    // Warning so it reaches App Insights: one hit is an author with a broken
                    // pattern, a cluster from one caller is load aimed at this endpoint.
                    _logger.LogWarning(
                        "Log-pattern test stopped: {Reason} after {Evaluated}/{Total} lines in {ElapsedMs} ms (pattern length {PatternLength}, mode={Mode})",
                        result.StopReason, result.Lines.Count, request.SampleLines!.Count, clock.ElapsedMilliseconds,
                        request.Pattern!.Length, isTextMode ? "text" : "cmtrace");
                }
                else
                {
                    _logger.LogInformation(
                        "Log-pattern test: {LineCount} lines, {MatchCount} matched, {ParseFailures} parse failures, mode={Mode}",
                        request.SampleLines!.Count, result.MatchCount, result.ParseFailureCount, isTextMode ? "text" : "cmtrace");
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new TestLogPatternResponse { Success = true, Format = isTextMode ? "text" : "cmtrace", Result = result });
                return response;
            }
        }

        internal static string? ValidateRequest(TestLogPatternRequest? request)
        {
            if (request == null || string.IsNullOrEmpty(request.Pattern))
                return "pattern is required";
            if (request.Pattern.Length > MaxPatternLength)
                return $"pattern exceeds {MaxPatternLength} characters";
            if (request.SampleLines == null || request.SampleLines.Count == 0)
                return "sampleLines is required — paste raw lines from the log file";
            if (request.SampleLines.Count > MaxSampleLines)
                return $"too many sampleLines ({request.SampleLines.Count}) — send at most {MaxSampleLines} representative lines";
            if (request.SampleLines.Any(l => l != null && l.Length > MaxLineLength))
                return $"a sample line exceeds {MaxLineLength} characters";
            if (!string.IsNullOrEmpty(request.Format)
                && !string.Equals(request.Format, "cmtrace", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(request.Format, "text", StringComparison.OrdinalIgnoreCase))
                return "format must be \"cmtrace\" (default) or \"text\"";
            return null;
        }

        /// <summary>
        /// The agent's construction (LogParserCollector) minus RegexOptions.Compiled: the options
        /// that carry semantics are identical (no IgnoreCase, no Multiline/Singleline) and so is
        /// the match timeout. Compiled emits IL and JITs it for a pattern that runs once over at
        /// most 200 lines — on caller-controlled input that costs more than it saves. Throws
        /// <see cref="ArgumentException"/> for an invalid pattern, exactly like the agent.
        /// </summary>
        internal static Regex BuildAgentRegex(string pattern) => new(pattern, RegexOptions.None, MatchTimeout);

        /// <summary>
        /// Per-line evaluation mirroring LogParserCollector's inner loop: one row per evaluated
        /// sample line so the author sees exactly which lines matched, which failed CMTrace
        /// parsing, and which data fields the emitted event would carry.
        ///
        /// The pattern is untrusted work: the FIRST match timeout ends the test (one timed-out
        /// line already proves catastrophic backtracking — the agent would emit nothing for such
        /// lines, and 199 more seconds of it would add no information), the budget ends it when
        /// every line is merely slow, and the token ends it between two lines. Lines after the
        /// stop get no row (the MCP result is size-capped); <see cref="LogPatternTestResult.StopReason"/>
        /// and a note say where and why.
        /// </summary>
        internal static LogPatternTestResult EvaluatePattern(
            Regex pattern, bool isTextMode, IReadOnlyList<string> lines, TimeSpan budget, CancellationToken cancellationToken)
        {
            var result = new LogPatternTestResult();
            var clock = Stopwatch.StartNew();

            for (var i = 0; i < lines.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Stop(result, StopReasonCancelled,
                        i == 0 ? "evaluation cancelled before the first line" : $"evaluation cancelled after line {i}",
                        i, lines.Count);
                    break;
                }
                // Checked before every line but the first: the previous line spent the budget.
                if (i > 0 && clock.Elapsed >= budget)
                {
                    Stop(result, StopReasonTimeBudget,
                        $"evaluation stopped after line {i}: the test exceeded its {budget.TotalSeconds:0.#} s budget — a pattern this slow would strain the device too; anchor it (^) and avoid unbounded .* scans",
                        i, lines.Count);
                    break;
                }

                var line = lines[i] ?? string.Empty;
                var row = new LogPatternLineResult { LineNumber = i + 1 };
                result.Lines.Add(row);

                string textToMatch;
                if (isTextMode)
                {
                    textToMatch = line;
                }
                else
                {
                    if (!CmTraceLogParser.TryParseLine(line, out var entry))
                    {
                        row.Outcome = "parse_failed";
                        result.ParseFailureCount++;
                        continue;
                    }
                    row.Component = entry.Component;
                    row.CmTraceType = entry.Type;
                    row.Message = Truncate(entry.Message, 500);
                    textToMatch = entry.Message;
                }

                Match match;
                try
                {
                    match = pattern.Match(textToMatch);
                }
                catch (RegexMatchTimeoutException)
                {
                    row.Outcome = "regex_timeout";
                    result.TimeoutCount++;
                    Stop(result, StopReasonRegexTimeout,
                        $"line {i + 1} hit the agent's {MatchTimeout.TotalSeconds:0.#} s match timeout (catastrophic backtracking) — on the device such lines are skipped and only counted as regex timeouts; remove nested or overlapping quantifiers before deploying",
                        i + 1, lines.Count);
                    break;
                }

                if (!match.Success)
                {
                    row.Outcome = "no_match";
                    continue;
                }

                row.Outcome = "matched";
                result.MatchCount++;
                row.MatchedText = Truncate(match.Value, 500);
                var groups = new Dictionary<string, string>();
                foreach (var groupName in pattern.GetGroupNames())
                {
                    if (groupName == "0") continue;
                    var group = match.Groups[groupName];
                    if (group.Success)
                        groups[groupName] = Truncate(group.Value, 500);
                }
                row.Groups = groups;
            }

            // Mirror the agent's debug-log hint verbatim — the single most common logparser
            // authoring mistake is running a plain-text log through the cmtrace default.
            if (!isTextMode && result.Lines.Count > 0 && result.ParseFailureCount == result.Lines.Count)
            {
                result.Notes.Add("every line failed CMTrace parsing — if this is a plain-text log, set parameter format=text");
            }
            // The case-sensitivity hint is noise once a timeout is the story.
            if (result.MatchCount == 0 && result.TimeoutCount == 0 && result.ParseFailureCount < result.Lines.Count)
            {
                result.Notes.Add("no line matched — remember logparser patterns are case-SENSITIVE (unlike analyze-rule regex conditions)");
            }

            return result;
        }

        /// <summary>
        /// Records why the test ended at <paramref name="lastLine"/> (1-based; 0 = before the first
        /// line). The reason is a stop only while lines remain unevaluated — a timeout on the
        /// last line keeps its row and note but is a complete test.
        /// </summary>
        private static void Stop(LogPatternTestResult result, string reason, string note, int lastLine, int totalLines)
        {
            if (lastLine < totalLines)
            {
                result.StopReason = reason;
                note += lastLine + 1 == totalLines
                    ? $"; line {totalLines} was not evaluated"
                    : $"; lines {lastLine + 1}-{totalLines} were not evaluated";
            }
            result.Notes.Add(note);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
        }

    }

    /// <summary>
    /// Process-wide cap on concurrent pattern tests (singleton). A slot is held from the regex
    /// construction to the end of the evaluation; a caller finding no free slot is answered 429
    /// at once, so this endpoint can never occupy more than <see cref="ConcurrencyGate.Limit"/>
    /// worker threads of an instance, whatever the request rate. Nothing but this test waits on
    /// this gate.
    /// </summary>
    public sealed class TestLogPatternGate : ConcurrencyGate
    {
        internal const int DefaultLimit = 2;

        public TestLogPatternGate() : this(DefaultLimit)
        {
        }

        internal TestLogPatternGate(int limit) : base(limit)
        {
        }
    }

    // LogPatternTestResult / LogPatternLineResult moved to
    // AutopilotMonitor.Shared.Models.RulesApiModels (wire contract, exported to TypeScript).
}
