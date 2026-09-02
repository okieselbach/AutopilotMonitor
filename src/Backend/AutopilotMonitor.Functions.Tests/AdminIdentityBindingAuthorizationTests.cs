using System.Security.Claims;
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
/// End-to-end authorization effect of identity binding through <see cref="PolicyEnforcementMiddleware"/>:
/// a validly-signed token from a FOREIGN Entra tenant (the API accepts every tenant) whose upn matches a
/// GlobalAdmins / DelegatedAdmins row must resolve NO platform or delegated role — and likewise a token
/// from the home tenant under a different object id, a token without an oid, and any UPN whose role row
/// has no binding at all. Also pins the ordering inside <see cref="DelegatedAdminService"/>: binding is
/// checked before the Pro entitlement gate, and an unbound caller never triggers the edition lookup.
/// </summary>
public class AdminIdentityBindingAuthorizationTests
{
    private const string HomeTenant = "11111111-1111-1111-1111-111111111111";
    private const string AttackerTenant = "22222222-2222-2222-2222-222222222222";
    private const string ManagedTenant = "33333333-3333-3333-3333-333333333333";
    private const string GaUpn = "cloudadmin@vendor.example";
    private const string MspUpn = "msp@partner.example";
    private const string Oid = "aaaaaaaa-1111-1111-1111-aaaaaaaaaaaa";
    private const string OtherOid = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";

    private sealed class Harness
    {
        public required PolicyEnforcementMiddleware Middleware { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }
    }

