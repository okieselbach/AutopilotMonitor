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
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Unit tests for <see cref="AppHomingService"/> — funnel eligibility, the consent probe's
/// role-superset rule, the consent-driven auto-flip, and the flip persist side-effect chain
/// (save + cache invalidation + audit + ops events). Token minting and the kill-switch flag
/// are mocked at their virtual seams.
/// </summary>
public class AppHomingServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string PrimaryId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string LegacyId = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Actor = "admin@contoso.com";
    private const string ValidationRole = "DeviceManagementServiceConfig.Read.All";
    private const string ScriptsRole = "DeviceManagementScripts.Read.All";

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

        // Defaults: flag on, legacy-homed config exists, both apps mint a role-less token (the
        // superset rule passes trivially). FlipAsync reads via the cache-bypassing
        // GetConfigurationFreshAsync (read-modify-write on the whole entity).
        _adminConfigMock.Setup(x => x.IsSelfServiceAppHomingEnabledAsync()).ReturnsAsync(true);
        var config = LegacyHomedConfig();
        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(config);
        _tenantConfigMock.Setup(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        SetupProbe(GraphTokenResult.Success(Jwt()));
        SetupLegacyToken(GraphTokenResult.Success(Jwt()));
    }

    private static TenantConfiguration LegacyHomedConfig()
    {
        var config = TenantConfiguration.CreateDefault(TenantId);
        config.HomedAppClientId = null;
        return config;
    }

    /// <summary>Unsigned app-only token carrying the given Graph application roles (the probe never validates signatures).</summary>
    private static string Jwt(params string[] roles)
    {
        var jwt = new JwtSecurityToken(
            issuer: "https://sts.windows.net/test",
            audience: "https://graph.microsoft.com",
            claims: roles.Select(r => new Claim("roles", r)),
            notBefore: null,
            expires: DateTime.UtcNow.AddHours(1));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    /// <summary>What the PRIMARY app's app-only token mint returns.</summary>
    private void SetupProbe(GraphTokenResult result) =>
        _graphTokenMock
            .Setup(x => x.GetAccessTokenForAppAsync(TenantId, It.Is<EntraAppCredentials>(c => !c.IsLegacy),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    /// <summary>What the LEGACY app's app-only token mint returns (the superset baseline).</summary>
    private void SetupLegacyToken(GraphTokenResult result) =>
        _graphTokenMock
            .Setup(x => x.GetAccessTokenForAppAsync(TenantId, It.Is<EntraAppCredentials>(c => c.IsLegacy),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

    private void VerifyNeverSaved() =>
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

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

    // ── ProbePrimaryConsentAsync (role-superset rule) ───────────────────────

    [Fact]
    public async Task Probe_refuses_when_primary_lacks_a_role_the_legacy_app_holds()
    {
        // The trap of the primary-default sign-in: a delegated User.Read consent created the
        // primary SP (token acquirable, no roles) while the legacy app carries the validation
        // role — flipping would break device validation until re-consent.
        SetupProbe(GraphTokenResult.Success(Jwt()));
        SetupLegacyToken(GraphTokenResult.Success(Jwt(ValidationRole)));

        var probe = await _sut.ProbePrimaryConsentAsync(TenantId);

        Assert.False(probe.Succeeded);
        Assert.False(probe.IsTransient);
        Assert.Equal(new[] { ValidationRole }, probe.MissingRoles);
    }

    [Fact]
    public async Task Probe_succeeds_when_primary_roles_cover_the_legacy_roles()
    {
        SetupProbe(GraphTokenResult.Success(Jwt(ValidationRole, ScriptsRole)));
        SetupLegacyToken(GraphTokenResult.Success(Jwt(ValidationRole)));

        var probe = await _sut.ProbePrimaryConsentAsync(TenantId);

        Assert.True(probe.Succeeded);
        Assert.Null(probe.MissingRoles);
    }

    [Fact]
    public async Task Probe_compares_roles_case_insensitively()
    {
        SetupProbe(GraphTokenResult.Success(Jwt(ValidationRole.ToUpperInvariant())));
        SetupLegacyToken(GraphTokenResult.Success(Jwt(ValidationRole)));

        Assert.True((await _sut.ProbePrimaryConsentAsync(TenantId)).Succeeded);
    }

    [Fact]
    public async Task Probe_succeeds_for_role_less_tenants()
    {
        // Neither app holds a Graph application role (validation never enabled): nothing to
        // lose, the flip stays free so such tenants keep migrating on their own.
        var probe = await _sut.ProbePrimaryConsentAsync(TenantId);

        Assert.True(probe.Succeeded);
    }

    [Fact]
    public async Task Probe_is_transient_when_the_legacy_token_is_transient()
    {
        SetupProbe(GraphTokenResult.Success(Jwt(ValidationRole)));
        SetupLegacyToken(GraphTokenResult.TransientFailure());

        var probe = await _sut.ProbePrimaryConsentAsync(TenantId);

        Assert.False(probe.Succeeded);
        Assert.True(probe.IsTransient);
    }

    [Fact]
    public async Task Probe_succeeds_when_the_legacy_token_is_permanently_unacquirable()
    {
        // Legacy SP gone (admin deleted the previous app early): nothing left to lose.
        SetupProbe(GraphTokenResult.Success(Jwt()));
        SetupLegacyToken(GraphTokenResult.PermanentFailure());

        Assert.True((await _sut.ProbePrimaryConsentAsync(TenantId)).Succeeded);
    }

    [Fact]
    public async Task Probe_never_mints_a_legacy_token_when_the_primary_token_fails()
    {
        SetupProbe(GraphTokenResult.PermanentFailure());

        var probe = await _sut.ProbePrimaryConsentAsync(TenantId);

        Assert.False(probe.Succeeded);
        _graphTokenMock.Verify(x => x.GetAccessTokenForAppAsync(TenantId, It.Is<EntraAppCredentials>(c => c.IsLegacy),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── TryAutoFlipToPrimaryAsync ───────────────────────────────────────────

    [Fact]
    public async Task AutoFlip_does_not_flip_when_primary_lacks_a_legacy_role()
    {
        SetupProbe(GraphTokenResult.Success(Jwt()));
        SetupLegacyToken(GraphTokenResult.Success(Jwt(ValidationRole)));

        var outcome = await _sut.TryAutoFlipToPrimaryAsync(LegacyHomedConfig(), Actor);

        Assert.Equal(AppHomingAutoFlipOutcome.ProbeFailed, outcome);
        VerifyNeverSaved();
        Assert.Empty(_savedOpsEvents);
    }

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
