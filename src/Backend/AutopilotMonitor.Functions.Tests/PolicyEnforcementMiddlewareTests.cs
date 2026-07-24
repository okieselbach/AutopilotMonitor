using System.Security.Claims;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="PolicyEnforcementMiddleware"/> via its transport-agnostic <c>DecideAsync</c> seam
/// (authorization + cross-tenant enforcement + RequestContext construction — everything <c>Invoke</c> does
/// minus reading the request / writing the response). Uses the REAL GlobalAdminService /
/// TenantAdminsService / TenantConfigurationService — they all resolve through <see cref="IAdminRepository"/>
/// + an in-memory cache, so only the repository is mocked.
///
/// Primary purpose: lock in the ADDITIVE GlobalReader semantics in the resolved RequestContext —
/// a UPN that is both a GlobalReader and its own tenant's Admin must still surface IsTenantAdmin=true
/// (otherwise admin SignalR group joins + admin-audience notifications regress), while a pure GlobalReader
/// stays read-only. Also covers the cross-tenant gate keying off HasGlobalScope.
/// </summary>
public class PolicyEnforcementMiddlewareTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private sealed class Harness
    {
        public required PolicyEnforcementMiddleware Middleware { get; init; }
        public required Mock<IAdminRepository> Repo { get; init; }
        public required Mock<IConfigRepository> ConfigRepo { get; init; }

        public void AsGlobalRole(string role) =>
            Repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync(role);

        public void AsTenantAdmin(string tenantId, string upn) =>
            Repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new TenantMember
                {
                    Upn = upn,
                    TenantId = tenantId,
                    Role = Constants.TenantRoles.Admin,
                    IsEnabled = true,
                });

        /// <summary>Grants the caller a delegated assignment over <paramref name="tenantId"/> at the given role.</summary>
        public void AsDelegated(string tenantId, string role = Constants.DelegatedRoles.DelegatedReader) =>
            Repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
                .ReturnsAsync(new List<DelegatedAdminEntry>
                {
                    new()
                    {
                        TenantId = tenantId.ToLowerInvariant(),
                        Role = role,
                        IsEnabled = true,
                        Status = Constants.DelegatedStatus.Active,
                        Source = Constants.DelegatedSource.OperatorGranted,
                    },
                });
    }

    private static Harness BuildHarness()
    {
        var repo = new Mock<IAdminRepository>();
        // Defaults: no platform role, no tenant membership, no delegated assignments. Tests override per-case.
        repo.Setup(r => r.GetGlobalRoleAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((TenantMember?)null);
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        // No group assignments by default (delegated scope resolves direct grants + group tenants).
        repo.Setup(r => r.GetGroupAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantGroupAssignment>());

        // Captured config repo: GetTenantConfigurationAsync returns null (tenant has no config row) so we can
        // assert that authorization role resolution never PERSISTS a default row as a side effect.
        var configRepo = new Mock<IConfigRepository>();
        configRepo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync((TenantConfiguration?)null);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var globalAdmin = new GlobalAdminService(repo.Object, cache, NullLogger<GlobalAdminService>.Instance);
        // Entitlements stubbed to Enterprise — the delegated-rescue tests here pin AUTHORIZATION
        // mechanics; the Enterprise-only edition gate is covered by DelegatedAdminEditionGateTests.
        var delegatedAdmin = new DelegatedAdminService(
            repo.Object,
            new StubTenantEntitlementService(AutopilotMonitor.Functions.Security.TenantEdition.Enterprise),
            cache,
            NullLogger<DelegatedAdminService>.Instance);
        var tenantAdmins = new TenantAdminsService(repo.Object, cache, NullLogger<TenantAdminsService>.Instance);
        var tenantConfig = new TenantConfigurationService(
            configRepo.Object, NullLogger<TenantConfigurationService>.Instance, cache);

        var mw = new PolicyEnforcementMiddleware(
            NullLogger<PolicyEnforcementMiddleware>.Instance, globalAdmin, delegatedAdmin, tenantAdmins, tenantConfig);

        return new Harness { Middleware = mw, Repo = repo, ConfigRepo = configRepo };
    }

    private static ClaimsPrincipal AuthedPrincipal(string tenantId, string upn)
        => new(new ClaimsIdentity(new[] { new Claim("tid", tenantId), new Claim("upn", upn) }, "TestAuth"));

    // ── ADDITIVE: GlobalReader + own-tenant Admin keeps IsTenantAdmin ──────────────

    [Fact]
    public async Task MemberRead_GlobalReaderWhoIsAlsoTenantAdmin_KeepsIsTenantAdmin()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // /api/notifications is MemberRead — its audience depends on IsTenantAdmin.
        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsGlobalReader);
        Assert.False(rc.IsGlobalAdmin);
        Assert.True(rc.IsTenantAdmin); // additive: own-tenant admin survives the global-role branch
        Assert.True(rc.HasGlobalScope);
    }

    [Fact]
    public async Task AuthenticatedUserWithRole_GlobalReaderWhoIsAlsoTenantAdmin_KeepsIsTenantAdmin()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // SignalR group join is AuthenticatedUserWithRole — the admin notification group gates on IsTenantAdmin.
        var result = await h.Middleware.DecideAsync("POST", "/api/realtime/groups/join", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalReader);
        Assert.True(result.Context!.IsTenantAdmin);
    }

    // ── Pure roles (regression guards for the TenantRole ?? UserRole fallback) ─────

    [Fact]
    public async Task MemberRead_PureGlobalReader_IsNotTenantAdmin()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        // No tenant membership (default null).

        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalReader);
        Assert.False(result.Context!.IsTenantAdmin); // no own-tenant role ⇒ not a tenant admin
    }

    [Fact]
    public async Task MemberRead_PureTenantAdmin_IsTenantAdmin_NoGlobalScope()
    {
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn); // enabled Admin row, no global role

        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsTenantAdmin);   // via the TenantRole ?? UserRole fallback (UserRole = "Admin")
        Assert.False(result.Context!.IsGlobalReader);
        Assert.False(result.Context!.IsGlobalAdmin);
    }

    // ── ADDITIVE write: GlobalReader + own-tenant Admin may still write own tenant ─

    [Fact]
    public async Task WriteRoute_GlobalReaderWhoIsAlsoTenantAdmin_AllowedInOwnTenant()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        // PUT config/{tenantId} is TenantAdminOrGA (write). Route tenant == JWT tenant ⇒ own-tenant write.
        var result = await h.Middleware.DecideAsync("PUT", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed); // additive: the tenant-admin hat still grants write in the own tenant
    }

    // ── Pure GlobalReader is read-only: write routes are denied ────────────────────

    [Fact]
    public async Task WriteRoute_PureGlobalReader_IsForbidden()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        // No tenant membership ⇒ no write path.

        var result = await h.Middleware.DecideAsync("PUT", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── TenantAdminOrOperator (POST sessions/{id}/actions — "Collect Logs") ────────

    private const string SessionA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static void AsTenantMemberRole(Harness h, string tenantId, string upn, string role) =>
        h.Repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new TenantMember { Upn = upn, TenantId = tenantId, Role = role, IsEnabled = true });

    [Fact]
    public async Task SessionActions_TenantOperator_IsAllowed_WithOperatorRoleOnContext()
    {
        const string upn = "operator@contoso.com";
        var h = BuildHarness();
        AsTenantMemberRole(h, TenantA, upn, Constants.TenantRoles.Operator);

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        // The function's per-type gate depends on these exact flags: an Operator must arrive
        // with IsTenantAdmin=false + UserRole=Operator so terminate_session/rotate_config 403.
        Assert.False(result.Context!.IsTenantAdmin);
        Assert.Equal(Constants.TenantRoles.Operator, result.Context!.UserRole);
    }

    [Fact]
    public async Task SessionActions_TenantAdmin_IsAllowed_WithAdminFlag()
    {
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn);

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsTenantAdmin);
    }

    [Fact]
    public async Task SessionActions_TenantViewer_IsForbidden()
    {
        const string upn = "viewer@contoso.com";
        var h = BuildHarness();
        AsTenantMemberRole(h, TenantA, upn, Constants.TenantRoles.Viewer);

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task SessionActions_NonMember_IsForbidden()
    {
        var h = BuildHarness(); // defaults: no membership, no global role

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, "outsider@contoso.com"));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task SessionActions_GlobalAdmin_IsAllowed()
    {
        const string upn = "ga@platform.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalAdmin);
    }

    [Fact]
    public async Task SessionActions_PureGlobalReader_IsForbidden_WriteTier()
    {
        const string upn = "reader@platform.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var result = await h.Middleware.DecideAsync(
            "POST", $"/api/sessions/{SessionA}/actions", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── config GET (MemberRead): own-tenant admin view vs redacted reader/member view ──
    // GetTenantConfigurationFunction redacts unless GA or own-tenant Admin (CanViewSecrets).
    // These lock in the RequestContext that drives that decision.

    [Fact]
    public async Task ConfigGet_GlobalReaderWhoIsOwnTenantAdmin_OwnTenant_IsFullView()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn);

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsTenantAdmin);
        Assert.Equal(rc.TenantId, rc.TargetTenantId); // own tenant ⇒ ownTenantAdminView ⇒ FULL config (not redacted)
    }

    [Fact]
    public async Task ConfigGet_GlobalReaderCrossTenant_IsReaderView()
    {
        const string upn = "dual@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);
        h.AsTenantAdmin(TenantA, upn); // admin of their OWN tenant (A); role resolves against JWT tenant

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsGlobalReader);
        Assert.True(rc.IsTenantAdmin);                  // admin of A…
        Assert.NotEqual(rc.TenantId, rc.TargetTenantId); // …but viewing B ⇒ reader view ⇒ REDACTED
        Assert.Equal(TenantB, rc.TargetTenantId);
    }

    [Fact]
    public async Task ConfigGet_OwnTenantOperator_IsAllowed_RedactedMemberView()
    {
        // Read-only Settings view for troubleshooting staff: an own-tenant Operator is admitted
        // (MemberRead) but arrives with IsTenantAdmin=false, so CanViewSecrets ⇒ redacted copy.
        const string upn = "operator@contoso.com";
        var h = BuildHarness();
        AsTenantMemberRole(h, TenantA, upn, Constants.TenantRoles.Operator);

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.False(rc.IsTenantAdmin);
        Assert.False(GetTenantConfigurationFunction.CanViewSecrets(rc));
    }

    [Fact]
    public async Task ConfigGet_CrossTenantOperator_IsForbidden()
    {
        // Membership is resolved against the caller's JWT tenant — an Operator of A naming
        // tenant B in the route is a cross-tenant read and must stay 403.
        const string upn = "operator@contoso.com";
        var h = BuildHarness();
        AsTenantMemberRole(h, TenantA, upn, Constants.TenantRoles.Operator);

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── Cross-tenant read: GlobalReader crosses tenants (HasGlobalScope gate) ───────

    [Fact]
    public async Task CrossTenantRead_GlobalReader_AllowedAndTargetsForeignTenant()
    {
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        // Reader in TenantA reading TenantB's config (RouteParam). HasGlobalScope ⇒ cross-tenant allowed.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.Equal(TenantB, result.Context!.TargetTenantId);
    }

    [Fact]
    public async Task CrossTenantRead_PureTenantAdmin_IsForbidden()
    {
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn); // admin of A only, no global scope

        // Admin of TenantA trying to read TenantB's config → cross-tenant blocked.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("CrossTenantAccessDenied", result.ErrorCode);
    }

    // ── Unauthenticated / unregistered ─────────────────────────────────────────────

    [Fact]
    public async Task MemberRead_NoPrincipal_IsUnauthorized()
    {
        var h = BuildHarness();
        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, principal: null);

        Assert.False(result.Allowed);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task UnregisteredRoute_IsForbidden()
    {
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);

        var result = await h.Middleware.DecideAsync("GET", "/api/does-not-exist", null, AuthedPrincipal(TenantA, "ga@contoso.com"));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("Forbidden", result.ErrorCode);
    }

    // ── Delegated admin ("scoped global" / MSP): reads a SUBSET of tenants ──────────
    // A delegated reader signs into their OWN home tenant (TenantA) but manages TenantB.

    [Fact]
    public async Task Delegated_CrossTenantRead_RouteParam_AllowedForAssignedTenant()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB); // delegated reader of B; NOT a member of A, no global role

        // GET config/{B} is TenantAdminOrGlobalReader + RouteParam — a delegated read tier.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.Equal(TenantB, rc.TargetTenantId);
        Assert.True(rc.IsDelegatedReader);
        Assert.True(rc.IsDelegated);
        Assert.True(rc.HasFleetScope);
        Assert.False(rc.IsGlobalReader);   // delegated is NOT platform scope
        Assert.False(rc.IsTenantAdmin);    // no own-tenant admin ⇒ config GET redacts secrets for them
        Assert.Contains(TenantB.ToLowerInvariant(), rc.AllowedTenantIds!);
    }

    [Fact]
    public async Task Delegated_CrossTenantRead_QueryParam_AllowedForAssignedTenant()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        // GET sessions/{id} is MemberRead + QueryParam — the function reads ?tenantId=.
        var result = await h.Middleware.DecideAsync(
            "GET", "/api/sessions/abc-123", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.Equal(TenantB, result.Context!.TargetTenantId);
        Assert.True(result.Context!.IsDelegatedReader);
    }

    [Fact]
    public async Task Delegated_SessionEvents_QueryParam_AllowedForAssignedTenant()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        // The core MSP drill-in read: a managed tenant's session event timeline.
        // GET sessions/{id}/events is MemberRead + QueryParam — reachable via the delegated scope.
        var result = await h.Middleware.DecideAsync(
            "GET", "/api/sessions/abc-123/events", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.Equal(TenantB, result.Context!.TargetTenantId);
        Assert.True(result.Context!.IsDelegatedReader);
    }

    [Fact]
    public async Task Delegated_SessionMutation_IsForbidden()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB); // read-only over B

        // A delegated reader must NOT mutate a managed tenant's session. POST sessions/{id}/mark-failed is
        // TenantAdminOrGA — not a delegated read tier — so the read-only drill-in stays read-only.
        var result = await h.Middleware.DecideAsync(
            "POST", "/api/sessions/abc-123/mark-failed", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_CrossTenantRead_UnassignedTenant_HitsCrossTenantGate()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);        // delegated to B only
        h.AsTenantAdmin(TenantA, upn); // admin of own tenant A ⇒ passes the read tier…

        // …but tries to cross into a THIRD tenant they were never assigned ⇒ blocked at the cross-tenant gate.
        const string tenantC = "33333333-3333-3333-3333-333333333333";
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{tenantC}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal("CrossTenantAccessDenied", result.ErrorCode);
    }

    [Fact]
    public async Task Delegated_NonMember_UnassignedTenant_IsForbidden()
    {
        // A pure delegated admin of B (no own-tenant membership) hitting an unassigned tenant C is denied
        // at the policy tier (not a member, not global, not delegated of C) — still 403, just earlier.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        const string tenantC = "33333333-3333-3333-3333-333333333333";
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{tenantC}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_WriteRoute_OnAssignedTenant_IsForbidden()
    {
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB, Constants.DelegatedRoles.DelegatedAdmin); // even a DelegatedAdmin row…

        // PUT config/{B} is TenantAdminOrGA (write). Phase 1: delegation never satisfies a write tier.
        var result = await h.Middleware.DecideAsync("PUT", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── Tenant Groups management — GlobalAdminOnly mutations, GlobalReadOrAdmin list ──

    [Fact]
    public async Task TenantGroups_Mutation_PureDelegated_IsForbidden()
    {
        // A delegated ("MSP") admin must never manage groups — every mutation is GlobalAdminOnly,
        // which is not a read tier, so the delegated rescue cannot admit it.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB, Constants.DelegatedRoles.DelegatedAdmin);

        var result = await h.Middleware.DecideAsync(
            "POST", "/api/global/tenant-groups/tpl-1/assignees", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task TenantGroups_RemoveTenant_PureGlobalReader_IsForbidden()
    {
        // The one {tenantId} route (RouteParam) is still GlobalAdminOnly — a read-only Global Reader is denied.
        const string upn = "reader@vendor.example";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var result = await h.Middleware.DecideAsync(
            "DELETE", $"/api/global/tenant-groups/tpl-1/tenants/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task TenantGroups_List_GlobalReader_IsAllowed()
    {
        // Listing groups is GlobalReadOrAdmin — a read-only Global Reader may audit them.
        const string upn = "reader@vendor.example";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var result = await h.Middleware.DecideAsync(
            "GET", "/api/global/tenant-groups", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task TenantGroups_Create_GlobalAdmin_IsAllowed()
    {
        const string upn = "ga@vendor.example";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);

        var result = await h.Middleware.DecideAsync(
            "POST", "/api/global/tenant-groups", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Delegated_OwnHomeTenant_NonMember_IsStillForbidden()
    {
        // A delegated admin of B who is NOT a member of their own home tenant A must not gain read of A
        // just by being a delegated admin of something else.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        // GET config/{A} — A is their JWT tenant, but they have no membership there.
        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public void GlobalSessions_IsBoundedSubsetTier()
    {
        // global/sessions + global/stats/sessions are GlobalReadOrDelegatedSubset (bounded aggregate for
        // delegated) yet keep QueryParam scoping (single-tenant drill). config/all is the same tier sans drill.
        foreach (var path in new[] { "/api/global/sessions", "/api/global/stats/sessions" })
        {
            var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", path);
            Assert.NotNull(entry);
            Assert.Equal(EndpointPolicy.GlobalReadOrDelegatedSubset, entry!.Policy);
            Assert.Equal(TenantScoping.QueryParam, entry.TenantScoping);
        }
    }

    [Fact]
    public async Task Delegated_GlobalSessions_NoTenantId_IsBoundedAggregate()
    {
        // global/sessions WITHOUT ?tenantId= is the BOUNDED aggregate for a delegated ("MSP") caller: the
        // subset tier admits them and publishes the managed set on AllowedTenantIds, which the handler uses
        // to restrict the cross-tenant fan-out to those tenants. (global/metrics/summary, still on the plain
        // GlobalReadOrAdmin tier, stays forbidden without a tenantId — see Delegated_MetricsSummary_* below.)
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/sessions", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsDelegatedReader);
        Assert.False(rc.IsGlobalReader);
        Assert.NotNull(rc.AllowedTenantIds);
        Assert.Contains(TenantB.ToLowerInvariant(), rc.AllowedTenantIds!);
    }

    [Fact]
    public async Task Delegated_Read_DoesNotCreateConfigForMspHomeTenant()
    {
        // Regression: authorization role resolution touches the caller's OWN home tenant role path before
        // the delegated rescue. That lookup must be side-effect-free — an external MSP user whose home
        // tenant (A) is not onboarded must NOT get a phantom TenantConfiguration row written on their first
        // cross-tenant read. (See ResolveEffectiveRoleAsync → TryGetConfigurationAsync, non-creating.)
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB); // delegated reader of B; home tenant A has no membership and no config row

        var result = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed); // the delegated read itself is allowed…
        // …but no config row was persisted for the MSP's home tenant A (nor any tenant).
        h.ConfigRepo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }

    // ── Phase 2a: delegated single-tenant access to cross-tenant /api/global/* read endpoints ──

    [Fact]
    public async Task Delegated_GlobalEndpoint_SingleTenant_InScope_IsAllowed()
    {
        // global/sessions?tenantId=B — SAFE route (handler restricts to the named tenant), B in scope.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/sessions", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.Equal(TenantB, rc.TargetTenantId);
        Assert.True(rc.IsDelegatedReader);
        Assert.False(rc.IsGlobalReader);
        Assert.Contains(TenantB.ToLowerInvariant(), rc.AllowedTenantIds!);
    }

    [Fact]
    public async Task Delegated_MetricsSummary_SingleTenant_InScope_IsAllowed()
    {
        // global/metrics/summary?tenantId=B — Phase 6 recatalog to QueryParam: the handler filters the
        // summary to the named tenant, so a delegated reader (get_metrics over MCP) reaches it for B.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/metrics/summary", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsDelegatedReader);
        Assert.Equal(TenantB, result.Context!.TargetTenantId);
    }

    [Fact]
    public async Task Delegated_MetricsSummary_Aggregate_NoTenantId_IsForbidden()
    {
        // Without ?tenantId= the delegated rescue resolves no target ⇒ the aggregate summary stays blocked.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/metrics/summary", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_GlobalEndpoint_SingleTenant_OutOfScope_IsForbidden()
    {
        // global/sessions?tenantId=C — C is not in the delegated set ⇒ blocked.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        const string tenantC = "33333333-3333-3333-3333-333333333333";
        var result = await h.Middleware.DecideAsync("GET", "/api/global/sessions", tenantC, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_GlobalSessions_HomeTenantDrill_AdmittedButNotInAllowedSet()
    {
        // global/sessions?tenantId=<the caller's OWN JWT tenant> — crossTenant is false, so the delegated
        // scoped-route check is skipped and the subset tier admits the caller. This is BY DESIGN (admit +
        // publish AllowedTenantIds, handler bounds): the home tenant is NOT in AllowedTenantIds, so the repo
        // (TableStorageService.DrillOutsideBound) returns empty. This test pins that contract so the bound is
        // never silently widened to include the unmanaged home tenant. Covered end-to-end by
        // DelegatedBoundedAggregateTests.BoundedDrill/Stats_UnmanagedTenant_*.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/sessions", TenantA, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsDelegatedReader);
        Assert.NotNull(rc.AllowedTenantIds);
        Assert.DoesNotContain(TenantA.ToLowerInvariant(), rc.AllowedTenantIds!);
        Assert.Contains(TenantB.ToLowerInvariant(), rc.AllowedTenantIds!);
    }

    [Fact]
    public async Task Delegated_AggregateOnlyRoute_WithTenantId_StaysBlocked()
    {
        // global/presence is AGGREGATE-ONLY: its handler ignores ?tenantId= and returns ALL tenants'
        // presence. It MUST stay TenantScoping.None so a delegated caller can never reach it — even if they
        // pass a tenantId in their scope. (If this ever flips to QueryParam it's a cross-tenant leak.)
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/presence", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_OpsEvents_WithManagedTenantId_IsForbidden()
    {
        // ops-events is platform-operational (GA/Reader-only BY INTENT), so it is TenantScoping.None — a
        // delegated ("MSP") admin must NOT reach it even with a tenantId in their managed set. Unlike the
        // aggregate-only routes above this is a DELIBERATE least-privilege choice (the handler COULD bound by
        // tenant), not a technical can't-bound limitation. If this ever flips to QueryParam, delegated regains it.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/ops-events", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task GlobalReader_OpsEvents_IsAllowed()
    {
        // The None scoping excludes only delegated — the read-only Global Reader (and GA) keep ops-events.
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/ops-events", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Delegated_CustomsArchive_OnManagedTenant_IsForbidden_ExcludeDelegated()
    {
        // customs-archive is offboarding cleanup (platform-operational) — GA/Reader-only via the
        // excludeDelegated flag, even though it is GlobalReadOrAdmin + RouteParam (a delegated read-tier
        // scoped route). A delegated admin with the archived tenant in scope is still denied. This is the
        // None-equivalent for a {tenantId} route (which the convention test forbids from being None).
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync(
            "GET", $"/api/global/customs-archive/{TenantB}/hist-1", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_DeletionManifests_OnManagedTenant_IsForbidden_ExcludeDelegated()
    {
        // global/tenants/{tenantId}/deletion-manifests is platform-operational (cascade-delete restore prep) —
        // excludeDelegated keeps it GA/Reader-only despite being a delegated read-tier RouteParam route.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync(
            "GET", $"/api/global/tenants/{TenantB}/deletion-manifests", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Delegated_PreviewNotificationEmail_OnManagedTenant_IsForbidden_ExcludeDelegated()
    {
        // preview/notification-email/{tenantId} is the platform's private-preview welcome-email config (a GA
        // ONBOARDING artifact, not the tenant's operational data) — excludeDelegated keeps it GA/Reader-only.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync(
            "GET", $"/api/preview/notification-email/{TenantB}", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task GlobalReader_ExcludeDelegatedRoutes_StillAllowed()
    {
        // excludeDelegated removes ONLY the delegated rescue — the read-only Global Reader (and GA) keep
        // these platform-operational reads (mirrors the ops-events / "GA und GA-Reader" intent).
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var manifests = await h.Middleware.DecideAsync(
            "GET", $"/api/global/tenants/{TenantB}/deletion-manifests", TenantB, AuthedPrincipal(TenantA, upn));
        var archive = await h.Middleware.DecideAsync(
            "GET", $"/api/global/customs-archive/{TenantB}/hist-1", TenantB, AuthedPrincipal(TenantA, upn));
        var previewEmail = await h.Middleware.DecideAsync(
            "GET", $"/api/preview/notification-email/{TenantB}", TenantB, AuthedPrincipal(TenantA, upn));

        Assert.True(manifests.Allowed);
        Assert.True(archive.Allowed);
        Assert.True(previewEmail.Allowed);
    }

    [Fact]
    public async Task GlobalReader_AggregateGlobalEndpoint_StillWorks()
    {
        // Regression: recataloging SAFE routes to QueryParam must not change full-platform behavior.
        // A Global Reader hitting the aggregate path (no tenantId) is still allowed (HasGlobalScope bypass).
        const string upn = "reader@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalReader);

        var result = await h.Middleware.DecideAsync("GET", "/api/global/sessions", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
    }

    // ── Catalog contract: which global routes are delegated-accessible (single-tenant) vs aggregate-only ──
    // QueryParam/RouteParam on a GlobalReadOrAdmin route == "handler restricts to the named tenant" ==
    // reachable by a delegated admin. None == aggregate-only == GA/Reader-only. Pins the leak-critical split.

    [Theory]
    // SAFE — opened to delegated single-tenant access. (global/sessions + global/stats/sessions moved to
    // the GlobalReadOrDelegatedSubset tier — see GlobalSessions_IsBoundedSubsetTier below.)
    [InlineData("GET", "/api/global/audit/logs", TenantScoping.QueryParam)]
    [InlineData("GET", "/api/global/metrics/app", TenantScoping.QueryParam)]
    [InlineData("GET", "/api/global/metrics/fleet-health", TenantScoping.QueryParam)]
    [InlineData("GET", "/api/global/metrics/summary", TenantScoping.QueryParam)]
    [InlineData("GET", "/api/global/search/sessions", TenantScoping.QueryParam)]
    // AGGREGATE-ONLY or GA/Reader-ONLY-by-intent — MUST stay None (unreachable by delegated):
    [InlineData("GET", "/api/global/presence", TenantScoping.None)]
    [InlineData("GET", "/api/global/metrics/platform", TenantScoping.None)]
    [InlineData("GET", "/api/global/distress-reports", TenantScoping.None)]
    // ops-events is platform-OPERATIONAL data — GA/Reader-only by intent (not a can't-bound limit), so None.
    [InlineData("GET", "/api/global/ops-events", TenantScoping.None)]
    public void GlobalRoute_DelegatedAccessibility_MatchesContract(string method, string path, TenantScoping expected)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);
        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalReadOrAdmin, entry!.Policy);
        Assert.Equal(expected, entry.TenantScoping);
    }

    // ── Catalog contract: platform-operational GA/Reader-only reads carry ExcludeDelegated ──
    // The None-equivalent for {tenantId}-RouteParam routes (which the {tenantId}-convention forbids from
    // being None): GlobalReadOrAdmin keeps GA + read-only Reader, ExcludeDelegated removes the delegated rescue.

    [Theory]
    [InlineData("GET", "/api/global/customs-archive")]
    [InlineData("GET", "/api/global/customs-archive/tid-1/hist-1")]
    [InlineData("GET", "/api/global/customs-archive/tid-1/hist-1/arc-1")]
    [InlineData("GET", "/api/global/tenants/tid-1/deletion-manifests")]
    [InlineData("GET", "/api/preview/notification-email/tid-1")]
    public void PlatformOperationalReads_ExcludeDelegated(string method, string path)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);
        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalReadOrAdmin, entry!.Policy);
        Assert.True(entry.ExcludeDelegated);
    }

    // ── Phase 2b: config/all is a bounded-subset aggregate (GlobalReadOrDelegatedSubset) ──

    [Fact]
    public void ConfigAll_IsGlobalReadOrDelegatedSubsetTier()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/config/all");
        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalReadOrDelegatedSubset, entry!.Policy);
    }

    [Fact]
    public async Task Delegated_ConfigAll_IsAdmitted_WithBoundedSubset()
    {
        // A delegated admin may list ITS tenants via config/all — the middleware publishes the managed set
        // on AllowedTenantIds and the handler binds the response to it.
        const string upn = "msp@partner.example";
        var h = BuildHarness();
        h.AsDelegated(TenantB);

        var result = await h.Middleware.DecideAsync("GET", "/api/config/all", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        var rc = result.Context!;
        Assert.True(rc.IsDelegatedReader);
        Assert.False(rc.IsGlobalReader);
        Assert.NotNull(rc.AllowedTenantIds);
        Assert.Contains(TenantB.ToLowerInvariant(), rc.AllowedTenantIds!);
    }

    [Fact]
    public async Task GlobalAdmin_ConfigAll_IsUnbounded()
    {
        // A Global Admin sees ALL tenants: no AllowedTenantIds bound is published (handler does not filter).
        const string upn = "ga@contoso.com";
        var h = BuildHarness();
        h.AsGlobalRole(Constants.GlobalRoles.GlobalAdmin);

        var result = await h.Middleware.DecideAsync("GET", "/api/config/all", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsGlobalAdmin);
        Assert.Null(result.Context!.AllowedTenantIds);
    }

    [Fact]
    public async Task NonDelegatedNonGlobal_ConfigAll_IsForbidden()
    {
        // A plain tenant admin (no delegated assignment, no platform role) cannot list tenants.
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn);

        var result = await h.Middleware.DecideAsync("GET", "/api/config/all", null, AuthedPrincipal(TenantA, upn));

        Assert.False(result.Allowed);
        Assert.Equal(403, result.StatusCode);
    }

    // ── Empty-UPN contract for device / anonymous routes ───────────────────────────
    //
    // UserRateLimitMiddleware buckets on RequestContext.UserPrincipalName and skips only when it is
    // EMPTY. Publishing the "anonymous" placeholder there collapsed every agent of every tenant into a
    // single shared user_ratelimit_anonymous bucket, which 429'd agent telemetry fleet-wide once the
    // per-user limit was reached. These tests pin the contract: no JWT ⇒ empty UPN.

    [Theory]
    [InlineData("POST", "/api/agent/telemetry")]
    [InlineData("GET", "/api/agent/config")]
    [InlineData("POST", "/api/agent/register-session")]
    [InlineData("POST", "/api/agent/upload-url")]
    [InlineData("POST", "/api/agent/error")]
    [InlineData("POST", "/api/bootstrap/register-session")]
    [InlineData("GET", "/api/bootstrap/config")]
    [InlineData("POST", "/api/bootstrap/error")]
    public async Task DeviceRoute_WithoutJwt_PublishesEmptyUpn(string method, string path)
    {
        var h = BuildHarness();

        var result = await h.Middleware.DecideAsync(method, path, null, principal: null);

        Assert.True(result.Allowed);
        // The rate-limit bucket key: empty means "no user bucket applies to this call".
        Assert.Equal(string.Empty, result.Context!.UserPrincipalName);
        // The placeholder still renders in log lines — it just never reaches the RequestContext.
        Assert.Equal("anonymous", result.UserIdentifier);
    }

    [Theory]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/stats/platform")]
    [InlineData("POST", "/api/agent/distress")]
    [InlineData("GET", "/api/diagnostics/download")]
    public async Task AnonymousRoute_WithoutJwt_PublishesEmptyUpn(string method, string path)
    {
        var h = BuildHarness();

        var result = await h.Middleware.DecideAsync(method, path, null, principal: null);

        Assert.True(result.Allowed);
        Assert.Equal(string.Empty, result.Context!.UserPrincipalName);
    }

    [Fact]
    public async Task AuthenticatedRoute_StillPublishesTheRealUpn()
    {
        // Guard the other direction: the fix must not blank the UPN for genuine JWT callers, or
        // per-user rate limiting and presence tracking would silently stop working.
        const string upn = "admin@contoso.com";
        var h = BuildHarness();
        h.AsTenantAdmin(TenantA, upn);

        var result = await h.Middleware.DecideAsync("GET", "/api/notifications", null, AuthedPrincipal(TenantA, upn));

        Assert.True(result.Allowed);
        Assert.Equal(upn, result.Context!.UserPrincipalName);
        Assert.Equal(upn, result.Context!.CallerId);
    }

    // ── CallerId: throttle identity survives tokens that carry no UPN ──────────────
    //
    // AuthenticationMiddleware validates issuer/audience/lifetime/signature but requires NO upn and no
    // scp, so an app-only (client-credentials) token authenticates and reaches every AuthenticatedUser
    // route (feedback, realtime/negotiate, progress/*, health/detailed, …). UserRateLimitMiddleware
    // skips on an EMPTY throttle identity — so if CallerId fell back to the (absent) UPN, those tokens
    // would bypass rate limiting entirely. These tests pin the fallback chain.

    /// <summary>Principal with a tid but NO upn/preferred_username — shaped like an app-only token.</summary>
    private static ClaimsPrincipal AppOnlyPrincipal(string tenantId, params (string Type, string Value)[] claims)
    {
        var all = new List<Claim> { new("tid", tenantId) };
        all.AddRange(claims.Select(c => new Claim(c.Type, c.Value)));
        return new ClaimsPrincipal(new ClaimsIdentity(all, "TestAuth"));
    }

    [Theory]
    [InlineData("oid", "aaaaaaaa-0000-0000-0000-000000000001")]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier", "aaaaaaaa-0000-0000-0000-000000000002")]
    [InlineData("sub", "subject-abc")]
    [InlineData("appid", "bbbbbbbb-0000-0000-0000-000000000003")]
    [InlineData("azp", "cccccccc-0000-0000-0000-000000000004")]
    public async Task AuthenticatedUserRoute_TokenWithoutUpn_StillGetsAThrottleIdentity(string claimType, string claimValue)
    {
        var h = BuildHarness();

        // /api/feedback/status is AuthenticatedUser — admits ANY valid principal, role unresolved.
        var result = await h.Middleware.DecideAsync(
            "GET", "/api/feedback/status", null, AppOnlyPrincipal(TenantA, (claimType, claimValue)));

        Assert.True(result.Allowed);
        // No UPN to report — audit/presence correctly see nothing...
        Assert.Equal(string.Empty, result.Context!.UserPrincipalName);
        // ...but the throttle still has something stable to bucket on.
        Assert.Equal(claimValue, result.Context!.CallerId);
    }

    [Fact]
    public async Task AuthenticatedUserRoute_TokenWithoutUpn_PrefersOidOverSubAndAppId()
    {
        var h = BuildHarness();
        var principal = AppOnlyPrincipal(TenantA,
            ("appid", "cccccccc-0000-0000-0000-000000000009"),
            ("sub", "subject-xyz"),
            ("oid", "aaaaaaaa-0000-0000-0000-00000000000a"));

        var result = await h.Middleware.DecideAsync("GET", "/api/feedback/status", null, principal);

        Assert.True(result.Allowed);
        Assert.Equal("aaaaaaaa-0000-0000-0000-00000000000a", result.Context!.CallerId);
    }

    [Fact]
    public async Task AuthenticatedUserRoute_TokenWithNoIdentifyingClaim_FailsClosedToASharedBucket()
    {
        // Pathological (every real Entra token has a sub): must NOT resolve to empty, because empty
        // means "skip the limiter". A shared bucket is the safe answer.
        var h = BuildHarness();

        var result = await h.Middleware.DecideAsync(
            "GET", "/api/feedback/status", null, AppOnlyPrincipal(TenantA));

        Assert.True(result.Allowed);
        Assert.NotEqual(string.Empty, result.Context!.CallerId);
        Assert.Equal(PolicyEnforcementMiddleware.UnidentifiedCallerBucket, result.Context!.CallerId);
        // Never reuse the log placeholder as a bucket key — that was the original fleet-wide collapse.
        Assert.NotEqual("anonymous", result.Context!.CallerId);
    }

    [Theory]
    [InlineData("POST", "/api/agent/telemetry")]
    [InlineData("GET", "/api/health")]
    [InlineData("GET", "/api/stats/platform")]
    public async Task UnauthenticatedRoute_HasEmptyCallerId(string method, string path)
    {
        // Empty CallerId is the ONLY signal that skips the user limiter — it must mean
        // "no JWT at all", never "authenticated but unnamed".
        var h = BuildHarness();

        var result = await h.Middleware.DecideAsync(method, path, null, principal: null);

        Assert.True(result.Allowed);
        Assert.Equal(string.Empty, result.Context!.CallerId);
    }
}
