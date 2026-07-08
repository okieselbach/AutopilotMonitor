using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the sunset-rule invariant on <see cref="GatherRuleService"/> — the mirror of
/// <see cref="AnalyzeRuleServiceSunsetTests"/>. When a built-in gather rule is removed from the
/// code catalog, both the global rule row AND all per-tenant <c>RuleState</c> overrides for that
/// ruleId must be cleaned on the next seed/reseed cycle, with the same safe-state → GC → tombstone
/// ordering and partial-failure retry semantics as analyze rules. Without this, gather rules would
/// accumulate orphan RuleState rows (the gap this closes: gather previously had no sunset path).
/// </summary>
public class GatherRuleServiceSunsetTests
{
    private const string SunsetRuleId = "GATHER-LEGACY-001";
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Fact]
    public async Task EnsureSeed_full_sunset_path_runs_safeState_then_GC_then_tombstone()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 0);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);
        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.Contains(SunsetRuleId, rig.DeleteGatherCalls);
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
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 0, safeStateOk: false);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);
        Assert.DoesNotContain(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteGatherCalls);
    }

    [Fact]
    public async Task EnsureSeed_partial_GC_failure_keeps_global_row_so_retry_works()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 1, gcFailed: 1);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(SunsetRuleId, rig.SafeStateWrites);
        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteGatherCalls);
    }

    [Fact]
    public async Task EnsureSeed_partial_sunset_failure_retries_on_next_call()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 1);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);

        await service.GetAllRulesForTenantAsync(TenantId);
        var gcCallsAfterFirst = rig.GcCalls.Count;
        rig.SetGcReturn(gcDeleted: 1, gcFailed: 0);

        await service.GetAllRulesForTenantAsync(TenantId);
        Assert.True(rig.GcCalls.Count > gcCallsAfterFirst,
            "Sunset should be retried on the next call when the previous attempt didn't complete.");
        Assert.Contains(SunsetRuleId, rig.DeleteGatherCalls);
    }

    [Fact]
    public async Task EnsureSeed_all_sunsets_completed_sets_seeded_flag()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 1, gcFailed: 0);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);

        await service.GetAllRulesForTenantAsync(TenantId);
        var gcCallsAfterFirst = rig.GcCalls.Count;
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Equal(gcCallsAfterFirst, rig.GcCalls.Count);
    }

    [Fact]
    public async Task EnsureSeed_does_not_GC_RuleStates_for_rules_still_in_catalog()
    {
        var stillLiveRuleId = BuiltInGatherRules.GetAll().First().RuleId;
        var rig = new SunsetRig(stillLiveRuleId, gcDeleted: 1, gcFailed: 0);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.DoesNotContain(stillLiveRuleId, rig.GcCalls);
    }

    [Fact]
    public async Task ProcessSunset_returns_SkippedOnSafeStateFailure_when_safe_state_fails()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 0, safeStateOk: false);
        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);

        var sunsetRule = new GatherRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetGatherRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnSafeStateFailure, outcome);
        Assert.Equal(0, gcd);
        Assert.False(sunsetRule.Enabled); // safe-state mutated the in-memory rule
    }

    [Fact]
    public async Task ProcessSunset_returns_SkippedOnGcFailure_when_GC_reports_failures()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 1);
        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);

        var sunsetRule = new GatherRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetGatherRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnGcFailure, outcome);
        Assert.Equal(2, gcd);
    }

    [Fact]
    public async Task ProcessSunset_returns_SkippedOnGlobalDeleteFailure_when_tombstone_fails()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 3, gcFailed: 0, globalDeleteOk: false);
        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);

        var sunsetRule = new GatherRule { RuleId = SunsetRuleId, IsBuiltIn = true, Enabled = true };
        var (outcome, gcd) = await service.ProcessSunsetGatherRuleAsync(sunsetRule);

        Assert.Equal(SunsetOutcome.SkippedOnGlobalDeleteFailure, outcome);
        Assert.Equal(3, gcd);
    }

    [Fact]
    public async Task ReseedBuiltIn_returns_orphanGc_count_in_tuple()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 3, gcFailed: 0);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.True(deleted >= 1, $"Expected at least 1 deletion (incl. sunset), got {deleted}");
        Assert.Equal(BuiltInGatherRules.GetAll().Count, written);
        Assert.Equal(3, orphanStatesGcd);
    }

    [Fact]
    public async Task ReseedBuiltIn_skips_global_delete_on_partial_GC_failure()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 2, gcFailed: 1);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        var (deleted, written, orphanStatesGcd) = await service.ReseedBuiltInRulesAsync();

        Assert.Contains(SunsetRuleId, rig.GcCalls);
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteGatherCalls);
        Assert.Equal(2, orphanStatesGcd);
    }

    [Fact]
    public async Task GetAllRules_runtime_filter_hides_sunset_rule_even_with_enabled_tenant_override()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 1);

        rig.Repo.Setup(r => r.GetRuleStatesAsync(TenantId))
            .ReturnsAsync(new Dictionary<string, RuleState>
            {
                [SunsetRuleId] = new RuleState { Enabled = true }
            });

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.DoesNotContain(merged, r => r.RuleId == SunsetRuleId);
        Assert.True(merged.Count >= BuiltInGatherRules.GetAll().Count,
            "Live catalog rules must still appear in the merged result.");
    }

    [Fact]
    public async Task GetAllRules_runtime_filter_does_not_hide_custom_tenant_rules()
    {
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 1, gcFailed: 0);

        var customRuleId = "TENANT-CUSTOM-XYZ";
        rig.Repo.Setup(r => r.GetGatherRulesAsync(TenantId)).ReturnsAsync(new List<GatherRule>
        {
            new GatherRule
            {
                RuleId = customRuleId,
                Title = "Custom",
                CollectorType = "registry",
                Target = "HKLM\\SOFTWARE\\X",
                Trigger = "startup",
                OutputEventType = "gather_custom",
                IsBuiltIn = false,
                IsCommunity = false,
                Enabled = true,
            }
        });

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(merged, r => r.RuleId == customRuleId);
    }

    [Fact]
    public async Task GitHubAhead_rule_is_not_sunset_and_stays_visible()
    {
        // A rule reseeded from GitHub ahead of this binary (Provenance=github, not in the embedded
        // catalog) must NOT be sunset by the embedded auto-seed, and must NOT be hidden by the
        // runtime filter — it is legitimately present, just not yet shipped in the binary.
        var rig = new SunsetRig(SunsetRuleId, gcDeleted: 0, gcFailed: 0, provenance: RuleProvenance.GitHubAhead);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        var merged = await service.GetAllRulesForTenantAsync(TenantId);

        Assert.DoesNotContain(SunsetRuleId, rig.GcCalls);           // not GC'd
        Assert.DoesNotContain(SunsetRuleId, rig.DeleteGatherCalls); // not tombstoned
        Assert.Contains(merged, r => r.RuleId == SunsetRuleId);     // still visible
    }

    [Fact]
    public async Task GitHubAhead_content_update_on_existing_id_survives_embedded_seed()
    {
        // Codex round 2: GitHub reseeded a NEWER version of an EXISTING built-in id while the
        // binary is still old (Provenance=github + content differs from the binary). The embedded
        // seed must NOT overwrite it with the older embedded definition — it stays until a redeploy
        // makes the embedded content match.
        var liveId = BuiltInGatherRules.GetAll().First().RuleId;
        var rig = new SunsetRig(liveId, gcDeleted: 0, gcFailed: 0,
            provenance: RuleProvenance.GitHubAhead, divergeContent: true);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.DoesNotContain(liveId, rig.SafeStateWrites); // NOT overwritten by the older binary def
    }

    [Fact]
    public async Task GitHubAhead_rule_is_reclaimed_to_embedded_once_binary_ships_it()
    {
        // A github-provenance row whose id IS in the embedded catalog (the binary caught up) is
        // rewritten with embedded provenance via the update-diff, so the embedded sunset manages it
        // again from then on.
        var liveId = BuiltInGatherRules.GetAll().First().RuleId;
        var rig = new SunsetRig(liveId, gcDeleted: 0, gcFailed: 0, provenance: RuleProvenance.GitHubAhead);

        var service = new GatherRuleService(rig.Repo.Object, NullLogger<GatherRuleService>.Instance);
        await service.GetAllRulesForTenantAsync(TenantId);

        Assert.Contains(liveId, rig.SafeStateWrites); // rewritten (reclaimed embedded) via update-diff
    }

    // ===== Helpers =====

    private sealed class SunsetRig
    {
        public Mock<IRuleRepository> Repo { get; }
        public List<string> SafeStateWrites { get; } = new();
        public List<string> GcCalls { get; } = new();
        public List<string> DeleteGatherCalls { get; } = new();
        public List<string> CallOrder { get; } = new();

        private (int gcDeleted, int gcFailed) _gcReturn;

        public SunsetRig(string sunsetRuleId, int gcDeleted, int gcFailed,
                         bool safeStateOk = true, bool globalDeleteOk = true, string? provenance = null,
                         bool divergeContent = false)
        {
            _gcReturn = (gcDeleted, gcFailed);

            var existingInDb = BuiltInGatherRules.GetAll().ToList();
            if (!existingInDb.Any(r => r.RuleId == sunsetRuleId))
            {
                existingInDb.Add(new GatherRule
                {
                    RuleId = sunsetRuleId,
                    Title = "Legacy",
                    Description = "Legacy",
                    Category = "device",
                    CollectorType = "registry",
                    Target = "HKLM\\SOFTWARE\\Legacy",
                    Trigger = "startup",
                    OutputEventType = "gather_legacy",
                    Version = "1.0.0",
                    IsBuiltIn = true,
                    Enabled = true,
                });
            }
            var targetRow = existingInDb.First(r => r.RuleId == sunsetRuleId);
            if (provenance != null)
                targetRow.Provenance = provenance;
            // Simulate a GitHub reseed that shipped a newer version than the binary for this id.
            if (divergeContent)
                targetRow.Version = (targetRow.Version ?? "1.0.0") + "-github";

            Repo = new Mock<IRuleRepository>();
            Repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(existingInDb);
            Repo.Setup(r => r.GetGatherRulesAsync(It.Is<string>(s => s != "global"))).ReturnsAsync(new List<GatherRule>());
            Repo.Setup(r => r.GetRuleStatesAsync(It.IsAny<string>())).ReturnsAsync(new Dictionary<string, RuleState>());

            Repo.Setup(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), It.IsAny<string>()))
                .Callback<GatherRule, string>((rule, _) =>
                {
                    if (rule.RuleId == sunsetRuleId)
                    {
                        SafeStateWrites.Add(rule.RuleId);
                        CallOrder.Add($"store:{rule.RuleId}");
                    }
                })
                .ReturnsAsync((GatherRule rule, string _) => rule.RuleId == sunsetRuleId ? safeStateOk : true);

            Repo.Setup(r => r.DeleteGatherRuleAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((_, ruleId) =>
                {
                    DeleteGatherCalls.Add(ruleId);
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

        public void SetGcReturn(int gcDeleted, int gcFailed) => _gcReturn = (gcDeleted, gcFailed);
    }
}
