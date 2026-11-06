using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// "View as tenant": a Global Admin reads one tenant's portal as its member, read-only. DecideAsync admits the
/// headers only for a Global Admin, refuses every write, and decides the rest with the SAME evaluators over an
/// emulated member of the viewed tenant — the parity test pins that the emulation answers every read route
/// exactly like a real member of that tenant.
/// </summary>
public partial class PolicyEnforcementMiddlewareTests
{
    private const string GaUpn = "ga@vendor.example";

    private static ViewAsTarget ViewB(string role = Constants.TenantRoles.Admin) => new(TenantB, role);

    /// <summary>A Global Admin homed in TenantA (the default identity binding) and an onboarded TenantB.</summary>
    private static Harness ViewAsHarness()
    {
        var h = BuildHarness();
        h.Repo.Setup(r => r.GetGlobalRoleAsync(GaUpn)).ReturnsAsync(Constants.GlobalRoles.GlobalAdmin);
        h.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantB))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantB });
        return h;
    }

    [Fact]
    public async Task ViewAs_GlobalAdmin_ReadsTheViewedTenantAsItsAdmin()
    {
        var h = ViewAsHarness();

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewB());

        Assert.True(r.Allowed);
        var ctx = r.Context!;
        Assert.Equal(TenantB, ctx.TenantId);
        Assert.Equal(TenantB, ctx.TargetTenantId);
        Assert.False(ctx.IsGlobalAdmin);
        Assert.False(ctx.HasFleetScope);
        Assert.Null(ctx.AllowedTenantIds);
        Assert.Equal(Constants.TenantRoles.Admin, ctx.UserRole);
        Assert.True(ctx.IsTenantAdmin);
        // Identity stays the real caller's: throttling, logs and the SignalR binding key off it.
        Assert.Equal(GaUpn, ctx.UserPrincipalName);
        Assert.Equal(new ViewAsContext(TenantA, Constants.TenantRoles.Admin), ctx.ViewAs);
        Assert.Equal(TenantA, ctx.IdentityTenantId());
    }

    [Theory]
    [InlineData("tenant admin")]
    [InlineData("global reader")]
    [InlineData("delegated admin of the viewed tenant")]
    [InlineData("no role")]
    public async Task ViewAs_IsRefusedToEveryoneButAGlobalAdmin_AndReported(string caller)
    {
        const string upn = "someone@contoso.com";
        var h = ViewAsHarness();
        switch (caller)
        {
            case "tenant admin": h.AsTenantAdmin(TenantA, upn); break;
            case "global reader": h.Repo.Setup(r => r.GetGlobalRoleAsync(upn)).ReturnsAsync(Constants.GlobalRoles.GlobalReader); break;
            case "delegated admin of the viewed tenant": h.AsDelegated(TenantB, Constants.DelegatedRoles.DelegatedAdmin); break;
        }

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, upn), viewAs: ViewB());

        Assert.False(r.Allowed);
        Assert.Equal(403, r.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.ViewAsNotAllowed, r.ErrorCode);
        Assert.True(PolicyEnforcementMiddleware.IsPrivilegedDenial(r, viewAsRequested: true));
    }

    [Fact]
    public async Task ViewAs_ThroughMcpOrAForeignClientApp_IsRefusedEvenForAGlobalAdmin()
    {
        var h = ViewAsHarness();
        var capped = AuthedPrincipal(TenantA, GaUpn);
        ClientAppBinding.MarkCapped(capped, "33333333-3333-3333-3333-333333333333");

        var mcp = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, GaUpn), isMcpClient: true, viewAs: ViewB());
        var foreign = await h.Middleware.DecideAsync("GET", "/api/sessions", null, capped, viewAs: ViewB());

        Assert.Equal(Constants.ApiErrorCodes.ViewAsNotAllowed, mcp.ErrorCode);
        Assert.Equal(Constants.ApiErrorCodes.ViewAsNotAllowed, foreign.ErrorCode);
    }

    [Fact]
    public async Task ViewAs_AdmissionAnswers_ForMalformedHeaders_UnknownTenants_AndAnonymousCallers()
    {
        var h = ViewAsHarness();

        var malformed = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewAsTarget.Invalid);
        var unknown = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, GaUpn),
            viewAs: new ViewAsTarget("33333333-3333-3333-3333-333333333333", Constants.TenantRoles.Admin));
        var anonymous = await h.Middleware.DecideAsync("GET", "/api/sessions", null, principal: null, viewAs: ViewB());

        Assert.Equal((400, Constants.ApiErrorCodes.ViewAsInvalid), (malformed.StatusCode, malformed.ErrorCode));
        Assert.Equal((404, Constants.ApiErrorCodes.ViewAsTenantNotFound), (unknown.StatusCode, unknown.ErrorCode));
        Assert.Equal((401, Constants.ApiErrorCodes.AuthenticationRequired), (anonymous.StatusCode, anonymous.ErrorCode));
    }

    [Fact]
    public async Task ViewAs_RefusesWrites_RunsTheReadsDeclaredOnPostRoutes_AndRefusesTheGetThatStartsAnAction()
    {
        var h = ViewAsHarness();
        var ga = AuthedPrincipal(TenantA, GaUpn);

        // A test send would reach the customer's channels.
        var write = await h.Middleware.DecideAsync("POST", $"/api/config/{TenantB}/test-notification", null, ga, viewAs: ViewB());
        var join = await h.Middleware.DecideAsync("POST", "/api/realtime/groups/join", null, ga, viewAs: ViewB());
        var consentFlow = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}/autopilot-device-validation/consent-url", null, ga, viewAs: ViewB());

        Assert.Equal((403, Constants.ApiErrorCodes.ViewAsReadOnly), (write.StatusCode, write.ErrorCode));
        Assert.Equal(ViewAsTarget.ReadOnlyMessage, write.ErrorMessage);
        Assert.True(join.Allowed);
        Assert.Equal((403, Constants.ApiErrorCodes.ViewAsReadOnly), (consentFlow.StatusCode, consentFlow.ErrorCode));
        Assert.False(PolicyEnforcementMiddleware.IsPrivilegedDenial(write, viewAsRequested: true));
    }

    [Fact]
    public async Task ViewAs_ThePlatformSurface_IsClosedLikeForAMember_AndTheDenialIsNotReported()
    {
        var h = ViewAsHarness();

        var r = await h.Middleware.DecideAsync("GET", "/api/global/raw/access-probe", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewB());

        Assert.False(r.Allowed);
        Assert.Equal((403, Constants.ApiErrorCodes.InsufficientPermissions), (r.StatusCode, r.ErrorCode));
        Assert.False(PolicyEnforcementMiddleware.IsPrivilegedDenial(r, viewAsRequested: true));
        // The same denial without view-as headers is the assume-breach report it always was.
        Assert.True(PolicyEnforcementMiddleware.IsPrivilegedDenial(r));
    }

    [Fact]
    public async Task ViewAs_AnotherTenantsRoute_IsCrossTenantDenied_EvenTheGlobalAdminsHomeTenant()
    {
        var h = ViewAsHarness();

        var r = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantA}", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewB());

        Assert.Equal((403, Constants.ApiErrorCodes.CrossTenantAccessDenied), (r.StatusCode, r.ErrorCode));
    }

    [Fact]
    public async Task ViewAs_ASuspendedTenant_ShowsTheSuspension()
    {
        var h = ViewAsHarness();
        h.ConfigRepo.Setup(r => r.GetTenantConfigurationAsync(TenantB))
            .ReturnsAsync(new TenantConfiguration { TenantId = TenantB, Disabled = true, DisabledReason = "Suspended for review" });

        var r = await h.Middleware.DecideAsync("GET", "/api/sessions", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewB());

        Assert.Equal((403, Constants.ApiErrorCodes.TenantSuspended), (r.StatusCode, r.ErrorCode));
        Assert.Equal("Suspended for review", r.ErrorMessage);
    }

    [Fact]
    public async Task ViewAs_AsViewer_ReadsTheTenantButStaysOutOfTheAdminTier()
    {
        var h = ViewAsHarness();
        var ga = AuthedPrincipal(TenantA, GaUpn);

        var sessions = await h.Middleware.DecideAsync("GET", "/api/sessions", null, ga, viewAs: ViewB(Constants.TenantRoles.Viewer));
        var adminTier = await h.Middleware.DecideAsync("GET", $"/api/config/{TenantB}/notification-channel-health", null, ga,
            viewAs: ViewB(Constants.TenantRoles.Viewer));

        Assert.True(sessions.Allowed);
        Assert.Equal(Constants.TenantRoles.Viewer, sessions.Context!.UserRole);
        Assert.False(sessions.Context.IsTenantAdmin);
        Assert.Equal((403, Constants.ApiErrorCodes.InsufficientPermissions), (adminTier.StatusCode, adminTier.ErrorCode));
    }

    [Fact]
    public async Task ViewAs_RoutesWithoutAJwtTier_IgnoreTheHeaders()
    {
        var h = ViewAsHarness();

        var r = await h.Middleware.DecideAsync("GET", "/api/health", null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewAsTarget.Invalid);

        Assert.True(r.Allowed);
        Assert.Null(r.Context!.ViewAs);
    }

    /// <summary>
    /// The core promise: what the Global Admin sees is what the member sees. For every route that runs in view-as
    /// mode and every tenant role, the emulated decision equals the decision for a real member of that role in the
    /// viewed tenant — admission, status, error code, role, admin flag, scope and tenants.
    /// </summary>
    [Fact]
    public async Task ViewAs_DecidesEveryReadRoute_ExactlyLikeARealMemberOfTheViewedTenant()
    {
        const string memberUpn = "member@tenant-b.example";
        const string memberOid = "bbbbbbbb-2222-2222-2222-bbbbbbbbbbbb";
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var role in new[] { Constants.TenantRoles.Admin, Constants.TenantRoles.Operator, Constants.TenantRoles.Viewer })
        {
            var h = ViewAsHarness();
            h.Repo.Setup(r => r.GetTenantMemberAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new TenantMember { Upn = memberUpn, TenantId = TenantB, Role = role, IsEnabled = true });

            foreach (var entry in EndpointAccessPolicyCatalog.Entries)
            {
                if (entry.ViewAsAccess != ViewAsAccess.Read
                    || entry.Policy is EndpointPolicy.PublicAnonymous or EndpointPolicy.DeviceOrBootstrapAuth)
                    continue;

                var path = "/api/" + Regex.Replace(entry.RouteTemplate, @"\{([^}]+)\}",
                    m => m.Groups[1].Value == "tenantId" ? TenantB : "x1");
                var member = await h.Middleware.DecideAsync(entry.HttpMethod, path, null, AuthedPrincipal(TenantB, memberUpn, memberOid));
                var viewed = await h.Middleware.DecideAsync(entry.HttpMethod, path, null, AuthedPrincipal(TenantA, GaUpn), viewAs: ViewB(role));

                compared++;
                var expected = Describe(member);
                var actual = Describe(viewed);
                if (expected != actual)
                    mismatches.Add($"{role} {entry.HttpMethod} {entry.RouteTemplate}: member={expected} viewAs={actual}");
            }
        }

        Assert.Empty(mismatches);
        Assert.True(compared > 300, $"only {compared} decisions compared — the catalog walk is broken");
    }

    private static string Describe(PolicyEnforcementMiddleware.PolicyResult r)
        => r.Allowed
            ? $"allow role={r.Context!.UserRole} admin={r.Context.IsTenantAdmin} scope={r.Context.HasFleetScope} tenant={r.Context.TenantId} target={r.Context.TargetTenantId}"
            : $"deny {r.StatusCode} {r.ErrorCode}";
}
