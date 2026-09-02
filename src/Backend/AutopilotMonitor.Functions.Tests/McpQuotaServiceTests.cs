using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="McpQuotaService"/>: window math (daily/monthly, reset times), plan
/// precedence (per-user override → tenant edition; SectionUsagePlans definition → catalog
/// fallback), and the fail-open contract on counter errors.
/// </summary>
public class McpQuotaServiceTests
{
    private const string Oid = "00000000-0000-0000-0000-000000000001";
    private const string Upn = "alice@contoso.com";
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime Now = new(2026, 7, 7, 15, 30, 0, DateTimeKind.Utc);

    // ── BuildDecision (pure window math) ─────────────────────────────────────────

    private static McpPlanLimits Limits(
        int daily = 100, int monthly = 3000, int tenantDaily = 300, int tenantMonthly = 9000, string plan = "community")
        => new(plan, daily, monthly, plan, tenantDaily, tenantMonthly);

    private static McpQuotaDecision Decide(
        McpPlanLimits limits, long dailyUsed, long monthlyUsed, long tenantDailyUsed = 0, long tenantMonthlyUsed = 0)
        => McpQuotaService.BuildDecision(limits, dailyUsed, monthlyUsed, tenantDailyUsed, tenantMonthlyUsed, Now);

    [Fact]
    public void BuildDecision_UnderAllLimits_Allowed()
    {
        var d = Decide(Limits(), dailyUsed: 99, monthlyUsed: 500, tenantDailyUsed: 299, tenantMonthlyUsed: 8999);
        Assert.True(d.Allowed);
        Assert.Null(d.Scope);
        Assert.Null(d.Level);
    }

