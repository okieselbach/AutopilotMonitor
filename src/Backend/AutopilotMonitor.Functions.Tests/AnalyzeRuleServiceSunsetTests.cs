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
    public async Task EnsureSeed_full_sunset_path_runs_safeState_then_GC_then_tombstone()
    {
        // Happy path: every step succeeds. We assert the side-effects of each step
        // landed AND that they happened in the correct order — safe-state (write
        // with Enabled=false) MUST precede GC (delete orphan tenant state).
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 0);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);
        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.Contains(SunsetRuleId, rig.DeleteAnalyzeCalls);
        // Order: safe-state was recorded BEFORE the first GC call.
        Assert.True(
            rig.CallOrder.IndexOf($"store:{SunsetRuleId}") < rig.CallOrder.IndexOf($"gc:{SunsetRuleId}"),
            "Safe-state write must precede the orphan-GC for the same ruleId.");
        Assert.True(
            rig.CallOrder.IndexOf($"gc:{SunsetRuleId}") < rig.CallOrder.IndexOf($"delete:{SunsetRuleId}"),
            "Orphan-GC must precede the global tombstone delete for the same ruleId.");
    }

    [Fact]
    public async Task EnsureSeed_safeState_failure_short_circuits_before_GC_and_delete()
    {
        // Codex review (Medium): the previous fix still let tenant RuleStates get
        // deleted before a transient Azure error blew up the global tombstone delete,
        // which could silently flip a future default-ENABLED sunset rule's
        // RuleState{Enabled=false} opt-outs back to enabled (because the tenant
        // override is gone and the global is still enabled).
        // Fix: safe-state (global Enabled=false) MUST succeed before any tenant state
        // is touched. If the safe-state write itself fails, we abort the whole sunset
        // sequence for that rule — no GC, no tombstone.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 0, safeStateOk: false);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);    // safe-state was attempted
        Assert.DoesNotContain(SunsetRuleId, rig.GcCalls);      // GC NOT attempted (tenant state preserved)
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteAnalyzeCalls); // global delete NOT attempted
    }

    [Fact]
    public async Task EnsureSeed_partial_GC_failure_keeps_global_row_so_retry_works()
    {
        // GC reports >0 failed rows → global delete must NOT run, the rule stays in
        // the sunset diff for the next cycle's retry. Safe-state (Enabled=false) is
        // already written, so default-enabled sunset rules don't reactivate any opt-out
        // tenant in the meantime.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 1, gcFailed: 1);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);
        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteAnalyzeCalls);
    }

    [Fact]
    public async Task EnsureSeed_GC_enumeration_failure_keeps_global_row_too()
    {
        // gcFailed == -1 sentinel = enumeration itself failed (we don't know how many
        // orphans are out there). Same contract as partial failure: keep global row.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: -1);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteAnalyzeCalls);
    }

    [Fact]
    public async Task EnsureSeed_partial_sunset_failure_leaves_seed_flag_clearable_for_same_instance_retry()
    {
        // Codex review (Medium): the previous fix unconditionally set _seeded=true at
        // the end of EnsureSeed, so a sunset that didn't complete had to wait for a
        // process restart to retry. On long-running Function-App instances this can
        // mean hours of delay. New contract: if ANY sunset rule didn't reach
        // Completed, _seeded stays false → next GetAllRulesForTenantAsync re-enters
        // EnsureSeed and retries the sunset path.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 1);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var tenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        await service.GetAllRulesForTenantAsync(tenantId);
        var gcCallsAfterFirst = rig.GcCalls.Count;
        // Now switch the mock so the retry has a clean GC — should complete on retry.
        rig.SetGcReturn(gcDeleted: 1, gcFailed: 0);

        await service.GetAllRulesForTenantAsync(tenantId);
        Assert.True(rig.GcCalls.Count > gcCallsAfterFirst,
            "Sunset should be retried on the next call when the previous attempt didn't complete.");
        Assert.Contains(SunsetRuleId, rig.DeleteAnalyzeCalls);
    }

    [Fact]
    public async Task EnsureSeed_all_sunsets_completed_sets_seeded_flag()
    {
        // Complement of the previous test: on a clean sunset cycle, _seeded gets set
        // so we don't re-scan on every call. (Verified indirectly: a second call
        // doesn't issue another GC against the now-deleted rule.)
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 1, gcFailed: 0);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var tenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

        await service.GetAllRulesForTenantAsync(tenantId);
        var gcCallsAfterFirst = rig.GcCalls.Count;
        await service.GetAllRulesForTenantAsync(tenantId);

        Assert.Equal(gcCallsAfterFirst, rig.GcCalls.Count);
    }

    [Fact]
    public async Task EnsureSeed_does_not_GC_RuleStates_for_rules_still_in_catalog()
    {
        // Live rules with tenant overrides MUST NOT be touched — those are legitimate
        // enabled/disabled preferences, not orphans.
        var stillLiveRuleId = BuiltInAnalyzeRules.GetAll().First().RuleId;
        var rig = new SunsetRig(stillLiveRuleId, gcDeleted: 1, gcFailed: 0);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        await service.GetAllRulesForTenantAsync("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

        Assert.DoesNotContain(stillLiveRuleId, rig.GcCalls);
    }

    [Fact]
    public async Task ProcessSunsetRuleAsync_returns_SkippedOnSafeStateFailure_when_safe_state_fails()
    {
        // Direct unit test of the helper — covers the safe-state branch independent
        // of EnsureSeed plumbing.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 0, safeStateOk: false);
        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var sunsetRule = new AnalyzeRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnSafeStateFailure, outcome);
        Assert.Equal(0, gcd);
        // Safe-state mutated the in-memory rule (defense-in-depth for callers that
        // re-use the object — they see the neutralised values, not the original).
        Assert.False(sunsetRule.Enabled);
        Assert.False(sunsetRule.MarkSessionAsFailedDefault);
    }

    [Fact]
    public async Task ProcessSunsetRuleAsync_returns_SkippedOnGcFailure_when_GC_reports_failures()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 1);
        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var sunsetRule = new AnalyzeRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnGcFailure, outcome);
        Assert.Equal(2, gcd);
    }

    [Fact]
    public async Task ProcessSunsetRuleAsync_returns_SkippedOnGlobalDeleteFailure_when_tombstone_fails()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 3, gcFailed: 0, globalDeleteOk: false);
        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var sunsetRule = new AnalyzeRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnGlobalDeleteFailure, outcome);
        Assert.Equal(3, gcd);
    }

    [Fact]
    public async Task ReseedBuiltIn_returns_orphanGc_count_in_tuple()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 3, gcFailed: 0);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.True(deleted >= 1, $"Expected at least 1 deletion (incl. sunset), got {deleted}");
        Assert.Equal(BuiltInAnalyzeRules.GetAll().Count, written);
        Assert.Equal(3, orphanStatesGcd);
    }

    [Fact]
    public async Task ReseedBuiltIn_skips_global_delete_on_partial_GC_failure()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 1);

        var service = new AnalyzeRuleService(rig.Repo.Object, NullLogger<AnalyzeRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteAnalyzeCalls);
        Assert.Equal(2, orphanStatesGcd);
    }

    // ===== Helpers =====

    /// <summary>
    /// Mock harness for sunset-path tests. Records every storage call against the
    /// sunset ruleId — safe-state writes (StoreAnalyzeRuleAsync), orphan GC, and
    /// tombstone deletes — and lets the test configure success/failure of each step.
    /// The <see cref="CallOrder"/> sequence captures relative ordering so tests can
    /// assert the safe-state → GC → tombstone invariant directly.
    /// </summary>
    private sealed class SunsetRig
    {
        public Mock<IRuleRepository> Repo { get; }
        public List<string> SafeStateWrites { get; } = new();
        public List<string> GcCalls { get; } = new();
        public List<string> DeleteAnalyzeCalls { get; } = new();
        public List<string> CallOrder { get; } = new();

        private (int gcDeleted, int gcFailed) _gcReturn;

        public SunsetRig(string sunsetRuleId, int gcDeleted, int gcFailed,
                         bool safeStateOk = true, bool globalDeleteOk = true)
        {
            _gcReturn = (gcDeleted, gcFailed);

            // Existing DB = real catalog + the synthetic sunset row.
            var existingInDb = BuiltInAnalyzeRules.GetAll().ToList();
            if (!existingInDb.Any(r => r.RuleId == sunsetRuleId))
            {
                existingInDb.Add(new AnalyzeRule
                {
                    RuleId = sunsetRuleId,
                    Title = "Legacy",
                    Description = "Legacy",
                    Severity = "warning",
                    Category = "test",
                    Version = "1.0.0",
                    IsBuiltIn = true,
                    Enabled = true,
                });
            }

            Repo = new Mock<IRuleRepository>();
            Repo.Setup(r => r.GetAnalyzeRulesAsync("global")).ReturnsAsync(existingInDb);
            Repo.Setup(r => r.GetAnalyzeRulesAsync(It.Is<string>(s => s != "global"))).ReturnsAsync(new List<AnalyzeRule>());
            Repo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());

            // StoreAnalyzeRuleAsync: only flip safeStateOk for writes against the
            // sunset ruleId — survivors / new rules in the catalog must still write OK.
            Repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), It.IsAny<string>()))
                .Callback<AnalyzeRule, string>((rule, _) =>
                {
                    if (rule.RuleId == sunsetRuleId)
                    {
                        SafeStateWrites.Add(rule.RuleId);
                        CallOrder.Add($"store:{rule.RuleId}");
                    }
                })
                .ReturnsAsync((AnalyzeRule rule, string _) => rule.RuleId == sunsetRuleId ? safeStateOk : true);

            Repo.Setup(r => r.DeleteAnalyzeRuleAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((_, ruleId) =>
                {
                    DeleteAnalyzeCalls.Add(ruleId);
                    CallOrder.Add($"delete:{ruleId}");
                })
                .ReturnsAsync((string _, string ruleId) => ruleId == sunsetRuleId ? globalDeleteOk : true);

            Repo.Setup(r => r.DeleteRuleStatesForRuleIdAcrossTenantsAsync(It.IsAny<string>()))
                .Callback<string>(ruleId =>
                {
                    GcCalls.Add(ruleId);
                    CallOrder.Add($"gc:{ruleId}");
                })
                .ReturnsAsync(() => _gcReturn);
        }

        /// <summary>
        /// Reconfigure the GC return value mid-test. Used by the
        /// "next call retries after a transient failure" scenario.
        /// </summary>
        public void SetGcReturn(int gcDeleted, int gcFailed) => _gcReturn = (gcDeleted, gcFailed);
    }
}
