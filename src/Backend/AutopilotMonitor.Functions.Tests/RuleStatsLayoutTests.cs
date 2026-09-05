using System;
using System.Linq;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>Key layout D-199 for RuleStats plus the legacy-layout detection the readers rely on.</summary>
public class RuleStatsLayoutTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Keys_are_scope_date_partition_and_rule_id_row()
    {
        Assert.Equal($"{Tenant}_2026-09-05", RuleStatsKeys.PartitionKey(Tenant, "2026-09-05"));
        Assert.Equal("global_2026-09-05", RuleStatsKeys.PartitionKey(RuleStatsKeys.GlobalScope, "2026-09-05"));
        Assert.Equal("ANALYZE-APP-001", RuleStatsKeys.RowKey("ANALYZE-APP-001"));
    }

    [Theory]
    [InlineData("2026-09-05", true)]
    [InlineData("2020-01-01", true)]
    [InlineData("global_2026-09-05", false)]
    [InlineData("2026abcd-1111-2222-3333-444444444444_2026-09-05", false)]
    [InlineData("2026-09-5", false)]
    [InlineData("2026_09_05", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Legacy_partition_key_is_exactly_a_bare_date(string? partitionKey, bool expected)
        => Assert.Equal(expected, RuleStatsKeys.IsLegacyPartitionKey(partitionKey));

    [Fact]
    public void Parse_reads_the_new_layout_and_keeps_underscores_in_the_rule_id()
    {
        var (scope, date, ruleId) = RuleStatsKeys.Parse($"{Tenant}_2026-09-05", "CONTOSO_WIFI_001");

        Assert.Equal(Tenant, scope);
        Assert.Equal("2026-09-05", date);
        Assert.Equal("CONTOSO_WIFI_001", ruleId);
    }

    [Fact]
    public void Parse_reads_the_legacy_layout_splitting_at_the_first_underscore()
    {
        var (scope, date, ruleId) = RuleStatsKeys.Parse("2026-09-05", $"{Tenant}_CONTOSO_WIFI_001");

        Assert.Equal(Tenant, scope);
        Assert.Equal("2026-09-05", date);
        Assert.Equal("CONTOSO_WIFI_001", ruleId);
    }

    [Fact]
    public void Legacy_layout_is_consulted_until_the_retention_horizon_only()
    {
        Assert.True(RuleStatsKeys.LegacyLayoutActive(new DateTime(2026, 12, 4, 23, 59, 0, DateTimeKind.Utc)));
        Assert.False(RuleStatsKeys.LegacyLayoutActive(RuleStatsKeys.LegacyLayoutUntil));
    }

    [Fact]
    public void Merge_sums_evaluations_fires_and_confidence_per_rule_and_keeps_the_latest_metadata()
    {
        var increments = new[]
        {
            new RuleStatIncrement("A", "analyze", "old title", "apps", "warning", fired: true, confidenceScore: 80),
            new RuleStatIncrement("B", "analyze", "b", "net", "info", fired: false, confidenceScore: null),
            new RuleStatIncrement("A", "analyze", "new title", "apps", "high", fired: false, confidenceScore: 99),
            new RuleStatIncrement("A", "analyze", "new title", "apps", "high", fired: true, confidenceScore: null),
        };

        var deltas = RuleStatDelta.Merge(increments).OrderBy(d => d.RuleId).ToList();

        var a = deltas[0];
        Assert.Equal(("A", 3, 2, 80L, "new title", "high"), (a.RuleId, a.Evaluations, a.Fires, a.ConfidenceScoreSum, a.RuleTitle, a.Severity));
        var b = deltas[1];
        Assert.Equal(("B", 1, 0, 0L), (b.RuleId, b.Evaluations, b.Fires, b.ConfidenceScoreSum));
    }
}
