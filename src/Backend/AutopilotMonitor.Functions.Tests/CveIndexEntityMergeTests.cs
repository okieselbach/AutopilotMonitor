using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="TableStorageService.BuildCveIndexEntities"/> collapses report findings to one
/// CveIndex entity per CVE. Production reports routinely list the same CVE under many
/// findings (one RD CVE across 14 installed RD components), which previously produced
/// 14 concurrent upserts racing on the same PK/RK.
/// </summary>
public class CveIndexEntityMergeTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Session = "22222222-2222-2222-2222-222222222222";

    private static Dictionary<string, object> Finding(string software, string risk, params Dictionary<string, object>[] vulns)
        => new()
        {
            ["softwareName"] = software,
            ["riskLevel"] = risk,
            ["vulnerabilities"] = vulns.ToList(),
        };

    private static Dictionary<string, object> Vuln(string cve, double score, string severity, bool kev = false)
        => new()
        {
            ["cveId"] = cve,
            ["cvssScore"] = score,
            ["cvssSeverity"] = severity,
            ["isKev"] = kev,
        };

    [Fact]
    public void SameCveAcrossManyFindings_CollapsesToOneEntity()
    {
        var findings = Enumerable.Range(0, 14)
            .Select(i => Finding($"RD Component {i}", "high", Vuln("CVE-2025-29966", 8.8, "HIGH")))
            .ToList();

        var entities = TableStorageService.BuildCveIndexEntities(Tenant, Session, findings);

        var e = Assert.Single(entities);
        Assert.Equal($"{Tenant}_CVE-2025-29966", e.PartitionKey);
        Assert.Equal(Session, e.RowKey);
        Assert.Equal("RD Component 0", e.GetString("SoftwareName")); // tie keeps report order
    }

    [Fact]
    public void Merge_HighestScoreWins_KevOrs_RiskMaxes()
    {
        var findings = new List<Dictionary<string, object>>
        {
            Finding("App A", "medium", Vuln("CVE-1", 5.0, "MEDIUM", kev: true)),
            Finding("App B", "critical", Vuln("CVE-1", 9.1, "CRITICAL")),
            Finding("App C", "low", Vuln("CVE-1", 3.0, "LOW")),
        };

        var e = Assert.Single(TableStorageService.BuildCveIndexEntities(Tenant, Session, findings));

        Assert.Equal(9.1, e.GetDouble("CvssScore"));
        Assert.Equal("CRITICAL", e.GetString("CvssSeverity"));
        Assert.Equal("App B", e.GetString("SoftwareName"));
        Assert.True(e.GetBoolean("IsKev"));
        Assert.Equal("critical", e.GetString("OverallRisk"));
    }

    [Fact]
    public void Merge_EpssMaxes_PriorityMaxes_AndUnscoredWritesNoEpssColumn()
    {
        var scoredLow = Vuln("CVE-1", 5.0, "MEDIUM"); scoredLow["epssScore"] = 0.02; scoredLow["priority"] = "track";
        var scoredHigh = Vuln("CVE-1", 4.0, "MEDIUM"); scoredHigh["epssScore"] = 0.35; scoredHigh["priority"] = "attend";
        var unscored = Vuln("CVE-1", 3.0, "LOW"); unscored["priority"] = "track";

        var e = Assert.Single(TableStorageService.BuildCveIndexEntities(Tenant, Session, new List<Dictionary<string, object>>
        {
            Finding("App A", "medium", unscored),
            Finding("App B", "medium", scoredLow),
            Finding("App C", "medium", scoredHigh),
        }));

        Assert.Equal(0.35, e.GetDouble("EpssScore"));
        Assert.Equal("attend", e.GetString("Priority"));

        // A CVE nobody scored must not carry a fake 0.0 — the aggregate treats absent as "unknown".
        var none = Assert.Single(TableStorageService.BuildCveIndexEntities(Tenant, Session, new List<Dictionary<string, object>>
        {
            Finding("App A", "low", Vuln("CVE-2", 3.0, "LOW")),
        }));
        Assert.False(none.ContainsKey("EpssScore"));
        Assert.Equal("", none.GetString("Priority"));
    }

    [Fact]
    public void DistinctCves_StayDistinct_AndInvalidEntriesAreSkipped()
    {
        var findings = new List<Dictionary<string, object>>
        {
            Finding("App A", "high", Vuln("CVE-1", 7.0, "HIGH"), Vuln("CVE-2", 6.0, "MEDIUM")),
            Finding("App B", "high", new Dictionary<string, object> { ["cvssScore"] = 9.0 }), // no cveId
            new() { ["softwareName"] = "No vulns" },
        };

        var entities = TableStorageService.BuildCveIndexEntities(Tenant, Session, findings);

        Assert.Equal(2, entities.Count);
        Assert.Contains(entities, x => x.GetString("CveId") == "CVE-1");
        Assert.Contains(entities, x => x.GetString("CveId") == "CVE-2");
    }

    [Fact]
    public void CveIdComparison_IsCaseInsensitive()
    {
        var findings = new List<Dictionary<string, object>>
        {
            Finding("A", "high", Vuln("CVE-2024-1", 7.0, "HIGH")),
            Finding("B", "high", Vuln("cve-2024-1", 7.0, "HIGH")),
        };

        Assert.Single(TableStorageService.BuildCveIndexEntities(Tenant, Session, findings));
    }
}
