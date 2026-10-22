using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the agent-parity semantics of the logparser pattern test endpoint: case-sensitive
/// matching (the field incident that motivated the endpoint: a pattern verified with a PHP
/// regex tester behaved differently under .NET), first-match-per-line, group extraction
/// identical to what LogParserCollector puts into the emitted event data, CMTrace parsing
/// via the SHARED CmTraceLogParser, and the format=text hint — plus the bounds that keep the
/// caller's regex from occupying the shared Function App (D-379): first timeout ends the
/// test, the time budget, cancellation, the pattern-length cap and the per-instance gate.
/// </summary>
public class TestLogPatternFunctionTests
{
    private static Regex AgentRegex(string pattern) => TestLogPatternFunction.BuildAgentRegex(pattern);

    /// <summary>The production call minus the bounds: full budget, no cancellation.</summary>
    private static LogPatternTestResult Evaluate(Regex pattern, bool isTextMode, IReadOnlyList<string> lines)
        => TestLogPatternFunction.EvaluatePattern(pattern, isTextMode, lines, TestLogPatternFunction.EvaluationBudget, CancellationToken.None);

    private const string CmTraceLine =
        "<![LOG[Installation failed for app Ivanti Secure Access Client with error 0x87D1041C]LOG]!>" +
        "<time=\"07:48:35.1783405\" date=\"8-4-2026\" component=\"AppWorkload\" context=\"\" type=\"3\" thread=\"42\" file=\"\">";

    // ===== text mode =====

    [Fact]
    public void TextMode_NamedGroups_LandInGroups()
    {
        var result = Evaluate(
            AgentRegex(@"(?<level>ERROR|FATAL)\s+(?<component>\w+):\s+(?<message>.+)"),
            isTextMode: true,
            new[] { "ERROR Installer: disk full", "INFO Installer: all good" });

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("matched", result.Lines[0].Outcome);
        Assert.Equal("ERROR", result.Lines[0].Groups!["level"]);
        Assert.Equal("Installer", result.Lines[0].Groups!["component"]);
        Assert.Equal("disk full", result.Lines[0].Groups!["message"]);
        Assert.Equal("no_match", result.Lines[1].Outcome);
        Assert.Null(result.StopReason);
    }

    /// <summary>The motivating field case: logparser matching has NO IgnoreCase — a pattern
    /// that matched in a case-insensitive tester silently never fires on the device.</summary>
    [Fact]
    public void TextMode_IsCaseSensitive_AndSaysSoInTheNote()
    {
        var result = Evaluate(AgentRegex("ERROR"), isTextMode: true, new[] { "error: something broke" });

        Assert.Equal(0, result.MatchCount);
        Assert.Contains(result.Notes, n => n.Contains("case-SENSITIVE"));
    }

    [Fact]
    public void TextMode_UnsuccessfulOptionalGroup_IsOmitted()
    {
        var result = Evaluate(AgentRegex(@"ERROR(?<code>\s0x[0-9A-F]+)?"), isTextMode: true, new[] { "ERROR without code" });

        Assert.Equal(1, result.MatchCount);
        Assert.False(result.Lines[0].Groups!.ContainsKey("code"));
    }

    // ===== cmtrace mode =====

    [Fact]
    public void CmTraceMode_MatchesAgainstParsedMessage_AndReportsComponentAndType()
    {
        var result = Evaluate(AgentRegex(@"error (?<errorCode>0x[0-9A-Fa-f]+)"), isTextMode: false, new[] { CmTraceLine });

        Assert.Equal(1, result.MatchCount);
        var row = result.Lines[0];
        Assert.Equal("matched", row.Outcome);
        Assert.Equal("0x87D1041C", row.Groups!["errorCode"]);
        Assert.Equal("AppWorkload", row.Component);
        Assert.Equal(3, row.CmTraceType);
        Assert.Contains("Installation failed", row.Message);
    }

    [Fact]
    public void CmTraceMode_RegexDoesNotSeeTheEnvelope()
    {
        // "component=" only exists in the CMTrace envelope, never in the message the
        // agent matches against — a pattern targeting it must NOT match.
        var result = Evaluate(AgentRegex("component="), isTextMode: false, new[] { CmTraceLine });

        Assert.Equal(0, result.MatchCount);
        Assert.Equal("no_match", result.Lines[0].Outcome);
    }

    [Fact]
    public void CmTraceMode_PlainTextLines_ParseFail_WithFormatHint()
    {
        var result = Evaluate(AgentRegex("ERROR"), isTextMode: false, new[] { "ERROR plain line", "another plain line" });

        Assert.Equal(2, result.ParseFailureCount);
        Assert.All(result.Lines, l => Assert.Equal("parse_failed", l.Outcome));
        Assert.Contains(result.Notes, n => n.Contains("format=text"));
    }

