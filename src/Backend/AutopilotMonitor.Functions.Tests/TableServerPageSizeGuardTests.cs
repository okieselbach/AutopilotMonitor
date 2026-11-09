using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Pagination;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Azure Tables refuses <c>$top</c> above 1000 with 400 InvalidInput. A computed page size
/// handed to <c>maxPerPage</c> unguarded reaches that limit only at the edge (a fan-out reader
/// that asked for <c>pageSize + 1</c> answered every <c>pageSize=1000</c> request with an empty
/// page). Ratchet: every <c>maxPerPage</c> argument is a literal of at most 1000 or goes through
/// <see cref="AzureTablesPaginator.ServerPageSize"/> / <c>Math.Min(…, 1000)</c>.
/// </summary>
public class TableServerPageSizeGuardTests
{
    private static readonly Regex AllowedForm = new(
        @"^(\d+|(AzureTablesPaginator\.)?ServerPageSize\(.+\)|Math\.Min\(.+,\s*1000\s*\))$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(int.MaxValue, 1000)]
    public void ServerPageSize_never_exceeds_the_Azure_limit(int rows, int expected)
    {
        Assert.Equal(expected, AzureTablesPaginator.ServerPageSize(rows));
    }

    [Fact]
    public void Every_maxPerPage_argument_stays_within_the_Azure_limit()
    {
        var root = FunctionsRoot();
        var offenders = new List<string>();
        var found = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(path);
            foreach (var arg in MaxPerPageArguments(source))
            {
                found++;
                if (!IsAllowed(arg)) offenders.Add($"{rel}: maxPerPage: {arg}");
            }
        }

        // Positive control: the scan must see the known call sites, or it proves nothing.
        Assert.True(found > 20, $"Only {found} maxPerPage arguments found — the scan lost its call sites.");
        Assert.True(offenders.Count == 0,
            "maxPerPage beyond Azure's $top limit is possible — pass AzureTablesPaginator.ServerPageSize(n) " +
            "or a literal of at most 1000:\n  " + string.Join("\n  ", offenders));
    }

    [Theory]
    [InlineData("1000", true)]
    [InlineData("25", true)]
    [InlineData("1001", false)]
    [InlineData("limit", false)]
    [InlineData("pageSize + 1", false)]
    [InlineData("AzureTablesPaginator.ServerPageSize(limit)", true)]
    [InlineData("Math.Min(maxResults + 1, 1000)", true)]
    [InlineData("Math.Min(maxResults, 5000)", false)]
    public void IsAllowed_accepts_only_bounded_forms(string argument, bool expected)
    {
        Assert.Equal(expected, IsAllowed(argument));
    }

    private static bool IsAllowed(string argument)
    {
        if (!AllowedForm.IsMatch(argument)) return false;
        return !int.TryParse(argument, out var literal) || literal <= AzureTablesPaginator.MaxServerPageSize;
    }

    /// <summary>The argument text after each <c>maxPerPage:</c>, up to the comma or parenthesis that ends it.</summary>
    private static IEnumerable<string> MaxPerPageArguments(string source)
    {
        const string marker = "maxPerPage:";
        var at = 0;
        while ((at = source.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            var i = at + marker.Length;
            at = i;
            // A mention inside a comment ("(maxPerPage: 1)") is not a call site.
            var lineStart = source.LastIndexOf('\n', i) + 1;
            if (source.AsSpan(lineStart, i - lineStart).TrimStart().StartsWith("//")) continue;

            var depth = 0;
            var start = i;
            for (; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) break; depth--; }
                else if (c == ',' && depth == 0) break;
            }
            yield return source[start..i].Trim();
        }
    }

    private static string FunctionsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Backend", "AutopilotMonitor.Functions");
    }
}
