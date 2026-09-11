using AutopilotMonitor.Functions.Functions.Metrics;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure folds behind GET metrics/mcp-usage/organization (and its global twin): one item per
/// account, the three windows (today / month / range), and the tenant's organization windows over every
/// account including the purchased-slot breakdown.
/// </summary>
public class McpOrganizationUsageAggregationTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Member = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string OtherMember = "bbbbbbbb-0000-0000-0000-000000000002";

    private static TenantUsageRecord Row(string user, string date, long count, string upn = "", DateTime? last = null)
        => new() { TenantId = Tenant, UserId = user, Date = date, RequestCount = count, UserPrincipalName = upn, LastRequestAt = last };

    [Fact]
    public void Aggregate_OneItemPerAccount_KeepsTheLatestUpn()
    {
        var rows = new[]
        {
            Row(Member, "20260901", 10),                       // written before the UPN column existed
            Row(Member, "20260902", 5, "alice@contoso.com"),
            Row(OtherMember, "20260902", 40, "bob@contoso.com"),
        };

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, "20260901", "20260902", "20260902", "20260901");

        Assert.Equal(2, items.Count);
        var alice = Assert.Single(items, i => i.UserId == Member);
        Assert.Equal("alice@contoso.com", alice.UserPrincipalName);
        Assert.Equal(15, alice.RequestsThisMonth);
        var bob = Assert.Single(items, i => i.UserId == OtherMember);
        Assert.Equal("bob@contoso.com", bob.UserPrincipalName);
    }

    [Fact]
    public void Aggregate_RowsWithoutUpn_LeaveItNull()
    {
        var rows = new[] { Row(Member, "20260902", 1), Row(Member, "20260901", 1) };
        var item = Assert.Single(McpUsageMetricsFunction.AggregateOrganizationUsage(rows, "20260901", "20260902", "20260902", "20260901"));
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

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, "20260815", "20260902", "20260902", "20260901");

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
            Row(OtherMember, "20260902", 50, last: older),
        };

        var items = McpUsageMetricsFunction.AggregateOrganizationUsage(rows, "20260901", "20260902", "20260902", "20260901");

        Assert.Equal(new[] { OtherMember, Member }, items.Select(i => i.UserId));
        Assert.Equal(newer, items[1].LastRequestAt);
    }

    [Fact]
    public void OrganizationQuota_SumsTodayAndMonthOverEveryAccount_AndCarriesTheTenantLimits()
    {
        var rows = new[]
        {
            Row(Member, "20260815", 100),       // last month: neither window
            Row(Member, "20260901", 20),        // month
            Row(Member, "20260902", 2),         // today + month
            Row(OtherMember, "20260902", 5),    // another account counts against the same windows
            Row(Member, "20260903", 7),         // read window past today (range only)
        };

        var quota = McpUsageMetricsFunction.BuildOrganizationQuota(
            rows, new McpQuotaService.TenantPlanLimits("pro", 1500, 15000), "20260902", "20260901");

        Assert.Equal("pro", quota.TenantPlan);
        Assert.Equal(1500, quota.DailyLimit);
        Assert.Equal(15000, quota.MonthlyLimit);
        Assert.Equal(7, quota.DailyUsed);
        Assert.Equal(27, quota.MonthlyUsed);
        // No purchased slot: the breakdown stays off the wire.
        Assert.Null(quota.PurchasedDelegatedSlots);
        Assert.Null(quota.SlotDailyLimit);
        Assert.Null(quota.SlotMonthlyLimit);
    }

    [Fact]
    public void OrganizationQuota_PurchasedSlots_CarryTheBreakdown()
    {
        // 3 000 + 3 × 900 = 5 700 — the limits arrive grown; the slot fields let the page show the sum.
        var quota = McpUsageMetricsFunction.BuildOrganizationQuota(
            new[] { Row(Member, "20260902", 3) },
            new McpQuotaService.TenantPlanLimits("pro", 5700, 114000, PurchasedDelegatedSlots: 3, SlotTenantDailyLimit: 900, SlotTenantMonthlyLimit: 18000),
            "20260902", "20260901");

        Assert.Equal(5700, quota.DailyLimit);
        Assert.Equal(3, quota.PurchasedDelegatedSlots);
        Assert.Equal(900, quota.SlotDailyLimit);
        Assert.Equal(18000, quota.SlotMonthlyLimit);
    }

    [Fact]
    public void OrganizationDaily_SumsEveryAccountPerDay_InsideTheRange_OldestFirst()
    {
        var rows = new[]
        {
            Row(Member, "20260815", 100),       // before the range
            Row(Member, "20260902", 2),
            Row(OtherMember, "20260902", 5),    // same day, another account — one bar
            Row(Member, "20260901", 20),
            Row(Member, "20260903", 7),         // after the range (read window)
        };

        var daily = McpUsageMetricsFunction.BuildOrganizationDaily(rows, "20260901", "20260902");

        Assert.Equal(new[] { ("20260901", 20L), ("20260902", 7L) }, daily.Select(d => (d.Date, d.Requests)));
    }

    [Fact]
    public void OrganizationQuota_LiftedWindows_StillReportTheCounters()
    {
        var quota = McpUsageMetricsFunction.BuildOrganizationQuota(
            new[] { Row(Member, "20260902", 3) }, new McpQuotaService.TenantPlanLimits("pro", 0, 0), "20260902", "20260901");

        Assert.Equal(0, quota.DailyLimit);
        Assert.Equal(0, quota.MonthlyLimit);
        Assert.Equal(3, quota.DailyUsed);
        Assert.Equal(3, quota.MonthlyUsed);
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
