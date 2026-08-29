using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Functions.Rules;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the agent-parity semantics of the logparser pattern test endpoint: case-sensitive
/// matching (the field incident that motivated the endpoint: a pattern verified with a PHP
/// regex tester behaved differently under .NET), first-match-per-line, group extraction
/// identical to what LogParserCollector puts into the emitted event data, CMTrace parsing
/// via the SHARED CmTraceLogParser, and the format=text hint.
/// </summary>
public class TestLogPatternFunctionTests
{
    private static Regex AgentRegex(string pattern)
        => new(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private const string CmTraceLine =
        "<![LOG[Installation failed for app Ivanti Secure Access Client with error 0x87D1041C]LOG]!>" +
        "<time=\"07:48:35.1783405\" date=\"8-4-2026\" component=\"AppWorkload\" context=\"\" type=\"3\" thread=\"42\" file=\"\">";

    // ===== text mode =====

    [Fact]
    public void TextMode_NamedGroups_LandInGroups()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex(@"(?<level>ERROR|FATAL)\s+(?<component>\w+):\s+(?<message>.+)"),
            isTextMode: true,
            new[] { "ERROR Installer: disk full", "INFO Installer: all good" });

        Assert.Equal(1, result.MatchCount);
        Assert.Equal("matched", result.Lines[0].Outcome);
        Assert.Equal("ERROR", result.Lines[0].Groups!["level"]);
        Assert.Equal("Installer", result.Lines[0].Groups!["component"]);
        Assert.Equal("disk full", result.Lines[0].Groups!["message"]);
        Assert.Equal("no_match", result.Lines[1].Outcome);
    }

    /// <summary>The motivating field case: logparser matching has NO IgnoreCase — a pattern
    /// that matched in a case-insensitive tester silently never fires on the device.</summary>
    [Fact]
    public void TextMode_IsCaseSensitive_AndSaysSoInTheNote()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("ERROR"), isTextMode: true, new[] { "error: something broke" });

        Assert.Equal(0, result.MatchCount);
        Assert.Contains(result.Notes, n => n.Contains("case-SENSITIVE"));
    }

    [Fact]
    public void TextMode_UnsuccessfulOptionalGroup_IsOmitted()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex(@"ERROR(?<code>\s0x[0-9A-F]+)?"), isTextMode: true, new[] { "ERROR without code" });

        Assert.Equal(1, result.MatchCount);
        Assert.False(result.Lines[0].Groups!.ContainsKey("code"));
    }

    // ===== cmtrace mode =====

    [Fact]
    public void CmTraceMode_MatchesAgainstParsedMessage_AndReportsComponentAndType()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex(@"error (?<errorCode>0x[0-9A-Fa-f]+)"), isTextMode: false, new[] { CmTraceLine });

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
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("component="), isTextMode: false, new[] { CmTraceLine });

        Assert.Equal(0, result.MatchCount);
        Assert.Equal("no_match", result.Lines[0].Outcome);
    }

    [Fact]
    public void CmTraceMode_PlainTextLines_ParseFail_WithFormatHint()
    {
        var result = TestLogPatternFunction.EvaluatePattern(
            AgentRegex("ERROR"), isTextMode: false, new[] { "ERROR plain line", "another plain line" });

        Assert.Equal(2, result.ParseFailureCount);
        Assert.All(result.Lines, l => Assert.Equal("parse_failed", l.Outcome));
        Assert.Contains(result.Notes, n => n.Contains("format=text"));
    }

    // ===== request validation =====

    [Fact]
    public void Validate_RejectsMissingPatternAndLines()
    {
        Assert.Contains("pattern", TestLogPatternFunction.ValidateRequest(
            new TestLogPatternFunction.TestLogPatternRequest { SampleLines = new List<string> { "x" } })!);
        Assert.Contains("sampleLines", TestLogPatternFunction.ValidateRequest(
            new TestLogPatternFunction.TestLogPatternRequest { Pattern = "a" })!);
    }

    [Fact]
    public void Validate_RejectsTooManyLines_AndBadFormat()
    {
        var tooMany = new TestLogPatternFunction.TestLogPatternRequest
        {
            Pattern = "a",
            SampleLines = Enumerable.Repeat("line", TestLogPatternFunction.MaxSampleLines + 1).ToList(),
        };
        Assert.Contains("at most", TestLogPatternFunction.ValidateRequest(tooMany)!);

        var badFormat = new TestLogPatternFunction.TestLogPatternRequest
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
            Assert.Null(TestLogPatternFunction.ValidateRequest(new TestLogPatternFunction.TestLogPatternRequest
            {
                Pattern = "a",
                Format = format,
                SampleLines = new List<string> { "x" },
            }));
        }
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
        var result = TestLogPatternFunction.EvaluatePattern(AgentRegex("error"), isTextMode: false, lines);
        sw.Stop();

        Assert.Equal(TestLogPatternFunction.MaxSampleLines, result.ParseFailureCount);
        Assert.All(result.Lines, l => Assert.Equal("parse_failed", l.Outcome));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"maximal worst-case request took {sw.ElapsedMilliseconds} ms");
    }
}
