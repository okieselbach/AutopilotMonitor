using System.Text.RegularExpressions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet: HTTP query parameters are parsed through <c>Helpers/QueryParams.cs</c> only. A
/// <c>int/long/DateTime/DateTimeOffset.TryParse</c> under <c>Functions/</c> or <c>Pagination/</c>
/// is a new private parser copy — the state the 2026-09-04 API audit found six times over for
/// <c>days</c> alone, each with its own default, cap and out-of-range rule. The baseline lists
/// the sites that parse something other than a query parameter (a header, a stored cell, a
/// version string); it only shrinks. The X-Agent-Version major parse moved to
/// <c>Services/AgentConfigResolver.cs</c> (outside the scanned roots) with D-216.
/// </summary>
public class QueryParamGuardTests
{
    private static readonly Regex TryParseCall = new(
        @"\b(int|long|DateTime|DateTimeOffset)\.TryParse(Exact)?\(", RegexOptions.Compiled);

    /// <summary>Non-query parses, reviewed: file → why it is allowed.</summary>
    private static readonly IReadOnlyDictionary<string, string> Baseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
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

    // ── One source of the query collection ──────────────────────────────────────────────
    // `req.Query` IS `HttpUtility.ParseQueryString(req.Url.Query)`, cached per request (the
    // fact below pins that against a worker update). A second spelling of the same call is
    // only a pattern for the next function to copy, so the request query is read through
    // `req.Query` and ParseQueryString stays for strings that are not the request's query.

    private static readonly Regex ParseQueryStringCall = new(@"\bParseQueryString\(", RegexOptions.Compiled);

    /// <summary>ParseQueryString over something other than the request query: file → why.</summary>
    private static readonly IReadOnlyDictionary<string, string> ParseQueryStringBaseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Functions/Config/GetTenantConfigurationFunction.cs"] = "ShouldRedact takes the raw query string so the redaction decision is unit-testable without a request",
        ["Functions/Diagnostics/GetDiagnosticsUploadUrlFunction.cs"] = "query of the tenant's stored SAS URL",
        ["Services/Diagnostics/SasPermissionParser.cs"] = "query of a SAS URL",
    };

    [Fact]
    public void The_request_query_is_read_through_req_Query_only()
    {
        var root = FunctionsRoot();
        var offenders = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(rel => !rel.StartsWith("bin/", StringComparison.Ordinal) && !rel.StartsWith("obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var rel in files)
        {
            if (!ParseQueryStringCall.IsMatch(File.ReadAllText(Path.Combine(root, rel)))) continue;
            seen.Add(rel);
            if (!ParseQueryStringBaseline.ContainsKey(rel)) offenders.Add(rel);
        }

        Assert.True(offenders.Count == 0,
            "HttpUtility.ParseQueryString in the Functions project — read the request query through " +
            "req.Query, or add the file to the baseline with what it parses instead:\n  " + string.Join("\n  ", offenders));

        var stale = ParseQueryStringBaseline.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "Baseline entries without a ParseQueryString any more — remove them:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Req_Query_is_ParseQueryString_over_the_request_url()
    {
        var url = new Uri("https://localhost/api/x?a=1&a=2&b=%20x%2B&c&=orphan&D=Case");
        var req = new Moq.Mock<Microsoft.Azure.Functions.Worker.Http.HttpRequestData>(
            Moq.Mock.Of<Microsoft.Azure.Functions.Worker.FunctionContext>()) { CallBase = true };
        req.SetupGet(r => r.Url).Returns(url);

        var viaRequest = req.Object.Query;
        var viaHelper = System.Web.HttpUtility.ParseQueryString(url.Query);

        Assert.Equal(viaHelper.AllKeys, viaRequest.AllKeys);
        foreach (var key in viaHelper.AllKeys)
            Assert.Equal(viaHelper.GetValues(key), viaRequest.GetValues(key));
        Assert.Equal("1,2", viaRequest["a"]);   // repeated key: comma-joined on both paths
        Assert.Equal(" x+", viaRequest["b"]);
        Assert.Same(viaRequest, req.Object.Query); // parsed once per request
    }
}