    // ===== request validation =====

    [Fact]
    public void Validate_RejectsMissingPatternAndLines()
    {
        Assert.Contains("pattern", TestLogPatternFunction.ValidateRequest(
            new TestLogPatternRequest { SampleLines = new List<string> { "x" } })!);
        Assert.Contains("sampleLines", TestLogPatternFunction.ValidateRequest(
            new TestLogPatternRequest { Pattern = "a" })!);
    }

    [Fact]
    public void Validate_RejectsTooManyLines_AndBadFormat()
    {
        var tooMany = new TestLogPatternRequest
        {
            Pattern = "a",
            SampleLines = Enumerable.Repeat("line", TestLogPatternFunction.MaxSampleLines + 1).ToList(),
        };
        Assert.Contains("at most", TestLogPatternFunction.ValidateRequest(tooMany)!);

        var badFormat = new TestLogPatternRequest
        {
            Pattern = "a",
            Format = "json",
            SampleLines = new List<string> { "x" },
        };
        Assert.Contains("format", TestLogPatternFunction.ValidateRequest(badFormat)!);
    }

    [Fact]
    public void Validate_AcceptsBothFormatsAndDefault()
    {
        foreach (var format in new[] { null, "cmtrace", "text", "TEXT" })
        {
            Assert.Null(TestLogPatternFunction.ValidateRequest(new TestLogPatternRequest
            {
                Pattern = "a",
                Format = format,
                SampleLines = new List<string> { "x" },
            }));
        }
    }

    /// <summary>The cap is the MCP schema's value; it is checked in validation, i.e. before
    /// the handler ever constructs a Regex from the caller's text.</summary>
    [Fact]
    public void Validate_RejectsOversizedPattern_AndAcceptsTheCapExactly()
    {
        var oversized = new TestLogPatternRequest
        {
            Pattern = new string('a', TestLogPatternFunction.MaxPatternLength + 1),
            SampleLines = new List<string> { "x" },
        };
        var error = TestLogPatternFunction.ValidateRequest(oversized);
        Assert.NotNull(error);
        Assert.Contains("pattern", error);
        Assert.Contains(TestLogPatternFunction.MaxPatternLength.ToString(), error);

        Assert.Null(TestLogPatternFunction.ValidateRequest(new TestLogPatternRequest
        {
            Pattern = new string('a', TestLogPatternFunction.MaxPatternLength),
            SampleLines = new List<string> { "x" },
        }));
    }

    // ===== the regex construction =====

    /// <summary>Agent parity minus Compiled: the semantic options and the 1 s match timeout
    /// are the agent's; Compiled (speed only) is deliberately absent for the one-shot pattern.</summary>
    [Fact]
    public void BuildAgentRegex_IsCaseSensitive_Interpreted_WithTheAgentsMatchTimeout()
    {
        var regex = TestLogPatternFunction.BuildAgentRegex("ERROR");

        Assert.Equal(RegexOptions.None, regex.Options);
        Assert.Equal(TimeSpan.FromSeconds(1), regex.MatchTimeout);
        Assert.Equal(TestLogPatternFunction.MatchTimeout, regex.MatchTimeout);
        Assert.DoesNotMatch(regex, "error");
        // RegexParseException derives from ArgumentException — the handler's catch clause.
        Assert.ThrowsAny<ArgumentException>(() => TestLogPatternFunction.BuildAgentRegex("(unclosed"));
    }

    // ===== hostile sample lines: the built-in CMTrace parser must stay bounded =====

