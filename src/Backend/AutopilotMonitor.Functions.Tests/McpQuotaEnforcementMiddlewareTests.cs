using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the pure seams of <see cref="McpQuotaEnforcementMiddleware"/>: which tenant a request is
/// charged to ("the budget follows the data"), the bound-narrowing isolation invariant, and the 429 texts
/// that name whose budget is exhausted.
/// </summary>
public class McpQuotaEnforcementMiddlewareTests
{
    private const string Home = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TenantB = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string TenantC = "cccccccc-0000-0000-0000-000000000003";
    private const string TenantD = "dddddddd-0000-0000-0000-000000000004";
    private static readonly DateTime Now = new(2026, 9, 2, 15, 0, 0, DateTimeKind.Utc);

    // ── ResolveChargeScope ─────────────────────────────────────────────────────────

    [Fact]
    public void Member_ReadingOwnTenant_ChargesHome()
    {
        var ctx = new RequestContext { TenantId = Home, TargetTenantId = Home };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.Home, scope.Kind);
        Assert.Null(scope.TargetTenantId);
    }

    [Fact]
    public void Delegated_SingleManagedTarget_ChargesThatTarget()
    {
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = TenantB, IsDelegatedReader = true,
            AllowedTenantIds = new[] { TenantB, TenantC },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.SingleTarget, scope.Kind);
        Assert.Equal(TenantB, scope.TargetTenantId);
    }

    [Fact]
    public void Delegated_ReadingHomeTenant_ChargesHome()
    {
        // The subset tier admits a delegated caller for ?tenantId=<home> too (crossTenant=false) — that read
        // is the caller's OWN tenant and draws on its own budget.
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = Home, IsDelegatedReader = true,
            AllowedTenantIds = new[] { TenantB },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.Home, scope.Kind);
    }

    [Fact]
    public void Delegated_TargetReachedThroughHomeChargedGroup_ChargesHome()
    {
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = TenantC, IsDelegatedReader = true,
            AllowedTenantIds = new[] { TenantB, TenantC },
            HomeChargedTenantIds = new[] { TenantC },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.Home, scope.Kind);
        Assert.Null(scope.TargetTenantId);
    }

    [Fact]
    public void Delegated_FleetAggregate_ChargesEveryManagedTenant()
    {
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = Home, IsDelegatedReader = true, IsDelegatedAggregate = true,
            AllowedTenantIds = new[] { TenantB, TenantC },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.BoundedAggregate, scope.Kind);
        Assert.Equal(new[] { TenantB, TenantC }, scope.ChargeMap!.Keys.OrderBy(k => k));
        Assert.Equal(new[] { TenantB }, scope.TargetsOf(new[] { TenantB }));
    }

    [Fact]
    public void Delegated_FleetAggregate_FoldsHomeChargedTenantsOntoHome()
    {
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = Home, IsDelegatedReader = true, IsDelegatedAggregate = true,
            AllowedTenantIds = new[] { TenantB, TenantC, TenantD },
            HomeChargedTenantIds = new[] { TenantC, TenantD },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.BoundedAggregate, scope.Kind);
        Assert.Equal(new[] { Home, TenantB }, scope.ChargeMap!.Keys.OrderBy(k => k));
        // Home is charged ONCE for both operator-managed tenants; they are served or dropped together.
        Assert.Equal(new[] { TenantC, TenantD }, scope.TargetsOf(new[] { Home }).OrderBy(t => t));
        Assert.Equal(new[] { TenantB }, scope.TargetsOf(new[] { TenantB }));
    }

    [Fact]
    public void Delegated_SubsetTierDirectoryListing_IsNotAnAggregate_ChargesHome()
    {
        // config/all: subset tier but TenantScoping.None → the policy middleware never marks it an aggregate.
        var ctx = new RequestContext
        {
            TenantId = Home, TargetTenantId = Home, IsDelegatedReader = true, IsDelegatedAggregate = false,
            AllowedTenantIds = new[] { TenantB, TenantC },
        };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.Home, scope.Kind);
    }

    [Fact]
    public void GlobalAdmin_IsNeverDelegated_ResolvesHome()
    {
        // The policy middleware short-circuits delegated resolution for platform scope, so a GA reading
        // ?tenantId=<customer> carries no delegated flags — the charge scope is Home (and Invoke tracks
        // GA on home only, exempt from the check).
        var ctx = new RequestContext { TenantId = Home, TargetTenantId = TenantB, IsGlobalAdmin = true };
        var scope = McpQuotaEnforcementMiddleware.ResolveChargeScope(ctx, Home);
        Assert.Equal(McpChargeKind.Home, scope.Kind);
    }

    // ── Narrow — ISOLATION INVARIANT ───────────────────────────────────────────────

    [Fact]
    public void Narrow_WithoutBound_LeavesContextUntouched()
    {
        // null = unbounded (GA/Reader). Narrowing must neither introduce a bound nor touch the context.
        var ctx = new RequestContext { TenantId = Home, AllowedTenantIds = null };
        var narrowed = McpQuotaEnforcementMiddleware.Narrow(ctx, new[] { TenantB }, new[] { TenantC });
        Assert.Same(ctx, narrowed);
        Assert.Null(narrowed.AllowedTenantIds);
        Assert.Null(narrowed.QuotaExcludedTenantIds);
    }

    [Theory]
    [InlineData(new[] { TenantB, TenantC }, new[] { TenantB }, new[] { TenantC })]
    [InlineData(new[] { TenantB, TenantC }, new string[0], new[] { TenantB, TenantC })]
    [InlineData(new[] { TenantB, TenantC }, new[] { TenantB, TenantC }, new string[0])]
    [InlineData(new[] { TenantB }, new[] { TenantB, TenantD }, new[] { TenantD })]
    [InlineData(new[] { TenantB, TenantC, TenantD }, new[] { TenantD, TenantB }, new[] { TenantC })]
    public void Narrow_NeverWidens_NeverNull(string[] bound, string[] admitted, string[] excluded)
    {
        var ctx = new RequestContext { TenantId = Home, AllowedTenantIds = bound, IsDelegatedReader = true };

        var narrowed = McpQuotaEnforcementMiddleware.Narrow(ctx, admitted, excluded);

        Assert.NotNull(narrowed.AllowedTenantIds);
        var boundSet = new HashSet<string>(bound, StringComparer.OrdinalIgnoreCase);
        Assert.All(narrowed.AllowedTenantIds!, t => Assert.Contains(t, boundSet));
        Assert.True(narrowed.AllowedTenantIds!.Count <= bound.Length);
        // A foreign tenant sneaking into "admitted" never widens the bound (TenantD ∉ bound in row 4).
        Assert.DoesNotContain(narrowed.AllowedTenantIds!, t => !boundSet.Contains(t));
        if (narrowed.QuotaExcludedTenantIds != null)
            Assert.All(narrowed.QuotaExcludedTenantIds, t => Assert.Contains(t, boundSet));
        // The rest of the context survives the copy.
        Assert.True(narrowed.IsDelegatedReader);
        Assert.Equal(Home, narrowed.TenantId);
    }

    [Fact]
    public void Narrow_EmptyAdmittedSet_PublishesEmptyBound_NotNull()
    {
        // Empty ⇒ the repository serves an empty page. NEVER null (= all tenants).
        var ctx = new RequestContext { TenantId = Home, AllowedTenantIds = new[] { TenantB, TenantC } };
        var narrowed = McpQuotaEnforcementMiddleware.Narrow(ctx, Array.Empty<string>(), new[] { TenantB, TenantC });
        Assert.NotNull(narrowed.AllowedTenantIds);
        Assert.Empty(narrowed.AllowedTenantIds!);
        Assert.Equal(new[] { TenantB, TenantC }, narrowed.QuotaExcludedTenantIds!.OrderBy(t => t));
    }

    [Fact]
    public void Narrow_NothingExcluded_OmitsTheExcludedSet()
    {
        var ctx = new RequestContext { TenantId = Home, AllowedTenantIds = new[] { TenantB, TenantC } };
        var narrowed = McpQuotaEnforcementMiddleware.Narrow(ctx, new[] { TenantB, TenantC }, Array.Empty<string>());
        Assert.Equal(2, narrowed.AllowedTenantIds!.Count);
        Assert.Null(narrowed.QuotaExcludedTenantIds);
    }

    // ── BuildExceededResponse — whose budget ───────────────────────────────────────

    private static McpQuotaDecision Blocked(string tenantPlan, string? target, long tenantDailyUsed = 300, int tenantDaily = 300)
        => McpQuotaService.BuildDecision(
            new McpPlanLimits("pro", 1000, 20000, tenantPlan, tenantDaily, 60000),
            dailyUsed: 5, monthlyUsed: 50, tenantDailyUsed: tenantDailyUsed, tenantMonthlyUsed: 400,
            Now, target);

    [Fact]
    public void ManagedCommunityTarget_NamesTheTenant_AndTheUpgradePath()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", TenantB), "customer.example");

        Assert.Equal("tenant", body.Level);
        Assert.Equal(TenantB, body.TargetTenantId);
        Assert.Contains("managed tenant 'customer.example'", body.Message);
        Assert.Contains("tenant plan 'community'", body.Message);
        Assert.Contains("Upgrading that tenant to Pro", body.Message);
        Assert.DoesNotContain("your organization", body.Message);
    }

    [Fact]
    public void ManagedTarget_WithoutLabel_FallsBackToTheId()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", TenantB));
        Assert.Contains($"managed tenant '{TenantB}'", body.Message);
    }

    [Fact]
    public void ManagedProTarget_OmitsTheUpgradeHint()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("pro", TenantB, 3000, 3000), "customer.example");
        Assert.Equal(TenantB, body.TargetTenantId);
        Assert.DoesNotContain("Upgrading", body.Message);
        Assert.Contains("shared by all its members and delegated admins", body.Message);
    }

    [Fact]
    public void OwnTenant_KeepsTheOrganizationWording_AndNoTarget()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", target: null));
        Assert.Null(body.TargetTenantId);
        Assert.Contains("of your organization", body.Message);
        Assert.DoesNotContain("managed tenant", body.Message);
    }

    [Fact]
    public void OwnCommunityTenant_NamesTheUpgradePath()
    {
        // The quota is the upgrade lever: a member blocked by their own Community organization window is told
        // that Community is sized for occasional use and that Pro lifts it — Pro members never see the hint.
        var community = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", target: null));
        Assert.Contains("sized for occasional use", community.Message);
        Assert.Contains("upgrading your organization to Pro", community.Message);
        Assert.EndsWith("Resets at 2026-09-03T00:00:00Z.", community.Message);

        var pro = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("pro", target: null, 3000, 3000));
        Assert.DoesNotContain("upgrading", pro.Message);
        Assert.DoesNotContain("occasional use", pro.Message);
    }

    [Fact]
    public void ManagedCommunityTarget_SaysWhosePlanGoverns()
    {
        // The delegated admin must learn that the CUSTOMER's plan (not their own Pro/MSP plan) governs the window.
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", TenantB), "customer.example");
        Assert.Contains("on the Community plan", body.Message);
        Assert.Contains("its own plan governs this window, not yours", body.Message);
        Assert.Contains("Upgrading that tenant to Pro", body.Message);
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
        Assert.StartsWith($"MCP daily request quota exceeded for plan '{userPlan}'.", body.Message);
        Assert.Equal(expectHint, body.Message.Contains("Pro raises your daily and monthly windows"));
        Assert.EndsWith("Resets at 2026-09-03T00:00:00Z.", body.Message);
    }

    [Fact]
    public void AllManagedTenantsExhausted_NamesTheCount_AndNoSingleTarget()
    {
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(Blocked("community", TenantB), exhaustedTenantCount: 3);
        Assert.Null(body.TargetTenantId);
        Assert.Equal("tenant", body.Level);
        Assert.Contains("all 3 managed tenants", body.Message);
        Assert.Contains("Earliest reset at 2026-09-03T00:00:00Z", body.Message);
    }

    [Fact]
    public void UserLevelBlock_IgnoresTargetAndCount()
    {
        // The caller's OWN budget is exhausted — the target tenant is irrelevant and must not be named.
        var decision = McpQuotaService.BuildDecision(
            new McpPlanLimits("community", 100, 3000, "pro", 3000, 60000),
            dailyUsed: 100, monthlyUsed: 500, tenantDailyUsed: 1, tenantMonthlyUsed: 1, Now, TenantB);
        var body = McpQuotaEnforcementMiddleware.BuildExceededResponse(decision, "customer.example", exhaustedTenantCount: 2);
        Assert.Equal("user", body.Level);
        Assert.Null(body.TargetTenantId);
        Assert.Equal(
            "MCP daily request quota exceeded for plan 'community'. The Community plan is sized for occasional use; Pro raises your daily and monthly windows. Resets at 2026-09-03T00:00:00Z.",
            body.Message);
    }
}
