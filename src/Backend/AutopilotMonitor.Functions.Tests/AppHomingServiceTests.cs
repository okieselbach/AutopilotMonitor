using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for <see cref="AppHomingService"/> — funnel eligibility, the consent-driven
/// auto-flip, and the flip persist side-effect chain (save + cache invalidation + audit +
/// ops events). The consent probe and the kill-switch flag are mocked at their virtual seams.
/// </summary>
public class AppHomingServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string PrimaryId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string LegacyId = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Actor = "admin@contoso.com";

    private readonly Mock<AdminConfigurationService> _adminConfigMock;
    private readonly Mock<TenantConfigurationService> _tenantConfigMock;
    private readonly Mock<GraphTokenService> _graphTokenMock;
    private readonly Mock<IGraphFeatureDetector> _detectorMock;
    private readonly Mock<IMaintenanceRepository> _maintenanceMock;
    private readonly List<OpsEventEntry> _savedOpsEvents = new();
    private readonly EntraAppRegistry _registry;
    private readonly AppHomingService _sut;

    public AppHomingServiceTests() : this(legacyConfigured: true) { }

    private AppHomingServiceTests(bool legacyConfigured)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = PrimaryId,
            ["EntraId:ClientSecret"] = "primary-secret",
        };
        if (legacyConfigured)
        {
            configValues["EntraId:LegacyClientId"] = LegacyId;
            configValues["EntraId:LegacyClientSecret"] = "legacy-secret";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        _registry = new EntraAppRegistry(configuration, NullLogger<EntraAppRegistry>.Instance);

        var configRepo = Mock.Of<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());

        _adminConfigMock = new Mock<AdminConfigurationService>(
            configRepo, NullLogger<AdminConfigurationService>.Instance, cache)
        { CallBase = false };
        _tenantConfigMock = new Mock<TenantConfigurationService>(
            configRepo, NullLogger<TenantConfigurationService>.Instance, cache)
        { CallBase = false };
        _graphTokenMock = new Mock<GraphTokenService>(
            NullLogger<GraphTokenService>.Instance,
            Mock.Of<System.Net.Http.IHttpClientFactory>(),
            cache,
            configuration,
            _registry,
            _tenantConfigMock.Object)
        { CallBase = false };
        _detectorMock = new Mock<IGraphFeatureDetector>();
        _maintenanceMock = new Mock<IMaintenanceRepository>();
        _maintenanceMock
            .Setup(m => m.LogAuditEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
            .ReturnsAsync(true);

        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => { lock (_savedOpsEvents) _savedOpsEvents.Add(e); })
            .Returns(Task.CompletedTask);
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(_adminConfigMock.Object);
        var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        _sut = new AppHomingService(
            NullLogger<AppHomingService>.Instance,
            _adminConfigMock.Object,
            _tenantConfigMock.Object,
            _registry,
            _graphTokenMock.Object,
            _detectorMock.Object,
            _maintenanceMock.Object,
            opsService,
            new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true }));

        // Defaults: flag on, legacy-homed config exists, probe succeeds. FlipAsync reads via
        // the cache-bypassing GetConfigurationFreshAsync (read-modify-write on the whole entity).
        _adminConfigMock.Setup(x => x.IsSelfServiceAppHomingEnabledAsync()).ReturnsAsync(true);
        var config = LegacyHomedConfig();
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(config);
        _tenantConfigMock.Setup(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        SetupProbe(GraphTokenResult.Success("tok"));
    }

    private static TenantConfiguration LegacyHomedConfig()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.HomedAppClientId = null;
        return config;
    }

    private void SetupProbe(GraphTokenResult result) =>
        _graphTokenMock
            .Setup(x => x.GetAccessTokenForAppAsync(TenantId, It.Is<EntraAppCredentials>(c => !c.IsLegacy),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    // ── IsFunnelEligibleAsync ───────────────────────────────────────────────

    [Fact]
    public async Task Funnel_is_eligible_for_legacy_homed_tenant_with_flag_on()
    {
        Assert.True(await _sut.IsFunnelEligibleAsync(LegacyHomedConfig()));
    }

    [Fact]
    public async Task Funnel_is_not_eligible_when_legacy_pair_unconfigured()
    {
        var sut = new AppHomingServiceTests(legacyConfigured: false)._sut;
        Assert.False(await sut.IsFunnelEligibleAsync(LegacyHomedConfig()));
    }

    [Fact]
    public async Task Funnel_is_not_eligible_for_primary_homed_tenant()
    {
        var config = LegacyHomedConfig();
        config.HomedAppClientId = PrimaryId;
        Assert.False(await _sut.IsFunnelEligibleAsync(config));
    }

    [Fact]
    public async Task Funnel_is_not_eligible_when_flag_off()
    {
        _adminConfigMock.Setup(x => x.IsSelfServiceAppHomingEnabledAsync()).ReturnsAsync(false);
        Assert.False(await _sut.IsFunnelEligibleAsync(LegacyHomedConfig()));
    }

    [Fact]
    public async Task Funnel_does_not_block_on_entra_app_roles()
    {
        // Deliberate: app-role tenants may flip (operator informs them personally); the
        // reminder trail is asserted in the flip test below.
        var config = LegacyHomedConfig();
        config.EntraAppRolesEnabled = true;
        Assert.True(await _sut.IsFunnelEligibleAsync(config));
    }

    // ── TryAutoFlipToPrimaryAsync ───────────────────────────────────────────

    [Fact]
    public async Task AutoFlip_flips_on_probe_success_with_full_side_effect_chain()
    {
        var outcome = await _sut.TryAutoFlipToPrimaryAsync(LegacyHomedConfig(), Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.Flipped, outcome);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.Is<TenantConfiguration>(c =>
            string.Equals(c.HomedAppClientId, PrimaryId, StringComparison.OrdinalIgnoreCase)
            && c.UpdatedBy == Actor), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _detectorMock.Verify(x => x.InvalidateTenant(TenantId), Times.Once);
        _maintenanceMock.Verify(m => m.LogAuditEntryAsync(TenantId, "UPDATE", "TenantConfiguration",
            TenantId, Actor, It.Is<Dictionary<string, string>?>(d =>
                d != null && d.ContainsKey("HomedAppClientId"))), Times.Once);
        Assert.Contains(_savedOpsEvents, e => e.EventType == "AppHomingFlipped");
        Assert.DoesNotContain(_savedOpsEvents, e => e.EventType == "AppHomingFlippedWithEntraRoles");
    }

    [Fact]
    public async Task AutoFlip_does_not_flip_on_permanent_probe_failure()
    {
        SetupProbe(GraphTokenResult.PermanentFailure());

        var outcome = await _sut.TryAutoFlipToPrimaryAsync(LegacyHomedConfig(), Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.ProbeFailed, outcome);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AutoFlip_does_not_flip_on_transient_probe()
    {
        SetupProbe(GraphTokenResult.TransientFailure());

        var outcome = await _sut.TryAutoFlipToPrimaryAsync(LegacyHomedConfig(), Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.ProbeTransient, outcome);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AutoFlip_is_not_eligible_for_primary_homed_tenant_and_never_probes()
    {
        var config = LegacyHomedConfig();
        config.HomedAppClientId = PrimaryId;

        var outcome = await _sut.TryAutoFlipToPrimaryAsync(config, Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.NotEligible, outcome);
        _graphTokenMock.Verify(x => x.GetAccessTokenForAppAsync(It.IsAny<string>(),
            It.IsAny<EntraAppCredentials>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoFlip_is_not_eligible_when_flag_off()
    {
        _adminConfigMock.Setup(x => x.IsSelfServiceAppHomingEnabledAsync()).ReturnsAsync(false);

        var outcome = await _sut.TryAutoFlipToPrimaryAsync(LegacyHomedConfig(), Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.NotEligible, outcome);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    // ── FlipAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Flip_of_entra_app_roles_tenant_emits_reminder_ops_event()
    {
        var config = LegacyHomedConfig();
        config.EntraAppRolesEnabled = true;
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(config);

        await _sut.FlipAsync(TenantId, PrimaryId, Actor, "manual-ga");

        Assert.Contains(_savedOpsEvents, e => e.EventType == "AppHomingFlipped");
        Assert.Contains(_savedOpsEvents, e => e.EventType == "AppHomingFlippedWithEntraRoles");
    }

    [Fact]
    public async Task Flip_noops_when_already_at_target()
    {
        var config = LegacyHomedConfig();
        config.HomedAppClientId = PrimaryId;
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(config);

        await _sut.FlipAsync(TenantId, PrimaryId, Actor, "manual-ga");

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _detectorMock.Verify(x => x.InvalidateTenant(It.IsAny<string>()), Times.Never);
        Assert.Empty(_savedOpsEvents);
    }

    [Fact]
    public async Task Flip_throws_when_config_row_missing_and_saves_nothing()
    {
        // A flip must never materialize a default row (e.g. resurrect an offboarded tenant);
        // the fresh read returning null is a hard stop.
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId))
            .ReturnsAsync((TenantConfiguration?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.FlipAsync(TenantId, PrimaryId, Actor, "manual-ga"));

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _detectorMock.Verify(x => x.InvalidateTenant(It.IsAny<string>()), Times.Never);
        Assert.Empty(_savedOpsEvents);
    }

    [Fact]
    public async Task Flip_back_to_legacy_persists_null_homing()
    {
        var config = LegacyHomedConfig();
        config.HomedAppClientId = PrimaryId;
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(config);

        await _sut.FlipAsync(TenantId, null, Actor, "manual-ga");

        // The null-homing invariant: legacy is represented as null, never as the legacy GUID.
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.Is<TenantConfiguration>(c =>
            c.HomedAppClientId == null), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _detectorMock.Verify(x => x.InvalidateTenant(TenantId), Times.Once);
    }
}
