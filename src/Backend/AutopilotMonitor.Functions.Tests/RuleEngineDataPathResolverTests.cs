using System.Text.Json;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for the dot-path resolver inside <see cref="RuleEngine.GetDataFieldValue"/>.
/// Exercises both nested <c>Dictionary&lt;string,object&gt;</c> and <see cref="JsonElement"/>
/// payload shapes — both occur in production depending on whether the event came in via
/// the in-process emitter (typed sidecar) or was rehydrated from Table Storage JSON.
/// </summary>
public class RuleEngineDataPathResolverTests
{
    [Fact]
    public void TryFlatLookup_LiteralKeyWithDot_StillResolves()
    {
        // Pre-existing v1 events used keys like "scan_summary.critical_cves" with a literal
        // dot. The flat lookup must keep working so legacy rules don't break.
        var data = new Dictionary<string, object>
        {
            ["scan_summary.critical_cves"] = 7,
        };

        var ok = RuleEngine.TryFlatLookup(data, "scan_summary.critical_cves", out var value);

        Assert.True(ok);
        Assert.Equal("7", value);
    }

    [Fact]
    public void TryFlatLookup_CaseInsensitive()
    {
        var data = new Dictionary<string, object> { ["FailedSubcategories"] = "Certificates,VPN" };

        var ok = RuleEngine.TryFlatLookup(data, "failedsubcategories", out var value);

        Assert.True(ok);
        Assert.Equal("Certificates,VPN", value);
    }

    [Fact]
    public void ResolveDotPath_NestedDictionary_ReturnsLeafScalar()
    {
        var data = new Dictionary<string, object>
        {
            ["scan_summary"] = new Dictionary<string, object>
            {
                ["critical_cves"] = 3,
                ["overall_risk"] = "high",
            },
        };

        Assert.Equal("3", RuleEngine.ResolveDotPath(data, "scan_summary.critical_cves"));
        Assert.Equal("high", RuleEngine.ResolveDotPath(data, "scan_summary.overall_risk"));
    }

    [Fact]
    public void ResolveDotPath_NestedJsonElement_ReturnsLeafScalar()
    {
        // Events read back from Table Storage hit the resolver as JsonElement subtrees.
        var json = """{"scan_summary":{"critical_cves":5,"kev_matches":2,"overall_risk":"critical"}}""";
        using var doc = JsonDocument.Parse(json);
        var data = new Dictionary<string, object>
        {
            ["scan_summary"] = doc.RootElement.GetProperty("scan_summary").Clone(),
        };

        Assert.Equal("5", RuleEngine.ResolveDotPath(data, "scan_summary.critical_cves"));
        Assert.Equal("2", RuleEngine.ResolveDotPath(data, "scan_summary.kev_matches"));
        Assert.Equal("critical", RuleEngine.ResolveDotPath(data, "scan_summary.overall_risk"));
    }

    [Fact]
    public void ResolveDotPath_CaseInsensitiveAtEachLevel()
    {
        var data = new Dictionary<string, object>
        {
            ["Scan_Summary"] = new Dictionary<string, object>
            {
                ["Critical_CVEs"] = 4,
            },
        };

        Assert.Equal("4", RuleEngine.ResolveDotPath(data, "scan_summary.critical_cves"));
    }

    [Fact]
    public void ResolveDotPath_MissingPath_ReturnsNull()
    {
        var data = new Dictionary<string, object>
        {
            ["scan_summary"] = new Dictionary<string, object> { ["critical_cves"] = 1 },
        };

        Assert.Null(RuleEngine.ResolveDotPath(data, "scan_summary.missing"));
        Assert.Null(RuleEngine.ResolveDotPath(data, "missing.critical_cves"));
    }

    [Fact]
    public void ResolveDotPath_DescendIntoScalar_ReturnsNull()
    {
        // Path traverses past a scalar — must not throw, must return null so the rule
        // misses cleanly instead of stringifying an unrelated value.
        var data = new Dictionary<string, object>
        {
            ["scan_summary"] = "not-an-object",
        };

        Assert.Null(RuleEngine.ResolveDotPath(data, "scan_summary.critical_cves"));
    }

    [Fact]
    public void TryFlatLookup_ListValue_FlattensToJoinedString()
    {
        // Regression: app_tracking_summary.pendingNames is a List<object> after DataJson
        // deserialization. ToString() on it yields the .NET type name, which broke both
        // contains-matching and {{pendingNames}} interpolation in ANALYZE-APP-015 findings.
        var data = new Dictionary<string, object>
        {
            ["pendingNames"] = new List<object> { "Contoso VPN", "Fabrikam Agent" },
        };

        var ok = RuleEngine.TryFlatLookup(data, "pendingNames", out var value);

        Assert.True(ok);
        Assert.Equal("Contoso VPN, Fabrikam Agent", value);
    }

    [Fact]
    public void ResolveDotPath_ListLeaf_FlattensToJoinedString()
    {
        var data = new Dictionary<string, object>
        {
            ["summary"] = new Dictionary<string, object>
            {
                ["names"] = new List<object> { "a", "b" },
            },
        };

        Assert.Equal("a, b", RuleEngine.ResolveDotPath(data, "summary.names"));
    }
}
