using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests that the centralized cross-tenant enforcement logic in EndpointAccessPolicyCatalog
/// correctly extracts {tenantId} from route-param routes and enables the middleware to block
/// cross-tenant access for non-Global-Admin users.
///
/// These tests validate the building blocks that PolicyEnforcementMiddleware uses:
/// 1. Routes with {tenantId} have TenantScoping.RouteParam
/// 2. The regex named capture group correctly extracts tenantId from request paths
/// 3. The extracted tenantId can be compared against the JWT tenantId
/// </summary>
public class CrossTenantAccessTests
{
    private const string TenantA = "00000000-0000-0000-0000-aaaaaaaaaaaa";
    private const string TenantB = "00000000-0000-0000-0000-bbbbbbbbbbbb";

    /// <summary>
    /// Simulates the middleware cross-tenant check for RouteParam routes: extract tenantId from route,
    /// compare to JWT tenant, return whether access should be blocked.
    /// </summary>
    private static (bool isBlocked, string? routeTenantId) SimulateCrossTenantCheck(
        string httpMethod, string requestPath, string jwtTenantId, bool isGlobalAdmin)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        if (entry == null)
            return (true, null); // unregistered route → blocked

        if (entry.TenantScoping != TenantScoping.RouteParam)
            return (false, null); // no cross-tenant check needed

        var normalizedPath = requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? requestPath.Substring(5)
            : requestPath;
        var match = entry.RouteRegex.Match(normalizedPath);
        var routeTenantId = match.Groups["tenantId"].Value;

        if (string.IsNullOrEmpty(routeTenantId))
            return (false, null); // no tenantId extracted → no check

        if (isGlobalAdmin)
            return (false, routeTenantId); // GA bypass

