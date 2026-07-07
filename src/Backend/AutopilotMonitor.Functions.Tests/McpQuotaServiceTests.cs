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

    [Fact]
    public void BuildDecision_UnderBothLimits_Allowed()
    {
        var d = McpQuotaService.BuildDecision("community", 100, 3000, dailyUsed: 99, monthlyUsed: 500, Now);
        Assert.True(d.Allowed);
        Assert.Null(d.Scope);
    }

    [Fact]
    public void BuildDecision_DailyExceeded_BlocksWithMidnightReset()
    {
        var d = McpQuotaService.BuildDecision("community", 100, 3000, dailyUsed: 100, monthlyUsed: 500, Now);
        Assert.False(d.Allowed);
        Assert.Equal("daily", d.Scope);
        Assert.Equal(new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_MonthlyExceeded_BlocksWithFirstOfNextMonthReset()
    {
        var d = McpQuotaService.BuildDecision("community", 100, 3000, dailyUsed: 10, monthlyUsed: 3000, Now);
        Assert.False(d.Allowed);
        Assert.Equal("monthly", d.Scope);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), d.ResetUtc);
    }

    [Fact]
    public void BuildDecision_MonthlyTakesPrecedenceOverDaily()
    {
        // Both exceeded → report the longer (monthly) window so Retry-After is honest.
        var d = McpQuotaService.BuildDecision("community", 100, 3000, dailyUsed: 100, monthlyUsed: 3000, Now);
        Assert.Equal("monthly", d.Scope);
    }

    [Fact]
    public void BuildDecision_ZeroLimit_MeansUnlimitedForThatScope()
    {
        var d = McpQuotaService.BuildDecision("custom", 0, 0, dailyUsed: 999999, monthlyUsed: 999999, Now);
        Assert.True(d.Allowed);
    }

    // ── CheckAsync (integration over mocked deps) ────────────────────────────────

    private static McpQuotaService Build(
        Mock<IUserUsageRepository> usageRepo,
        string? planDefinitionsJson = null,
        string? mcpUserPlanOverride = null,
        TenantEdition edition = TenantEdition.Community,
        IMemoryCache? cache = null)
    {
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(r => r.GetMcpUserAsync(It.IsAny<string>()))
            .ReturnsAsync(mcpUserPlanOverride == null
                ? null
                : new McpUserEntry { Upn = Upn, IsEnabled = true, UsagePlan = mcpUserPlanOverride });

        cache ??= new MemoryCache(new MemoryCacheOptions());
        var mcpUserService = new McpUserService(
            adminRepo.Object, cache, NullLogger<McpUserService>.Instance,
            globalAdminService: null!, delegatedAdminService: null!, adminConfigService: null!);

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
            new StubTenantEntitlementService(edition),
            cache,
            NullLogger<McpQuotaService>.Instance,
            new TestTimeProvider(Now));
    }

    private static Mock<IUserUsageRepository> UsageRepo(params (string Date, long Count)[] rows)
    {
        var repo = new Mock<IUserUsageRepository>();
        repo.Setup(r => r.GetUsageByUserAsync(Oid, It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(rows.Select(r => new UserUsageRecord
            {
                UserId = Oid,
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
    public async Task Check_EnterpriseEdition_UsesEnterpriseFallbackLimits()
    {
        var svc = Build(UsageRepo(("20260707", 100)), edition: TenantEdition.Enterprise);

        var d = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.True(d.Allowed);
        Assert.Equal("enterprise", d.Plan);
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
        cache.Remove($"mcp-quota:{Oid}"); // stands in for the 60s TTL elapsing

        var decision = await svc.CheckAsync(Oid, Upn, TenantId);

        Assert.False(decision.Allowed);
        Assert.Equal("daily", decision.Scope);
        Assert.Equal(150, decision.DailyUsed);
    }
}
