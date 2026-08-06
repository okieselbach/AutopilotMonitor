using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the reserved built-in rule-ID namespace: tenant custom rules must never
/// occupy (ANALYZE|GATHER)-&lt;CATEGORY&gt;-&lt;NUMBER&gt; — a gap or retired ID there can be
/// re-shipped as a built-in later, which would silently shadow the tenant's copy at
/// merge time. The CUSTOM category ("ANALYZE-CUSTOM-001") is the sanctioned tenant
/// namespace; the platform never ships built-ins in a category named CUSTOM.
/// Enforced on BOTH the create path and the custom-branch of the update path
/// (updates upsert, so a PUT would otherwise bypass the create check).
/// </summary>
public class RuleIdReservationTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    [Theory]
    [InlineData("ANALYZE-SEC-002")]       // retired gap — the incident that motivated the policy
    [InlineData("ANALYZE-APP-999")]       // unused number in an existing category
    [InlineData("GATHER-DEVICE-006")]
    [InlineData("analyze-sec-002")]       // case variants are equally confusing → reserved
    [InlineData("Gather-Net-001")]
    [InlineData("ANALYZE-NEWCAT-001")]    // future built-in categories are covered too
    public void Reserved_builtin_ids_are_detected(string ruleId)
        => Assert.True(RuleIdPolicy.IsReservedBuiltInId(ruleId));

    [Theory]
    [InlineData("ANALYZE-CUSTOM-001")]    // sanctioned tenant namespace (portal suggestion)
    [InlineData("GATHER-CUSTOM-042")]
    [InlineData("ANALYZE-ID-001-CUSTOM")] // template copies end in -CUSTOM, not a number
    [InlineData("CONTOSO-WIFI-001")]      // organization prefix
    [InlineData("ANALYZE-SIDECAR-PROVIDER")] // no trailing number (existing prod custom rule)
    [InlineData("dcu-restart-required")]  // freeform (existing prod custom rule)
    [InlineData("")]
    public void Custom_schemes_are_allowed(string ruleId)
        => Assert.False(RuleIdPolicy.IsReservedBuiltInId(ruleId));

    // ---- create path -------------------------------------------------------

    [Fact]
    public async Task Analyze_create_rejects_reserved_id_before_touching_storage()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateRuleAsync(TenantId, new AnalyzeRule { RuleId = "ANALYZE-SEC-002" }));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
        repo.VerifyNoOtherCalls(); // rejected before any storage lookup
    }

    [Fact]
    public async Task Gather_create_rejects_reserved_id_before_touching_storage()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateRuleAsync(TenantId, new GatherRule { RuleId = "GATHER-NET-099" }));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Analyze_create_still_accepts_custom_namespace()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        repo.Setup(r => r.AnalyzeRuleExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.StoreAnalyzeRuleAsync(It.IsAny<AnalyzeRule>(), TenantId)).ReturnsAsync(true);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        Assert.True(await service.CreateRuleAsync(TenantId, new AnalyzeRule { RuleId = "ANALYZE-CUSTOM-001" }));
        repo.Verify(r => r.StoreAnalyzeRuleAsync(It.Is<AnalyzeRule>(x => x.RuleId == "ANALYZE-CUSTOM-001"), TenantId), Times.Once);
    }

    // ---- update path (upserts — must not be a bypass) ----------------------

    [Fact]
    public async Task Analyze_update_custom_branch_rejects_reserved_id()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        // IsBuiltIn=false routes into the custom-rule upsert branch.
        var rule = new AnalyzeRule { RuleId = "ANALYZE-SEC-002", IsBuiltIn = false, IsCommunity = false };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateRuleAsync(TenantId, rule));
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Analyze_update_builtin_state_toggle_is_unaffected()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, "ANALYZE-SEC-001", It.IsAny<RuleState>())).ReturnsAsync(true);
        var service = new AnalyzeRuleService(repo.Object, NullLogger<AnalyzeRuleService>.Instance);

        // Disabling a real built-in stays possible — that flow stores per-tenant state only.
        var rule = new AnalyzeRule { RuleId = "ANALYZE-SEC-001", IsBuiltIn = true, Enabled = false };

        Assert.True(await service.UpdateRuleAsync(TenantId, rule));
        repo.Verify(r => r.StoreRuleStateAsync(TenantId, "ANALYZE-SEC-001", It.IsAny<RuleState>()), Times.Once);
    }

    [Fact]
    public async Task Gather_update_custom_branch_rejects_reserved_id_when_no_global_row_exists()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        // Retired scenario: the ID is reserved but no global row exists anymore.
        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>());
        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);

        var rule = new GatherRule
        {
            RuleId = "GATHER-DEVICE-099",
            Title = "squat",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Microsoft\\Provisioning",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateRuleAsync(TenantId, rule));
        repo.Verify(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Gather_update_builtin_state_toggle_is_unaffected()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>
        {
            new() { RuleId = "GATHER-DEVICE-001", IsBuiltIn = true },
        });
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, "GATHER-DEVICE-001", It.IsAny<RuleState>())).ReturnsAsync(true);
        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);

        var rule = new GatherRule { RuleId = "GATHER-DEVICE-001", Enabled = false };

        Assert.True(await service.UpdateRuleAsync(TenantId, rule));
        repo.Verify(r => r.StoreRuleStateAsync(TenantId, "GATHER-DEVICE-001", It.IsAny<RuleState>()), Times.Once);
    }
}
