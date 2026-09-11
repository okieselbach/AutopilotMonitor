using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure seams of <see cref="McpQuotaEnforcementMiddleware"/>: who bypasses the check (the Global
/// Admin only — counted, never blocked) and the 429 texts that name whose window is exhausted. Every request is
/// charged to the caller's HOME tenant ("the budget follows the delegating tenant"), so no seam names a target
/// tenant any more.
/// </summary>
public class McpQuotaEnforcementMiddlewareTests
{
    private const string Home = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TenantB = "bbbbbbbb-0000-0000-0000-000000000002";
    private static readonly DateTime Now = new(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc);

    // ── IsExempt — the Global Admin runs, the Global Reader is quota'd ─────────────

    [Fact]
    public void GlobalAdmin_IsExempt_EvenOnAForeignTarget()
    {
        // Platform operations must never be blocked by a budget; Invoke still counts the GA on home.
        var ctx = new RequestContext { TenantId = Home, TargetTenantId = TenantB, IsGlobalAdmin = true };
        Assert.True(McpQuotaEnforcementMiddleware.IsExempt(ctx));
    }

    [Fact]
    public void GlobalReader_IsNotExempt()
    {
        // A customer-facing read role: it stays within its home tenant's windows like every member.
        var ctx = new RequestContext { TenantId = Home, TargetTenantId = TenantB, IsGlobalReader = true };
        Assert.False(McpQuotaEnforcementMiddleware.IsExempt(ctx));
    }

    [Fact]
    public void Member_And_DelegatedReader_AreNotExempt()
    {
        Assert.False(McpQuotaEnforcementMiddleware.IsExempt(new RequestContext { TenantId = Home, TargetTenantId = Home }));
        Assert.False(McpQuotaEnforcementMiddleware.IsExempt(new RequestContext
        {
            TenantId = Home, TargetTenantId = TenantB, IsDelegatedReader = true, AllowedTenantIds = new[] { TenantB },
        }));
    }

    // ── BuildExceededResponse — whose budget ───────────────────────────────────────

    private static McpQuotaDecision Blocked(string tenantPlan, long tenantDailyUsed = 300, int tenantDaily = 300)
        => McpQuotaService.BuildDecision(
            new McpPlanLimits("pro", 1000, 20000, tenantPlan, tenantDaily, 60000),
            dailyUsed: 5, monthlyUsed: 50, tenantDailyUsed: tenantDailyUsed, tenantMonthlyUsed: 400,
            Now);

    [Fact]
    public void OrganizationBlock_KeepsTheOrganizationWording()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community"));
        Assert.Equal("tenant", body.Level);
        Assert.Contains("of your organization", body.Error);
        Assert.Contains("tenant plan 'community'", body.Error);
        Assert.DoesNotContain("managed tenant", body.Error);
    }

    [Fact]
    public void OrganizationBlock_OnCommunity_NamesTheUpgradePath()
    {
        // The quota is the upgrade lever: a member blocked by their own Community organization window is told
        // that Community is sized for occasional use and that Pro lifts it — Pro members never see the hint.
        var community = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community"));
        Assert.Contains("sized for occasional use", community.Error);
        Assert.Contains("upgrading your organization to Pro", community.Error);
        Assert.EndsWith("Resets at 2026-09-03T00:00:00Z.", community.Error);

        var pro = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("pro", 3000, 3000));
        Assert.DoesNotContain("upgrading", pro.Error);
        Assert.DoesNotContain("occasional use", pro.Error);
        Assert.Contains("shared by all its members", pro.Error);
    }

    [Theory]
    [InlineData("community", true)]
    [InlineData("Community", true)]
    [InlineData("pro", false)]
    [InlineData("power", false)] // per-user override plan: an individual budget, "upgrade to Pro" would be wrong advice
    public void UserLevelBlock_HintsProOnlyForTheCommunityEdition(string userPlan, bool expectHint)
    {
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits(userPlan, 100, 3000, "pro", 3000, 60000),
            dailyUsed: 100, monthlyUsed: 500, tenantDailyUsed: 1, tenantMonthlyUsed: 1, Now);
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(decision);
        Assert.Equal("user", body.Level);
        Assert.StartsWith($"MCP daily request quota exceeded for plan '{userPlan}'.", body.Error);
        Assert.Equal(expectHint, body.Error.Contains("Pro raises your daily and monthly windows"));
        Assert.EndsWith("Resets at 2026-09-03T00:00:00Z.", body.Error);
    }

    [Fact]
    public void UserLevelBlock_ReportsTheUserWindow()
    {
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("community", 100, 3000, "pro", 3000, 60000),
            dailyUsed: 100, monthlyUsed: 500, tenantDailyUsed: 1, tenantMonthlyUsed: 1, Now);
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(decision);
        Assert.Equal(100, body.Limit);
        Assert.Equal(100, body.Used);
        Assert.Equal(
            "MCP daily request quota exceeded for plan 'community'. The Community plan is sized for occasional use; Pro raises your daily and monthly windows. Resets at 2026-09-03T00:00:00Z.",
            body.Error);
    }
}