    [Fact]
    public void BuildDecision_DailyExceeded_BlocksWithMidnightReset()
    {
        var d = Decide(Limits(), dailyUsed: 100, monthlyUsed: 500);
        Assert.False(d.Allowed);
        Assert.Equal("daily", d.Scope);
        Assert.Equal("user", d.Level);
        Assert.Equal(100, d.ExceededLimit);
        Assert.Equal(new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_MonthlyExceeded_BlocksWithFirstOfNextMonthReset()
    {
        var d = Decide(Limits(), dailyUsed: 10, monthlyUsed: 3000);
        Assert.False(d.Allowed);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal("user", d.Level);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_MonthlyTakesPrecedenceOverDaily()
    {
        // Both exceeded → report the longer (monthly) window so Retry-After is honest.
        var d = Decide(Limits(), dailyUsed: 100, monthlyUsed: 3000);
        Assert.Equal("monthly", d.Scope);
    }

    [Fact]
    public void BuildDecision_ZeroLimit_MeansUnlimitedForThatWindow()
    {
        var d = Decide(Limits(0, 0, 0, 0, "custom"), dailyUsed: 999999, monthlyUsed: 999999, tenantDailyUsed: 999999, tenantMonthlyUsed: 999999);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void BuildDecision_TenantDailyExceeded_BlocksAMemberInsideTheirOwnPlan()
    {
        // The organization's window is exhausted by OTHER members — this caller has used 5 of 100.
        var d = Decide(Limits(), dailyUsed: 5, monthlyUsed: 5, tenantDailyUsed: 300, tenantMonthlyUsed: 300);
        Assert.False(d.Allowed);
        Assert.Equal("daily", d.Scope);
        Assert.Equal("tenant", d.Level);
        Assert.Equal(300, d.ExceededLimit);
        Assert.Equal(300, d.ExceededUsed);
        Assert.Equal(new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_TenantMonthlyExceeded_ResetsOnTheFirst()
    {
        var d = Decide(Limits(), dailyUsed: 5, monthlyUsed: 50, tenantDailyUsed: 10, tenantMonthlyUsed: 9000);
        Assert.False(d.Allowed);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal("tenant", d.Level);
        Assert.Equal(9000, d.ExceededLimit);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_UserMonthly_IsNamedBeforeTenantMonthly()
    {
        // Same reset either way; the caller's OWN exhausted budget is the more actionable message.
        var d = Decide(Limits(), dailyUsed: 10, monthlyUsed: 3000, tenantDailyUsed: 10, tenantMonthlyUsed: 9000);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal("user", d.Level);
    }

    [Fact]
    public void BuildDecision_TenantMonthly_IsNamedBeforeUserDaily()
    {
        // Longest exceeded window first: a tenant-monthly block outranks the caller's daily block.
        var d = Decide(Limits(), dailyUsed: 100, monthlyUsed: 500, tenantDailyUsed: 10, tenantMonthlyUsed: 9000);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal("tenant", d.Level);
    }

    // ── CheckAsync (integration over mocked deps) ────────────────────────────────

    private static McpQuotaService Build(
        Mock<IUserUsageRepository> usageRepo,
        string? planDefinitionsJson = null,
        string? mcpUserPlanOverride = null,
        TenantEdition edition = TenantEdition.Community,
        IMemoryCache? cache = null,
        bool bound = true,
        Func<string?, TenantEdition>? editionResolver = null)
    {
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(r => r.GetMcpUserAsync(It.IsAny<string>()))
            .ReturnsAsync(mcpUserPlanOverride == null
                ? null
                : new McpUserEntry { Upn = Upn, IsEnabled = true, UsagePlan = mcpUserPlanOverride });

        cache ??= new MemoryCache(new MemoryCacheOptions());
        var mcpUserService = new McpUserService(
            adminRepo.Object, new StubAdminIdentityBindingService(bound), cache, NullLogger<McpUserService>.Instance,
            globalAdminService: null!, delegatedAdminService: null!, adminConfigService: null!, memberRoleResolver: null!);

        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetAdminConfigurationAsync())
            .ReturnsAsync(new AutopilotMonitor.Shared.Models.AdminConfiguration
            {
                UpdatedBy = "test",
                PlanTierDefinitionsJson = planDefinitionsJson
            });
        var adminConfigService = new AdminConfigurationService(
            configRepo.Object, NullLogger<AdminConfigurationService>.Instance, cache);

        return new McpQuotaService(
            usageRepo.Object,
            mcpUserService,
            adminConfigService,
            new StubTenantEntitlementService(editionResolver ?? (_ => edition)),
            cache,
            NullLogger<McpQuotaService>.Instance,
            new TestTimeProvider(Now));
    }

    private static Mock<IUserUsageRepository> UsageRepo(params (string Date, long Count)[] rows)
        => UsageRepo(rows, tenantRows: Array.Empty<(string, long)>());

    private static Mock<IUserUsageRepository> UsageRepo((string Date, long Count)[] rows, (string Date, long Count)[] tenantRows)
    {
        var repo = new Mock<IUserUsageRepository>();
        repo.Setup(r => r.GetUsageByUserAsync(Oid, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(rows.Select(r => new UserUsageRecord
            {
                UserId = Oid,
                Date = r.Date,
                RequestCount = r.Count
            }).ToList());
        repo.Setup(r => r.GetTenantUsageAsync(TenantId, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(tenantRows.Select(r => new TenantUsageRecord
            {
                TenantId = TenantId,
                UserId = "some-other-member",
                Date = r.Date,
                RequestCount = r.Count
            }).ToList());
        return repo;
    }

    [Fact]
    public async Task Check_CommunityFallback_DailyLimitEnforced()
    {
        // 100 requests today (catalog Community fallback: 100/day) → blocked.
        var svc = Build(UsageRepo(("20260707", 100)));

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("community", d.Plan);
        Assert.Equal("daily", d.Scope);
        Assert.Equal(100, d.DailyLimit);
    }

    [Fact]
    public async Task Check_ProEdition_UsesProFallbackLimits()
    {
        var svc = Build(UsageRepo(("20260707", 100)), edition: TenantEdition.Pro);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(d.Allowed);
        Assert.Equal("pro", d.Plan);
        Assert.Equal(1000, d.DailyLimit);
        Assert.Equal(20000, d.MonthlyLimit);
    }

    [Fact]
    public async Task Check_MonthlySum_SpansAllRowsOfTheMonth()
    {
        // 2990 across the month + 20 today = 3010 ≥ 3000 monthly Community limit.
        var svc = Build(UsageRepo(("20260701", 1500), ("20260703", 1490), ("20260707", 20)));

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal(3010, d.MonthlyUsed);
        Assert.Equal(20, d.DailyUsed);
    }

    [Fact]
    public async Task Check_AdminDefinedPlan_OverridesCatalogFallback()
    {
        var json = """[{"name":"community","dailyRequestLimit":5,"monthlyRequestLimit":50,"description":""}]""";
        var svc = Build(UsageRepo(("20260707", 5)), planDefinitionsJson: json);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal(5, d.DailyLimit);
        Assert.Equal(50, d.MonthlyLimit);
    }

    [Fact]
    public async Task Check_PerUserOverride_WinsOverTenantEdition()
    {
        var json = """[{"name":"power","dailyRequestLimit":10000,"monthlyRequestLimit":100000,"description":""}]""";
        var svc = Build(UsageRepo(("20260707", 500)), planDefinitionsJson: json, mcpUserPlanOverride: "power");

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(d.Allowed);
        Assert.Equal("power", d.Plan);
        Assert.Equal(10000, d.DailyLimit);
    }

    [Fact]
    public async Task Check_PerUserOverride_IgnoredWhenCallerIsNotTheBoundIdentity()
    {
        // A McpUsers row is keyed on the UPN string; its plan override belongs to the identity (tid + oid)
        // the UPN is bound to. A same-UPN caller from another tenant gets its own tenant's edition plan.
        var json = """[{"name":"power","dailyRequestLimit":10000,"monthlyRequestLimit":100000,"description":""}]""";
        var svc = Build(UsageRepo(("20260707", 500)), planDefinitionsJson: json, mcpUserPlanOverride: "power", bound: false);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("community", d.Plan);
        Assert.Equal(100, d.DailyLimit);
    }

    [Fact]
    public async Task Check_UnknownOverridePlan_FailsClosedToCommunityLimits()
    {
        // Override names a plan that exists nowhere → Community fallback limits.
        var svc = Build(UsageRepo(("20260707", 100)), mcpUserPlanOverride: "no-such-plan");

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("no-such-plan", d.Plan);
        Assert.Equal(100, d.DailyLimit);
    }

    [Fact]
    public async Task Check_UsageLookupThrows_FailsOpen_AndDoesNotCache()
    {
        var repo = new Mock<IUserUsageRepository>();
        repo.Setup(r => r.GetUsageByUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var svc = Build(repo);

        var first = await svc.CheckAsync(Oid, Upn, TenantId);
        var second = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        // Fail-open decisions are not cached — the counter read is retried every request.
        repo.Verify(r => r.GetUsageByUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Check_Decision_IsCachedPerUser()
    {
        var repo = UsageRepo(("20260707", 1));
        var svc = Build(repo);

        await svc.CheckAsync(Oid, Upn, TenantId);
        await svc.CheckAsync(Oid, Upn, TenantId);

        repo.Verify(r => r.GetUsageByUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    // ── Soft-boundary contract (Codex finding 2026-07-07) ─────────────────────────
    // The quota is DELIBERATELY soft: the per-user decision is cached for 60s, so an allowed
    // decision keeps admitting requests inside that window even after the fire-and-forget
    // increments push the stored counters past the limit. Overshoot is bounded (~TTL × request
    // rate per instance) — the same posture as the sliding-window rate limiter. These tests PIN
    // that contract; if strict limit+1 blocking is ever wanted, the decision cache must be
    // reworked and these tests consciously rewritten.

    [Fact]
    public async Task Check_SoftBoundary_CachedAllowedDecision_KeepsAdmitting_WithinTtl()
    {
        // First read: one request below the daily limit → allowed, decision cached.
        var repo = UsageRepo(("20260707", 99));
        var svc = Build(repo);

        var first = await svc.CheckAsync(Oid, Upn, TenantId);
        Assert.True(first.Allowed);

        // The async increments land: stored counters are now OVER the limit — but the cached
        // allowed decision keeps admitting within the TTL, without re-reading storage.
        repo.Setup(r => r.GetUsageByUserAsync(Oid, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<UserUsageRecord>
            {
                new() { UserId = Oid, Date = "20260707", RequestCount = 150 }
            });

        var second = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(second.Allowed, "soft boundary: cached allowed decision persists within the TTL");
        repo.Verify(r => r.GetUsageByUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task Check_SoftBoundary_AfterCacheEviction_OverLimitCounters_Block()
    {
        // Same scenario as above, but once the cached decision is gone (TTL expiry — simulated
        // via eviction), the next check reads the over-limit counters and blocks.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repo = UsageRepo(("20260707", 99));
        var svc = Build(repo, cache: cache);

        Assert.True((await svc.CheckAsync(Oid, Upn, TenantId)).Allowed);

        repo.Setup(r => r.GetUsageByUserAsync(Oid, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<UserUsageRecord>
            {
                new() { UserId = Oid, Date = "20260707", RequestCount = 150 }
            });
        cache.Remove(McpQuotaService.UserCacheKey(Oid)); // stands in for the 60s TTL elapsing

        var decision = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(decision.Allowed);
        Assert.Equal("daily", decision.Scope);
        Assert.Equal(150, decision.DailyUsed);
    }

    // ── Tenant (organization-wide) quota ─────────────────────────────────────────

    [Fact]
    public async Task Check_TenantDailyExceeded_BlocksAMemberUnderTheirOwnLimit()
    {
        // Community catalog tenant window = 300/day; other members burned it, this caller made 5 requests.
        var svc = Build(UsageRepo(new[] { ("20260707", 5L) }, tenantRows: new[] { ("20260707", 300L) }));

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("tenant", d.Level);
        Assert.Equal("daily", d.Scope);
        Assert.Equal("community", d.TenantPlan);
        Assert.Equal(300, d.TenantDailyLimit);
        Assert.Equal(300, d.TenantDailyUsed);
        Assert.Equal(5, d.DailyUsed);
    }

    [Fact]
    public async Task Check_TenantMonthlySum_SpansAllMembersAndDaysOfTheMonth()
    {
        var svc = Build(UsageRepo(
            new[] { ("20260707", 1L) },
            tenantRows: new[] { ("20260701", 4000L), ("20260703", 4990L), ("20260707", 10L) }));

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("tenant", d.Level);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal(9000, d.TenantMonthlyUsed);
        Assert.Equal(10, d.TenantDailyUsed);
    }

    [Fact]
    public async Task Check_TenantLimits_FollowTheTenantPlan_NeverThePerUserOverride()
    {
        // The caller's override plan is huge; the tenant's edition plan (community) carries a tiny tenant
        // window. The override lifts only the caller's OWN budget — the organization's window still bites.
        var json = """[{"name":"power","dailyRequestLimit":10000,"monthlyRequestLimit":100000,"tenantDailyRequestLimit":1000000,"tenantMonthlyRequestLimit":1000000,"description":""},{"name":"community","dailyRequestLimit":100,"monthlyRequestLimit":3000,"tenantDailyRequestLimit":10,"tenantMonthlyRequestLimit":100,"description":""}]""";
        var svc = Build(UsageRepo(new[] { ("20260707", 1L) }, tenantRows: new[] { ("20260707", 10L) }),
            planDefinitionsJson: json, mcpUserPlanOverride: "power");

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(d.Allowed);
        Assert.Equal("power", d.Plan);
        Assert.Equal(10000, d.DailyLimit);
        Assert.Equal("community", d.TenantPlan);
        Assert.Equal(10, d.TenantDailyLimit);
        Assert.Equal("tenant", d.Level);
    }

    [Fact]
    public async Task Check_DefinitionWithoutTenantLimits_FallsBackToTheEditionCatalog()
    {
        // Pre-existing SectionUsagePlans rows carry no tenant fields → the edition's catalog tenant windows.
        var json = """[{"name":"pro","dailyRequestLimit":5,"monthlyRequestLimit":50,"description":""}]""";
        var svc = Build(UsageRepo(("20260707", 1)), planDefinitionsJson: json, edition: TenantEdition.Pro);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(d.Allowed);
        Assert.Equal(5, d.DailyLimit);
        Assert.Equal(3000, d.TenantDailyLimit);
        Assert.Equal(60000, d.TenantMonthlyLimit);
    }

    [Fact]
    public async Task Check_TenantWindowsLiftedToZero_SkipTheTenantRead()
    {
        var json = """[{"name":"community","dailyRequestLimit":100,"monthlyRequestLimit":3000,"tenantDailyRequestLimit":0,"tenantMonthlyRequestLimit":0,"description":""}]""";
        var repo = UsageRepo(new[] { ("20260707", 1L) }, tenantRows: new[] { ("20260707", 999999L) });
        var svc = Build(repo, planDefinitionsJson: json);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(d.Allowed);
        Assert.Equal(0, d.TenantDailyLimit);
        repo.Verify(r => r.GetTenantUsageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Check_WithoutTenantId_SkipsTheTenantRead()
    {
        var repo = UsageRepo(("20260707", 1));
        var svc = Build(repo);

        var d = await svc.CheckAsync(Oid, Upn, tenantId: null);

        Assert.True(d.Allowed);
        repo.Verify(r => r.GetTenantUsageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Check_TenantLookupThrows_FailsOpen_AndDoesNotCache()
    {
        var repo = UsageRepo(("20260707", 1));
        repo.Setup(r => r.GetTenantUsageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var svc = Build(repo);

        var first = await svc.CheckAsync(Oid, Upn, TenantId);
        var second = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.Equal(-1, first.TenantDailyUsed);
        repo.Verify(r => r.GetTenantUsageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    // ── Delegated (MSP) reads: the budget follows the data ───────────────────────
    // The CHARGED tenant (the managed customer) supplies the organization windows and counters; the
    // caller's own windows still follow their HOME tenant's plan (and their per-user override).

    private const string Target = "22222222-2222-2222-2222-222222222222";
    private const string Target2 = "33333333-3333-3333-3333-333333333333";
    private const string OtherOid = "00000000-0000-0000-0000-000000000002";

    private static void TenantRows(Mock<IUserUsageRepository> repo, string tenantId, params (string Date, long Count)[] rows)
        => repo.Setup(r => r.GetTenantUsageAsync(tenantId, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(rows.Select(r => new TenantUsageRecord
            {
                TenantId = tenantId,
                UserId = "some-member-or-msp",
                Date = r.Date,
                RequestCount = r.Count
            }).ToList());

    private static TenantEdition HomeProTargetsCommunity(string? tenantId)
        => tenantId == TenantId ? TenantEdition.Pro : TenantEdition.Community;

    [Fact]
    public async Task Check_DelegatedTarget_ChargesTheManagedTenantsPlanAndCounters()
    {
        // Pro MSP reads a Community customer whose organization window (300/day) other readers burned.
        var repo = UsageRepo(("20260707", 5));
        TenantRows(repo, Target, ("20260707", 300));
        var svc = Build(repo, editionResolver: HomeProTargetsCommunity);

        var d = await svc.CheckAsync(Oid, Upn, homeTenantId: TenantId, chargeTenantId: Target);

        Assert.False(d.Allowed);
        Assert.Equal("tenant", d.Level);
        Assert.Equal("daily", d.Scope);
        Assert.Equal("community", d.TenantPlan);
        Assert.Equal(300, d.TenantDailyLimit);
        Assert.Equal(Target, d.TargetTenantId);
        // The caller's OWN windows still come from the Pro home tenant.
        Assert.Equal("pro", d.Plan);
        Assert.Equal(1000, d.DailyLimit);
        repo.Verify(r => r.GetTenantUsageAsync(TenantId, It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Check_DelegatedTarget_UserBudgetStillFollowsHome()
    {
        // Community home (100/day) reading a Pro customer: the caller's own daily window bites first.
        var repo = UsageRepo(("20260707", 100));
        TenantRows(repo, Target, ("20260707", 1));
        var svc = Build(repo, editionResolver: t => t == Target ? TenantEdition.Pro : TenantEdition.Community);

        var d = await svc.CheckAsync(Oid, Upn, homeTenantId: TenantId, chargeTenantId: Target);

        Assert.False(d.Allowed);
        Assert.Equal("user", d.Level);
        Assert.Equal("community", d.Plan);
        Assert.Equal("pro", d.TenantPlan);
        Assert.Equal(Target, d.TargetTenantId);
    }

    [Fact]
    public async Task Check_OwnTenant_NamesNoTarget()
    {
        var svc = Build(UsageRepo(("20260707", 1)));
        var legacy = await svc.CheckAsync(Oid, Upn, TenantId);
        var explicitHome = await svc.CheckAsync(Oid, Upn, TenantId, TenantId);
        Assert.Null(legacy.TargetTenantId);
        Assert.Null(explicitHome.TargetTenantId);
    }

    [Fact]
    public async Task Check_TenantSnapshot_IsSharedAcrossCallersOfTheSameTenant()
    {
        // Two different callers charged to the same customer read its organization counters ONCE per TTL.
        var repo = UsageRepo(("20260707", 1));
        repo.Setup(r => r.GetUsageByUserAsync(OtherOid, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<UserUsageRecord>());
        TenantRows(repo, Target, ("20260707", 10));
        var svc = Build(repo, editionResolver: HomeProTargetsCommunity);

        await svc.CheckAsync(Oid, Upn, TenantId, Target);
        await svc.CheckAsync(OtherOid, "bob@contoso.com", TenantId, Target);

        repo.Verify(r => r.GetTenantUsageAsync(Target, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        repo.Verify(r => r.GetUsageByUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CheckMany_ExcludesExhaustedTenants_AdmitsTheRest()
    {
        var repo = UsageRepo(("20260707", 1));
        TenantRows(repo, Target, ("20260707", 300));
        TenantRows(repo, Target2, ("20260707", 10));
        var svc = Build(repo, editionResolver: HomeProTargetsCommunity);

        var r = await svc.CheckManyAsync(Oid, Upn, TenantId, new[] { Target, Target2 });

        Assert.True(r.Allowed);
        Assert.Null(r.BlockingDecision);
        Assert.True(r.UserDecision.Allowed);
        Assert.Equal(new[] { Target2 }, r.AdmittedTenantIds);
        Assert.Equal(new[] { Target }, r.ExcludedTenantIds);
    }

    [Fact]
    public async Task CheckMany_AllExhausted_BlocksWithTheEarliestReset()
    {
        var repo = UsageRepo(("20260707", 1));
        TenantRows(repo, Target, ("20260707", 300));                       // daily window → resets tomorrow
        TenantRows(repo, Target2, ("20260701", 9000), ("20260707", 1));   // monthly window → resets on the 1st
        var svc = Build(repo, editionResolver: HomeProTargetsCommunity);

        var r = await svc.CheckManyAsync(Oid, Upn, TenantId, new[] { Target, Target2 });

        Assert.False(r.Allowed);
        Assert.Empty(r.AdmittedTenantIds);
        Assert.Equal(2, r.ExcludedTenantIds.Count);
        Assert.Equal("tenant", r.BlockingDecision!.Level);
        Assert.Equal("daily", r.BlockingDecision.Scope);
        Assert.Equal(new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), r.BlockingDecision.ResetUtc);
    }

    [Fact]
    public async Task CheckMany_UserExhausted_SkipsEveryTenantRead()
    {
        var repo = UsageRepo(("20260707", 100)); // Community home: 100/day
        var svc = Build(repo);

        var r = await svc.CheckManyAsync(Oid, Upn, TenantId, new[] { Target, Target2 });

        Assert.False(r.Allowed);
        Assert.Equal("user", r.BlockingDecision!.Level);
        Assert.Equal(2, r.ExcludedTenantIds.Count);
        repo.Verify(r2 => r2.GetTenantUsageAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task CheckMany_TenantReadFails_AdmitsThatTenant_FailOpen()
    {
        var repo = UsageRepo(("20260707", 1));
        repo.Setup(r => r.GetTenantUsageAsync(Target, It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        TenantRows(repo, Target2, ("20260707", 10));
        var svc = Build(repo, editionResolver: HomeProTargetsCommunity);

        var r = await svc.CheckManyAsync(Oid, Upn, TenantId, new[] { Target, Target2 });

        Assert.True(r.Allowed);
        Assert.Equal(new[] { Target, Target2 }, r.AdmittedTenantIds.OrderBy(t => t));
        Assert.Empty(r.ExcludedTenantIds);
    }
}
