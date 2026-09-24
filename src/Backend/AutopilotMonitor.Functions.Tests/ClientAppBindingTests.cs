using System.Security.Claims;
using System.Text;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Client-app binding of delegated tokens: a user token obtained by an application outside the platform's
/// own registrations is measured always and, while <c>EnforceClientAppBinding</c> is on, admitted only when
/// that application is an enabled member of the caller's tenant — the person then acts under the same caps
/// as an application principal (Viewer, no platform role, DelegatedReader). The switch off must leave every
/// request exactly as it was: only the request-row items appear.
/// </summary>
public class ClientAppBindingTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string OwnClientId = "aaaaaaaa-0000-0000-0000-00000000000a";
    private const string LegacyClientId = "bbbbbbbb-0000-0000-0000-00000000000b";
    private const string ForeignClientId = "cccccccc-0000-0000-0000-00000000000c";
    private const string UserUpn = "alice@contoso.example";
    private const string UserOid = "dddddddd-0000-0000-0000-00000000000d";
    private static readonly string ForeignAppKey = Constants.PrincipalKeys.ForApplication(ForeignClientId);
    private static readonly string[] Trusted = { OwnClientId, LegacyClientId };

    private static ClaimsPrincipal UserToken(string? clientId = ForeignClientId, string clientClaim = "azp",
        string upn = UserUpn, string tenantId = TenantA)
    {
        var claims = new List<Claim>
        {
            new("tid", tenantId), new("upn", upn), new("oid", UserOid), new("scp", "access_as_user"),
        };
        if (clientId != null) claims.Add(new Claim(clientClaim, clientId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "AuthenticationTypes.Federation"));
    }

    private static ClaimsPrincipal AppToken(string clientId = ForeignClientId)
        => new(new ClaimsIdentity(new[]
        {
            new Claim("tid", TenantA), new Claim("idtyp", "app"), new Claim("appid", clientId),
            new Claim("oid", "eeeeeeee-0000-0000-0000-00000000000e"),
            new Claim("roles", Constants.ApplicationPermissions.AccessAsApplication),
        }, "AuthenticationTypes.Federation"));

    // ── Classification ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OwnClientId, "azp")]
    [InlineData(LegacyClientId, "azp")]
    [InlineData(OwnClientId, "appid")]
    [InlineData("AAAAAAAA-0000-0000-0000-00000000000A", "azp")]
    public void OwnRegistrations_AreNotForeign(string clientId, string claim)
        => Assert.False(ClientAppBinding.IsForeignDelegatedToken(UserToken(clientId, claim), Trusted, out _));

    [Theory]
    [InlineData("azp")]
    [InlineData("appid")]
    public void AnotherClient_IsForeign_AndNamed(string claim)
    {
        Assert.True(ClientAppBinding.IsForeignDelegatedToken(UserToken(ForeignClientId, claim), Trusted, out var id));
        Assert.Equal(ForeignClientId, id);
    }

    [Fact]
    public void ATokenNamingNoClient_IsForeign_Unidentified()
    {
        Assert.True(ClientAppBinding.IsForeignDelegatedToken(UserToken(clientId: null), Trusted, out var id));
        Assert.Null(id);
    }

    [Fact]
    public void AppOnlyTokens_AreNeverForeign_TheyHaveTheirOwnGate()
        => Assert.False(ClientAppBinding.IsForeignDelegatedToken(AppToken(), Trusted, out _));

    [Fact]
    public void TheTrustedSet_IsTheAudienceTrustSet_PrimaryLegacyAndAdditional()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = OwnClientId,
            ["EntraId:LegacyClientId"] = LegacyClientId,
            ["EntraId:AdditionalClientIds"] = ForeignClientId.ToUpperInvariant() + ", not-a-guid",
        }).Build();

        var ids = AuthenticationMiddleware.ResolveTrustedClientIds(config, out _, out var rejected);

        Assert.Equal(new[] { OwnClientId, LegacyClientId, ForeignClientId }, ids);
        Assert.Equal(new[] { "not-a-guid" }, rejected);
    }

    [Fact]
    public void ClientOnlyTrust_WidensTheClientSet_ButNotTheAudiences()
    {
        const string DevClientId = "ffffffff-0000-0000-0000-00000000000f";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = OwnClientId,
            ["EntraId:TrustedClientAppIds"] = DevClientId,
        }).Build();

        var clients = ClientAppBindingMiddleware.ResolveTrustedClientApps(config);
        var audiences = AuthenticationMiddleware.ResolveTrustedClientIds(config, out _, out _);

        Assert.Contains(DevClientId, clients);
        Assert.Contains(OwnClientId, clients);
        Assert.DoesNotContain(DevClientId, audiences);
        Assert.False(ClientAppBinding.IsForeignDelegatedToken(UserToken(DevClientId), clients, out _));
    }

    // ── The cap marker ──────────────────────────────────────────────────────────

    [Fact]
    public void AMarkedPrincipal_KeepsItsIdentity_ButIsCapped()
    {
        var principal = UserToken();
        ClientAppBinding.MarkCapped(principal, ForeignClientId);

        var identity = AdminIdentity.FromPrincipal(principal)!;

        Assert.True(principal.IsClientCapped());
        Assert.True(principal.IsCappedPrincipal());
        Assert.Equal(UserUpn, principal.GetUserPrincipalName());
        Assert.Equal(UserUpn, identity.Upn);
        Assert.False(identity.IsApplication);
        Assert.True(identity.IsCapped);
    }

    [Fact]
    public void ATokenCarryingTheMarkerClaim_IsNotCapped()
    {
        // The marker is an identity with the middleware's own authentication type — a token-derived identity
        // can carry the same claim name but never that authentication type.
        var principal = UserToken();
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClientAppBinding.ClientAppClaimType, ForeignClientId));

        Assert.False(principal.IsClientCapped());
        Assert.False(AdminIdentity.FromPrincipal(principal)!.IsCapped);
    }

    // ── Middleware: measure always, enforce behind the switch ─────────────────────

    private sealed class Run
    {
        public required bool NextCalled { get; init; }
        public required IDictionary<object, object> Items { get; init; }
        public required HttpContext Http { get; init; }
        public required ClaimsPrincipal? Principal { get; init; }

        public string Body()
        {
            Http.Response.Body.Position = 0;
            return new StreamReader(Http.Response.Body, Encoding.UTF8).ReadToEnd();
        }
    }

    private static async Task<Run> InvokeAsync(ClaimsPrincipal? principal, bool enforced, TenantMember? appRow = null)
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        if (appRow != null)
            repo.Setup(r => r.GetTenantMemberAsync(TenantA, ForeignAppKey)).ReturnsAsync(appRow);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var adminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync()).ReturnsAsync(new AdminConfiguration { EnforceClientAppBinding = enforced });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = OwnClientId,
            ["EntraId:LegacyClientId"] = LegacyClientId,
        }).Build();
        var sut = new ClientAppBindingMiddleware(
            NullLogger<ClientAppBindingMiddleware>.Instance, config,
            new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance), adminConfig.Object);

        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var items = new Dictionary<object, object> { ["HttpRequestContext"] = http, ["CorrelationId"] = "cid-1" };
        if (principal != null) items["ClaimsPrincipal"] = principal;
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);

        var nextCalled = false;
        await sut.Invoke(context.Object, _ => { nextCalled = true; return Task.CompletedTask; });
        return new Run { NextCalled = nextCalled, Items = items, Http = http, Principal = principal };
    }

    private static TenantMember AppRow(bool enabled = true)
        => new() { TenantId = TenantA, Upn = ForeignAppKey, Role = Constants.TenantRoles.Viewer, IsEnabled = enabled };

    [Fact]
    public async Task NoValidatedPrincipal_PassesThrough_Unmeasured()
    {
        var run = await InvokeAsync(principal: null, enforced: true);

        Assert.True(run.NextCalled);
        Assert.False(run.Items.ContainsKey(ClientAppBinding.OutcomeItemKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnClientTokens_PassUntouched_InBothModes(bool enforced)
    {
        var run = await InvokeAsync(UserToken(OwnClientId), enforced);

        Assert.True(run.NextCalled);
        Assert.False(run.Items.ContainsKey(ClientAppBinding.OutcomeItemKey));
        Assert.False(run.Principal!.IsClientCapped());
    }

    [Fact]
    public async Task SwitchOff_MeasuresAnUnregisteredClient_AndLetsItThrough()
    {
        var run = await InvokeAsync(UserToken(), enforced: false);

        Assert.True(run.NextCalled);
        Assert.Equal(ClientAppBinding.Outcomes.Unregistered, run.Items[ClientAppBinding.OutcomeItemKey]);
        Assert.Equal(ForeignClientId, run.Items[ClientAppBinding.ClientAppIdItemKey]);
        Assert.False(run.Principal!.IsClientCapped());
    }

    [Fact]
    public async Task SwitchOff_MeasuresARegisteredClient_WithoutCapping()
    {
        var run = await InvokeAsync(UserToken(), enforced: false, appRow: AppRow());

        Assert.True(run.NextCalled);
        Assert.Equal(ClientAppBinding.Outcomes.Registered, run.Items[ClientAppBinding.OutcomeItemKey]);
        Assert.False(run.Principal!.IsClientCapped());
    }

    [Fact]
    public async Task SwitchOn_RefusesAnUnregisteredClient_With403NamingTheApp()
    {
        var run = await InvokeAsync(UserToken(), enforced: true);

        Assert.False(run.NextCalled);
        Assert.Equal(403, run.Http.Response.StatusCode);
        var body = run.Body();
        Assert.Contains(Constants.ApiErrorCodes.ClientAppNotRegistered, body);
        Assert.Contains(ForeignClientId, body);
        Assert.Equal(ClientAppBinding.Outcomes.Unregistered, run.Items[ClientAppBinding.OutcomeItemKey]);
    }

    [Fact]
    public async Task SwitchOn_RefusesADisabledAppRow()
    {
        var run = await InvokeAsync(UserToken(), enforced: true, appRow: AppRow(enabled: false));

        Assert.False(run.NextCalled);
        Assert.Equal(403, run.Http.Response.StatusCode);
    }

    [Fact]
    public async Task SwitchOn_RefusesATokenNamingNoClient()
    {
        var run = await InvokeAsync(UserToken(clientId: null), enforced: true);

        Assert.False(run.NextCalled);
        Assert.Equal(403, run.Http.Response.StatusCode);
        Assert.Equal(ClientAppBinding.Outcomes.Unidentified, run.Items[ClientAppBinding.OutcomeItemKey]);
    }

    [Fact]
    public async Task SwitchOn_AdmitsARegisteredClient_AndCapsThePerson()
    {
        var run = await InvokeAsync(UserToken(), enforced: true, appRow: AppRow());

        Assert.True(run.NextCalled);
        Assert.True(run.Principal!.IsClientCapped());
        Assert.Equal(UserUpn, run.Principal!.GetUserPrincipalName());
    }

    [Fact]
    public async Task TheAppRowIsLookedUp_InTheCallersOwnTenant()
    {
        // A registration in another tenant admits nothing: the row must live in the token's tid.
        var run = await InvokeAsync(UserToken(tenantId: TenantB), enforced: true, appRow: AppRow());

        Assert.False(run.NextCalled);
        Assert.Equal(403, run.Http.Response.StatusCode);
    }

    // ── Caps through the real policy middleware ─────────────────────────────────

    private sealed class Harness
    {
        public required PolicyEnforcementMiddleware Middleware { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }

        public void UserRow(string role) =>
            Repo.Setup(r => r.GetTenantMemberAsync(TenantA, UserUpn))
                .ReturnsAsync(new TenantMember { TenantId = TenantA, Upn = UserUpn, Role = role, IsEnabled = true, CanManageBootstrapTokens = true });
    }

    private static Harness BuildHarness()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>()))
            .ReturnsAsync(new AdminIdentityBinding { TenantId = TenantA, ObjectId = UserOid });
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync((TenantConfiguration?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var bindings = new AdminIdentityBindingService(repo.Object, cache, NullLogger<AdminIdentityBindingService>.Instance);
        var globalAdmin = new GlobalAdminService(repo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance);
        var delegatedAdmin = new DelegatedAdminService(
            repo.Object, bindings, new StubTenantEntitlementService(TenantEdition.Pro), cache, NullLogger<DelegatedAdminService>.Instance);
        var tenantAdmins = new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance);
        var tenantConfig = new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache);
        var mw = new PolicyEnforcementMiddleware(
            NullLogger<PolicyEnforcementMiddleware>.Instance, globalAdmin, delegatedAdmin,
            new TenantMemberRoleResolver(tenantAdmins, tenantConfig), tenantConfig, new RecordingDenialReporter());
        return new Harness { Middleware = mw, Repo = repo };
    }

    private static ClaimsPrincipal CappedUser()
    {
        var principal = UserToken();
        ClientAppBinding.MarkCapped(principal, ForeignClientId);
        return principal;
    }

    [Fact]
    public async Task Control_TheSamePersonUncapped_KeepsTheirAdminRole()
    {
        var h = BuildHarness();
        h.UserRow(Constants.TenantRoles.Admin);

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, UserToken(OwnClientId));

        Assert.True(r.Allowed);
        Assert.Equal(Constants.TenantRoles.Admin, r.Context!.UserRole);
        Assert.False(r.Context.ClientCapped);
    }

    [Fact]
    public async Task ACappedAdmin_ReadsAsViewer()
    {
        var h = BuildHarness();
        h.UserRow(Constants.TenantRoles.Admin);

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, CappedUser());

        Assert.True(r.Allowed);
        Assert.Equal(Constants.TenantRoles.Viewer, r.Context!.UserRole);
        Assert.False(r.Context.IsTenantAdmin);
        Assert.True(r.Context.ClientCapped);
        Assert.True(AdminIdentity.FromRequestContext(r.Context)!.IsCapped);
    }

    [Theory]
    [InlineData("POST", "/api/sessions/abc/actions")]
    [InlineData("PUT", "/api/sessions/abc/annotations/operator")]
    [InlineData("POST", "/api/config/11111111-1111-1111-1111-111111111111")]
    public async Task ACappedAdmin_CannotWrite(string method, string path)
    {
        var h = BuildHarness();
        h.UserRow(Constants.TenantRoles.Admin);

        var r = await h.Middleware.DecideAsync(method, path, null, CappedUser());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
    }

    [Fact]
    public async Task RolelessTiers_AreClosed_ButTheMcpFrontDoorIsOpen()
    {
        var h = BuildHarness();
        h.UserRow(Constants.TenantRoles.Viewer);

        var me = await h.Middleware.DecideAsync("GET", "/api/auth/me", null, CappedUser());
        var mcp = await h.Middleware.DecideAsync("GET", "/api/auth/mcp", null, CappedUser());

        Assert.False(me.Allowed);
        Assert.Equal("ApplicationPrincipalNotAllowed", me.LogReason);
        Assert.True(mcp.Allowed);
        Assert.Equal(UserUpn, mcp.Context!.UserPrincipalName);
    }

    [Fact]
    public async Task APlatformRole_IsInert_WhileCapped()
    {
        var h = BuildHarness();
        h.Repo.Setup(r => r.GetGlobalRoleAsync(UserUpn)).ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);

        var uncapped = await h.Middleware.DecideAsync("GET", "/api/global/raw/access-probe", null, UserToken(OwnClientId));
        var capped = await h.Middleware.DecideAsync("GET", "/api/global/raw/access-probe", null, CappedUser());

        Assert.True(uncapped.Allowed);
        Assert.False(capped.Allowed);
        Assert.Equal(403, capped.StatusCode);
    }

    [Fact]
    public async Task ADelegatedAdminGrant_IsReaderOnly_WhileCapped()
    {
        var h = BuildHarness();
        h.Repo.Setup(r => r.GetDelegatedTenantsAsync(UserUpn)).ReturnsAsync(new List<DelegatedAdminEntry>
        {
            new()
            {
                TenantId = TenantB, Role = Constants.DelegatedRoles.DelegatedAdmin, IsEnabled = true,
                Status = Constants.DelegatedStatus.Active, Source = Constants.DelegatedSource.OperatorGranted,
            },
        });

        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, CappedUser());

        Assert.True(r.Allowed);
        Assert.True(r.Context!.IsDelegatedReader);
        Assert.False(r.Context.IsDelegatedAdmin);
    }

    // ── Resolver overload and the MCP front door ────────────────────────────────

    [Theory]
    [InlineData(false, Constants.TenantRoles.Admin, true)]
    [InlineData(true, Constants.TenantRoles.Viewer, false)]
    public async Task Resolver_CapsOnlyWhenAsked(bool capped, string expectedRole, bool expectedBootstrap)
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetTenantMemberAsync(TenantA, UserUpn))
            .ReturnsAsync(new TenantMember { TenantId = TenantA, Upn = UserUpn, Role = Constants.TenantRoles.Admin, IsEnabled = true, CanManageBootstrapTokens = true });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new TenantMemberRoleResolver(
            new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance),
            new TenantConfigurationService(Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache));

        var role = await sut.ResolveAsync(TenantA, UserUpn, null, capped);

        Assert.Equal(expectedRole, role?.Role);
        Assert.Equal(expectedBootstrap, role!.CanManageBootstrapTokens);
    }

    [Fact]
    public async Task Resolver_NeverTurnsANonMemberIntoAViewer()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new TenantMemberRoleResolver(
            new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance),
            new TenantConfigurationService(Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache));

        Assert.Null(await sut.ResolveAsync(TenantA, UserUpn, null, clientCapped: true));
    }

    [Fact]
    public async Task McpAccessCheck_ResolvesTheCappedIdentity()
    {
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(x => x.GetMcpUserAsync(It.IsAny<string>())).ReturnsAsync((McpUserEntry?)null);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var bindings = new StubAdminIdentityBindingService(bound: true);
        var seen = new List<AdminIdentity?>();
        var globalAdmin = new Mock<GlobalAdminService>(adminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>()))
            .Callback<AdminIdentity?>(seen.Add).ReturnsAsync((string?)null);
        var delegatedAdmin = new Mock<DelegatedAdminService>(
            adminRepo.Object, bindings, new StubTenantEntitlementService(TenantEdition.Pro), cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>()))
            .Callback<AdminIdentity?>(seen.Add).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.AllMembers.ToString() });
        var members = StubTenantMemberRoleResolver.Everyone();
        var sut = new McpUserService(
            adminRepo.Object, bindings, cache, NullLogger<McpUserService>.Instance,
            globalAdmin.Object, delegatedAdmin.Object, adminConfig.Object, members,
            new TenantConfigurationService(Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, cache));

        var result = await sut.IsAllowedAsync(UserUpn, TenantA, UserOid, null, clientCapped: true);

        Assert.True(result.IsAllowed);
        Assert.Equal("AllMembers", result.AccessGrant);
        Assert.NotEmpty(seen);
        Assert.All(seen, identity => Assert.True(identity!.IsCapped));
    }
}
