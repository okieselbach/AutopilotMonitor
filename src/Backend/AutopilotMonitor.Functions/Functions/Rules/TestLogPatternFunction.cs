using System.Net;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

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
    /// - Regex options: Compiled, 1s timeout, NO IgnoreCase — logparser matching is
    ///   case-SENSITIVE (unlike analyze-rule regex conditions).
    /// - First match per line (Regex.Match, not Matches).
    /// - format "cmtrace" (default): CmTraceLogParser.TryParseLine first (shared code, the
    ///   same implementation the agent runs), regex against the parsed MESSAGE only.
    /// - format "text": regex against the raw line.
    /// - Captured data: every named/numbered group except "0", unsuccessful groups omitted.
    ///
    /// No tenant data is touched; the endpoint is pure compute on the request body.
    /// </summary>
    public class TestLogPatternFunction
    {
        private readonly ILogger<TestLogPatternFunction> _logger;

        public TestLogPatternFunction(ILogger<TestLogPatternFunction> logger)
        {
            _logger = logger;
        }

        public sealed class TestLogPatternRequest
        {
            public string? Pattern { get; set; }
            public string? Format { get; set; }
            public List<string>? SampleLines { get; set; }
        }

        internal const int MaxSampleLines = 200;
        internal const int MaxLineLength = 8192;

        [Function("TestLogPattern")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/gather/test-pattern")] HttpRequestData req)
        {
            // Authentication + TenantAdminOrGlobalReader authorization enforced by PolicyEnforcementMiddleware.

            if (req.Headers.TryGetValues("Content-Length", out var clValues)
                && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                && contentLength > 1_048_576) // 1 MB limit
            {
                return await BadRequestAsync(req, "Request body too large");
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            TestLogPatternRequest? request;
            try
            {
                request = JsonConvert.DeserializeObject<TestLogPatternRequest>(body);
            }
            catch (JsonException ex)
            {
                return await BadRequestAsync(req, $"Request body is not valid JSON: {ex.Message}");
            }

            var validationError = ValidateRequest(request);
            if (validationError != null)
            {
                return await BadRequestAsync(req, validationError);
            }

            var isTextMode = string.Equals(request!.Format, "text", StringComparison.OrdinalIgnoreCase);

            Regex pattern;
            try
            {
                // EXACT agent construction (LogParserCollector): Compiled + 1s timeout,
                // no IgnoreCase — logparser matching is case-sensitive.
                pattern = new Regex(request.Pattern!, RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return await BadRequestAsync(req, $"Invalid regex pattern (this is the agent's .NET engine speaking): {ex.Message}");
            }

            var result = EvaluatePattern(pattern, isTextMode, request.SampleLines!);

            _logger.LogInformation(
                "Log-pattern test: {LineCount} lines, {MatchCount} matched, {ParseFailures} parse failures, mode={Mode}",
                request.SampleLines!.Count, result.MatchCount, result.ParseFailureCount, isTextMode ? "text" : "cmtrace");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new TestLogPatternResponse { Success = true, Format = isTextMode ? "text" : "cmtrace", Result = result });
            return response;
        }

        internal static string? ValidateRequest(TestLogPatternRequest? request)
        {
            if (request == null || string.IsNullOrEmpty(request.Pattern))
                return "pattern is required";
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
        /// Per-line evaluation mirroring LogParserCollector's inner loop. Returns one row per
        /// sample line so the author sees exactly which lines matched, which failed CMTrace
        /// parsing, and which data fields the emitted event would carry.
        /// </summary>
        internal static LogPatternTestResult EvaluatePattern(Regex pattern, bool isTextMode, IReadOnlyList<string> lines)
        {
            var result = new LogPatternTestResult();

            for (var i = 0; i < lines.Count; i++)
            {
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
                    continue;
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
            if (result.MatchCount == 0 && result.ParseFailureCount < result.Lines.Count)
            {
                result.Notes.Add("no line matched — remember logparser patterns are case-SENSITIVE (unlike analyze-rule regex conditions)");
            }

            return result;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
        }

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { success = false, message });
            return badRequest;
        }
    }

    // LogPatternTestResult / LogPatternLineResult moved to
    // AutopilotMonitor.Shared.Models.RulesApiModels (wire contract, exported to TypeScript).
}
