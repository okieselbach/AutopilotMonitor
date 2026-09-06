using System.Text.RegularExpressions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet: HTTP query parameters are parsed through <c>Helpers/QueryParams.cs</c> only. A
/// <c>int/long/DateTime/DateTimeOffset.TryParse</c> under <c>Functions/</c> or <c>Pagination/</c>
/// is a new private parser copy — the state the 2026-09-04 API audit found six times over for
/// <c>days</c> alone, each with its own default, cap and out-of-range rule. The baseline lists
/// the sites that parse something other than a query parameter (a header, a stored cell, a
/// version string); it only shrinks.
/// </summary>
public class QueryParamGuardTests
{
    private static readonly Regex TryParseCall = new(
        @"\b(int|long|DateTime|DateTimeOffset)\.TryParse(Exact)?\(", RegexOptions.Compiled);

    /// <summary>Non-query parses, reviewed: file → why it is allowed.</summary>
    private static readonly IReadOnlyDictionary<string, string> Baseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Functions/Config/GetAgentConfigFunction.cs"] = "major of the X-Agent-Version header",
        ["Functions/Ingest/IngestTelemetryFunction.cs"] = "X-Send-Time-Utc header (RoundtripKind, deliberately not a query instant)",
        ["Functions/Raw/QueryRawEventsFunction.cs"] = "Severity / Sequence cells of raw table rows",
        ["Functions/Diagnostics/GetDiagnosticsUploadUrlFunction.cs"] = "se= expiry inside the tenant's stored SAS URL",
        // Content-Length header guard on body-taking routes (long.TryParse on the header value).
        ["Functions/Admin/SubmitOffboardingFeedbackFunction.cs"] = "Content-Length header",
        ["Functions/Bootstrap/BootstrapRegisterSessionFunction.cs"] = "Content-Length header",
        ["Functions/Ingest/ReportAgentErrorFunction.cs"] = "Content-Length header",
        ["Functions/Ingest/ReportDistressFunction.cs"] = "Content-Length header",
        ["Functions/Sessions/RegisterSessionFunction.cs"] = "Content-Length header",
    };

    private static string FunctionsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Backend", "AutopilotMonitor.Functions");
    }

    private static IEnumerable<string> ScannedFiles(string root) =>
        new[] { "Functions", "Pagination" }
            .SelectMany(sub => Directory.EnumerateFiles(Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void Query_parameters_are_parsed_through_QueryParams_only()
    {
        var root = FunctionsRoot();
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rel in ScannedFiles(root))
        {
            var source = File.ReadAllText(Path.Combine(root, rel));
            if (!TryParseCall.IsMatch(source)) continue;
            seen.Add(rel);
            if (!Baseline.ContainsKey(rel)) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "TryParse outside Helpers/QueryParams.cs — parse query parameters through QueryParams " +
            "(Int / TryInt / TryPageSize / UtcInstant / TryUtcInstant), or add the file to the baseline " +
            "with the non-query reason:\n  " + string.Join("\n  ", offenders));

        var stale = Baseline.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "Baseline entries without a TryParse any more — remove them (the ratchet only shrinks):\n  " +
            string.Join("\n  ", stale));
    }
}
