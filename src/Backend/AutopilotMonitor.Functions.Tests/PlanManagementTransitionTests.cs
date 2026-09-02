using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="PlanManagementFunction.ApplyPlanChanges"/> — the pure mutation core
/// of the PATCH plan endpoint. Pins the retention-grace anchor lifecycle: an EFFECTIVE
/// Pro→Community transition stamps <see cref="TenantConfiguration.ProDowngradedUtc"/>, any
/// effectively-Pro outcome clears it, and a planTier downgrade under an active trial does
/// NOT stamp (the edition is still Pro — the later trial expiry is the anchor then).
/// </summary>
public class PlanManagementTransitionTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    private const string Caller = "ga@operator.example";

    private static (TenantEdition Before, TenantEdition After, Dictionary<string, string> Changes) Apply(
        TenantConfiguration config, string? planTier = null, bool trialProvided = false, DateTime? trialExpiresUtc = null)
    {
        var changes = new Dictionary<string, string>();
        var (before, after) = PlanManagementFunction.ApplyPlanChanges(
            config, planTier, trialProvided, trialExpiresUtc, Caller, Now, changes);
        return (before, after, changes);
    }

    [Fact]
    public void Downgrade_ProToCommunity_StampsAnchorAndRecordsChange()
    {
        var config = new TenantConfiguration { TenantId = "t1", PlanTier = "pro" };

        var (before, after, changes) = Apply(config, planTier: "community");

        Assert.Equal(TenantEdition.Pro, before);
        Assert.Equal(TenantEdition.Community, after);
        Assert.Equal(Now, config.ProDowngradedUtc);
        Assert.True(changes.ContainsKey("ProDowngradedUtc"));
        Assert.True(changes.ContainsKey("PlanTier"));
    }

    [Fact]
    public void Upgrade_CommunityToPro_ClearsAnchor()
    {
        var config = new TenantConfiguration
        {
            TenantId = "t1",
            PlanTier = "community",
            ProDowngradedUtc = Now.AddDays(-10)
        };

        var (_, after, changes) = Apply(config, planTier: "pro");

        Assert.Equal(TenantEdition.Pro, after);
        Assert.Null(config.ProDowngradedUtc);
        Assert.True(changes.ContainsKey("ProDowngradedUtc"));
    }

    [Fact]
    public void Downgrade_WithActiveTrial_DoesNotStamp_EditionStaysPro()
    {
        // planTier drops but the trial keeps the EFFECTIVE edition Pro: no anchor now —
        // the trial's own expiry timestamp anchors the grace later, read-time.
        var config = new TenantConfiguration
        {
            TenantId = "t1",
            PlanTier = "pro",
            TrialExpiresUtc = Now.AddDays(5)
        };

        var (before, after, changes) = Apply(config, planTier: "community");

        Assert.Equal(TenantEdition.Pro, before);
        Assert.Equal(TenantEdition.Pro, after);
        Assert.Null(config.ProDowngradedUtc);
        Assert.False(changes.ContainsKey("ProDowngradedUtc"));
    }

    [Fact]
    public void EndingTrialExplicitly_StampsAnchor()
    {
        // GA sets trialExpiresUtc: null on a trial-Pro tenant — TrialExpiresUtc is gone, so
        // WITHOUT the explicit stamp the grace would have no anchor at all.
        var config = new TenantConfiguration
        {
            TenantId = "t1",
            PlanTier = "free",
            TrialExpiresUtc = Now.AddDays(5)
        };

        var (before, after, _) = Apply(config, trialProvided: true, trialExpiresUtc: null);

        Assert.Equal(TenantEdition.Pro, before);
        Assert.Equal(TenantEdition.Community, after);
        Assert.Equal(Now, config.ProDowngradedUtc);
        Assert.Null(config.TrialExpiresUtc);
    }

    [Fact]
    public void GrantingTrial_ToDowngradedTenant_ClearsAnchor()
    {
        var config = new TenantConfiguration
        {
            TenantId = "t1",
            PlanTier = "community",
            ProDowngradedUtc = Now.AddDays(-10)
        };

        var (_, after, _) = Apply(config, trialProvided: true, trialExpiresUtc: Now.AddDays(30));

        Assert.Equal(TenantEdition.Pro, after);
        Assert.Null(config.ProDowngradedUtc);
        Assert.Equal(Caller, config.TrialGrantedBy);
    }

    [Fact]
    public void NoOpPatch_SameTier_RecordsNothing()
    {
        var config = new TenantConfiguration { TenantId = "t1", PlanTier = "community" };

        var (before, after, changes) = Apply(config, planTier: "community");

        Assert.Equal(TenantEdition.Community, before);
        Assert.Equal(TenantEdition.Community, after);
        Assert.Empty(changes);
        Assert.Null(config.ProDowngradedUtc);
    }

    [Fact]
    public void RepeatedDowngradeAfterReUpgrade_RestampsWithNewTimestamp()
    {
        var config = new TenantConfiguration { TenantId = "t1", PlanTier = "pro" };

        Apply(config, planTier: "community");
        Assert.Equal(Now, config.ProDowngradedUtc);

        Apply(config, planTier: "pro");
        Assert.Null(config.ProDowngradedUtc);

        var changes = new Dictionary<string, string>();
        var later = Now.AddDays(40);
        PlanManagementFunction.ApplyPlanChanges(config, "community", false, null, Caller, later, changes);
        Assert.Equal(later, config.ProDowngradedUtc);
    }

    // ── Delegated (MSP) slot override ────────────────────────────────────────────

    [Fact]
    public void SlotChange_NotProvided_IsANoOp()
    {
        var config = new TenantConfiguration { TenantId = "t1", MaxDelegatedTenantsOverride = 3 };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyDelegatedSlotChange(config, provided: false, maxDelegatedTenants: null, changes);
        Assert.Equal(3, config.MaxDelegatedTenantsOverride);
        Assert.Empty(changes);
    }

    [Fact]
    public void SlotChange_SetFromCatalog_RecordsAndApplies()
    {
        var config = new TenantConfiguration { TenantId = "t1" };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyDelegatedSlotChange(config, provided: true, maxDelegatedTenants: 4, changes);
        Assert.Equal(4, config.MaxDelegatedTenantsOverride);
        Assert.Equal("(catalog) -> 4", changes["MaxDelegatedTenantsOverride"]);
    }

    [Fact]
    public void SlotChange_ClearToCatalog_RecordsAndApplies()
    {
        var config = new TenantConfiguration { TenantId = "t1", MaxDelegatedTenantsOverride = 4 };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyDelegatedSlotChange(config, provided: true, maxDelegatedTenants: null, changes);
        Assert.Null(config.MaxDelegatedTenantsOverride);
        Assert.Equal("4 -> (catalog)", changes["MaxDelegatedTenantsOverride"]);
    }

    [Fact]
    public void SlotChange_SameValue_RecordsNothing()
    {
        var config = new TenantConfiguration { TenantId = "t1", MaxDelegatedTenantsOverride = 4 };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyDelegatedSlotChange(config, provided: true, maxDelegatedTenants: 4, changes);
        Assert.Empty(changes);
    }

    // ── Tenant-wide MCP usage-plan override ──────────────────────────────────────

    [Fact]
    public void McpPlanChange_NotProvided_IsANoOp()
    {
        var config = new TenantConfiguration { TenantId = "t1", McpUsagePlanOverride = "msp" };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyMcpUsagePlanChange(config, provided: false, mcpUsagePlan: null, changes);
        Assert.Equal("msp", config.McpUsagePlanOverride);
        Assert.Empty(changes);
    }

    [Fact]
    public void McpPlanChange_SetFromDefault_NormalizesRecordsAndApplies()
    {
        var config = new TenantConfiguration { TenantId = "t1", PlanTier = "pro" };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyMcpUsagePlanChange(config, provided: true, mcpUsagePlan: " MSP ", changes);
        Assert.Equal("msp", config.McpUsagePlanOverride);
        Assert.Equal("(edition default) -> msp", changes["McpUsagePlanOverride"]);
        // The override never touches the edition.
        Assert.Equal("pro", config.PlanTier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void McpPlanChange_ClearToDefault_RecordsAndApplies(string? cleared)
    {
        var config = new TenantConfiguration { TenantId = "t1", McpUsagePlanOverride = "msp" };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyMcpUsagePlanChange(config, provided: true, mcpUsagePlan: cleared, changes);
        Assert.Null(config.McpUsagePlanOverride);
        Assert.Equal("msp -> (edition default)", changes["McpUsagePlanOverride"]);
    }

    [Fact]
    public void McpPlanChange_SameValue_RecordsNothing()
    {
        var config = new TenantConfiguration { TenantId = "t1", McpUsagePlanOverride = "msp" };
        var changes = new Dictionary<string, string>();
        PlanManagementFunction.ApplyMcpUsagePlanChange(config, provided: true, mcpUsagePlan: "Msp", changes);
        Assert.Equal("msp", config.McpUsagePlanOverride);
        Assert.Empty(changes);
    }

    [Fact]
    public void IsKnownUsagePlan_MatchesDefinitionsCaseInsensitively()
    {
        var definitions = new List<PlanTierDefinition>
        {
            new() { Name = "community" }, new() { Name = "pro" }, new() { Name = " MSP " },
        };
        Assert.True(PlanManagementFunction.IsKnownUsagePlan("msp", definitions));
        Assert.True(PlanManagementFunction.IsKnownUsagePlan("pro", definitions));
        Assert.False(PlanManagementFunction.IsKnownUsagePlan("enterprise", definitions));
        Assert.False(PlanManagementFunction.IsKnownUsagePlan("msp", new List<PlanTierDefinition>()));
    }
}
