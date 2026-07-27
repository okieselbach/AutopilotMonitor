using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// F3 PR6 (insights spec §F3): the Wilson primitive (hand-computed vectors), the radar's
/// detection gates and EVERY suppression branch (table-driven), the re-arm rule, the
/// dimension-concentration gates, the tracker keyspace round-trip (tri-states included)
/// and the alert wording contracts ("correlated, not necessarily causal").
/// </summary>
public class RuleRegressionRadarTests
{
    private static readonly DateTime Target = new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
    private const string TenantA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string RuleX = "ANALYZE-NET-001";

    // ── Wilson interval (MetricsMath) — hand-computed 95 % vectors ──────────

    [Theory]
    [InlineData(10, 20, 0.2993, 0.7007)]  // symmetric mid-range
    [InlineData(0, 20, 0.0000, 0.1611)]   // zero successes: lower pinned at 0
    [InlineData(5, 25, 0.0886, 0.3913)]
    public void WilsonInterval_MatchesHandComputedVectors(int successes, int trials, double lower, double upper)
    {
        var (l, u) = MetricsMath.WilsonInterval(successes, trials);
        Assert.Equal(lower, l, 3);
        Assert.Equal(upper, u, 3);
    }

    [Fact]
    public void WilsonInterval_ZeroTrials_IsUninformative_NeverClaimsSeparation()
    {
        Assert.Equal((0.0, 1.0), MetricsMath.WilsonInterval(0, 0));
        // A zero-denominator window can never be "separated" from anything.
        Assert.False(MetricsMath.RateIncreaseSeparated(0, 0, 10, 500));
    }

    [Fact]
    public void RateIncreaseSeparated_DisjointIntervals_True_OverlappingFalse()
    {
        // 15 % on n=100 (lower ≈ 0.093) vs 2 % baseline on n=500 (upper ≈ 0.036) → separated.
        Assert.True(MetricsMath.RateIncreaseSeparated(15, 100, 10, 500));
        // 5 % on n=100 vs 4 % baseline on n=500 → intervals overlap → noise, not a regression.
        Assert.False(MetricsMath.RateIncreaseSeparated(5, 100, 20, 500));
    }

    // ── radar evaluation — gates + suppression branches ─────────────────────

    private static RuleStatsEntry Entry(
        string date, int fire, int sessions, string ruleId = RuleX, string ruleType = "analyze")
        => new()
        {
            TenantId = TenantA,
            Date = date,
            RuleId = ruleId,
            RuleType = ruleType,
            RuleTitle = "Network check",
            Category = "network",
            Severity = "warning",
            FireCount = fire,
            EvaluationCount = sessions,
            SessionsEvaluated = sessions,
        };

    private static string Day(int daysBeforeTarget) => Target.AddDays(-daysBeforeTarget).ToString("yyyy-MM-dd");

    /// <summary>Window fire=15/100 across two days; baseline fire=10/500 across three days — fires all gates.</summary>
    private static List<RuleStatsEntry> RegressedEntries() => new()
    {
        Entry(Day(0), 10, 60),
        Entry(Day(6), 5, 40),      // windowStart boundary — still window
        Entry(Day(7), 4, 200),     // first baseline day
        Entry(Day(20), 4, 200),
        Entry(Day(34), 2, 100),    // baselineStart boundary — still baseline
    };

