using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Functions.Infrastructure;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Tests.Offboarding;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using AutopilotMonitor.Functions.Services;
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
    private readonly Mock<TelegramNotificationService> _telegramMock;
    private readonly Mock<OpsEventService> _opsEventsMock;
    private readonly Mock<GlobalNotificationService> _globalNotificationMock;
    private readonly Mock<IMetricsRepository> _metricsRepoMock;
    private readonly Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer> _autoApproveEnqueuerMock;
    private readonly FakeSignalRNotificationService _signalR;
    private readonly FakeOffboardingAuditRepository _offboardingRepo = new();
    private readonly StoredTenantConfig _store;
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
            adminRepo, new StubAdminIdentityBindingService(bound: true), cache, Mock.Of<ILogger<McpUserService>>(),
            globalAdminMock.Object, delegatedAdminMock.Object, adminConfigService.Object,
            new TenantMemberRoleResolver(_tenantAdminsMock.Object, _tenantConfigMock.Object), _tenantConfigMock.Object)
        { CallBase = false };

        _metricsRepoMock = new Mock<IMetricsRepository>();
        _autoApproveEnqueuerMock = new Mock<AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer>();

        _signalR = new FakeSignalRNotificationService();
        _opsEventsMock = new Mock<OpsEventService>(null!, null!, null!) { CallBase = false };
        _opsEventsMock.Setup(x => x.RecordTenantSignupAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

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
                _metricsRepoMock.Object, _tenantConfigMock.Object, Mock.Of<ILogger<AdminIdentityResolver>>()) { CallBase = false }.Object,
            _signalR,
            adminConfigService.Object,
            _offboardingRepo,
            opsEvents: _opsEventsMock.Object);

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
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<WorkerOptions>(o => o.Serializer =
            new Azure.Core.Serialization.JsonObjectSerializer(
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var provider = services.BuildServiceProvider();

        var claims = new List<Claim> { new("preferred_username", callerUpn) };
        if (tenantId != null) claims.Add(new Claim("tid", tenantId));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

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
            .Setup(x => x.AddTenantAdminAsync(TenantId, Upn, "System"))
            .ReturnsAsync(new TenantAdminRow());

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
            .ReturnsAsync(new TenantAdminRow());

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
