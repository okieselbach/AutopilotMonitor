using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the sunset-rule invariant on <see cref="AnalyzeRuleService"/>: when a built-in
/// rule is removed from the code catalog (e.g. ANALYZE-CORR-001 and ANALYZE-APP-009 in
/// the session 080edee9 follow-up), both the global rule row AND all per-tenant
/// <c>RuleState</c> overrides for that ruleId must be deleted on the next seed/reseed
/// cycle. Without this, opt-in tenants would keep the rule alive via stale RuleState
/// records that point to a non-existent global rule.
/// </summary>
public class AnalyzeRuleServiceSunsetTests
{
    private const string SunsetRuleId = "ANALYZE-LEGACY-001";

    [Fact]
    public async Task EnsureSeed_drops_sunset_rule_and_GCs_orphan_RuleStates()
    {
        // Arrange: DB has a built-in rule that's no longer in the code catalog
        // (BuiltInAnalyzeRules.GetAll() returns the embedded analyze-rules.json — we
        // can't easily inject a fake catalog, so we use a ruleId that the real catalog
        // doesn't contain). Plus the rule has TWO per-tenant RuleState overrides that
        // would normally keep it firing.
        var (ruleRepo, deleteAnalyzeCalls, gcCalls) = BuildRepoWithSunsetRule(SunsetRuleId, gcDeleted: 2, gcFailed: 0);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);

