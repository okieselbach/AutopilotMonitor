using System.Reflection;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Contract tests for <see cref="ValidateBootstrapCodeResponse"/>, the DTO returned
/// by <c>GET /api/bootstrap/validate/{code}</c>.
///
/// The values in this response are interpolated into a PowerShell bootstrap
/// script that runs as SYSTEM during Autopilot OOBE, and the Next.js frontend
/// at <c>src/Web/autopilot-monitor-web/lib/bootstrapValidation.ts</c> enforces
/// a very strict shape (GUID tenantId+token, locked-down AgentDownloadUrl, etc.).
///
/// These tests mirror the frontend's invariants so that a backend rename or
/// change to <c>AgentDownloadUrl</c> is caught in CI — not during OOBE rollout.
/// </summary>
public class ValidateBootstrapCodeResponseShapeTests
{
    /// <summary>
    /// Mirrors the frontend regex at <c>bootstrapValidation.ts</c> (lines ~119–125).
    /// </summary>
    private static readonly Regex AgentDownloadUrlPath =
        new(@"^/agent/[A-Za-z0-9_-][A-Za-z0-9._-]{0,79}\.zip$", RegexOptions.Compiled);

    /// <summary>
    /// The response DTO must expose exactly these public instance properties with
    /// these types. Any rename or removal breaks the frontend validator silently.
    /// </summary>
    [Fact]
    public void ValidateBootstrapCodeResponse_HasExpectedPublicProperties()
    {
        var expected = new Dictionary<string, Type>
        {
            ["Success"]          = typeof(bool),
            ["TenantId"]         = typeof(string),
            ["Token"]            = typeof(string),
            ["AgentDownloadUrl"] = typeof(string),
            ["ExpiresAt"]        = typeof(DateTime),
            ["Message"]          = typeof(string),
        };

        var actual = typeof(ValidateBootstrapCodeResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p.PropertyType);

        // Missing / renamed properties
        var missing = expected.Keys.Except(actual.Keys).ToList();
        Assert.True(missing.Count == 0,
            "ValidateBootstrapCodeResponse is missing expected properties: " + string.Join(", ", missing));

        // Unexpected additions (forces a deliberate test update when the contract grows)
        var extra = actual.Keys.Except(expected.Keys).ToList();
        Assert.True(extra.Count == 0,
            "ValidateBootstrapCodeResponse has unexpected extra properties (update frontend validator + this test): "
            + string.Join(", ", extra));

        // Type mismatches
        foreach (var kvp in expected)
        {
            Assert.Equal(kvp.Value, actual[kvp.Key]);
        }
    }

    /// <summary>
    /// The agent download URL served by <c>ValidateBootstrapCodeFunction</c> must
    /// satisfy the frontend's host allow-list and path regex.
    /// </summary>
    [Fact]
    public void AgentDownloadUrl_MatchesFrontendAllowList()
    {
        // Independent oracle, deliberately spelled out: if Constants or the function
        // change the URL, this literal AND bootstrapValidation.ts (AGENT_DOWNLOAD_HOSTNAMES)
        // must be updated consciously — that pairing is what keeps portal and backend in sync.
        const string ExpectedAgentUrl =
            "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip";

        var built = $"{AutopilotMonitor.Shared.Constants.AgentDownloadBaseUrl}/{AutopilotMonitor.Shared.Constants.AgentZipFileName}";
        Assert.Equal(ExpectedAgentUrl, built);

        Assert.True(Uri.TryCreate(built, UriKind.Absolute, out var uri),
            "AgentDownloadUrl is not a well-formed absolute URI");

        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.Equal("download.autopilotmonitor.com", uri.Host);
        Assert.True(AgentDownloadUrlPath.IsMatch(uri.AbsolutePath),
            $"AgentDownloadUrl path '{uri.AbsolutePath}' does not match the frontend allow-list regex");
    }
}