    /// <summary>A maximal request (200 x 8192-char lines) built from the reported worst case:
    /// each line passes the CMTrace prefix gate and never matches. The built-in parser used to
    /// backtrack quadratically here (~1.4e9 steps per request) — before the tenant's own
    /// 1-second-timeout regex ever ran.</summary>
    [Fact]
    public void CmTraceMode_WorstCaseSampleLines_CompleteInBoundedTime()
    {
        var line = string.Concat(Enumerable.Repeat("<![LOG[", 585)) + new string(']', 4097);
        Assert.Equal(TestLogPatternFunction.MaxLineLength, line.Length);
        var lines = Enumerable.Repeat(line, TestLogPatternFunction.MaxSampleLines).ToArray();

        var sw = Stopwatch.StartNew();
        var result = Evaluate(AgentRegex("error"), isTextMode: false, lines);
        sw.Stop();

        Assert.Equal(TestLogPatternFunction.MaxSampleLines, result.ParseFailureCount);
        Assert.All(result.Lines, l => Assert.Equal("parse_failed", l.Outcome));
        Assert.Null(result.StopReason);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"maximal worst-case request took {sw.ElapsedMilliseconds} ms");
    }

    // ===== hostile pattern: the caller's regex must stay bounded (D-379) =====

    /// <summary>The reported attack: a catastrophic pattern over 200 non-matching lines used to
    /// cost one 1 s timeout per line (~200 s of a worker thread). The first timeout now ends the
    /// test — one row, the reason, and the unevaluated range named in a note.</summary>
    [Fact]
    public void TextMode_CatastrophicPattern_StopsAtTheFirstTimeout()
    {
        var hostile = new string('a', 60) + "!";
        var lines = Enumerable.Repeat(hostile, TestLogPatternFunction.MaxSampleLines).ToArray();

        var sw = Stopwatch.StartNew();
        var result = Evaluate(AgentRegex(@"^(\w+\s?)+$"), isTextMode: true, lines);
        sw.Stop();

        Assert.Equal(1, result.TimeoutCount);
        Assert.Single(result.Lines);
        Assert.Equal("regex_timeout", result.Lines[0].Outcome);
        Assert.Equal(TestLogPatternFunction.StopReasonRegexTimeout, result.StopReason);
        Assert.Contains(result.Notes, n => n.Contains("line 1 hit") && n.Contains("lines 2-200 were not evaluated"));
        Assert.DoesNotContain(result.Notes, n => n.Contains("case-SENSITIVE"));
        Assert.True(sw.ElapsedMilliseconds < 4000, $"catastrophic request took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>A timeout on the LAST line is a complete test: its row and note stay, but
    /// nothing was skipped, so there is no stop reason.</summary>
    [Fact]
    public void TextMode_TimeoutOnTheLastLine_IsNotAStop()
    {
        var result = Evaluate(AgentRegex(@"^(\w+\s?)+$"), isTextMode: true, new[] { "a b", new string('a', 60) + "!" });

        Assert.Equal(1, result.MatchCount);
        Assert.Equal(1, result.TimeoutCount);
        Assert.Equal(2, result.Lines.Count);
        Assert.Equal("regex_timeout", result.Lines[1].Outcome);
        Assert.Null(result.StopReason);
        Assert.Contains(result.Notes, n => n.Contains("line 2 hit") && !n.Contains("not evaluated"));
    }

    /// <summary>The budget is checked before every line but the first, so a spent budget
    /// (here: zero) still yields exactly one evaluated line and names the rest.</summary>
    [Fact]
    public void BudgetSpent_StopsBeforeTheNextLine_AndNamesTheRest()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("ERROR"), isTextMode: true, new[] { "ERROR one", "ERROR two", "ERROR three" },
            TimeSpan.Zero, CancellationToken.None);

        Assert.Single(result.Lines);
        Assert.Equal("matched", result.Lines[0].Outcome);
        Assert.Equal(TestLogPatternFunction.StopReasonTimeBudget, result.StopReason);
        Assert.Contains(result.Notes, n => n.Contains("after line 1") && n.Contains("lines 2-3 were not evaluated"));

        var twoLines = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("ERROR"), isTextMode: true, new[] { "ERROR one", "ERROR two" },
            TimeSpan.Zero, CancellationToken.None);
        Assert.Contains(twoLines.Notes, n => n.Contains("line 2 was not evaluated"));
    }

    /// <summary>A cancelled invocation (client gone, host stopping) ends the loop before the
    /// next line without throwing — a thrown OperationCanceledException would surface as a
    /// failed invocation in App Insights.</summary>
    [Fact]
    public void CancelledToken_StopsBeforeTheFirstLine_WithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("ERROR"), isTextMode: true, new[] { "ERROR one" },
            TestLogPatternFunction.EvaluationBudget, cts.Token);

        Assert.Empty(result.Lines);
        Assert.Equal(TestLogPatternFunction.StopReasonCancelled, result.StopReason);
        Assert.Equal(0, result.MatchCount);
    }

    // ===== the per-instance gate =====

    [Fact]
    public void Gate_HandsOutLimitSlots_AndTakesThemBackOnDispose()
    {
        var gate = new TestLogPatternGate(1);
        Assert.Equal(1, gate.Limit);

        var first = gate.TryEnter();
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter());

        first.Dispose();
        first.Dispose(); // idempotent: a double release must not widen the gate
        var second = gate.TryEnter();
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter());
        second.Dispose();
    }

    [Fact]
    public void Gate_DefaultLimit_IsTwoPerInstance()
    {
        var gate = new TestLogPatternGate();
        Assert.Equal(2, gate.Limit);
        Assert.Equal(TestLogPatternGate.DefaultLimit, gate.Limit);
    }
}
