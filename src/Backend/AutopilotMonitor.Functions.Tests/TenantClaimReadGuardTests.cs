using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ratchet: a function takes the tenant whose data it reads from the request context — <c>TenantHelper.GetTenantId(req)</c>
/// or <c>RequestContext.TenantId</c> — never from the token. Only those follow "view as tenant", where a Global Admin
/// reads another tenant as its member; a data read off the token's <c>tid</c> would silently show the Global Admin's
/// own tenant under the viewed tenant's name. The baseline lists the reads that mean the caller's IDENTITY; it only
/// shrinks.
/// </summary>
public class TenantClaimReadGuardTests
{
    private static readonly Regex TokenTenantRead = new(
        @"\.GetTenantId\(\)|FindFirst\(""tid""\)|identity/claims/tenantid", RegexOptions.Compiled);

    /// <summary>Token-tenant reads that identify the caller: file → why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Baseline = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Functions/Infrastructure/AuthFunction.cs"] =
            "auth/me identifies and onboards the sign-in tenant (its view-as branch runs first), the caller's own What's-new marker and Terms + DPA",
        ["Functions/Infrastructure/McpUserFunction.cs"] = "the MCP front door binds the caller's identity",
        ["Functions/Metrics/McpUsageMetricsFunction.cs"] = "the caller's own MCP usage (me)",
        ["Functions/Rules/PreviewWhitelistFunction.cs"] = "the caller's own tenant's activation notification address",
    };

    private static string FunctionsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "Backend", "AutopilotMonitor.Functions");
    }

    [Fact]
    public void Functions_read_the_data_tenant_from_the_request_context_not_from_the_token()
    {
        var root = FunctionsRoot();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var sub in new[] { "Functions", "Pagination" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories))
            {
                if (!TokenTenantRead.IsMatch(File.ReadAllText(path)))
                    continue;
                var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                seen.Add(rel);
                if (!Baseline.ContainsKey(rel))
                    offenders.Add(rel);
            }
        }

        Assert.True(offenders.Count == 0,
            "Token tenant read in a function — take the data tenant from TenantHelper.GetTenantId(req) or the request " +
            "context, or add the file to the baseline with the identity reason:\n  " + string.Join("\n  ", offenders));

        var stale = Baseline.Keys.Where(k => !seen.Contains(k)).ToList();
        Assert.True(stale.Count == 0,
            "Baseline entries without a token tenant read any more — remove them (the ratchet only shrinks):\n  " +
            string.Join("\n  ", stale));
    }
}
