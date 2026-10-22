using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Tests.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly Mock<PreviewWhitelistService> _previewMock;
    private readonly Mock<McpUserService> _mcpUserMock;
    private readonly Mock<TelegramNotificationService> _telegramMock;
    private readonly Mock<OpsEventService> _opsEventsMock;
    private readonly Mock<GlobalNotificationService> _globalNotificationMock;
    private readonly Mock<IMetricsRepository> _metricsRepoMock;
    private readonly Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer> _autoApproveEnqueuerMock;
    private readonly FakeSignalRNotificationService _signalR;
    private readonly FakeOffboardingAuditRepository _offboardingRepo = new();
    private readonly StoredTenantConfig _store;
    private readonly Mock<GlobalAdminService> _globalAdminMock;
    /// <summary>Builds the function; null arguments fall back to the class mocks.</summary>
    private readonly Func<PushOwnerSignInService?, TenantAdminsService?, PreviewWhitelistService?, AuthFunction> _buildSut;
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
        _globalAdminMock = new Mock<GlobalAdminService>(
            adminRepo, bindings, cache, Mock.Of<ILogger<GlobalAdminService>>())
        { CallBase = false };
        var globalAdminMock = _globalAdminMock;

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

        _previewMock = new Mock<PreviewWhitelistService>(
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

        _mcpUserMock = new Mock<McpUserService>(
            adminRepo, new StubAdminIdentityBindingService(bound: true), cache, Mock.Of<ILogger<McpUserService>>(),
            globalAdminMock.Object, delegatedAdminMock.Object, adminConfigService.Object,
            new TenantMemberRoleResolver(_tenantAdminsMock.Object, _tenantConfigMock.Object), _tenantConfigMock.Object)
        { CallBase = false };

        _metricsRepoMock = new Mock<IMetricsRepository>();
        _autoApproveEnqueuerMock = new Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer>();

        _signalR = new FakeSignalRNotificationService();
        _opsEventsMock = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
        _opsEventsMock.Setup(x => x.RecordTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _buildSut = (pushSignIn, tenantAdmins, preview) => new AuthFunction(
            Mock.Of<ILogger<AuthFunction>>(),
            globalAdminMock.Object,
            delegatedAdminMock.Object,
            _tenantConfigMock.Object,
            tenantAdmins ?? _tenantAdminsMock.Object,
            _metricsRepoMock.Object,
            preview ?? _previewMock.Object,
            _telegramMock.Object,
            _globalNotificationMock.Object,
            _mcpUserMock.Object,
            _autoApproveEnqueuerMock.Object,
            new AutopilotMonitor.Functions.Security.EntraAppRegistry(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Mock.Of<ILogger<AutopilotMonitor.Functions.Security.EntraAppRegistry>>()),
            new Mock<AdminIdentityResolver>(
                _metricsRepoMock.Object, _tenantConfigMock.Object, Mock.Of<ILogger<AdminIdentityResolver>>()) { CallBase = false }.Object,
            _signalR,
            adminConfigService.Object,
            _offboardingRepo,
            pushSignIn: pushSignIn,
            opsEvents: _opsEventsMock.Object);
        _sut = _buildSut(null, null, null);

        // Writes go through UpdateAsync / CreateOrUpdateAsync over one stored row (none by default:
        // a brand-new tenant). Default: all fire-and-forget calls succeed.
        _store = _tenantConfigMock.StubWrites(TenantId, row: null);
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

    // ── RemoveGlobalAdmin cuts live SignalR streams (join-time-only group authz) ──

    [Fact]
    public async Task RemoveGlobalAdmin_DisconnectsTheRemovedAdminsSignalRConnections()
    {
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: "operator@contoso.com");

        var response = await _sut.RemoveGlobalAdmin(req, "Removed@Contoso.com", ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "removed@contoso.com" }, _signalR.DisconnectedUsers);
    }

    [Fact]
    public async Task RemoveGlobalAdmin_SelfRemovalIsRejectedWithoutDisconnect()
    {
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: "operator@contoso.com");

        var response = await _sut.RemoveGlobalAdmin(req, "operator@contoso.com", ctx);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_signalR.DisconnectedUsers);
    }

    private static (HttpRequestData Req, FunctionContext Ctx) BuildAuthenticatedRequest(string callerUpn, string? tenantId = null)
    {
        var claims = new List<Claim> { new("preferred_username", callerUpn) };
        if (tenantId != null) claims.Add(new Claim("tid", tenantId));
        return BuildRequest(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    private static (HttpRequestData Req, FunctionContext Ctx) BuildRequest(ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer =
            new Azure.Core.Serialization.JsonObjectSerializer(
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var provider = services.BuildServiceProvider();

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(
            new Dictionary<object, object> { ["ClaimsPrincipal"] = principal });
        context.SetupGet(c => c.InstanceServices).Returns(provider);

        var req = new Mock<HttpRequestData>(context.Object);
        req.SetupGet(r => r.Headers).Returns(new HttpHeadersCollection());
        req.SetupGet(r => r.Body).Returns(new MemoryStream());
        req.Setup(r => r.CreateResponse()).Returns(() => new FakeHttpResponseData(context.Object));
        return (req.Object, context.Object);
    }

    private sealed class FakeHttpResponseData : HttpResponseData
    {
        public FakeHttpResponseData(FunctionContext context) : base(context) { }
        public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public override HttpHeadersCollection Headers { get; set; } = new();
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
    }

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
        Assert.Empty(_store.Writes);
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
        // A first sign-in without the get-started tick leaves the Terms + DPA pending: the portal asks.
        Assert.True(config.DpaAcceptancePending);
        Assert.Null(config.DpaVersion);
        var (written, source, _) = Assert.Single(_store.Writes);
        Assert.Equal("contoso.com", written.DomainName);
        Assert.Equal(Upn, written.OnboardedBy);
        Assert.True(written.DpaAcceptancePending);
        Assert.Null(written.DpaVersion);
        Assert.Null(written.DpaAcceptedBy);
        Assert.Null(written.DpaAcceptedAt);
        Assert.Equal("auth", source);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(TenantId, Upn), Times.Once);
        _opsEventsMock.Verify(x => x.RecordTenantSignupAsync(TenantId, Upn), Times.Once);   // the routable record next to the ping
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
    // Returning tenant (D-288): the offboarding deleted the configuration, the pointer
    // carries the consumed self-service trial into the first-login write.
    // -------------------------------------------------------------------------

    private void SeedOffboardingPointer(bool trialConsumed) =>
        _offboardingRepo.Pointers[TenantId] = (new OffboardingByTenantPointer
        {
            PartitionKey = Constants.OffboardingPartitionKeys.ByTenant,
            RowKey = TenantId,
            TenantId = TenantId,
            LatestStatus = "Completed",
            OffboardCount = 1,
            TrialConsumed = trialConsumed,
        }, "\"etag\"");

    private static TenantConfiguration ReturningTenantDefault()
    {
        // What auth/me hands over once the offboarding deleted the row: an unpersisted default.
        var config = TenantConfiguration.CreateDefault(TenantId);
        Assert.False(config.TrialConsumed);
        return config;
    }

    [Fact]
    public async Task HandleNewTenantDomain_ReturningTenantThatUsedTheTrial_CarriesItIntoTheOnboardingWrite()
    {
        SeedOffboardingPointer(trialConsumed: true);
        var config = ReturningTenantDefault();

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.True(config.TrialConsumed);
        Assert.Equal(Upn, config.OnboardedBy);
        var written = Assert.Single(_store.Writes).Row;
        Assert.True(written.TrialConsumed);
        Assert.Equal(Upn, written.OnboardedBy);
        // Re-onboarding itself stays unrestricted: same signup path, auto-approve included.
        _autoApproveEnqueuerMock.Verify(x => x.EnqueueAsync(
            It.IsAny<AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Once);
        // The verify criterion of the finding: the trial gate refuses the returning tenant.
        var deny = PlanManagementFunction.EvaluateTrialStart(config, DateTime.UtcNow);
        Assert.Equal(Constants.ApiErrorCodes.TrialAlreadyConsumed, deny?.Code);
    }

    [Theory]
    [InlineData(false)] // offboarded without ever using the trial
    [InlineData(null)]  // never offboarded
    public async Task HandleNewTenantDomain_NoConsumedTrialOnRecord_LeavesTheTrialAvailable(bool? pointerTrialConsumed)
    {
        if (pointerTrialConsumed is { } consumed)
            SeedOffboardingPointer(consumed);
        var config = ReturningTenantDefault();

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.False(config.TrialConsumed);
        Assert.Equal("contoso.com", config.DomainName);
        Assert.False(Assert.Single(_store.Writes).Row.TrialConsumed);
    }

    [Fact]
    public async Task HandleNewTenantDomain_OffboardingLookupFails_WritesNothingAndRetriesOnTheNextLogin()
    {
        // An onboarded row without the carry-over would hand the trial back; no write at all instead.
        _offboardingRepo.ThrowOnPointerRead = new Azure.RequestFailedException(503, "Server busy");
        var config = ReturningTenantDefault();

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.True(string.IsNullOrEmpty(config.DomainName));
        Assert.Null(config.OnboardedBy);
        Assert.Empty(_store.Writes);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _autoApproveEnqueuerMock.Verify(x => x.EnqueueAsync(
            It.IsAny<AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_ConcurrentFirstLogin_NeverOverwritesOnboardedBy()
    {
        // This login's view still has no domain, but another login seeded the stored row meanwhile:
        // OnboardedBy is write-once (D-061), and the signup side effects belong to the winner.
        var stored = DefaultConfig();
        stored.DomainName = "contoso.com";
        stored.OnboardedBy = "first@contoso.com";
        _store.Row = stored;
        var view = DefaultConfig();

        await _sut.HandleNewTenantDomainAsync(view, TenantId, "second@contoso.com");

        Assert.Empty(_store.Writes);
        Assert.Equal("first@contoso.com", _store.Row!.OnboardedBy);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _autoApproveEnqueuerMock.Verify(x => x.EnqueueAsync(
            It.IsAny<AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_OffboardingTombstone_IsNeverSeeded()
    {
        // The offboard click writes a Disabled tombstone even onto a row without a domain.
        var tombstone = DefaultConfig();
        tombstone.Disabled = true;
        tombstone.DisabledReason = AutopilotMonitor.Functions.Functions.Admin.TenantOffboardFunction.OffboardingDisabledReason;
        _store.Row = tombstone;

        await _sut.HandleNewTenantDomainAsync(DefaultConfig(), TenantId, Upn);

        Assert.Empty(_store.Writes);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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

        _store.Row = fresh;

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, $"api://{PrimaryAppId}");

        // The FRESH row was mutated and written — the flip survives, the stale view is discarded.
        var written = Assert.Single(_store.Writes).Row;
        Assert.Equal(PrimaryAppId, written.HomedAppClientId);
        Assert.Equal(PrimaryAppId, written.LastAuthClientId);
        Assert.NotNull(written.LastAuthClientIdSince);
        Assert.Null(cached.HomedAppClientId);
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

        _store.Row = fresh;

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, PrimaryAppId);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task HandleAuthClientIdTracking_UnchangedOnCachedView_SkipsFreshRead()
    {
        // Steady state (same app as last time) must stay a zero-I/O no-op.
        var cached = DefaultConfig();
        cached.DomainName = "contoso.com";
        cached.LastAuthClientId = PrimaryAppId;

        await _sut.HandleAuthClientIdTrackingAsync(cached, TenantId, $"api://{PrimaryAppId}");

        _tenantConfigMock.Verify(x => x.UpdateAsync(It.IsAny<string>(), It.IsAny<Func<TenantConfiguration, bool>>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_store.Writes);
    }

    private const string CurrentDpa = AutopilotMonitor.Shared.Constants.CurrentDpaVersion;

    [Fact]
    public async Task HandleNewTenantDomain_ThroughTheGetStartedTick_AcceptsWithTheOnboarding()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        var before = DateTime.UtcNow;
        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn, signupConsent: true);

        var written = Assert.Single(_store.Writes).Row;
        Assert.False(written.DpaAcceptancePending);
        Assert.Equal(CurrentDpa, written.DpaVersion);
        Assert.Equal(Upn, written.DpaAcceptedBy);
        Assert.NotNull(written.DpaAcceptedAt);
        Assert.True(written.DpaAcceptedAt >= before);
        // This login's view follows the stored row, so its auth/me carries no dialog.
        Assert.False(config.DpaAcceptancePending);
        Assert.Equal(CurrentDpa, config.DpaVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleNewTenantDomain_WhenDomainAlreadySet_NeverAsksOrStamps(bool signupConsent)
    {
        // Decided once, with the onboarding: an existing tenant's login writes nothing — no stamp,
        // no pending flag, whichever button the user came through (tenants that pre-date the
        // acceptance are never asked).
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        config.DpaVersion = null;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn, signupConsent: signupConsent);

        Assert.Null(config.DpaVersion);
        Assert.False(config.DpaAcceptancePending);
        Assert.Empty(_store.Writes);
    }

    // -------------------------------------------------------------------------
    // AcceptDpa — POST auth/dpa-acceptance (portal dialog)
    // -------------------------------------------------------------------------

    private static TenantConfiguration PendingTenant()
    {
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        config.OnboardedBy = "first@contoso.com";
        config.DpaAcceptancePending = true;
        return config;
    }

    [Fact]
    public async Task AcceptDpa_PendingTenant_RecordsTheAcceptanceAndClearsTheDialog()
    {
        _store.Row = PendingTenant();
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: Upn, tenantId: TenantId);

        var before = DateTime.UtcNow;
        var response = await _sut.AcceptDpa(req, ctx);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (written, source, _) = Assert.Single(_store.Writes);
        Assert.Equal("auth", source);
        Assert.False(written.DpaAcceptancePending);
        Assert.Equal(CurrentDpa, written.DpaVersion);
        Assert.Equal(Upn, written.DpaAcceptedBy);
        Assert.True(written.DpaAcceptedAt >= before);
        Assert.Equal("first@contoso.com", written.OnboardedBy);
    }

    [Fact]
    public async Task AcceptDpa_NothingPending_IsANoOp()
    {
        // Already accepted (a double click, a second user) or a tenant that was never asked.
        var accepted = DefaultConfig();
        accepted.DomainName = "contoso.com";
        accepted.DpaVersion = "2026-01-1.0";
        accepted.DpaAcceptedBy = "first@contoso.com";
        _store.Row = accepted;
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: Upn, tenantId: TenantId);

        var response = await _sut.AcceptDpa(req, ctx);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(_store.Writes);
        Assert.Equal("2026-01-1.0", _store.Row!.DpaVersion);
        Assert.Equal("first@contoso.com", _store.Row.DpaAcceptedBy);
    }

    [Fact]
    public async Task AcceptDpa_OnlyEverWritesTheCallersOwnTenant()
    {
        // The row is keyed by the token's tid: a caller from another tenant reaches no pending row.
        const string otherTenant = "ffffffff-0000-1111-2222-333333333333";
        var otherStore = _tenantConfigMock.StubWrites(otherTenant, row: null);
        _store.Row = PendingTenant();
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: "user@fabrikam.com", tenantId: otherTenant);

        var response = await _sut.AcceptDpa(req, ctx);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(otherStore.Writes);
        Assert.Empty(_store.Writes);
        Assert.True(_store.Row!.DpaAcceptancePending);
    }

    [Fact]
    public async Task AcceptDpa_OffboardingTombstone_IsRefused()
    {
        var tombstone = PendingTenant();
        tombstone.Disabled = true;
        tombstone.DisabledReason = AutopilotMonitor.Functions.Functions.Admin.TenantOffboardFunction.OffboardingDisabledReason;
        _store.Row = tombstone;
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: Upn, tenantId: TenantId);

        var response = await _sut.AcceptDpa(req, ctx);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task AcceptDpa_LostWriteRace_AsksToRetry()
    {
        _store.Row = PendingTenant();
        _store.Force = TenantConfigUpdateStatus.Conflict;
        var (req, ctx) = BuildAuthenticatedRequest(callerUpn: Upn, tenantId: TenantId);

        var response = await _sut.AcceptDpa(req, ctx);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task HandleNewTenantDomain_PreservesExistingOnboardedBy()
    {
        // Belt-and-suspenders: the method exits early when DomainName is set, but if
        // anyone ever loosens that guard, OnboardedBy must still be immutable once set.
        var config = DefaultConfig();
        config.DomainName = null!;
        config.OnboardedBy = "original.requester@contoso.com";
        _store.Row = ConfigRepoMockExtensions.Clone(config);

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.Equal("original.requester@contoso.com", config.OnboardedBy);
        Assert.Equal("original.requester@contoso.com", Assert.Single(_store.Writes).Row.OnboardedBy);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenDomainAlreadySet_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = "existing.com";

        await _sut.HandleNewTenantDomainAsync(config, TenantId, Upn);

        Assert.Empty(_store.Writes);
        _telegramMock.Verify(x => x.SendNewTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenUpnEmpty_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, "");

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task HandleNewTenantDomain_WhenUpnHasNoDomain_NoOp()
    {
        var config = DefaultConfig();
        config.DomainName = null!;

        await _sut.HandleNewTenantDomainAsync(config, TenantId, "nodomain");

        Assert.Empty(_store.Writes);
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

        // The write still landed before the fire-and-forget
        Assert.Single(_store.Writes);
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
        _store.Row = ConfigRepoMockExtensions.Clone(config);

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.False(config.Disabled);
        Assert.Null(config.DisabledReason);
        Assert.Null(config.DisabledUntil);
        var written = Assert.Single(_store.Writes).Row;
        Assert.False(written.Disabled);
        Assert.Null(written.DisabledUntil);
        Assert.Equal("System (auto-re-enable)", written.UpdatedBy);
    }

    [Fact]
    public async Task HandleAutoReEnable_SuspensionRenewedMeanwhile_StaysSuspended()
    {
        // This instance cached the expired suspension; a Global Admin renewed it since.
        var view = DefaultConfig();
        view.Disabled = true;
        view.DisabledReason = "Maintenance";
        view.DisabledUntil = DateTime.UtcNow.AddHours(-1);
        var stored = ConfigRepoMockExtensions.Clone(view);
        stored.DisabledReason = "Abuse review";
        stored.DisabledUntil = DateTime.UtcNow.AddDays(2);
        _store.Row = stored;

        await _sut.HandleAutoReEnableAsync(view, TenantId);

        Assert.Empty(_store.Writes);
        Assert.True(view.IsCurrentlyDisabled());
        Assert.Equal("Abuse review", view.DisabledReason);
    }

    [Fact]
    public async Task HandleAutoReEnable_RowIsTheOffboardingTombstone_StaysSuspended()
    {
        var view = DefaultConfig();
        view.Disabled = true;
        view.DisabledUntil = DateTime.UtcNow.AddHours(-1);
        var tombstone = ConfigRepoMockExtensions.Clone(view);
        tombstone.DisabledReason = AutopilotMonitor.Functions.Functions.Admin.TenantOffboardFunction.OffboardingDisabledReason;
        tombstone.DisabledUntil = null;
        _store.Row = tombstone;

        await _sut.HandleAutoReEnableAsync(view, TenantId);

        Assert.Empty(_store.Writes);
        Assert.True(view.IsCurrentlyDisabled());
    }

    [Fact]
    public async Task HandleAutoReEnable_StorageFails_ThisLoginStillPasses()
    {
        var view = DefaultConfig();
        view.Disabled = true;
        view.DisabledUntil = DateTime.UtcNow.AddHours(-1);
        _store.ThrowOnRead = new Azure.RequestFailedException(503, "Server busy");

        await _sut.HandleAutoReEnableAsync(view, TenantId);

        Assert.False(view.Disabled);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task HandleAutoReEnable_ActiveSuspension_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledUntil = DateTime.UtcNow.AddHours(1); // still active

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.True(config.Disabled);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task HandleAutoReEnable_NotDisabled_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = false;

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task HandleAutoReEnable_DisabledNoExpiry_NoOp()
    {
        var config = DefaultConfig();
        config.Disabled = true;
        config.DisabledUntil = null; // indefinite suspension

        await _sut.HandleAutoReEnableAsync(config, TenantId);

        Assert.True(config.Disabled);
        Assert.Empty(_store.Writes);
    }

    // -------------------------------------------------------------------------
    // HandlePostDecisionSideEffectsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PostDecision_NeedsAutoAdmin_CallsAddTenantAdmin()
    {
        _tenantAdminsMock
            .Setup(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(true);

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"), Times.Once);
    }

    [Fact]
    public async Task PostDecision_NoAutoAdmin_DoesNotCallAddTenantAdmin()
    {
        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: false);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(
            x => x.TryAddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
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
            .Setup(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(true);

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"), Times.Once);
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
            .Setup(x => x.TryAddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Table Storage down"));

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        // Auto-admin is NOT fire-and-forget — exception must propagate
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId));
    }

    [Fact]
    public async Task PostDecision_AutoAdminSeatAlreadyTaken_DoesNotThrow()
    {
        // The activation seated the same person a moment earlier (conditional insert lost): nothing to overwrite.
        _tenantAdminsMock
            .Setup(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(false);

        var decision = AuthDecisionResult.Success(new { }, needsAutoAdmin: true);

        await _sut.HandlePostDecisionSideEffectsAsync(decision, TenantId, Upn, DisplayName, ObjectId);

        _tenantAdminsMock.Verify(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"), Times.Once);
    }

    // -------------------------------------------------------------------------
    // GetCurrentUser end to end: an external identity never claims the tenant
    // -------------------------------------------------------------------------

    /// <summary>
    /// A validated token as the middleware hands it on: tid, oid and idp only under their mapped names. The
    /// member's idp names its own tenant, the guest's another one — the same principal shape otherwise.
    /// </summary>
    private static ClaimsPrincipal SignedIn(bool external, params string[] appRoles) => new(new ClaimsIdentity(new[]
    {
        new Claim("http://schemas.microsoft.com/identity/claims/tenantid", TenantId),
        new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", ObjectId),
        new Claim(ClaimTypes.Upn, Upn),
        new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", external
            ? "https://sts.windows.net/bbbbbbbb-1111-2222-3333-444444444444/"
            : $"https://sts.windows.net/{TenantId}/"),
    }.Concat(appRoles.Select(role => new Claim("roles", role))), "test"));

    private void ArrangeAuthMe(TenantConfiguration config, bool exists, bool approved)
    {
        _tenantConfigMock.Setup(x => x.TryGetConfigurationAsync(TenantId)).ReturnsAsync((config, exists));
        _previewMock.Setup(x => x.IsApprovedAsync(TenantId)).ReturnsAsync(approved);
        _tenantAdminsMock.Setup(x => x.GetMembershipSnapshotAsync(TenantId, Upn))
            .ReturnsAsync((TableMemberState.NotPresent, null, false));
        _mcpUserMock
            .Setup(x => x.IsAllowedAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>()))
            .ReturnsAsync(McpAccessCheckResult.Denied("not allowed"));
        _metricsRepoMock.Setup(x => x.GetUserWhatsNewSeenAsync(TenantId, Upn)).ReturnsAsync(new UserWhatsNewSeen());
    }

    private static async Task<string> ReadBodyAsync(HttpResponseData response)
    {
        response.Body.Position = 0;
        return await new StreamReader(response.Body).ReadToEndAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCurrentUser_FirstSignInOfANewTenant_OnlyAMemberOnboardsIt(bool external)
    {
        // The pointer read stays healthy on purpose: a throwing read would skip the seed for the guest too, and
        // the test would pass for the wrong reason.
        ArrangeAuthMe(DefaultConfig(), exists: false, approved: false);
        var (req, ctx) = BuildRequest(SignedIn(external));

        var response = await _sut.GetCurrentUser(req, ctx);

        // Both wait at the activation gate; only the member's sign-in seeded the tenant and queued its activation.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("PendingActivation", await ReadBodyAsync(response));
        Assert.Equal(external ? 0 : 1, _store.Writes.Count);
        _autoApproveEnqueuerMock.Verify(x => x.EnqueueAsync(
            It.IsAny<AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveEnvelope>(),
            It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), external ? Times.Never() : Times.Once());
        _opsEventsMock.Verify(x => x.RecordTenantSignupAsync(TenantId, Upn), external ? Times.Never() : Times.Once());
        if (!external)
            Assert.Equal(Upn, _store.Row!.OnboardedBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetCurrentUser_ActivatedTenantWithoutAdmins_OnlyAMemberBecomesItsFirstAdmin(bool external)
    {
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        ArrangeAuthMe(config, exists: true, approved: true);
        _tenantAdminsMock
            .Setup(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(true);
        var (req, ctx) = BuildRequest(SignedIn(external));

        var response = await _sut.GetCurrentUser(req, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _tenantAdminsMock.Verify(
            x => x.TryAddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            external ? Times.Never() : Times.Once());
        Assert.Contains(external ? "\"isTenantAdmin\":false" : "\"isTenantAdmin\":true", await ReadBodyAsync(response));
    }

    /// <summary>
    /// The activation race: the tenant is activated and its partition is empty for a moment while an
    /// ordinary member polls auth/me. The first-user seat belongs to the designated onboarding member
    /// alone; everybody else is answered roleless and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("first.member@contoso.com", false)]
    [InlineData("USER@CONTOSO.COM", true)]
    public async Task GetCurrentUser_ActivatedTenantWithoutAdmins_OnlyTheDesignatedOnboardingMemberTakesTheSeat(
        string onboardedBy, bool callerIsTheOnboardingMember)
    {
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        config.OnboardedBy = onboardedBy;
        ArrangeAuthMe(config, exists: true, approved: true);
        _tenantAdminsMock.Setup(x => x.TryAddTenantAdminAsync(TenantId, Upn, "System")).ReturnsAsync(true);
        var (req, ctx) = BuildRequest(SignedIn(external: false));

        var response = await _sut.GetCurrentUser(req, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(callerIsTheOnboardingMember ? "\"isTenantAdmin\":true" : "\"isTenantAdmin\":false", await ReadBodyAsync(response));
        _tenantAdminsMock.Verify(
            x => x.TryAddTenantAdminAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            callerIsTheOnboardingMember ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// The sequence behind new tenants' first admins landing on the progress page, over the real role caches.
    /// While the tenant waits for activation, this instance resolves the member's role (the feedback-status
    /// read after every activation poll) and finds no row. The auto-approve worker then activates the tenant
    /// and promotes the member on another instance, whose cache invalidation never reaches this one. The next
    /// poll must answer the member as Admin: not as still pending (a cached "not activated") and not as
    /// roleless (a cached "no row" next to a fresh member list).
    /// </summary>
    [Fact]
    public async Task GetCurrentUser_FirstPollAfterActivation_AnswersThePromotedMemberAsAdmin()
    {
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        config.OnboardedBy = Upn;
        ArrangeAuthMe(config, exists: true, approved: false);

        var activated = false;
        var rows = new List<TenantMember>();
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.IsInPreviewWhitelistAsync(TenantId)).ReturnsAsync(() => activated);
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(r => r.GetTenantMembersAsync(TenantId)).ReturnsAsync(() => rows.ToList());
        adminRepo.Setup(r => r.GetTenantMemberAsync(TenantId, Upn)).ReturnsAsync(() => rows.FirstOrDefault());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantAdmins = new TenantAdminsService(adminRepo.Object, cache, Mock.Of<ILogger<TenantAdminsService>>());
        var sut = _buildSut(null, tenantAdmins, new PreviewWhitelistService(
            configRepo.Object, cache, Mock.Of<ILogger<PreviewWhitelistService>>(), _tenantConfigMock.Object));

        var (pollReq, pollCtx) = BuildRequest(SignedIn(external: false));
        Assert.Equal(HttpStatusCode.Forbidden, (await sut.GetCurrentUser(pollReq, pollCtx)).StatusCode);
        Assert.Equal(TableMemberState.NotPresent, (await tenantAdmins.GetTableMembershipAsync(TenantId, Upn)).State);

        activated = true;
        rows.Add(new TenantMember { TenantId = TenantId, Upn = Upn, Role = Constants.TenantRoles.Admin, IsEnabled = true });

        var (req, ctx) = BuildRequest(SignedIn(external: false));
        var response = await sut.GetCurrentUser(req, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("\"isTenantAdmin\":true", body);
        Assert.Contains("\"role\":\"Admin\"", body);
        // The promotion already wrote the row: no first-user auto-admin on top of it.
        adminRepo.Verify(r => r.AddTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        adminRepo.Verify(r => r.TryAddTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // GetCurrentUser: the push sign-in (stamp with the app-role claim, resume)
    // -------------------------------------------------------------------------

    /// <summary>An onboarded, activated tenant with app roles on and an admin already present (no auto-admin).</summary>
    private void ArrangeActivatedTenantWithAppRoles(TableMemberState memberRow)
    {
        var config = DefaultConfig();
        config.DomainName = "contoso.com";
        config.EntraAppRolesEnabled = true;
        ArrangeAuthMe(config, exists: true, approved: true);
        _tenantAdminsMock.Setup(x => x.GetMembershipSnapshotAsync(TenantId, Upn)).ReturnsAsync((memberRow,
            memberRow == TableMemberState.Enabled ? new MemberRoleInfo { Role = Constants.TenantRoles.Operator } : null,
            true));
    }

    private (AuthFunction Sut, Push.PushDeliveryServiceTests.Harness Push) BuildWithPushSignIn()
    {
        var push = new Push.PushDeliveryServiceTests.Harness();
        var signIn = new PushOwnerSignInService(push.Repo, new Mock<IAdminRepository>().Object, push.Settings, push.Sut,
            Mock.Of<ILogger<PushOwnerSignInService>>());
        return (_buildSut(signIn, null, null), push);
    }

    [Theory]
    [InlineData(TableMemberState.NotPresent, Constants.TenantRoles.Admin)]
    [InlineData(TableMemberState.Enabled, null)]
    [InlineData(TableMemberState.Disabled, null)]
    public async Task GetCurrentUser_PushStamp_CarriesTheAppRoleClaimOnlyWithoutAMemberRow(TableMemberState memberRow, string? expected)
    {
        ArrangeActivatedTenantWithAppRoles(memberRow);
        var (sut, push) = BuildWithPushSignIn();
        var (req, ctx) = BuildRequest(SignedIn(external: false, Constants.TenantRoles.Admin));

        var response = await sut.GetCurrentUser(req, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var owner = push.Repo.Owners[(TenantId, ObjectId)];
        Assert.Equal(expected, owner.ClaimRole);
        Assert.Equal(Upn, owner.Upn);
    }

    [Fact]
    public async Task GetCurrentUser_GlobalAdminBackFromInactivity_ResumesTheirPlatformPause()
    {
        ArrangeActivatedTenantWithAppRoles(TableMemberState.Enabled);
        _globalAdminMock.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);
        var (sut, push) = BuildWithPushSignIn();
        push.Repo.Owners[(TenantId, ObjectId)] = new PushOwner
        {
            HomeTenantId = TenantId, ObjectId = ObjectId, Upn = Upn, LastSignInUtc = DateTime.UtcNow.AddDays(-45),
        };
        var device = push.AddDevice("ga-phone", Constants.Push.DeviceStatus.Paused, upn: Upn,
            scope: Constants.Push.PlatformScope, statusReason: "owner_inactive");
        device.OwnerObjectId = ObjectId;
        device.OwnerHomeTenantId = TenantId;
        var (req, ctx) = BuildRequest(SignedIn(external: false));

        var response = await sut.GetCurrentUser(req, ctx);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Constants.Push.DeviceStatus.Active, push.Repo.Devices[(Constants.Push.PlatformScope, "ga-phone")].Status);
    }
}
