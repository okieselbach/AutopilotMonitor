using AutopilotMonitor.Functions.Functions.Rules;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the three gather-rule scope/emit fields (ActivePhases, ActiveFromPhase, EmitMode):
/// Store↔Map table-serialization roundtrip (table-serialization rule: every model field in BOTH
/// directions), legacy rows without the new columns, the <see cref="GatherRuleService.ContentEquivalent"/>
/// null-tolerance (no reseed churn), and the create/update validation matrix in
/// <see cref="GatherRulesFunction.ValidateScopeAndEmitMode"/>.
/// </summary>
public class GatherRuleScopeFieldsTests
{
    // ── Store ↔ Map roundtrip ──────────────────────────────────────────────

    private static (TableStorageService service, Func<TableEntity?> lastUpserted) BuildStorageHarness()
    {
        TableEntity? captured = null;
        var tableClient = new Mock<TableClient>();
        tableClient
            .Setup(c => c.UpsertEntityAsync(
                It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns<TableEntity, TableUpdateMode, CancellationToken>((e, _, _) =>
            {
                captured = e;
                return Task.FromResult(new Mock<Response>().Object);
            });

        var serviceClient = new Mock<TableServiceClient>();
        serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(tableClient.Object);

        var service = new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance);
        return (service, () => captured);
    }

    [Fact]
    public async Task Roundtrip_ScopeFields_SurviveStoreAndMap()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var rule = new GatherRule
        {
            RuleId = "GATHER-SCOPE-001",
            Title = "Scoped rule",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Contoso",
            Trigger = "interval",
            IntervalSeconds = 60,
            OutputEventType = "gather_scoped",
            ActivePhases = new List<string> { "AccountSetup", "AppsUser" },
            EmitMode = "on_change",
        };

        Assert.True(await service.StoreGatherRuleAsync(rule, "global"));
        var mapped = service.MapToGatherRule(lastUpserted()!);

        Assert.NotNull(mapped.ActivePhases);
        Assert.Equal(new[] { "AccountSetup", "AppsUser" }, mapped.ActivePhases);
        Assert.Null(mapped.ActiveFromPhase);
        Assert.Equal("on_change", mapped.EmitMode);
    }

    [Fact]
    public async Task Roundtrip_FromPhase_SurvivesStoreAndMap()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var rule = new GatherRule
        {
            RuleId = "GATHER-SCOPE-002",
            Title = "From-phase rule",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Contoso",
            Trigger = "interval",
            OutputEventType = "gather_scoped",
            ActiveFromPhase = "AccountSetup",
        };

        Assert.True(await service.StoreGatherRuleAsync(rule, "global"));
        var mapped = service.MapToGatherRule(lastUpserted()!);

