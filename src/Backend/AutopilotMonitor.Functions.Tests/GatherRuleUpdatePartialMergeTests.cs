using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the custom-rule partial-PUT fix on <see cref="GatherRuleService.UpdateRuleAsync"/>:
/// the portal toggle PUTs only { enabled, isBuiltIn, isCommunity }, and full-storing that
/// payload wiped Title/Target/Trigger/Parameters of the custom rule. Toggle-style partials
/// (empty Title/CollectorType/Target) must merge Enabled into the existing row; full payloads
/// (edit flow) must still replace the row but preserve the original CreatedAt.
/// </summary>
public class GatherRuleUpdatePartialMergeTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string RuleId = "GATHER-CUST-001";

    private static GatherRule ExistingCustomRule() => new()
    {
        RuleId = RuleId,
        Title = "Collect BIOS Config",
        Description = "Reads the RealmJoin BIOS key",
        Category = "device",
        CollectorType = "registry",
        Target = "HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS",
        Parameters = new Dictionary<string, string> { ["valueName"] = "Version" },
        Trigger = "interval",
        IntervalSeconds = 60,
        OutputEventType = "gather_bios_config",
        Enabled = true,
        IsBuiltIn = false,
        IsCommunity = false,
        CreatedAt = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        ActiveFromPhase = "AccountSetup",
        EmitMode = "on_change",
    };

    private static (GatherRuleService service, Mock<IRuleRepository> repo, List<GatherRule> stored) BuildService(
        GatherRule? existingTenantRule)
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        var stored = new List<GatherRule>();

        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>());
        repo.Setup(r => r.GetGatherRulesAsync(TenantId)).ReturnsAsync(
            existingTenantRule == null ? new List<GatherRule>() : new List<GatherRule> { existingTenantRule });
        repo.Setup(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), TenantId))
            .Callback<GatherRule, string>((rule, _) => stored.Add(rule))
            .ReturnsAsync(true);

        return (new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance), repo, stored);
    }

    [Fact]
    public async Task TogglePartial_merges_enabled_and_preserves_definition_fields()
    {
        var existing = ExistingCustomRule();
        var (service, _, stored) = BuildService(existing);

        // Exactly what the portal toggle sends: { enabled, isBuiltIn, isCommunity }.
        var togglePayload = new GatherRule { RuleId = RuleId, Enabled = false, IsBuiltIn = false, IsCommunity = false };

        var ok = await service.UpdateRuleAsync(TenantId, togglePayload);

        Assert.True(ok);
        var written = Assert.Single(stored);
        Assert.False(written.Enabled);
        Assert.Equal("Collect BIOS Config", written.Title);
        Assert.Equal("HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS", written.Target);
        Assert.Equal("registry", written.CollectorType);
        Assert.Equal("interval", written.Trigger);
        Assert.Equal(60, written.IntervalSeconds);
        Assert.Equal("Version", written.Parameters["valueName"]);
        Assert.Equal("AccountSetup", written.ActiveFromPhase);
        Assert.Equal("on_change", written.EmitMode);
        Assert.Equal(existing.CreatedAt, written.CreatedAt);
    }

    [Fact]
    public async Task TogglePartial_for_unknown_custom_rule_fails_without_storing()
    {
        var (service, repo, stored) = BuildService(existingTenantRule: null);

        var togglePayload = new GatherRule { RuleId = RuleId, Enabled = false, IsBuiltIn = false, IsCommunity = false };

        var ok = await service.UpdateRuleAsync(TenantId, togglePayload);

        Assert.False(ok);
        Assert.Empty(stored);
        repo.Verify(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FullPayload_replaces_rule_but_preserves_original_CreatedAt()
    {
        var existing = ExistingCustomRule();
        var (service, _, stored) = BuildService(existing);

        var fullPayload = new GatherRule
        {
            RuleId = RuleId,
            Title = "Collect BIOS Config v2",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS2",
            Trigger = "interval",
            IntervalSeconds = 120,
            OutputEventType = "gather_bios_config",
            Enabled = true,
            // Edit flow sends createdAt from the client; a JSON-mode edit may omit it and
            // default to UtcNow — either way the stored row must keep the original.
            CreatedAt = DateTime.UtcNow,
        };

        var ok = await service.UpdateRuleAsync(TenantId, fullPayload);

        Assert.True(ok);
        var written = Assert.Single(stored);
        Assert.Equal("Collect BIOS Config v2", written.Title);
        Assert.Equal("HKLM\\SOFTWARE\\RealmJoin\\Custom\\BIOS2", written.Target);
        Assert.Equal(120, written.IntervalSeconds);
        Assert.False(written.IsBuiltIn);
        Assert.False(written.IsCommunity);
        Assert.Equal(existing.CreatedAt, written.CreatedAt);
    }

    [Fact]
    public async Task BuiltInRule_toggle_still_goes_through_RuleState_path()
    {
        var repo = new Mock<IRuleRepository>(MockBehavior.Loose);
        repo.Setup(r => r.GetGatherRulesAsync("global")).ReturnsAsync(new List<GatherRule>
        {
            new() { RuleId = RuleId, IsBuiltIn = true, Title = "Built-in", Enabled = true }
        });
        repo.Setup(r => r.StoreRuleStateAsync(TenantId, RuleId, It.IsAny<RuleState>())).ReturnsAsync(true);
        var service = new GatherRuleService(repo.Object, NullLogger<GatherRuleService>.Instance);

        var ok = await service.UpdateRuleAsync(TenantId, new GatherRule { RuleId = RuleId, Enabled = false });

        Assert.True(ok);
        repo.Verify(r => r.StoreRuleStateAsync(TenantId, RuleId, It.Is<RuleState>(s => !s.Enabled)), Times.Once);
        repo.Verify(r => r.StoreGatherRuleAsync(It.IsAny<GatherRule>(), It.IsAny<string>()), Times.Never);
    }
}