    private static Dictionary<string, RuleRegressionRadar.RuleTimestamps> OldRule(
        DateTime? createdAt = null, DateTime? updatedAt = null, string ruleId = RuleX)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [ruleId] = new RuleRegressionRadar.RuleTimestamps(
                createdAt ?? Target.AddDays(-100), updatedAt ?? Target.AddDays(-50)),
        };

    [Fact]
    public void Evaluate_AllGatesPass_FiresWithFullNumbers()
    {
        var findings = RuleRegressionRadar.Evaluate(RegressedEntries(), Target, OldRule());

        var finding = Assert.Single(findings);
        Assert.Equal(RuleX, finding.RuleId);
        Assert.Equal(15, finding.WindowFireCount);
        Assert.Equal(100, finding.WindowSessionCount);
        Assert.Equal(10, finding.BaselineFireCount);
        Assert.Equal(500, finding.BaselineSessionCount);
        Assert.Equal(15.0, finding.WindowRatePct);
        Assert.Equal(2.0, finding.BaselineRatePct);
        Assert.Equal(7.5, finding.Lift);
        Assert.Equal(Day(6), finding.WindowStartDate);
        Assert.Equal(Day(0), finding.WindowEndDate);
    }

    [Fact]
    public void Evaluate_ZeroBaselineFires_FiresAsNewSignal_WithNullLift()
    {
        var entries = new List<RuleStatsEntry>
        {
            Entry(Day(0), 15, 100),
            Entry(Day(10), 0, 500),
        };

        var finding = Assert.Single(RuleRegressionRadar.Evaluate(entries, Target, OldRule()));
        Assert.Null(finding.Lift); // no finite lift is ever invented (truthfulness rule 1)
        Assert.Equal(0.0, finding.BaselineRatePct);
    }

    public static TheoryData<string, List<RuleStatsEntry>> SuppressedByCounts() => new()
    {
        // Below the ≥5 hit-session gate.
        { "below hit gate", new List<RuleStatsEntry> { Entry(Day(0), 4, 100), Entry(Day(10), 1, 500) } },
        // Below the ≥20 evaluated-session gate.
        { "below session gate", new List<RuleStatsEntry> { Entry(Day(0), 5, 19), Entry(Day(10), 1, 500) } },
        // No baseline denominator at all (rule disabled through the baseline, re-enabled recently).
        { "no baseline sessions", new List<RuleStatsEntry> { Entry(Day(0), 15, 100) } },
        // Lift below 2× (10 % vs 6 %).
        { "lift below 2x", new List<RuleStatsEntry> { Entry(Day(0), 10, 100), Entry(Day(10), 30, 500) } },
        // Lift 2.1× but Wilson intervals overlap (5/20 vs 60/500) — small-n noise never alerts.
        { "not separated", new List<RuleStatsEntry> { Entry(Day(0), 5, 20), Entry(Day(10), 60, 500) } },
        // Gather rows are ignored entirely (audit Q6: per-batch dedup — bad math until fixed).
        { "gather ignored", new List<RuleStatsEntry>
            { Entry(Day(0), 50, 60, ruleType: "gather"), Entry(Day(10), 1, 500, ruleType: "gather") } },
    };

    [Theory]
    [MemberData(nameof(SuppressedByCounts))]
    public void Evaluate_SuppressionBranches_ProduceNoFinding(string branch, List<RuleStatsEntry> entries)
    {
        Assert.True(RuleRegressionRadar.Evaluate(entries, Target, OldRule()).Count == 0,
            $"branch '{branch}' must not fire");
    }

    [Fact]
    public void Evaluate_RuleEntityGates_Suppress()
    {
        var entries = RegressedEntries();

        // Deleted rule (no entity) never alerts.
        Assert.Empty(RuleRegressionRadar.Evaluate(
            entries, Target, new Dictionary<string, RuleRegressionRadar.RuleTimestamps>()));

        // Grace period: created inside baseline+window (< 34 days before target).
        Assert.Empty(RuleRegressionRadar.Evaluate(
            entries, Target, OldRule(createdAt: Target.AddDays(-20))));

        // Edited inside the 7-day window — edits legitimately change hit rates.
        Assert.Empty(RuleRegressionRadar.Evaluate(
            entries, Target, OldRule(updatedAt: Target.AddDays(-2))));

        // Edited just BEFORE the window → not suppressed.
        Assert.Single(RuleRegressionRadar.Evaluate(
            entries, Target, OldRule(updatedAt: Target.AddDays(-8))));
    }

    [Fact]
    public void SumWindows_BoundaryDates_LandInTheRightBucket()
    {
        var entries = new List<RuleStatsEntry>
        {
            Entry(Target.AddDays(1).ToString("yyyy-MM-dd"), 100, 100), // future — ignored
            Entry(Day(0), 1, 10),
            Entry(Day(6), 2, 20),   // last window day
            Entry(Day(7), 4, 40),   // first baseline day
            Entry(Day(34), 8, 80),  // last baseline day
            Entry(Day(35), 100, 100), // beyond horizon — ignored
        };

        var sums = RuleRegressionRadar.SumWindows(entries, Target);
        Assert.Equal(3, sums.WindowFire);
        Assert.Equal(30, sums.WindowSessions);
        Assert.Equal(12, sums.BaselineFire);
        Assert.Equal(120, sums.BaselineSessions);
    }

    // ── re-arm rule ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldReArm_FiresStopped_OrRateBackUnderOnePointFive()
    {
        // Nothing fired in the window anymore → re-arm (also covers "no stats rows at all").
        Assert.True(RuleRegressionRadar.ShouldReArm(new List<RuleStatsEntry>(), Target));
        Assert.True(RuleRegressionRadar.ShouldReArm(
            new List<RuleStatsEntry> { Entry(Day(0), 0, 100), Entry(Day(10), 10, 500) }, Target));

        // 2 % window vs 2 % baseline → 1.0× < 1.5 → re-arm.
        Assert.True(RuleRegressionRadar.ShouldReArm(
            new List<RuleStatsEntry> { Entry(Day(0), 2, 100), Entry(Day(10), 10, 500) }, Target));

        // 4 % window vs 2 % baseline → 2.0× ≥ 1.5 → still elevated, keep the episode.
        Assert.False(RuleRegressionRadar.ShouldReArm(
            new List<RuleStatsEntry> { Entry(Day(0), 4, 100), Entry(Day(10), 10, 500) }, Target));

        // Zero-baseline episode ("new signal"): no rate threshold to fall under — only the
        // fires-stopped branch re-arms it.
        Assert.False(RuleRegressionRadar.ShouldReArm(
            new List<RuleStatsEntry> { Entry(Day(0), 3, 100), Entry(Day(10), 0, 500) }, Target));
    }

    // ── dimension concentration ─────────────────────────────────────────────

    private static SessionSummary DimSession(string osBuild = "", string model = "", string agentVersion = "")
        => new()
        {
            TenantId = TenantA,
            SessionId = Guid.NewGuid().ToString(),
            SerialNumber = "PF4X1ABC",
            OsBuild = osBuild,
            Manufacturer = "Contoso",
            Model = model,
            AgentVersion = agentVersion,
            ImeAgentVersion = "",
            StartedAt = Target,
        };

    [Fact]
    public void DimensionConcentration_DominantOsBuild_ReportsBothShares()
    {
        var all = new List<SessionSummary>();
        for (var i = 0; i < 20; i++) all.Add(DimSession(osBuild: "26100.4652"));
        for (var i = 0; i < 80; i++) all.Add(DimSession(osBuild: "22631.3880"));
        var hits = all.Take(16).Concat(all.Skip(20).Take(4)).ToList(); // 16 on the new build, 4 on the old

        var result = RuleRegressionRadar.ComputeDimensionConcentration(hits, all)!;

        Assert.Equal("osBuild", result.Dimension);
        Assert.Equal("26100.4652", result.Value);
        Assert.Equal(16, result.HitCount);
        Assert.Equal(80.0, result.HitSharePct);
        Assert.Equal(20.0, result.AllSharePct);
        Assert.Equal(4.0, result.Lift);
    }

    [Fact]
    public void DimensionConcentration_BelowGates_YieldsNull_NeverStretched()
    {
        var all = Enumerable.Range(0, 100).Select(_ => DimSession(osBuild: "26100.1")).ToList();

        // Every session shares the build → lift 1.0 < 2 → no claim.
        Assert.Null(RuleRegressionRadar.ComputeDimensionConcentration(all.Take(10).ToList(), all));

        // Concentrated but only 4 hit sessions (< 5) → no claim.
        var mixed = Enumerable.Range(0, 96).Select(_ => DimSession(osBuild: "22631.1"))
            .Concat(Enumerable.Range(0, 4).Select(_ => DimSession(osBuild: "26100.9")))
            .ToList();
        var hits4 = mixed.Skip(96).ToList();
        Assert.Null(RuleRegressionRadar.ComputeDimensionConcentration(hits4, mixed));

        // No hit sessions at all → no claim.
        Assert.Null(RuleRegressionRadar.ComputeDimensionConcentration(new List<SessionSummary>(), all));
    }

    [Fact]
    public void DimensionConcentration_PicksTheHighestLiftAcrossDimensions()
    {
        var all = new List<SessionSummary>();
        for (var i = 0; i < 25; i++) all.Add(DimSession(osBuild: "26100.1", model: "Laptop 5"));
        for (var i = 0; i < 75; i++) all.Add(DimSession(osBuild: "22631.1", model: i < 25 ? "Laptop 5" : "Laptop 4"));
        // Hits: 8 of 10 on the new build (lift 80/25 = 3.2); model "Contoso Laptop 5" is 50 % of
        // fleet and 80 % of hits (lift 1.6 — below gate).
        var hits = all.Take(8).Concat(all.Skip(25).Take(2)).ToList();

        var result = RuleRegressionRadar.ComputeDimensionConcentration(hits, all)!;
        Assert.Equal("osBuild", result.Dimension);
        Assert.Equal(3.2, result.Lift);
    }

    // ── tracker keyspace (round-trip + key shape) ───────────────────────────

    [Fact]
    public void RuleRegressionRowKey_PrefixedAndCaseFolded()
    {
        Assert.Equal("ruleregression|analyze-net-001",
            TableHardwareRejectionNotificationTracker.BuildRuleRegressionRowKey("  ANALYZE-NET-001 "));
    }

    [Fact]
    public void RuleRegressionEntity_RoundTripsAllFields_IncludingDimension()
    {
        var alert = new RuleRegressionAlert
        {
            TenantId = TenantA,
            RuleId = RuleX,
            RuleTitle = "Network check",
            WindowFireCount = 15,
            WindowSessionCount = 100,
            BaselineFireCount = 10,
            BaselineSessionCount = 500,
            WindowRatePct = 15.0,
            BaselineRatePct = 2.0,
            Lift = 7.5,
            WindowStartDate = "2026-07-20",
            WindowEndDate = "2026-07-26",
            Dimension = new RuleRegressionDimension
            {
                Dimension = "osBuild", Value = "26100.4652",
                HitCount = 12, HitSharePct = 80.0, AllSharePct = 20.0, Lift = 4.0,
            },
            FirstNotifiedAt = Target,
            LastEvaluatedAt = Target.AddHours(2),
        };

        var entity = TableHardwareRejectionNotificationTracker.BuildRuleRegressionEntity(TenantA, alert);
        Assert.Equal(TenantA.ToLowerInvariant(), entity.PartitionKey);
        Assert.Equal("ruleregression|analyze-net-001", entity.RowKey);

        var mapped = TableHardwareRejectionNotificationTracker.MapToRuleRegressionAlert(entity);
        Assert.Equal(RuleX, mapped.RuleId);
        Assert.Equal("Network check", mapped.RuleTitle);
        Assert.Equal(15, mapped.WindowFireCount);
        Assert.Equal(100, mapped.WindowSessionCount);
        Assert.Equal(10, mapped.BaselineFireCount);
        Assert.Equal(500, mapped.BaselineSessionCount);
        Assert.Equal(15.0, mapped.WindowRatePct);
        Assert.Equal(2.0, mapped.BaselineRatePct);
        Assert.Equal(7.5, mapped.Lift);
        Assert.Equal("2026-07-20", mapped.WindowStartDate);
        Assert.Equal("2026-07-26", mapped.WindowEndDate);
        Assert.Equal(Target, mapped.FirstNotifiedAt);
        Assert.Equal(Target.AddHours(2), mapped.LastEvaluatedAt);
        Assert.NotNull(mapped.Dimension);
        Assert.Equal("osBuild", mapped.Dimension!.Dimension);
        Assert.Equal(4.0, mapped.Dimension.Lift);
    }

    [Fact]
    public void RuleRegressionEntity_TriStates_AbsentColumnsMapToNull_NeverInvented()
    {
        var alert = new RuleRegressionAlert
        {
            TenantId = TenantA, RuleId = RuleX,
            Lift = null,       // new signal — no finite lift
            Dimension = null,  // no clear concentration
            FirstNotifiedAt = Target, LastEvaluatedAt = Target,
        };

        var entity = TableHardwareRejectionNotificationTracker.BuildRuleRegressionEntity(TenantA, alert);
        Assert.False(entity.ContainsKey("Lift"));
        Assert.False(entity.ContainsKey("DimensionJson"));

        var mapped = TableHardwareRejectionNotificationTracker.MapToRuleRegressionAlert(entity);
        Assert.Null(mapped.Lift);
        Assert.Null(mapped.Dimension);
    }

    // ── wording contracts ───────────────────────────────────────────────────

    [Fact]
    public void DescribeDimension_UsesCorrelationWording_NeverCausal()
    {
        var text = MaintenanceService.DescribeDimension(new RuleRegressionDimension
        {
            Dimension = "osBuild", Value = "26100.4652",
            HitCount = 12, HitSharePct = 87.0, AllSharePct = 22.0, Lift = 4.0,
        })!;

        Assert.Contains("87% of affected sessions are on osBuild 26100.4652", text);
        Assert.Contains("vs 22% of all sessions (lift 4x)", text);
        Assert.Contains("correlated, not necessarily causal", text);
        Assert.Null(MaintenanceService.DescribeDimension(null));
    }

    [Fact]
    public void BuildRegressionMessage_CarriesFullNumbers_AndHonestNoDimensionFallback()
    {
        var finding = new RuleRegressionFinding
        {
            TenantId = TenantA, RuleId = RuleX, RuleTitle = "Network check",
            WindowFireCount = 15, WindowSessionCount = 100, WindowRatePct = 15.0,
            BaselineFireCount = 10, BaselineSessionCount = 500, BaselineRatePct = 2.0,
            Lift = 7.5, WindowStartDate = "2026-07-20", WindowEndDate = "2026-07-26",
        };

        var message = MaintenanceService.BuildRegressionMessage(finding, dimensionSummary: null);
        Assert.Contains("15 of 100 evaluated sessions", message);
        Assert.Contains("(15%)", message);
        Assert.Contains("baseline 2% (10/500 over the prior 28 days)", message);
        Assert.Contains("lift 7.5x", message);
        Assert.Contains("No clear dimension concentration.", message);

        var newSignal = MaintenanceService.BuildRegressionMessage(
            new RuleRegressionFinding
            {
                RuleId = RuleX, RuleTitle = "Network check",
                WindowFireCount = 15, WindowSessionCount = 100, WindowRatePct = 15.0,
                BaselineFireCount = 0, BaselineSessionCount = 500, BaselineRatePct = 0.0,
                Lift = null, WindowStartDate = "2026-07-20", WindowEndDate = "2026-07-26",
            },
            dimensionSummary: "dim summary");
        Assert.Contains("new signal", newSignal);
        Assert.Contains("dim summary.", newSignal);
    }
}
