using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure fold behind GET metrics/mcp-usage/organization: one item per account, the
/// delegated (MSP) marker from the row's home tenant, and the three windows (today / month / range).
/// </summary>
public class McpOrganizationUsageAggregationTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string MspHome = "99999999-9999-9999-9999-999999999999";
    private const string Member = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string MspAdmin = "bbbbbbbb-0000-0000-0000-000000000002";

    private static TenantUsageRecord Row(string user, string date, long count, string upn = "", string home = "", DateTime? last = null)
        => new() { TenantId = Tenant, UserId = user, Date = date, RequestCount = count, UserPrincipalName = upn, HomeTenantId = home, LastRequestAt = last };

    [Fact]
    public void Aggregate_MarksDelegatedAccounts_ByForeignHomeTenant()
    {
        var rows = new[]
        {
            Row(Member, "20260902", 10, "alice@contoso.com"),
            Row(MspAdmin, "20260902", 40, "msp@partner.example", MspHome),
        };

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, Tenant, "20260901", "20260902", "20260902", "20260901");

        var msp = Assert.Single(items, i => i.UserId == MspAdmin);
        Assert.True(msp.Delegated);
        Assert.Equal(MspHome, msp.HomeTenantId);
        Assert.Equal("msp@partner.example", msp.UserPrincipalName);
        var member = Assert.Single(items, i => i.UserId == Member);
        Assert.False(member.Delegated);
        Assert.Null(member.HomeTenantId);
    }

    [Fact]
    public void Aggregate_OwnTenantAsHome_IsNotDelegated()
    {
        // Legacy/own rows may carry the tenant itself (or nothing) as home — both mean "own member".
        var rows = new[] { Row(Member, "20260902", 1, home: Tenant), Row(Member, "20260901", 1) };
        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, Tenant, "20260901", "20260902", "20260902", "20260901");
        var item = Assert.Single(items);
        Assert.False(item.Delegated);
        Assert.Null(item.UserPrincipalName);
    }

    [Fact]
    public void Aggregate_Windows_TodayMonthAndRange_AreIndependent()
    {
        var rows = new[]
        {
            Row(Member, "20260815", 100), // in range only (last month)
            Row(Member, "20260901", 20),  // month + range
            Row(Member, "20260902", 5),   // today + month + range
            Row(Member, "20260903", 7),   // read window past the range end (never counted in range)
        };

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, Tenant, "20260815", "20260902", "20260902", "20260901");

        var item = Assert.Single(items);
        Assert.Equal(5, item.RequestsToday);
        Assert.Equal(25, item.RequestsThisMonth);
        Assert.Equal(125, item.RequestsInRange);
    }

    [Fact]
    public void Aggregate_OrdersByThisMonth_AndKeepsLatestRequestTime()
    {
        var older = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Row(Member, "20260901", 3, last: newer),
            Row(Member, "20260902", 1, last: older),
            Row(MspAdmin, "20260902", 50, home: MspHome, last: older),
        };

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, Tenant, "20260901", "20260902", "20260902", "20260901");

        Assert.Equal(new[] { MspAdmin, Member }, items.Select(i => i.UserId));
        Assert.Equal(newer, items[1].LastRequestAt);
    }

    [Theory]
    [InlineData("2026-09-02", "20260902")]
    [InlineData("20260902", "20260902")]
    [InlineData("", null)]
    [InlineData("yesterday", null)]
    [InlineData("2026-9-2", null)]
    public void NormalizeDay_AcceptsIsoAndCompact_RejectsTheRest(string raw, string? expected)
        => Assert.Equal(expected, McpUsageMetricsFunction.NormalizeDay(raw));
}
