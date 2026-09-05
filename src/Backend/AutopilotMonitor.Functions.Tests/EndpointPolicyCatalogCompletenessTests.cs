using System.Reflection;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Security;
using Microsoft.Azure.Functions.Worker;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Ensures the EndpointAccessPolicyCatalog is a complete and accurate mapping
/// of every HTTP route in the Functions project. Fail-closed: any unregistered
/// route causes a test failure.
/// </summary>
public class EndpointPolicyCatalogCompletenessTests
{
    /// <summary>
    /// Every [HttpTrigger] route + method combination must have a matching catalog entry.
    /// This prevents new endpoints from being deployed without an explicit policy decision.
    /// </summary>
    [Fact]
    public void AllHttpTriggers_HaveMatchingCatalogEntry()
    {
        var assembly = typeof(AuthenticationMiddleware).Assembly;
        var missing = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var triggerParam = method.GetParameters()
                    .FirstOrDefault(p => p.GetCustomAttribute<HttpTriggerAttribute>() != null);

                if (triggerParam == null)
                    continue;

                var trigger = triggerParam.GetCustomAttribute<HttpTriggerAttribute>()!;
                var route = trigger.Route;

                if (string.IsNullOrEmpty(route))
                    continue;

                var httpMethods = trigger.Methods ?? Array.Empty<string>();
                if (httpMethods.Length == 0)
                    httpMethods = new[] { "GET" }; // default if no method specified

                foreach (var httpMethod in httpMethods)
                {
                    var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, $"/api/{route}");
                    if (entry == null)
                    {
                        missing.Add($"{httpMethod.ToUpper()} /api/{route}");
                    }
                }
            }
        }

        Assert.True(missing.Count == 0,
            $"The following HTTP routes are NOT registered in EndpointAccessPolicyCatalog (fail-closed):\n" +
            string.Join("\n", missing.Select(m => $"  - {m}")));
    }

    /// <summary>
    /// Every catalog entry must match at least one actual [HttpTrigger] route.
    /// Prevents stale/orphaned entries that could mask security misconfigurations.
    /// </summary>
    [Fact]
    public void AllCatalogEntries_MatchExistingRoutes()
    {
        var assembly = typeof(AuthenticationMiddleware).Assembly;

        // Collect all actual (method, route) pairs from HttpTrigger attributes
        var actualRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var triggerParam = method.GetParameters()
                    .FirstOrDefault(p => p.GetCustomAttribute<HttpTriggerAttribute>() != null);

                if (triggerParam == null)
                    continue;

                var trigger = triggerParam.GetCustomAttribute<HttpTriggerAttribute>()!;
                var route = trigger.Route;

                if (string.IsNullOrEmpty(route))
                    continue;

                var httpMethods = trigger.Methods ?? Array.Empty<string>();
                if (httpMethods.Length == 0)
                    httpMethods = new[] { "GET" };

                foreach (var httpMethod in httpMethods)
                {
                    actualRoutes.Add($"{httpMethod.ToUpper()}:{route}");
                }
            }
        }

        var orphaned = new List<string>();

        foreach (var entry in EndpointAccessPolicyCatalog.Entries)
        {
            // Check if any actual route matches this catalog entry via FindPolicy
            var hasMatch = actualRoutes.Any(ar =>
            {
                var parts = ar.Split(':', 2);
                var method = parts[0];
                var route = parts[1];
                var found = EndpointAccessPolicyCatalog.FindPolicy(method, $"/api/{route}");
                return found != null && found.RouteTemplate == entry.RouteTemplate && found.HttpMethod == entry.HttpMethod;
            });

            if (!hasMatch)
            {
                orphaned.Add($"{entry.HttpMethod} {entry.RouteTemplate} [{entry.Policy}]");
            }
        }

        Assert.True(orphaned.Count == 0,
            $"The following catalog entries have no matching [HttpTrigger] route (stale entries):\n" +
            string.Join("\n", orphaned.Select(o => $"  - {o}")));
    }

    /// <summary>
    /// Verifies that parameterized route templates correctly match actual request paths.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/sessions/abc-123", "sessions/{sessionId}")]
    [InlineData("GET", "/api/sessions/abc-123/events", "sessions/{sessionId}/events")]
    [InlineData("GET", "/api/sessions/abc-123/analysis", "sessions/{sessionId}/analysis")]
    [InlineData("DELETE", "/api/sessions/abc-123", "sessions/{sessionId}")]
    [InlineData("GET", "/api/bootstrap/validate/ABCD12", "bootstrap/validate/{code}")]
    [InlineData("DELETE", "/api/bootstrap/sessions/MYCODE", "bootstrap/sessions/{code}")]
    [InlineData("GET", "/api/config/00000000-0000-0000-0000-000000000001", "config/{tenantId}")]
    [InlineData("PUT", "/api/rules/gather/rule-1", "rules/gather/{ruleId}")]
    [InlineData("DELETE", "/api/tenants/tid-1/admins/user@contoso.com", "tenants/{tenantId}/admins/{adminUpn}")]
    [InlineData("PATCH", "/api/tenants/tid-1/admins/user@contoso.com/permissions", "tenants/{tenantId}/admins/{adminUpn}/permissions")]
    [InlineData("DELETE", "/api/devices/block/SN123456", "devices/block/{encodedSerialNumber}")]
    [InlineData("DELETE", "/api/versions/block/v1.0.*", "versions/block/{encodedPattern}")]
    [InlineData("PATCH", "/api/global/session-reports/report-1/note", "global/session-reports/{reportId}/note")]
    [InlineData("POST", "/api/rules/analyze/ANALYZE-ID-001/create-from-template", "rules/analyze/{ruleId}/create-from-template")]
    public void ParameterizedRoutes_MatchCorrectly(string httpMethod, string requestPath, string expectedTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);

        Assert.NotNull(entry);
        Assert.Equal(expectedTemplate, entry.RouteTemplate);
    }

    /// <summary>
    /// Routes that differ only by HTTP method should resolve to different policies.
    /// </summary>
    [Fact]
    public void SameRoute_DifferentMethods_ResolveToDifferentPolicies()
    {
        var getConfig = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/config/tenant-1");
        var putConfig = EndpointAccessPolicyCatalog.FindPolicy("PUT", "/api/config/tenant-1");

        Assert.NotNull(getConfig);
        Assert.NotNull(putConfig);
        // GET config read is member-tier (Operators/Viewers get the redacted read-only Settings
        // view; the handler redacts for every non-admin caller); PUT config write stays Admin/GA only.
        Assert.Equal(EndpointPolicy.MemberRead, getConfig.Policy);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, putConfig.Policy);

        var getRules = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/rules/gather");
        var postRules = EndpointAccessPolicyCatalog.FindPolicy("POST", "/api/rules/gather");

        Assert.NotNull(getRules);
        Assert.NotNull(postRules);
        Assert.Equal(EndpointPolicy.MemberRead, getRules.Policy);
        Assert.Equal(EndpointPolicy.TenantAdminOrGA, postRules.Policy);
    }

    /// <summary>
    /// IME log pattern edits write GLOBAL patterns (all tenants) and the handler has no GA
    /// re-gate, so the PUT must stay GlobalAdminOnly — a downgrade to TenantAdminOrGA would let
    /// any customer tenant admin mutate platform-wide patterns via ?global=true.
    /// </summary>
    [Fact]
    public void ImeLogPatternWrites_AreGlobalAdminOnly()
    {
        var putPattern = EndpointAccessPolicyCatalog.FindPolicy("PUT", "/api/rules/ime-log-patterns/IME-001");
        var reseed = EndpointAccessPolicyCatalog.FindPolicy("POST", "/api/rules/ime-log-patterns/reseed");

        Assert.NotNull(putPattern);
        Assert.NotNull(reseed);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, putPattern.Policy);
        Assert.Equal(EndpointPolicy.GlobalAdminOnly, reseed.Policy);
    }

    /// <summary>
    /// The built-in diagnostics catalog + global paths are platform-wide, secret-free data that
    /// every settings viewer needs (what is collected from the tenant's devices). Member tier,
    /// JWT-scoped — an upgrade to a global tier would hide it from tenant admins again.
    /// </summary>
    [Fact]
    public void DiagnosticsPaths_IsMemberReadJwtScoped()
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy("GET", "/api/diagnostics/paths");

        Assert.NotNull(entry);
        Assert.Equal(EndpointPolicy.MemberRead, entry.Policy);
        Assert.Equal(TenantScoping.Jwt, entry.TenantScoping);
    }

    /// <summary>
    /// Unregistered routes must return null (fail-closed).
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/nonexistent")]
    [InlineData("POST", "/api/sessions")]  // GET exists, POST doesn't
    [InlineData("DELETE", "/api/health")]   // GET exists, DELETE doesn't
    public void UnregisteredRoutes_ReturnNull(string httpMethod, string requestPath)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        Assert.Null(entry);
    }

    /// <summary>
    /// Every route template containing {tenantId} must declare TenantScoping.RouteParam.
    /// This prevents new endpoints with {tenantId} from bypassing cross-tenant validation.
    /// </summary>
    [Fact]
    public void RoutesWithTenantIdParam_MustHaveTenantScopingRouteParam()
    {
        var missing = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.RouteTemplate.Contains("{tenantId}")
                     && e.TenantScoping != TenantScoping.RouteParam)
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} [{e.Policy}]")
            .ToList();

        Assert.True(missing.Count == 0,
            "Routes with {tenantId} in template must declare TenantScoping.RouteParam:\n" +
            string.Join("\n", missing.Select(m => $"  - {m}")));
    }

    /// <summary>
    /// Every entry with TenantScoping.RouteParam must have {tenantId} in its route template.
    /// Prevents misattributed scoping declarations.
    /// </summary>
    [Fact]
    public void RouteParamScoping_RequiresTenantIdInTemplate()
    {
        var invalid = EndpointAccessPolicyCatalog.Entries
            .Where(e => e.TenantScoping == TenantScoping.RouteParam
                     && !e.RouteTemplate.Contains("{tenantId}"))
            .Select(e => $"{e.HttpMethod} {e.RouteTemplate} [{e.Policy}]")
            .ToList();

        Assert.True(invalid.Count == 0,
            "Entries with TenantScoping.RouteParam must have {tenantId} in their route template:\n" +
            string.Join("\n", invalid.Select(i => $"  - {i}")));
    }

    /// <summary>
    /// The three global/apps/* routes must be cross-tenant READS — GlobalReadOrAdmin (GA + read-only
    /// GlobalReader). As of Phase 2a they carry TenantScoping.QueryParam: their handlers strictly restrict
    /// to the ?tenantId= named tenant, which is exactly what makes them reachable by a delegated ("MSP")
    /// admin bounded to its allowed tenant set (single-tenant path only; the no-tenantId aggregate path
    /// stays GA/Reader-only). A future accidental downgrade below the global-scope tier (e.g. to MemberRead,
    /// exposing them to tenant members) would be caught here.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/global/apps/list",                       "global/apps/list")]
    [InlineData("GET", "/api/global/apps/Company%20Portal/analytics", "global/apps/{appName}/analytics")]
    [InlineData("GET", "/api/global/apps/Company%20Portal/sessions",  "global/apps/{appName}/sessions")]
    public void GlobalAppsRoutes_AreGlobalReadOrAdmin(string method, string path, string expectedTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);

        Assert.NotNull(entry);
        Assert.Equal(expectedTemplate, entry!.RouteTemplate);
        Assert.Equal(EndpointPolicy.GlobalReadOrAdmin, entry.Policy);
        Assert.Equal(TenantScoping.QueryParam, entry.TenantScoping);
    }

    /// <summary>
    /// SignalR group join/leave MUST be AuthenticatedUserWithRole — NOT MemberRead and NOT plain
    /// AuthenticatedUser. The Progress Portal admits non-member end users, so MemberRead 403s them
    /// (no live updates). Plain AuthenticatedUser would admit them but leaves IsGlobalAdmin/
    /// IsTenantAdmin/UserRole unresolved, breaking the function's per-group gates (GA global-admins,
    /// cross-tenant, admin/member notification groups). Only AuthenticatedUserWithRole satisfies both.
    /// Guards against either accidental downgrade. TenantScoping stays None — the function enforces
    /// the group's tenant against the caller's JWT tenant itself.
    /// </summary>
    [Theory]
    [InlineData("POST", "/api/realtime/groups/join",  "realtime/groups/join")]
    [InlineData("POST", "/api/realtime/groups/leave", "realtime/groups/leave")]
    public void SignalRGroupJoinLeave_AreAuthenticatedUserWithRole(string method, string path, string expectedTemplate)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(method, path);

        Assert.NotNull(entry);
        Assert.Equal(expectedTemplate, entry!.RouteTemplate);
        Assert.Equal(EndpointPolicy.AuthenticatedUserWithRole, entry.Policy);
        Assert.Equal(TenantScoping.None, entry.TenantScoping);
    }

    /// <summary>
    /// REGRESSION GUARD (drift bug 2026-06-22): AuthenticationMiddleware runs BEFORE
    /// PolicyEnforcementMiddleware, so every catalog route whose policy is PublicAnonymous (fully
    /// public) or DeviceOrBootstrapAuth (authenticated in-function via cert/bootstrap token, not
    /// JWT) MUST be JWT-exempt in the middleware — otherwise it is 401'd before its policy can ever
    /// be honored. That is exactly how the ticket-gated diagnostics/download route broke: it was
    /// registered PublicAnonymous in the catalog but missing from the middleware's (former) parallel
    /// allowlist. Deriving the exempt set from the catalog makes the drift structurally impossible;
    /// this test locks the invariant so a future refactor can't reintroduce a hand-kept list.
    /// </summary>
    [Fact]
    public void AnonymousAndDeviceRoutes_AreJwtExemptInAuthMiddleware()
    {
        var notExempt = new List<string>();

        foreach (var entry in EndpointAccessPolicyCatalog.Entries)
        {
            if (entry.Policy is not (EndpointPolicy.PublicAnonymous or EndpointPolicy.DeviceOrBootstrapAuth))
                continue;

            // Build a concrete request path from the template (sample value for any {param}).
            var concrete = "/api/" + Regex.Replace(entry.RouteTemplate, @"\{[^}]+}", "sample");
            if (!AuthenticationMiddleware.SkipsJwtValidation(entry.HttpMethod, concrete))
                notExempt.Add($"{entry.HttpMethod} {entry.RouteTemplate} [{entry.Policy}]");
        }

        Assert.True(notExempt.Count == 0,
            "These PublicAnonymous/DeviceOrBootstrapAuth routes are NOT JWT-exempt in " +
            "AuthenticationMiddleware — they will be 401'd before their policy is honored:\n" +
            string.Join("\n", notExempt.Select(r => $"  - {r}")));
    }

    /// <summary>
    /// Conversely, JWT-gated tiers (MemberRead and up) must NOT be exempt — otherwise the middleware
    /// would wave authenticated traffic through unvalidated. Includes the deliberate contrast pair:
    /// the ticket-gated diagnostics/download IS exempt (its HMAC ticket is the sole authority), but
    /// its JWT sibling diagnostics/download-url (MemberRead) stays gated.
    /// </summary>
    [Theory]
    [InlineData("GET",  "/api/diagnostics/download",      true)]   // PublicAnonymous (ticket-gated)
    [InlineData("GET",  "/api/diagnostics/download-url",  false)]  // MemberRead (JWT)
    [InlineData("POST", "/api/diagnostics/download-ticket", false)]// MemberRead (JWT) — mints the ticket
    [InlineData("GET",  "/api/global/session-reports/download", true)]         // PublicAnonymous (ticket-gated report stream)
    [InlineData("POST", "/api/global/session-reports/download-ticket", false)] // GlobalReadOrAdmin (JWT) — mints the ticket
    [InlineData("GET",  "/api/health",                   true)]   // PublicAnonymous
    [InlineData("POST", "/api/agent/telemetry",          true)]   // DeviceOrBootstrapAuth
    [InlineData("GET",  "/api/bootstrap/validate/ABC123", true)]  // PublicAnonymous (param route)
    [InlineData("GET",  "/api/sessions",                 false)]  // MemberRead
    [InlineData("GET",  "/api/config/00000000-0000-0000-0000-000000000001", false)] // MemberRead
    [InlineData("GET",  "/api/global/sessions",          false)]  // GlobalReadOrAdmin
    [InlineData("GET",  "/api/nonexistent",              false)]  // unregistered → fail-closed
    public void SkipsJwtValidation_MatchesPolicyTier(string method, string path, bool expectedExempt)
    {
        Assert.Equal(expectedExempt, AuthenticationMiddleware.SkipsJwtValidation(method, path));
    }

    /// <summary>
    /// Named capture group for {tenantId} correctly extracts the value from request paths.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/config/00000000-0000-0000-0000-000000000001", "00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/api/tenants/abc-def-123/admins", "abc-def-123")]
    [InlineData("DELETE", "/api/tenants/tid-1/admins/user@contoso.com", "tid-1")]
    public void RouteParamRoutes_ExtractTenantIdFromPath(string httpMethod, string requestPath, string expectedTenantId)
    {
        var entry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        Assert.NotNull(entry);
        Assert.Equal(TenantScoping.RouteParam, entry.TenantScoping);

        var normalizedPath = requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? requestPath.Substring(5)
            : requestPath;
        var match = entry.RouteRegex.Match(normalizedPath);
        Assert.True(match.Success);
        Assert.Equal(expectedTenantId, match.Groups["tenantId"].Value);
    }

    [Fact]
    public void SelfServiceDelegationRoutes_NeverCarryTenantIdParam_AndAreTenantAdminTiers()
    {
        // The managing tenant acts on CUSTOMER tenants (remove, revoke) and a customer accepts an invitation
        // from a FOREIGN tenant: a {tenantId} template would put every such call behind the cross-tenant guard.
        // The tenant is therefore ALWAYS the caller's JWT tenant, and the tiers are the tenant-admin ones.
        var routes = EndpointAccessPolicyCatalog.Entries.Where(e => e.RouteTemplate.StartsWith("delegations/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            Assert.DoesNotContain("{tenantId}", route.RouteTemplate);
            Assert.NotEqual(TenantScoping.RouteParam, route.TenantScoping);
            Assert.NotEqual(TenantScoping.QueryParam, route.TenantScoping);
            Assert.True(route.Policy is EndpointPolicy.TenantAdminOrGlobalReader or EndpointPolicy.TenantAdminOrGA,
                $"{route.HttpMethod} {route.RouteTemplate} must be a tenant-admin tier, was {route.Policy}");
        }
        // Mutations never admit the read-only Global Reader.
        foreach (var route in routes.Where(r => r.HttpMethod != "GET"))
            Assert.Equal(EndpointPolicy.TenantAdminOrGA, route.Policy);
    }
}
