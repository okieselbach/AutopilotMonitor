using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Application principals — a service principal presenting an app-only token (idtyp=app, typically behind a
/// federated credential) — flow through the same role tables as a person under the key
/// <c>app:&lt;client-id&gt;</c>. These tests pin the four places where that is decided: the classification
/// (<see cref="ClaimsPrincipalExtensions.GetUserPrincipalName"/>), the authentication gate
/// (<see cref="AuthenticationMiddleware.ValidateApplicationPrincipal"/>: the access_as_application
/// permission is mandatory), the roleless-tier deny in the policy middleware (an application is never
/// "some authenticated person"), and the caps — Viewer in a tenant, DelegatedReader on managed tenants,
/// never a platform role — enforced at resolution rather than only at grant time.
/// </summary>
public class ApplicationPrincipalTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string AppId = "AAAAAAAA-0000-0000-0000-00000000AAAA";
    private const string AppOid = "bbbbbbbb-0000-0000-0000-0000bbbbbbbb";
    private static readonly string AppKey = Constants.PrincipalKeys.ForApplication(AppId);

    private static ClaimsPrincipal AppPrincipal(
        string tenantId = TenantA, string? appId = AppId, string? oid = AppOid, bool withPermission = true,
        string appIdClaim = "appid", bool idtyp = true)
    {
        var claims = new List<Claim> { new("tid", tenantId) };
        if (idtyp) claims.Add(new Claim("idtyp", "app"));
        if (appId != null) claims.Add(new Claim(appIdClaim, appId));
        if (oid != null) claims.Add(new Claim("oid", oid));
        if (withPermission) claims.Add(new Claim("roles", Constants.ApplicationPermissions.AccessAsApplication));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal UserPrincipal(string upn = "alice@contoso.example", string tenantId = TenantA)
        => new(new ClaimsIdentity(new[]
        {
            new Claim("tid", tenantId), new Claim("upn", upn), new Claim("oid", "cccccccc-0000-0000-0000-0000cccccccc"),
            new Claim("scp", "access_as_user"),
        }, "TestAuth"));

    // ── Classification: the principal key ───────────────────────────────────────

    [Theory]
    [InlineData("appid")]
    [InlineData("azp")]
    public void AppOnlyToken_YieldsTheApplicationKey_FromV1AndV2Claims(string claim)
    {
        var principal = AppPrincipal(appIdClaim: claim);

        Assert.True(principal.IsApplicationPrincipal());
        Assert.Equal(AppId.ToLowerInvariant(), principal.GetApplicationId());
        Assert.Equal($"app:{AppId.ToLowerInvariant()}", principal.GetUserPrincipalName());
    }

    [Fact]
    public void TokenWithoutIdtyp_IsNotAnApplication_AndHasNoKey()
    {
        // Fail-closed classification: no idtyp ⇒ a person, who then needs a UPN like everyone else.
        var principal = AppPrincipal(idtyp: false);

        Assert.False(principal.IsApplicationPrincipal());
        Assert.Null(principal.GetUserPrincipalName());
        Assert.Null(AdminIdentity.FromPrincipal(principal));
    }

    [Fact]
    public void UserToken_KeepsItsUpn_EvenWhenAnAppIdIsPresent()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tid", TenantA), new Claim("upn", "Alice@Contoso.Example"), new Claim("appid", AppId),
        }, "TestAuth"));

        Assert.Equal("Alice@Contoso.Example", principal.GetUserPrincipalName());
        Assert.False(principal.IsApplicationPrincipal());
    }

    [Fact]
    public void AdminIdentity_OfAnApplication_IsFlagged()
    {
        var identity = AdminIdentity.FromPrincipal(AppPrincipal());

        Assert.NotNull(identity);
        Assert.True(identity!.IsApplication);
        Assert.Equal(AppKey, identity.Upn);
        Assert.Equal(AppOid, identity.ObjectId);
        Assert.False(AdminIdentity.FromPrincipal(UserPrincipal())!.IsApplication);
    }

    [Fact]
    public void PrincipalKeys_CannotCollideWithAUpn()
    {
        Assert.True(Constants.PrincipalKeys.IsApplication(AppKey));
        Assert.False(Constants.PrincipalKeys.IsApplication("app@contoso.example"));
        Assert.Equal(AppId.ToLowerInvariant(), Constants.PrincipalKeys.TryGetApplicationId(AppKey));
        Assert.Null(Constants.PrincipalKeys.TryGetApplicationId("alice@contoso.example"));
    }

    // ── Authentication gate ─────────────────────────────────────────────────────

    [Fact]
    public void Gate_PassesUserTokens_Untouched()
        => Assert.Null(AuthenticationMiddleware.ValidateApplicationPrincipal(UserPrincipal()));

    [Fact]
    public void Gate_PassesAnAdmissibleAppToken()
        => Assert.Null(AuthenticationMiddleware.ValidateApplicationPrincipal(AppPrincipal()));

    [Fact]
    public void Gate_RejectsAnAppToken_WithoutTheApplicationPermission()
    {
        var reason = AuthenticationMiddleware.ValidateApplicationPrincipal(AppPrincipal(withPermission: false));

        Assert.NotNull(reason);
        Assert.Contains(Constants.ApplicationPermissions.AccessAsApplication, reason);
    }

    [Fact]
    public void Gate_RejectsAnAppToken_WithoutApplicationIdOrObjectId()
    {
        Assert.Contains("application id", AuthenticationMiddleware.ValidateApplicationPrincipal(AppPrincipal(appId: null))!);
        Assert.Contains("object id", AuthenticationMiddleware.ValidateApplicationPrincipal(AppPrincipal(oid: null))!);
    }

    // ── Policy middleware: roleless tiers and caps ──────────────────────────────

    private sealed class Harness
    {
        public required PolicyEnforcementMiddleware Middleware { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }

        public void MemberRow(string role, bool enabled = true) =>
            Repo.Setup(r => r.GetTenantMemberAsync(TenantA, AppKey))
                .ReturnsAsync(new TenantMember { TenantId = TenantA, Upn = AppKey, Role = role, IsEnabled = enabled });
    }

    private static Harness BuildHarness()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>());
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>()))
            .ReturnsAsync(new AdminIdentityBinding { TenantId = TenantA, ObjectId = AppOid });
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

    [Theory]
    [InlineData("GET", "/api/feedback/status")]      // AuthenticatedUser
    [InlineData("GET", "/api/auth/me")]              // AuthenticatedUser
    [InlineData("POST", "/api/realtime/groups/join")] // AuthenticatedUserWithRole
    public async Task RolelessTiers_DenyAnApplication_UnlessTheEntryOptsIn(string method, string path)
    {
        var h = BuildHarness();
        h.MemberRow(Constants.TenantRoles.Viewer);

        var r = await h.Middleware.DecideAsync(method, path, null, AppPrincipal());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal("ApplicationPrincipalNotAllowed", r.LogReason);
    }

    [Fact]
    public async Task TheMcpFrontDoor_AdmitsAnApplication()
    {
        var h = BuildHarness();

        var r = await h.Middleware.DecideAsync("GET", "/api/auth/mcp", null, AppPrincipal());

        Assert.True(r.Allowed);
        Assert.Equal(AppKey, r.Context!.UserPrincipalName);
        Assert.Equal(AppOid, r.Context.ObjectId);
    }

    [Fact]
    public async Task MemberRead_AdmitsAnApplicationMember_CappedToViewer()
    {
        var h = BuildHarness();
        h.MemberRow(Constants.TenantRoles.Admin); // a row edited to Admin still reads as Viewer

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AppPrincipal());

        Assert.True(r.Allowed);
        Assert.Equal(Constants.TenantRoles.Viewer, r.Context!.UserRole);
        Assert.False(r.Context.IsTenantAdmin);
    }

    [Fact]
    public async Task MemberRead_DeniesAnApplication_WithoutARow()
    {
        var h = BuildHarness();

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AppPrincipal());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/sessions/abc/actions")]  // TenantAdminOrOperator
    [InlineData("PUT", "/api/sessions/abc/annotations/operator")]
    [InlineData("POST", "/api/config/11111111-1111-1111-1111-111111111111")] // TenantAdminOrGA
    public async Task WriteTiers_NeverAdmitAnApplication_EvenWithAnAdminRow(string method, string path)
    {
        var h = BuildHarness();
        h.MemberRow(Constants.TenantRoles.Admin);

        var r = await h.Middleware.DecideAsync(method, path, null, AppPrincipal());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
    }

    [Fact]
    public async Task PlatformRoleRow_IsInert_ForAnApplication()
    {
        var h = BuildHarness();
        h.Repo.Setup(r => r.GetGlobalRoleAsync(AppKey)).ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);

        var r = await h.Middleware.DecideAsync("GET", "/api/global/raw/access-probe", null, AppPrincipal());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
    }

    [Fact]
    public async Task DelegatedScope_OfAnApplication_IsCappedToReader()
    {
        var h = BuildHarness();
        h.Repo.Setup(r => r.GetDelegatedTenantsAsync(AppKey)).ReturnsAsync(new List<DelegatedAdminEntry>
        {
            new()
            {
                TenantId = TenantB, Role = Constants.DelegatedRoles.DelegatedAdmin, IsEnabled = true,
                Status = Constants.DelegatedStatus.Active, Source = Constants.DelegatedSource.OperatorGranted,
            },
        });

        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AppPrincipal());

        Assert.True(r.Allowed);
        Assert.True(r.Context!.IsDelegatedReader);
        Assert.False(r.Context.IsDelegatedAdmin);
    }

    // ── Member-role resolver cap ────────────────────────────────────────────────

    [Theory]
    [InlineData(Constants.TenantRoles.Admin)]
    [InlineData(Constants.TenantRoles.Operator)]
    [InlineData(Constants.TenantRoles.Viewer)]
    public async Task Resolver_CapsAnApplicationRow_ToViewer(string rowRole)
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetTenantMemberAsync(TenantA, AppKey))
            .ReturnsAsync(new TenantMember { TenantId = TenantA, Upn = AppKey, Role = rowRole, IsEnabled = true });
        var sut = Resolver(repo);

        var role = await sut.ResolveAsync(TenantA, AppKey, new[] { Constants.ApplicationPermissions.AccessAsApplication });

        Assert.Equal(Constants.TenantRoles.Viewer, role?.Role);
        Assert.False(role!.CanManageBootstrapTokens);
    }

    [Fact]
    public async Task Resolver_NeverDerivesAnApplicationRole_FromClaims()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        var sut = Resolver(repo, appRolesEnabled: true);

        // Even a (hypothetical) Admin app-role claim on an app token grants nothing without a row.
        var role = await sut.ResolveAsync(TenantA, AppKey, new[] { Constants.TenantRoles.Admin });

        Assert.Null(role);
    }

    private static TenantMemberRoleResolver Resolver(Mock<IAdminRepository> repo, bool appRolesEnabled = false)
    {
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantA, EntraAppRolesEnabled = appRolesEnabled });
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new TenantMemberRoleResolver(
            new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance),
            new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache));
    }

    // ── MCP front door ──────────────────────────────────────────────────────────

    [Fact]
    public async Task McpAccess_ForAnApplicationMember_IsAllMembers_AndNamesTheKey()
    {
        var (sut, members) = McpService();
        members.Verdict = (tid, key, _) => tid == TenantA && key == AppKey ? new MemberRoleInfo { Role = Constants.TenantRoles.Viewer } : null;

        var result = await sut.IsAllowedAsync(AppKey, TenantA, AppOid, new[] { Constants.ApplicationPermissions.AccessAsApplication });

        Assert.True(result.IsAllowed);
        Assert.Equal("AllMembers", result.AccessGrant);
        Assert.Equal(AppKey, result.Upn);
    }

    [Fact]
    public async Task McpAccess_ForAnApplication_WithoutMembership_SaysSo()
    {
        var (sut, _) = McpService();

        var result = await sut.IsAllowedAsync(AppKey, TenantA, AppOid);

        Assert.False(result.IsAllowed);
        Assert.Contains("Service principal", result.Reason);
    }

    private static (McpUserService Sut, StubTenantMemberRoleResolver Members) McpService()
    {
        var adminRepo = new Mock<IAdminRepository>();
        adminRepo.Setup(x => x.GetMcpUserAsync(It.IsAny<string>())).ReturnsAsync((McpUserEntry?)null);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var bindings = new StubAdminIdentityBindingService(bound: true);
        var globalAdmin = new Mock<GlobalAdminService>(adminRepo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance) { CallBase = false };
        globalAdmin.Setup(x => x.GetGlobalRoleAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync((string?)null);
        var delegatedAdmin = new Mock<DelegatedAdminService>(
            adminRepo.Object, bindings, new StubTenantEntitlementService(TenantEdition.Pro), cache, NullLogger<DelegatedAdminService>.Instance) { CallBase = false };
        delegatedAdmin.Setup(x => x.GetScopeAsync(It.IsAny<AdminIdentity?>())).ReturnsAsync(DelegatedScope.Empty);
        var adminConfig = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        adminConfig.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { McpAccessPolicy = McpAccessPolicy.AllMembers.ToString() });
        var members = new StubTenantMemberRoleResolver();
        var sut = new McpUserService(
            adminRepo.Object, bindings, cache, NullLogger<McpUserService>.Instance,
            globalAdmin.Object, delegatedAdmin.Object, adminConfig.Object, members);
        return (sut, members);
    }
}