        var isCrossTenant = !string.Equals(routeTenantId, jwtTenantId, StringComparison.OrdinalIgnoreCase);
        return (isCrossTenant, routeTenantId);
    }

    /// <summary>
    /// Simulates the middleware cross-tenant check for QueryParam routes: read tenantId from query string,
    /// fall back to JWT tenant, compare against JWT tenant.
    /// </summary>
    private static (bool isBlocked, string targetTenantId) SimulateQueryParamCheck(
        string httpMethod, string requestPath, string jwtTenantId, bool isGlobalAdmin, string? queryTenantId)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        if (entry == null)
            return (true, string.Empty);

        if (entry.TenantScoping != TenantScoping.QueryParam)
            return (false, jwtTenantId); // no query param check, defaults to JWT

        var targetTenantId = string.IsNullOrWhiteSpace(queryTenantId) ? jwtTenantId : queryTenantId;

        if (isGlobalAdmin)
            return (false, targetTenantId);

        var isCrossTenant = !string.Equals(targetTenantId, jwtTenantId, StringComparison.OrdinalIgnoreCase);
        return (isCrossTenant, targetTenantId);
    }

    // ── Same-tenant access (must be allowed) ───────────────────────────

    [Theory]
    [InlineData("GET", "/api/config/{0}")]
    [InlineData("PUT", "/api/config/{0}")]
    [InlineData("POST", "/api/config/{0}")]
    [InlineData("GET", "/api/config/{0}/feature-flags")]
    [InlineData("GET", "/api/tenants/{0}/admins")]
    [InlineData("POST", "/api/tenants/{0}/admins")]
    [InlineData("DELETE", "/api/tenants/{0}/offboard")]
    [InlineData("GET", "/api/config/{0}/autopilot-device-validation/consent-url")]
    [InlineData("GET", "/api/config/{0}/autopilot-device-validation/consent-status")]
    [InlineData("POST", "/api/config/{0}/test-notification")]
    public void SameTenant_NonGA_IsAllowed(string httpMethod, string pathTemplate)
    {
        var path = string.Format(pathTemplate, TenantA);
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck(httpMethod, path, TenantA, isGlobalAdmin: false);

        Assert.False(isBlocked, $"Same-tenant access should be allowed: {httpMethod} {path}");
        Assert.Equal(TenantA, routeTenantId);
    }

    // ── Cross-tenant access by non-GA (must be blocked) ────────────────

    [Theory]
    [InlineData("GET", "/api/config/{0}")]
    [InlineData("PUT", "/api/config/{0}")]
    [InlineData("POST", "/api/config/{0}")]
    [InlineData("GET", "/api/config/{0}/feature-flags")]
    [InlineData("GET", "/api/tenants/{0}/admins")]
    [InlineData("POST", "/api/tenants/{0}/admins")]
    [InlineData("DELETE", "/api/tenants/{0}/offboard")]
    [InlineData("GET", "/api/config/{0}/autopilot-device-validation/consent-url")]
    [InlineData("GET", "/api/config/{0}/autopilot-device-validation/consent-status")]
    [InlineData("POST", "/api/config/{0}/test-notification")]
    public void CrossTenant_NonGA_IsBlocked(string httpMethod, string pathTemplate)
    {
        var path = string.Format(pathTemplate, TenantB); // accessing TenantB
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck(httpMethod, path, TenantA, isGlobalAdmin: false);

        Assert.True(isBlocked, $"Cross-tenant access should be blocked: {httpMethod} {path}");
        Assert.Equal(TenantB, routeTenantId);
    }

    // ── Cross-tenant access by Global Admin (must be allowed) ──────────

    [Theory]
    [InlineData("GET", "/api/config/{0}")]
    [InlineData("PUT", "/api/config/{0}")]
    [InlineData("GET", "/api/tenants/{0}/admins")]
    [InlineData("DELETE", "/api/tenants/{0}/offboard")]
    public void CrossTenant_GlobalAdmin_IsAllowed(string httpMethod, string pathTemplate)
    {
        var path = string.Format(pathTemplate, TenantB); // accessing TenantB
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck(httpMethod, path, TenantA, isGlobalAdmin: true);

        Assert.False(isBlocked, $"Global Admin should be allowed cross-tenant access: {httpMethod} {path}");
        Assert.Equal(TenantB, routeTenantId);
    }

    // ── JWT-implicit routes have no cross-tenant check ─────────────────

    [Theory]
    [InlineData("GET", "/api/sessions")]
    [InlineData("GET", "/api/metrics/app")]
    [InlineData("GET", "/api/audit/logs")]
    [InlineData("GET", "/api/rules/gather")]
    public void JwtImplicitRoutes_NoCrossTenantCheck(string httpMethod, string path)
    {
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck(httpMethod, path, TenantA, isGlobalAdmin: false);

        Assert.False(isBlocked, $"JWT-implicit route should have no cross-tenant check: {httpMethod} {path}");
        Assert.Null(routeTenantId);
    }

    // ── Case-insensitive tenant ID comparison ──────────────────────────

    [Fact]
    public void SameTenant_DifferentCase_IsAllowed()
    {
        var upperTenant = TenantA.ToUpperInvariant();
        var (isBlocked, _) = SimulateCrossTenantCheck("GET", $"/api/config/{upperTenant}", TenantA, isGlobalAdmin: false);

        Assert.False(isBlocked, "Tenant ID comparison should be case-insensitive");
    }

    // ── Admin sub-routes with {adminUpn} correctly extract {tenantId} ──

    [Fact]
    public void AdminSubRoutes_ExtractCorrectTenantId()
    {
        var path = $"/api/tenants/{TenantA}/admins/user@contoso.com";
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck("DELETE", path, TenantA, isGlobalAdmin: false);

        Assert.False(isBlocked);
        Assert.Equal(TenantA, routeTenantId);
    }

    [Fact]
    public void AdminSubRoutes_CrossTenant_IsBlocked()
    {
        var path = $"/api/tenants/{TenantB}/admins/user@contoso.com/permissions";
        var (isBlocked, routeTenantId) = SimulateCrossTenantCheck("PATCH", path, TenantA, isGlobalAdmin: false);

        Assert.True(isBlocked);
        Assert.Equal(TenantB, routeTenantId);
    }

    // ── GlobalAdminOnly routes with {tenantId} (defense-in-depth) ──────

    [Theory]
    [InlineData("PATCH", "/api/config/{0}/plan")]
    [InlineData("POST", "/api/preview/whitelist/{0}")]
    [InlineData("DELETE", "/api/preview/whitelist/{0}")]
    [InlineData("GET", "/api/preview/notification-email/{0}")]
    [InlineData("POST", "/api/preview/send-welcome-email/{0}")]
    public void GlobalAdminOnlyRoutes_WithTenantId_HaveRouteParamScoping(string httpMethod, string pathTemplate)
    {
        var path = string.Format(pathTemplate, TenantA);
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, path);

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, entry.Policy);
        Assert.Equal(TenantScoping.RouteParam, entry.TenantScoping);
    }

    // ── QueryParam: no query param → defaults to JWT tenant ───────────

    [Theory]
    [InlineData("GET", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/sessions/abc-123/events")]
    [InlineData("GET", "/api/sessions/abc-123/analysis")]
    [InlineData("GET", "/api/sessions/abc-123/vulnerability-report")]
    [InlineData("GET", "/api/bootstrap/sessions")]
    [InlineData("GET", "/api/diagnostics/download-url")]
    [InlineData("GET", "/api/progress/sessions/abc-123/events")]
    public void QueryParam_NoQueryTenant_DefaultsToJwt(string httpMethod, string path)
    {
        var (isBlocked, targetTenantId) = SimulateQueryParamCheck(httpMethod, path, TenantA, isGlobalAdmin: false, queryTenantId: null);

        Assert.False(isBlocked);
        Assert.Equal(TenantA, targetTenantId);
    }

    // ── QueryParam: same tenant in query → allowed ────────────────────

    [Theory]
    [InlineData("GET", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/sessions/abc-123/events")]
    [InlineData("GET", "/api/bootstrap/sessions")]
    [InlineData("GET", "/api/diagnostics/download-url")]
    [InlineData("GET", "/api/progress/sessions/abc-123/events")]
    public void QueryParam_SameTenant_IsAllowed(string httpMethod, string path)
    {
        var (isBlocked, targetTenantId) = SimulateQueryParamCheck(httpMethod, path, TenantA, isGlobalAdmin: false, queryTenantId: TenantA);

        Assert.False(isBlocked);
        Assert.Equal(TenantA, targetTenantId);
    }

    // ── QueryParam: cross-tenant by non-GA → blocked ──────────────────

    [Theory]
    [InlineData("GET", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/sessions/abc-123/events")]
    [InlineData("GET", "/api/sessions/abc-123/analysis")]
    [InlineData("GET", "/api/sessions/abc-123/vulnerability-report")]
    [InlineData("DELETE", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/bootstrap/sessions")]
    [InlineData("DELETE", "/api/bootstrap/sessions/CODE123")]
    [InlineData("GET", "/api/diagnostics/download-url")]
    [InlineData("GET", "/api/progress/sessions/abc-123/events")]
    public void QueryParam_CrossTenant_NonGA_IsBlocked(string httpMethod, string path)
    {
        var (isBlocked, targetTenantId) = SimulateQueryParamCheck(httpMethod, path, TenantA, isGlobalAdmin: false, queryTenantId: TenantB);

        Assert.True(isBlocked, $"Cross-tenant query param should be blocked: {httpMethod} {path}");
        Assert.Equal(TenantB, targetTenantId);
    }

    // ── QueryParam: cross-tenant by GA → allowed ──────────────────────

    [Theory]
    [InlineData("GET", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/sessions/abc-123/events")]
    [InlineData("DELETE", "/api/sessions/abc-123")]
    [InlineData("GET", "/api/bootstrap/sessions")]
    [InlineData("GET", "/api/diagnostics/download-url")]
    [InlineData("GET", "/api/progress/sessions/abc-123/events")]
    public void QueryParam_CrossTenant_GlobalAdmin_IsAllowed(string httpMethod, string path)
    {
        var (isBlocked, targetTenantId) = SimulateQueryParamCheck(httpMethod, path, TenantA, isGlobalAdmin: true, queryTenantId: TenantB);

        Assert.False(isBlocked);
        Assert.Equal(TenantB, targetTenantId);
    }

    // ── QueryParam routes have correct scoping ────────────────────────

    [Theory]
    [InlineData("GET", "sessions/{sessionId}")]
    [InlineData("GET", "sessions/{sessionId}/events")]
    [InlineData("GET", "sessions/{sessionId}/analysis")]
    [InlineData("GET", "sessions/{sessionId}/vulnerability-report")]
    // Codex-followup F1+F4: DELETE sessions/{sessionId} must carry QueryParam scoping so a
    // Global Admin targeting tenant B from the portal does not silently delete in their own
    // (JWT) tenant A. Lifting this assertion into the theory closes the regression that hid
    // the original finding.
    [InlineData("DELETE", "sessions/{sessionId}")]
    [InlineData("GET", "bootstrap/sessions")]
    [InlineData("POST", "bootstrap/sessions")]
    [InlineData("DELETE", "bootstrap/sessions/{code}")]
    [InlineData("GET", "diagnostics/download-url")]
    [InlineData("GET", "progress/sessions/{sessionId}/events")]
    public void QueryParamRoutes_HaveCorrectScoping(string httpMethod, string routeTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.Entries
            .FirstOrDefault(e => e.HttpMethod == httpMethod.ToUpperInvariant() && e.RouteTemplate == routeTemplate);

        Assert.NotNull(entry);
        Assert.Equal(TenantScoping.QueryParam, entry.TenantScoping);
    }
}
