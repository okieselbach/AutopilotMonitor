using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Services.Vulnerability;
using static AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelationService;

namespace AutopilotMonitor.Functions.Tests;

public class CveCacheBudgetTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);

    private static CachedCve Cve(string id, double score, DateTime publishedUtc, int rangeCount = 1)
    {
        return new CachedCve
        {
            CveId = id,
            CvssScore = score,
            CvssSeverity = score >= 9 ? "CRITICAL" : score >= 7 ? "HIGH" : "MEDIUM",
            Description = "",
            PublishedDate = publishedUtc.ToString("o"),
            AffectedVersions = Enumerable.Range(0, rangeCount)
                .Select(i => new CachedVersionRange { Criteria = $"cpe:2.3:a:vendor:prod:{i}:*:*:*:*:*:*:*" })
                .ToList()
        };
    }

    // -----------------------------------------------------------------------
    // Date filter
    // -----------------------------------------------------------------------

    [Fact]
    public void Apply_KeepsAllCvesInsideRecentWindow()
    {
        var cves = new[]
        {
            Cve("CVE-A", 5.0, Now.AddMonths(-1)),
            Cve("CVE-B", 5.0, Now.AddMonths(-12)),
            Cve("CVE-C", 5.0, Now.AddMonths(-23))
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(3, r.TotalIn);
        Assert.Equal(3, r.KeptAfterDateFilter);
        Assert.Equal(3, r.KeptAfterCap);
    }

    [Fact]
    public void Apply_DropsLowSevCvesOlderThanRecentWindow()
    {
        var cves = new[]
        {
            Cve("CVE-Stale-Low", 5.0, Now.AddMonths(-30)),
            Cve("CVE-Stale-Medium", 6.9, Now.AddMonths(-25))
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(2, r.TotalIn);
        Assert.Equal(0, r.KeptAfterDateFilter);
        Assert.Empty(r.Cves);
    }

    [Fact]
    public void Apply_KeepsOldHighSevCvesWithinHighSevWindow()
    {
        // LTS-software escape hatch: HIGH/CRITICAL up to 60 months back.
        var cves = new[]
        {
            Cve("CVE-Old-High", 7.5, Now.AddMonths(-40)),
            Cve("CVE-Old-Critical", 9.8, Now.AddMonths(-50)),
            Cve("CVE-OutsideHighSev", 9.8, Now.AddMonths(-61))
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(2, r.KeptAfterDateFilter);
        Assert.Contains(r.Cves, c => c.CveId == "CVE-Old-High");
        Assert.Contains(r.Cves, c => c.CveId == "CVE-Old-Critical");
        Assert.DoesNotContain(r.Cves, c => c.CveId == "CVE-OutsideHighSev");
    }

    [Fact]
    public void Apply_UnparseablePublishedDate_IsKept()
    {
        var cves = new[]
        {
            new CachedCve { CveId = "CVE-NoDate", CvssScore = 5.0, PublishedDate = "" },
            new CachedCve { CveId = "CVE-BadDate", CvssScore = 5.0, PublishedDate = "not-a-date" }
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(2, r.KeptAfterDateFilter);
        Assert.Equal(2, r.Cves.Count);
    }

    // -----------------------------------------------------------------------
    // Sort + cap
    // -----------------------------------------------------------------------

    [Fact]
    public void Apply_CapsAtMaxCachedCves_PreservingHighestCvssFirst()
    {
        // 150 in-window CVEs with ascending score 1..150 (rescaled to 0..10).
        var rng = new Random(42);
        var cves = Enumerable.Range(1, 150)
            .Select(i => Cve($"CVE-{i:000}", Math.Min(10.0, i * 10.0 / 150.0), Now.AddMonths(-rng.Next(0, 23))))
            .ToList();

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(CveCacheBudget.MaxCachedCves, r.KeptAfterCap);
        // Top kept item should have the highest score in the input.
        Assert.True(r.Cves.First().CvssScore >= r.Cves.Last().CvssScore);
        Assert.Equal(10.0, r.Cves.First().CvssScore, 5);
    }

    [Fact]
    public void Apply_SecondarySort_PrefersNewerPublishedDate_OnTiedCvssScore()
    {
        var cves = new[]
        {
            Cve("CVE-Older", 8.0, Now.AddMonths(-12)),
            Cve("CVE-Newer", 8.0, Now.AddMonths(-1))
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal("CVE-Newer", r.Cves[0].CveId);
        Assert.Equal("CVE-Older", r.Cves[1].CveId);
    }

    // -----------------------------------------------------------------------
    // AffectedVersions truncate
    // -----------------------------------------------------------------------

    [Fact]
    public void Apply_TruncatesOversizedAffectedVersions_LeavesSmallerOnesAlone()
    {
        var cves = new[]
        {
            Cve("CVE-Huge", 9.0, Now.AddMonths(-1), rangeCount: CveCacheBudget.MaxRangesPerCve + 30),
            Cve("CVE-Small", 8.0, Now.AddMonths(-1), rangeCount: 3),
            Cve("CVE-Exactly-At-Cap", 7.5, Now.AddMonths(-1), rangeCount: CveCacheBudget.MaxRangesPerCve)
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(1, r.CvesWithTruncatedRanges);
        var huge = r.Cves.Single(c => c.CveId == "CVE-Huge");
        Assert.Equal(CveCacheBudget.MaxRangesPerCve, huge.AffectedVersions.Count);

        var small = r.Cves.Single(c => c.CveId == "CVE-Small");
        Assert.Equal(3, small.AffectedVersions.Count);

        var atCap = r.Cves.Single(c => c.CveId == "CVE-Exactly-At-Cap");
        Assert.Equal(CveCacheBudget.MaxRangesPerCve, atCap.AffectedVersions.Count);
    }

    [Fact]
    public void Apply_NullAffectedVersions_DoesNotThrow()
    {
        var cves = new[]
        {
            new CachedCve
            {
                CveId = "CVE-NullRanges",
                CvssScore = 8.0,
                PublishedDate = Now.AddMonths(-1).ToString("o"),
                AffectedVersions = null!
            }
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(1, r.KeptAfterCap);
        Assert.Equal(0, r.CvesWithTruncatedRanges);
    }

    // -----------------------------------------------------------------------
    // Edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Apply_EmptyInput_ReturnsEmptyResult()
    {
        var r = CveCacheBudget.Apply(Array.Empty<CachedCve>(), Now);

        Assert.Equal(0, r.TotalIn);
        Assert.Equal(0, r.KeptAfterDateFilter);
        Assert.Equal(0, r.KeptAfterCap);
        Assert.Equal(0, r.CvesWithTruncatedRanges);
        Assert.Empty(r.Cves);
    }

    [Fact]
    public void Apply_NullInput_ReturnsEmptyResult()
    {
        var r = CveCacheBudget.Apply(null, Now);

        Assert.Equal(0, r.TotalIn);
        Assert.Empty(r.Cves);
    }

    // -----------------------------------------------------------------------
    // Known-exploited CVEs and the cap
    // -----------------------------------------------------------------------

    private static CachedCve Exploited(string id, double score, DateTime publishedUtc)
    {
        var cve = Cve(id, score, publishedUtc);
        cve.KevAdded = "2026-03-01";
        return cve;
    }

    [Fact]
    public void Apply_KnownExploitedCves_SurviveTheCapAheadOfHigherScores()
    {
        // More critical CVEs in the window than a row holds: by score alone the three exploited
        // ones would be the first to go.
        var cves = Enumerable.Range(1, 105)
            .Select(i => Cve($"CVE-Critical-{i:000}", 9.8, Now.AddMonths(-2)))
            .Concat(new[]
            {
                Exploited("CVE-Kev-1", 5.0, Now.AddMonths(-3)),
                Exploited("CVE-Kev-2", 5.0, Now.AddMonths(-10)),
                Exploited("CVE-Kev-3", 5.0, Now.AddMonths(-20)),
            })
            .ToList();

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(108, r.KeptAfterDateFilter);
        Assert.Equal(CveCacheBudget.MaxCachedCves, r.Cves.Count);
        Assert.Equal(new[] { "CVE-Kev-1", "CVE-Kev-2", "CVE-Kev-3" }, r.Cves.Take(3).Select(c => c.CveId));
        Assert.All(r.Cves.Skip(3), c => Assert.Equal(9.8, c.CvssScore));
    }

    [Fact]
    public void Apply_KnownExploitedCveOutsideTheDateWindow_IsStillDropped()
    {
        var cves = new[]
        {
            Exploited("CVE-Kev-OldMedium", 5.0, Now.AddMonths(-30)),   // below HIGH: 24 months
            Exploited("CVE-Kev-OldCritical", 9.8, Now.AddMonths(-61)), // HIGH and above: 60 months
            Exploited("CVE-Kev-OldHigh", 7.5, Now.AddMonths(-40)),
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal("CVE-Kev-OldHigh", Assert.Single(r.Cves).CveId);
    }

    [Fact]
    public void Apply_OrderAmongTheOtherCves_StaysScoreThenDate()
    {
        var cves = new[]
        {
            Cve("CVE-Medium", 5.0, Now.AddMonths(-1)),
            Cve("CVE-High-Older", 8.0, Now.AddMonths(-12)),
            Exploited("CVE-Kev-Low", 3.1, Now.AddMonths(-6)),
            Cve("CVE-Critical", 9.8, Now.AddMonths(-20)),
            Cve("CVE-High-Newer", 8.0, Now.AddMonths(-1)),
        };

        var r = CveCacheBudget.Apply(cves, Now);

        Assert.Equal(
            new[] { "CVE-Kev-Low", "CVE-Critical", "CVE-High-Newer", "CVE-High-Older", "CVE-Medium" },
            r.Cves.Select(c => c.CveId));
    }
}
