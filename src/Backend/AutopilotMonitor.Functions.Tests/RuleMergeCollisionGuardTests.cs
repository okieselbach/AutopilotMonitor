using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the merge collision guard in <see cref="AnalyzeRuleService.GetAllRulesForTenantAsync"/>
/// and <see cref="GatherRuleService.GetAllRulesForTenantAsync"/>: a tenant-partition row whose
/// RuleId collides with a merged global rule (legacy debris from the pre-global-partition
/// seeding era, e.g. tenant 5ca2b350's ANALYZE-ID-002 copy from 2026-03) must be skipped —
/// the global definition wins. Without the guard the same RuleId appears twice in the merged
/// list and every ToDictionary(r =&gt; r.RuleId) consumer throws (prod: ArgumentException in
/// AnalyzeOnEnrollmentEndHandler.SafeNotifyRuleChannelsAsync, killing rule notifications).
/// </summary>
public class RuleMergeCollisionGuardTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Fact]
    public async Task Analyze_stale_tenant_copy_of_global_rule_is_skipped()
    {
        var collidingId = BuiltInAnalyzeRules.GetAll().First().RuleId;
        var repo = CreateAnalyzeRepo(tenantRules: new List<AnalyzeRule>
        {
            StaleTenantAnalyzeCopy(collidingId),
        });

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        var instances = merged.Where(r => r.RuleId == collidingId).ToList();
        Assert.Single(instances);
        Assert.True(instances[0].IsBuiltIn, "The global built-in definition must win over the stale tenant copy.");
        // The exact downstream consumer that crashed in prod must work again.
        _ = merged.ToDictionary(r => r.RuleId);
    }

    [Fact]
    public async Task Analyze_non_colliding_custom_rule_still_merges()
    {
        var repo = CreateAnalyzeRepo(tenantRules: new List<AnalyzeRule>
        {
            StaleTenantAnalyzeCopy("TENANT-CUSTOM-XYZ"),
        });

        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(merged, r => r.RuleId == "TENANT-CUSTOM-XYZ");
    }

    [Fact]
    public async Task Gather_stale_tenant_copy_of_global_rule_is_skipped()
    {
        var collidingId = BuiltInGatherRules.GetAll().First().RuleId;
        var repo = CreateGatherRepo(tenantRules: new List<GatherRule>
        {
            StaleTenantGatherCopy(collidingId),
        });

        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        var instances = merged.Where(r => r.RuleId == collidingId).ToList();
        Assert.Single(instances);
        Assert.True(instances[0].IsBuiltIn, "The global built-in definition must win over the stale tenant copy.");
        _ = merged.ToDictionary(r => r.RuleId);
    }

    [Fact]
    public async Task Gather_non_colliding_custom_rule_still_merges()
    {
        var repo = CreateGatherRepo(tenantRules: new List<GatherRule>
        {
            StaleTenantGatherCopy("TENANT-CUSTOM-XYZ"),
        });

        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(merged, r => r.RuleId == "TENANT-CUSTOM-XYZ");
    }

    // ===== Helpers =====

    private static AnalyzeRule StaleTenantAnalyzeCopy(string ruleId) => new()
    {
        RuleId = ruleId,
        Title = "Stale tenant copy",
        Description = "Legacy per-tenant seeded row",
        Severity = "warning",
        Category = "test",
        IsBuiltIn = false,
        IsCommunity = false,
        Enabled = true,
    };

    private static GatherRule StaleTenantGatherCopy(string ruleId) => new()
    {
        RuleId = ruleId,
        Title = "Stale tenant copy",
        Description = "Legacy per-tenant seeded row",
        Category = "test",
        CollectorType = "registry",
        Target = "HKLM\\SOFTWARE\\X",
        Trigger = "startup",
        IsBuiltIn = false,
        IsCommunity = false,
        Enabled = true,
    };

    /// <summary>DB state = exactly the shipped catalog (clean seed diff), plus the given tenant rows.</summary>
    private static Mock<IRuleRepository> CreateAnalyzeRepo(List<AnalyzeRule> tenantRules)
    {
        var repo = new Mock<IRuleRepository>();
        repo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(BuiltInAnalyzeRules.GetAll().ToList());
        repo.Setup(r => r.GetAnalyzeRulesAsync(TenantId)).ReturnsAsync(tenantRules);
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.DeleteAnalyzeRuleAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.DeleteRuleStatesForRuleIdAcrossTenantsAsync(It.IsAny<string>())).ReturnsAsync((0, 0));
        return repo;
    }

    private static Mock<IRuleRepository> CreateGatherRepo(List<GatherRule> tenantRules)
    {
        var repo = new Mock<IRuleRepository>();
        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(BuiltInGatherRules.GetAll().ToList());
        repo.Setup(r => r.GetGatherRulesAsync(TenantId)).ReturnsAsync(tenantRules);
        repo.Setup(r => r.GetRuleStatesAsync(TenantId)).ReturnsAsync(new Dictionary<string, RuleState>());
        repo.Setup(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.DeleteGatherRuleAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.DeleteRuleStatesForRuleIdAcrossTenantsAsync(It.IsAny<string>())).ReturnsAsync((0, 0));
        return repo;
    }
}