        // Act: a regular GetAllRulesForTenantAsync triggers EnsureBuiltInRulesSeededAsync.
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        // Assert: the sunset rule's per-tenant orphan RuleStates got GC'd AND its
        // global row got deleted. The order matters (GC first), but for a clean GC
        // the end-state is the same: both call sites recorded.
        Assert.Contains(SunsetRuleId, deleteAnalyzeCalls);
        Assert.Contains(SunsetRuleId, gcCalls);
    }

    [Fact]
    public async Task EnsureSeed_partial_GC_failure_keeps_global_row_so_retry_works()
    {
        // Codex review (Medium): if GC reports >0 failed rows, the global delete must NOT
        // run — otherwise the rule falls out of the sunset-diff and the remaining orphan
        // RuleState rows are unreachable. With the global row kept, the next startup will
        // see the rule in the diff again and re-attempt the GC.
        var (ruleRepo, deleteAnalyzeCalls, gcCalls) = BuildRepoWithSunsetRule(SunsetRuleId, gcDeleted: 1, gcFailed: 1);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, gcCalls);             // GC was attempted
        Assert.DoesNotContain(SunsetRuleId, deleteAnalyzeCalls); // global delete SKIPPED on partial GC
    }

    [Fact]
    public async Task EnsureSeed_GC_enumeration_failure_keeps_global_row_too()
    {
        // The gcFailed == -1 sentinel signals "we couldn't enumerate the RuleStates at
        // all". Same contract as partial failure: do NOT delete the global row.
        var (ruleRepo, deleteAnalyzeCalls, gcCalls) = BuildRepoWithSunsetRule(SunsetRuleId, gcDeleted: 0, gcFailed: -1);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, gcCalls);
        Assert.DoesNotContain(SunsetRuleId, deleteAnalyzeCalls);
    }

    [Fact]
    public async Task EnsureSeed_does_not_GC_RuleStates_for_rules_still_in_catalog()
    {
        // Arrange: pick a rule that IS still in the catalog. EnsureSeed must NOT touch
        // its RuleState rows even if tenants have overrides — those are legitimate opt-out
        // / opt-in settings.
        var stillLiveRuleId = BuiltInAnalyzeRules.GetAll().First().RuleId;
        var (ruleRepo, deleteAnalyzeCalls, gcCalls) = BuildRepoWithSunsetRule(stillLiveRuleId, gcDeleted: 1, gcFailed: 0);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);

        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        // Live rule's RuleState must NOT be GC'd.
        Assert.DoesNotContain(stillLiveRuleId, gcCalls);
    }

    [Fact]
    public async Task ReseedBuiltIn_returns_orphanGc_count_in_tuple()
    {
        // The new ReseedBuiltInRulesAsync return shape is (deleted, written, orphanStatesGcd)
        // — pin that so external callers (admin UI, future scripts) can surface the
        // cleanup count to the operator.
        var (ruleRepo, _, _) = BuildRepoWithSunsetRule(SunsetRuleId, gcDeleted: 3, gcFailed: 0);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.True(deleted >= 1, $"Expected at least 1 deletion (incl. sunset), got {deleted}");
        Assert.Equal(BuiltInAnalyzeRules.GetAll().Count, written);
        Assert.Equal(3, orphanStatesGcd);
    }

    [Fact]
    public async Task ReseedBuiltIn_skips_global_delete_on_partial_GC_failure()
    {
        // Same invariant as the EnsureSeed test — the explicit Reseed path must also
        // refuse to drop the global row when GC didn't go all the way through.
        var (ruleRepo, deleteAnalyzeCalls, gcCalls) = BuildRepoWithSunsetRule(SunsetRuleId, gcDeleted: 2, gcFailed: 1);

        var service = new AnalyzeRuleService(ruleRepo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.Contains(SunsetRuleId, gcCalls);
        Assert.DoesNotContain(SunsetRuleId, deleteAnalyzeCalls);
        // orphanStatesGcd still surfaces the partial successes (2/3 rows deleted before
        // the failure) so operators can see progress was made.
        Assert.Equal(2, orphanStatesGcd);
    }

    // ===== Helpers =====

    /// <summary>
    /// Builds a mock <see cref="IRuleRepository"/> that:
    /// (a) returns the full built-in catalog PLUS one extra rule with the given
    ///     <paramref name="extraRuleId"/> on the "global" partition — so the sunset
    ///     detection has something to find when extraRuleId is not in the catalog;
    /// (b) reports the configured GC result via
    ///     <see cref="IRuleRepository.DeleteRuleStatesForRuleIdAcrossTenantsAsync"/>.
    /// Returns the mock plus two recorder lists capturing which ruleIds got
    /// DeleteAnalyzeRuleAsync and which got DeleteRuleStatesForRuleIdAcrossTenantsAsync
    /// invoked.
    /// </summary>
    private static (Mock<IRuleRepository> repo, List<string> deleteAnalyzeCalls, List<string> gcCalls)
        BuildRepoWithSunsetRule(string extraRuleId, int gcDeleted, int gcFailed)
    {
        var deleteAnalyzeCalls = new List<string>();
        var gcCalls = new List<string>();

        // The "existing" set in the DB: real built-ins (so version-bump logic doesn't
        // re-write everything every test) PLUS one extra synthetic entry. The extra
        // entry mimics a previous build-in that has since been removed from the code.
        var existingInDb = BuiltInAnalyzeRules.GetAll().ToList();
        if (!existingInDb.Any(r => r.RuleId == extraRuleId))
        {
            existingInDb.Add(new AnalyzeRule
            {
                RuleId = extraRuleId,
                Title = "Legacy",
                Description = "Legacy",
                Severity = "warning",
                Category = "test",
                Version = "1.0.0",
                IsBuiltIn = true,
                Enabled = false,
            });
        }

        var ruleRepo = new Mock<IRuleRepository>();
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(existingInDb);
        ruleRepo.Setup(r => r.GetAnalyzeRulesAsync(It.Is<string>(s => s != "global"))).ReturnsAsync(new List<AnalyzeRule>());
        ruleRepo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());
        ruleRepo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>())).ReturnsAsync(true);
        ruleRepo.Setup(r => r.DeleteAnalyzeRuleAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, ruleId) => deleteAnalyzeCalls.Add(ruleId))
            .ReturnsAsync(true);
        ruleRepo.Setup(r => r.DeleteRuleStatesForRuleIdAcrossTenantsAsync(It.IsAny<string>()))
            .Callback<string>(ruleId => gcCalls.Add(ruleId))
            .ReturnsAsync((gcDeleted, gcFailed));

        return (ruleRepo, deleteAnalyzeCalls, gcCalls);
    }
}