    private static Harness Build(AdminIdentityBinding? binding, string? globalRole = null, string? delegatedTenant = null)
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync(globalRole);
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((TenantMember?)null);
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(
            delegatedTenant == null
                ? new List<DelegatedAdminEntry>()
                : new List<DelegatedAdminEntry>
                {
                    new()
                    {
                        Upn = MspUpn, TenantId = delegatedTenant.ToLowerInvariant(),
                        Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true,
                        Status = Constants.DelegatedStatus.Active, Source = Constants.DelegatedSource.OperatorGranted,
                    },
                });
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        repo.Setup(r => r.GetIdentityBindingAsync(It.IsAny<string>())).ReturnsAsync(() => binding);
        repo.Setup(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string _, string tid, string oid) =>
            {
                if (binding != null && !binding.IsObjectIdPinned && binding.TenantId == tid)
                    binding.ObjectId = oid;
                return binding;
            });

        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync((TenantConfiguration?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var bindings = new AdminIdentityBindingService(repo.Object, cache, NullLogger<AdminIdentityBindingService>.Instance);
        var globalAdmin = new GlobalAdminService(repo.Object, bindings, cache, NullLogger<GlobalAdminService>.Instance);
        var delegatedAdmin = new DelegatedAdminService(
            repo.Object, bindings,
            new StubTenantEntitlementService(TenantEdition.Pro),
            cache, NullLogger<DelegatedAdminService>.Instance);
        var tenantAdmins = new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance);
        var tenantConfig = new TenantConfigurationService(configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache);

        return new Harness
        {
            Middleware = new PolicyEnforcementMiddleware(
                NullLogger<PolicyEnforcementMiddleware>.Instance, globalAdmin, delegatedAdmin,
                new TenantMemberRoleResolver(tenantAdmins, tenantConfig), tenantConfig),
            Repo = repo,
        };
    }

    private static ClaimsPrincipal Token(string tenantId, string upn, string? oid = Oid)
    {
        var claims = new List<Claim> { new("tid", tenantId), new("upn", upn) };
        if (oid != null) claims.Add(new Claim("oid", oid));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static AdminIdentityBinding Bound(string upn, string tenantId, string objectId = Oid)
        => new() { Upn = upn, TenantId = tenantId, ObjectId = objectId };

    // ── Global Admin tier ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BoundGlobalAdmin_FromHomeTenant_IsAdmitted()
    {
        var h = Build(Bound(GaUpn, HomeTenant), globalRole: Constants.GlobalRoles.GlobalAdmin);
        var r = await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn));
        Assert.True(r.Allowed);
        Assert.True(r.Context!.IsGlobalAdmin);
    }

    [Theory]
    [InlineData("GET", "/api/global/raw/tables")]       // GlobalAdminOnly — raw table dump incl. tenant secrets
    [InlineData("POST", "/api/auth/global-admins")]     // GlobalAdminOnly — persist access
    [InlineData("GET", "/api/config/all")]              // GlobalReadOrDelegatedSubset
    [InlineData("GET", "/api/auth/global-admins")]      // GlobalReadOrAdmin
    public async Task ForeignTenantToken_WithGlobalAdminUpn_IsDenied(string method, string path)
    {
        // The finding: attacker registers the lapsed GA domain in their own tenant, creates the same UPN,
        // signs in (any tenant is accepted) — the token is genuine, only the tid differs.
        var h = Build(Bound(GaUpn, HomeTenant), globalRole: Constants.GlobalRoles.GlobalAdmin);
        var r = await h.Middleware.DecideAsync(method, path, null, Token(AttackerTenant, GaUpn));
        Assert.False(r.Allowed, $"{method} {path} must deny a foreign-tenant token even with a matching GA UPN");
    }

    [Fact]
    public async Task ForeignTenantToken_WithGlobalAdminUpn_CannotReachAnotherTenant()
    {
        // Cross-tenant read on a tenant-scoped route: no GlobalScope ⇒ the cross-tenant guard blocks.
        var h = Build(Bound(GaUpn, HomeTenant), globalRole: Constants.GlobalRoles.GlobalAdmin);
        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{ManagedTenant}", null, Token(AttackerTenant, GaUpn));
        Assert.False(r.Allowed);
    }

    [Fact]
    public async Task HomeTenantToken_DifferentObjectId_IsDenied()
    {
        // UPN recycled inside the home tenant (User Administrator re-assigns it to another account).
        var h = Build(Bound(GaUpn, HomeTenant, Oid), globalRole: Constants.GlobalRoles.GlobalAdmin);
        var r = await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, OtherOid));
        Assert.False(r.Allowed);
    }

    [Fact]
    public async Task TokenWithoutObjectId_ResolvesNoPlatformRole()
    {
        var h = Build(Bound(GaUpn, HomeTenant), globalRole: Constants.GlobalRoles.GlobalAdmin);
        var r = await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, oid: null));
        Assert.False(r.Allowed);
    }

    [Fact]
    public async Task UnboundGlobalAdminRow_IsInert()
    {
        // Legacy row, no binding yet: fail-closed until the operator seeds the binding — never "first come".
        var h = Build(binding: null, globalRole: Constants.GlobalRoles.GlobalAdmin);
        Assert.False((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn))).Allowed);
        Assert.False((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(AttackerTenant, GaUpn))).Allowed);
    }

    [Fact]
    public async Task UnpinnedBinding_HomeTenantFirstSignIn_Pins_ThenForeignAndRecycledAreDenied()
    {
        var h = Build(Bound(GaUpn, HomeTenant, objectId: ""), globalRole: Constants.GlobalRoles.GlobalAdmin);

        // First sign-in from the bound tenant pins the oid and is admitted.
        Assert.True((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, Oid))).Allowed);
        h.Repo.Verify(r => r.TryPinIdentityObjectIdAsync(GaUpn, HomeTenant, Oid), Times.Once);

        // From then on: same account ok, another account in the home tenant denied, foreign tenant denied.
        Assert.True((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, Oid))).Allowed);
        Assert.False((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, OtherOid))).Allowed);
        Assert.False((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(AttackerTenant, GaUpn, Oid))).Allowed);
    }

    [Fact]
    public async Task UnpinnedBinding_ForeignTenantCannotClaimThePin()
    {
        var h = Build(Bound(GaUpn, HomeTenant, objectId: ""), globalRole: Constants.GlobalRoles.GlobalAdmin);

        Assert.False((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(AttackerTenant, GaUpn, OtherOid))).Allowed);
        h.Repo.Verify(r => r.TryPinIdentityObjectIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        // The real admin still gets the pin afterwards.
        Assert.True((await h.Middleware.DecideAsync("GET", "/api/global/raw/tables", null, Token(HomeTenant, GaUpn, Oid))).Allowed);
    }

    // ── Delegated (MSP) tier ──────────────────────────────────────────────────────

    [Fact]
    public async Task BoundDelegatedAdmin_FromHomeTenant_IsRescuedOnManagedTenantRead()
    {
        var h = Build(Bound(MspUpn, HomeTenant), delegatedTenant: ManagedTenant);
        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{ManagedTenant}", null, Token(HomeTenant, MspUpn));
        Assert.True(r.Allowed);
        Assert.True(r.Context!.IsDelegatedReader);
    }

    [Fact]
    public async Task ForeignTenantToken_WithDelegatedUpn_ResolvesEmptyScope()
    {
        // The finding's MSP variant: an attacker tenant (Pro via self-service trial) mints the delegated UPN.
        var h = Build(Bound(MspUpn, HomeTenant), delegatedTenant: ManagedTenant);
        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{ManagedTenant}", null, Token(AttackerTenant, MspUpn));
        Assert.False(r.Allowed);
        Assert.False((await h.Middleware.DecideAsync("GET", "/api/config/all", null, Token(AttackerTenant, MspUpn))).Allowed);
    }

    [Fact]
    public async Task DelegatedService_ChecksBindingBeforeEntitlementGate()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(new List<DelegatedAdminEntry>
        {
            new()
            {
                Upn = MspUpn, TenantId = ManagedTenant, Role = Constants.DelegatedRoles.DelegatedReader,
                IsEnabled = true, Status = Constants.DelegatedStatus.Active, Source = Constants.DelegatedSource.OperatorGranted,
            },
        });
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(new List<TenantGroupAssignment>());
        var editionLookups = 0;
        var svc = new DelegatedAdminService(
            repo.Object,
            new StubAdminIdentityBindingService(bound: false),
            new StubTenantEntitlementService(_ => { editionLookups++; return TenantEdition.Pro; }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DelegatedAdminService>.Instance);

        var scope = await svc.GetScopeAsync(new AdminIdentity(MspUpn, AttackerTenant, Oid));

        Assert.True(scope.IsEmpty);
        Assert.Equal(0, editionLookups);
    }
}
