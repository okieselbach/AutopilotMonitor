using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the side-effect methods extracted from AuthFunction.GetCurrentUser().
/// Uses Moq to verify service interactions (domain persistence, auto-re-enable, auto-admin, metrics).
/// </summary>
public class AuthFunctionSideEffectTests
{
    private const string TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Upn = "user@contoso.com";
    private const string DisplayName = "Test User";
    private const string ObjectId = "oid-12345";

    private readonly Mock<TenantConfigurationService> _tenantConfigMock;
    private readonly Mock<TenantAdminsService> _tenantAdminsMock;
    private readonly Mock<TelegramNotificationService> _telegramMock;
    private readonly Mock<GlobalNotificationService> _globalNotificationMock;
    private readonly Mock<IMetricsRepository> _metricsRepoMock;
    private readonly Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer> _autoApproveEnqueuerMock;
    private readonly AuthFunction _sut;

    public AuthFunctionSideEffectTests()
    {
        // Shared interface mocks for constructor injection
        var adminRepo = Mock.Of<IAdminRepository>();
        var configRepo = Mock.Of<IConfigRepository>();
        var notificationRepo = Mock.Of<INotificationRepository>();
        var cache = Mock.Of<IMemoryCache>();

        _tenantConfigMock = new Mock<TenantConfigurationService>(
            configRepo, Mock.Of<ILogger<TenantConfigurationService>>(), cache)
        { CallBase = false };

        var bindings = new StubAdminIdentityBindingService(bound: true);
        var globalAdminMock = new Mock<GlobalAdminService>(
            adminRepo, bindings, cache, Mock.Of<ILogger<GlobalAdminService>>())
        { CallBase = false };

        var delegatedAdminMock = new Mock<DelegatedAdminService>(
            adminRepo,
            bindings,
            new StubTenantEntitlementService(AutopilotMonitor.Functions.Security.TenantEdition.Pro),
            cache, Mock.Of<ILogger<DelegatedAdminService>>())
        { CallBase = false };
        delegatedAdminMock.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>()))
            .ReturnsAsync(DelegatedScope.Empty);

        _tenantAdminsMock = new Mock<TenantAdminsService>(
            adminRepo, cache, Mock.Of<ILogger<TenantAdminsService>>())
        { CallBase = false };

        var previewMock = new Mock<PreviewWhitelistService>(
            configRepo, cache, Mock.Of<ILogger<PreviewWhitelistService>>(), _tenantConfigMock.Object)
        { CallBase = false };

        _telegramMock = new Mock<TelegramNotificationService>(
            new HttpClient(), configRepo, Mock.Of<ILogger<TelegramNotificationService>>())
        { CallBase = false };

        _globalNotificationMock = new Mock<GlobalNotificationService>(
            notificationRepo, new FakeSignalRNotificationService(), Mock.Of<ILogger<GlobalNotificationService>>())
        { CallBase = false };

        var adminConfigService = new Mock<AdminConfigurationService>(
            configRepo, Mock.Of<ILogger<AdminConfigurationService>>(), cache)
        { CallBase = false };

        var mcpUserMock = new Mock<McpUserService>(
            adminRepo, cache, Mock.Of<ILogger<McpUserService>>(),
            globalAdminMock.Object, delegatedAdminMock.Object, adminConfigService.Object)
        { CallBase = false };

        _metricsRepoMock = new Mock<IMetricsRepository>();
        _autoApproveEnqueuerMock = new Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer>();

        _sut = new AuthFunction(
            Mock.Of<ILogger<AuthFunction>>(),
            globalAdminMock.Object,
            delegatedAdminMock.Object,
            _tenantConfigMock.Object,
            _tenantAdminsMock.Object,
            _metricsRepoMock.Object,
            previewMock.Object,
            _telegramMock.Object,
            _globalNotificationMock.Object,
            mcpUserMock.Object,
            _autoApproveEnqueuerMock.Object,
            new AutopilotMonitor.Functions.Security.EntraAppRegistry(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Mock.Of<ILogger<AutopilotMonitor.Functions.Security.EntraAppRegistry>>()),
            new Mock<AdminIdentityResolver>(
                _metricsRepoMock.Object, _tenantConfigMock.Object, Mock.Of<ILogger<AdminIdentityResolver>>()) { CallBase = false }.Object);

        // Default: all fire-and-forget calls succeed
        _tenantConfigMock
            .Setup(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _telegramMock
            .Setup(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _globalNotificationMock
            .Setup(x => x.CreateNotificationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _metricsRepoMock
            .Setup(x => x.RecordUserLoginAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
    }

    private static TenantConfiguration DefaultConfig() => TenantConfiguration.CreateDefault(TenantId);

    // -------------------------------------------------------------------------
    // HandleNewTenantDomainAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleNewTenantDomain_WhenUpnDomainIsNotAHostName_DoesNotSeed()
    {
        var config = DefaultConfig();
        config.DomainName = null!;
        config.OnboardedBy = null;

        // The seed is rendered into transactional mails: anything but a strict host name is refused.
        await _sut.HandleNewTenantDomainAsync(config, TenantId, "user@<b>contoso.com</b>");

        Assert.True(string.IsNullOrEmpty(config.DomainName));
        Assert.Null(config.OnboardedBy);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenDomainEmpty_ExtractsAndSaves()
    {
        var config = DefaultConfig();
        config.DomainName = null!;
        config.OnboardedBy = null;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.Equal("contoso.com", config.DomainName);
        Assert.Equal(Upn, config.UpdatedBy);
        // OnboardedBy is the immutable copy of the first-login UPN that auto-promote on
        // preview approval reads — UpdatedBy may later be clobbered by background syncs.
        Assert.Equal(Upn, config.OnboardedBy);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(config, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(TenantId, Upn), Times.Once);
        _globalNotificationMock.Verify(x => x.CreateNotificationAsync(
            "preview_signup", "New Tenant Signup",
            It.Is<string>(m => m.Contains(TenantId) && m.Contains("contoso.com") && m.Contains(Upn)),
            $"/admin/tenants/management?tenantId={TenantId}"), Times.Once);
        // Signup enqueues the delayed auto-approve unconditionally — the worker is the
        // decision point (flag check at processing time).
        _autoApproveEnqueuerMock.Verify(x => x.EnqueueAsync(
            It.Is<AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope>(
                e => e.TenantId == TenantId && e.SignupUpn == Upn),
            AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope.ActivationDelay,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // HandleAuthClientIdTrackingAsync — the write must mutate a cache-BYPASSING
    // read, never the (possibly stale) cached view. Regression for the prod
    // incident 2026-07-31: the login right after an app-homing flip carries the
    // changed audience; blind-saving the stale cached entity reverted the flip.
    // -------------------------------------------------------------------------

    private const string LegacyAppId = "1a400946-62c1-4ab4-aa37-f730ac89704d";
    private const string PrimaryAppId = "886ab5e2-6144-442c-80cc-9b28e0667731";

    [Fact]
    public async Task HandleAuthClientIdTracking_MutatesFreshConfig_PreservingConcurrentHomingFlip()
    {
        // Cached view: pre-flip (homing null, last-seen legacy). Fresh row: flipped meanwhile.
        var cached = DefaultConfig();
        cached.DomainName = "contoso.com";
        cached.LastAuthClientId = LegacyAppId;
        cached.HomedAppClientId = null;

        var fresh = DefaultConfig();
        fresh.DomainName = "contoso.com";
        fresh.LastAuthClientId = LegacyAppId;
        fresh.HomedAppClientId = PrimaryAppId; // the concurrent flip that must survive

        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(fresh);

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, $"api://{PrimaryAppId}");

        // The FRESH entity was mutated and saved — the flip survives, the stale view is discarded.
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(fresh, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(cached, It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(PrimaryAppId, fresh.HomedAppClientId);
        Assert.Equal(PrimaryAppId, fresh.LastAuthClientId);
        Assert.NotNull(fresh.LastAuthClientIdSince);
    }

    [Fact]
    public async Task HandleAuthClientIdTracking_FreshAlreadyCurrent_DoesNotWrite()
    {
        // Another instance already recorded the new client id — the fresh re-check must
        // suppress the redundant write even though the cached view still looks outdated.
        var cached = DefaultConfig();
        cached.DomainName = "contoso.com";
        cached.LastAuthClientId = LegacyAppId;

        var fresh = DefaultConfig();
        fresh.DomainName = "contoso.com";
        fresh.LastAuthClientId = PrimaryAppId;

        _tenantConfigMock.Setup(x => x.GetConfigurationFreshAsync(TenantId)).ReturnsAsync(fresh);

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, PrimaryAppId);

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleAuthClientIdTracking_UnchangedOnCachedView_SkipsFreshRead()
    {
        // Steady state (same app as last time) must stay a zero-I/O no-op.
        var cached = DefaultConfig();
        cached.DomainName = "contoso.com";
        cached.LastAuthClientId = PrimaryAppId;

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, $"api://{PrimaryAppId}");

        _tenantConfigMock.Verify(x => x.GetConfigurationFreshAsync(It.IsAny<string>()), Times.Never);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_PreservesExistingOnboardedBy()
    {
        // Belt-and-suspenders: the method exits early when DomainName is set, but if
        // anyone ever loosens that guard, OnboardedBy must still be immutable once set.
        var config = DefaultConfig();
        config.DomainName = null!;
        config.OnboardedBy = "original.requester@contoso.com";

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.Equal("original.requester@contoso.com", config.OnboardedBy);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenDomainAlreadySet_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = "existing.com";

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenUpnEmpty_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, "");

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenUpnHasNoDomain_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, "nodomain");

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_TelegramFailure_DoesNotThrow()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        _telegramMock
            .Setup(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Telegram unreachable"));

        // Should not throw — Telegram is fire-and-forget
        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        // SaveConfig should still have been called before the fire-and-forget
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(config, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // HandleAutoReEnableAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAutoReEnable_ExpiredSuspension_ClearsDisabledAndSaves()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledReason = "Maintenance";
        config.DisabledUntil = DateTime.UtcNow.AddHours(-1); // expired

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.False(config.Disabled);
        Assert.Null(config.DisabledReason);
        Assert.Null(config.DisabledUntil);
        Assert.Equal("System (auto-re-enable)", config.UpdatedBy);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(config, It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task HandleAutoReEnable_ActiveSuspension_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledUntil = DateTime.UtcNow.AddHours(1); // still active

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.True(config.Disabled);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleAutoReEnable_NotDisabled_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = false;

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleAutoReEnable_DisabledNoExpiry_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledUntil = null; // indefinite suspension

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.True(config.Disabled);
        _tenantConfigMock.Verify(x => x.SaveConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // HandlePostDecisionSideEffectsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDecision_NeedsAutoAdmin_CallsAddTenantAdmin()
    {
        _tenantAdminsMock
            .Setup(x => x.AddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(new TenantAdminEntity());

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(x => x.AddTenantAdminAsync(TenantId, Upn, "System"), Times.Once);
    }

    [Fact]
    public async Task PostDecision_NoAutoAdmin_DoesNotCallAddTenantAdmin()
    {
        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: false);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(
            x => x.AddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PostDecision_AlwaysRecordsMetrics()
    {
        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: false);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _metricsRepoMock.Verify(
            x => x.RecordUserLoginAsync(TenantId, Upn, DisplayName, ObjectId),
            Times.Once);
    }

    [Fact]
    public async Task PostDecision_AutoAdminAndMetrics_BothExecute()
    {
        _tenantAdminsMock
            .Setup(x => x.AddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(new TenantAdminEntity());

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(x => x.AddTenantAdminAsync(TenantId, Upn, "System"), Times.Once);
        _metricsRepoMock.Verify(
            x => x.RecordUserLoginAsync(TenantId, Upn, DisplayName, ObjectId),
            Times.Once);
    }

    [Fact]
    public async Task PostDecision_MetricsFailure_DoesNotThrow()
    {
        _metricsRepoMock
            .Setup(x => x.RecordUserLoginAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("Storage unavailable"));

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: false);

        // Should not throw — metrics is fire-and-forget
        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);
    }

    [Fact]
    public async Task PostDecision_AutoAdminFailure_Throws()
    {
        _tenantAdminsMock
            .Setup(x => x.AddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Table Storage down"));

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        // Auto-admin is NOT fire-and-forget — exception must propagate
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId));
    }
}