        Assert.Null(mapped.ActivePhases);
        Assert.Equal("AccountSetup", mapped.ActiveFromPhase);
        Assert.Null(mapped.EmitMode);
    }

    [Fact]
    public async Task Roundtrip_NullScopeFields_StayNull()
    {
        var (service, lastUpserted) = BuildStorageHarness();
        var rule = new GatherRule
        {
            RuleId = "GATHER-SCOPE-003",
            Title = "Unscoped rule",
            CollectorType = "registry",
            Target = "HKLM\\SOFTWARE\\Contoso",
            Trigger = "startup",
            OutputEventType = "gather_plain",
        };

        Assert.True(await service.StoreGatherRuleAsync(rule, "global"));
        var mapped = service.MapToGatherRule(lastUpserted()!);

        Assert.Null(mapped.ActivePhases);
        Assert.Null(mapped.ActiveFromPhase);
        Assert.Null(mapped.EmitMode);
    }

    [Fact]
    public void Map_LegacyRow_WithoutScopeColumns_DefaultsToNull()
    {
        var (service, _) = BuildStorageHarness();
        // A row written before the scope fields existed: none of the new columns present.
        var legacy = new TableEntity("global", "GATHER-LEGACY-001")
        {
            ["Title"] = "Legacy rule",
            ["CollectorType"] = "registry",
            ["Target"] = "HKLM\\SOFTWARE\\Contoso",
            ["Trigger"] = "startup",
            ["OutputEventType"] = "gather_legacy",
        };

        var mapped = service.MapToGatherRule(legacy);

        Assert.Null(mapped.ActivePhases);
        Assert.Null(mapped.ActiveFromPhase);
        Assert.Null(mapped.EmitMode);
    }

    // ── ContentEquivalent ──────────────────────────────────────────────────

    private static GatherRule BaseRule() => new()
    {
        RuleId = "GATHER-EQ-001",
        Title = "Rule",
        Description = "Desc",
        Version = "1.0.0",
        CollectorType = "registry",
        Target = "HKLM\\SOFTWARE\\Contoso",
        Trigger = "interval",
        TriggerPhase = "",
        OutputEventType = "gather_eq",
    };

    [Fact]
    public void ContentEquivalent_NullAndEmptyActivePhases_AreEquivalent()
    {
        // Pre-existing DB rows map absent columns to null; seeds are also null — and an
        // explicitly-empty list means the same "unrestricted". No reseed churn allowed.
        var a = BaseRule();
        var b = BaseRule();
        b.ActivePhases = new List<string>();

        Assert.True(GatherRuleService.ContentEquivalent(a, b));
        Assert.True(GatherRuleService.ContentEquivalent(b, a));
    }

    [Fact]
    public void ContentEquivalent_DetectsChangesInEachScopeField()
    {
        var baseline = BaseRule();

        var phases = BaseRule();
        phases.ActivePhases = new List<string> { "AccountSetup" };
        Assert.False(GatherRuleService.ContentEquivalent(baseline, phases));

        var from = BaseRule();
        from.ActiveFromPhase = "AccountSetup";
        Assert.False(GatherRuleService.ContentEquivalent(baseline, from));

        var emit = BaseRule();
        emit.EmitMode = "on_change";
        Assert.False(GatherRuleService.ContentEquivalent(baseline, emit));
    }

    [Fact]
    public void ContentEquivalent_SameScopeFields_AreEquivalent()
    {
        var a = BaseRule();
        a.ActivePhases = new List<string> { "DeviceSetup", "AccountSetup" };
        a.EmitMode = "on_change";
        var b = BaseRule();
        b.ActivePhases = new List<string> { "DeviceSetup", "AccountSetup" };
        b.EmitMode = "on_change";

        Assert.True(GatherRuleService.ContentEquivalent(a, b));
    }

    // ── Validation ─────────────────────────────────────────────────────────

    private static GatherRule ValidationRule(
        List<string>? activePhases = null, string? activeFromPhase = null, string? emitMode = null) => new()
    {
        RuleId = "GATHER-VAL-001",
        Title = "Rule",
        CollectorType = "registry",
        Target = "HKLM\\SOFTWARE\\Contoso",
        Trigger = "interval",
        OutputEventType = "gather_val",
        ActivePhases = activePhases,
        ActiveFromPhase = activeFromPhase,
        EmitMode = emitMode,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("Start")]
    [InlineData("DevicePreparation")]
    [InlineData("AccountSetup")]
    [InlineData("Complete")]
    [InlineData("accountsetup")] // case-insensitive
    public void Validate_AcceptsValidFromPhase(string? fromPhase)
    {
        Assert.Null(GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(activeFromPhase: fromPhase)));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Failed")]
    [InlineData("4")]          // numeric enum value — canonical names only
    [InlineData("DeviceESP")]  // the invalid token the old free-text UI suggested
    public void Validate_RejectsInvalidFromPhase(string fromPhase)
    {
        Assert.NotNull(GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(activeFromPhase: fromPhase)));
    }

    [Fact]
    public void Validate_AcceptsValidActivePhases_AndRejectsInvalidEntries()
    {
        Assert.Null(GatherRulesFunction.ValidateScopeAndEmitMode(
            ValidationRule(activePhases: new List<string> { "DeviceSetup", "AppsDevice" })));

        Assert.NotNull(GatherRulesFunction.ValidateScopeAndEmitMode(
            ValidationRule(activePhases: new List<string> { "DeviceSetup", "Failed" })));
        Assert.NotNull(GatherRulesFunction.ValidateScopeAndEmitMode(
            ValidationRule(activePhases: new List<string> { "" })));
    }

    [Fact]
    public void Validate_RejectsBothScopeFieldsSet()
    {
        var error = GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(
            activePhases: new List<string> { "AccountSetup" }, activeFromPhase: "DeviceSetup"));

        Assert.NotNull(error);
        Assert.Contains("mutually exclusive", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("always")]
    [InlineData("on_change")]
    [InlineData("ON_CHANGE")] // case-insensitive
    public void Validate_AcceptsValidEmitModes(string? emitMode)
    {
        Assert.Null(GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(emitMode: emitMode)));
    }

    [Fact]
    public void Validate_RejectsUnknownEmitMode()
    {
        Assert.NotNull(GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(emitMode: "sometimes")));
    }

    [Fact]
    public void Validate_TogglePartialPayload_PassesThrough()
    {
        // The portal toggle sends only { enabled, isBuiltIn, isCommunity } — none of the
        // scope fields — and must not be rejected.
        var toggle = new GatherRule { RuleId = "GATHER-VAL-002", Enabled = false };
        Assert.Null(GatherRulesFunction.ValidateScopeAndEmitMode(toggle));
    }

    [Fact]
    public void Validate_EmptyActivePhasesList_IsUnrestricted_AndPasses()
    {
        Assert.Null(GatherRulesFunction.ValidateScopeAndEmitMode(ValidationRule(activePhases: new List<string>())));
    }
}
