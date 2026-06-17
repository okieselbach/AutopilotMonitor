using System.Net;
using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Policy enforcement middleware that evaluates the EndpointAccessPolicyCatalog against each request
/// and blocks requests that don't meet the required policy. Fail-closed: unregistered routes are denied.
///
/// Phase 3 of the auth refactor: replaces both PolicyAuditMiddleware (logging-only) and
/// MemberAuthorizationMiddleware (coarse member check) with catalog-driven enforcement.
///
/// Middleware order: AuthenticationMiddleware → PolicyEnforcementMiddleware → Function
/// </summary>
public class PolicyEnforcementMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<PolicyEnforcementMiddleware> _logger;
    private readonly GlobalAdminService _globalAdminService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly TenantConfigurationService _tenantConfigService;

    public PolicyEnforcementMiddleware(
        ILogger<PolicyEnforcementMiddleware> logger,
        GlobalAdminService globalAdminService,
        TenantAdminsService tenantAdminsService,
        TenantConfigurationService tenantConfigService)
    {
        _logger = logger;
        _globalAdminService = globalAdminService;
        _tenantAdminsService = tenantAdminsService;
        _tenantConfigService = tenantConfigService;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            // Non-HTTP trigger (e.g. timer, queue) — no auth needed
            await next(context);
            return;
        }

        var requestPath = httpContext.Request.Path.Value ?? string.Empty;
        var httpMethod = httpContext.Request.Method;

        // Look up the catalog policy for this route
        var catalogEntry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);

        if (catalogEntry == null)
        {
            // Fail-closed: unregistered route → 403
            _logger.LogError("[PolicyEnforcement] BLOCKED unregistered route: {Method} {Path} — not in catalog (fail-closed)",
                httpMethod, requestPath);
            httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "Forbidden",
                message = "Access denied."
            });
            return;
        }

        // Evaluate the catalog policy for the current user.
        // Fail-closed on service errors: return 503 (not 500) so clients can retry.
        CatalogDecisionResult decision;
        try
        {
            decision = await EvaluateCatalogPolicyAsync(context, catalogEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PolicyEnforcement] Service error evaluating policy for {Method} {Path}", httpMethod, requestPath);
            httpContext.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.Headers["Retry-After"] = "5";
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "ServiceUnavailable",
                message = "Authorization service temporarily unavailable. Please retry."
            });
            return;
        }

        if (decision.IsAllowed)
        {
            // Store resolved context so functions can read IsGlobalAdmin/IsTenantAdmin without re-querying services
            var principal = context.GetUser();
            var jwtTenantId = principal?.GetTenantId() ?? string.Empty;
            var isGlobalAdmin = decision.UserRole == "GlobalAdmin";
            var targetTenantId = jwtTenantId; // default: JWT tenant

            // Cross-tenant enforcement for routes with {tenantId} in the URL
            if (catalogEntry.TenantScoping == TenantScoping.RouteParam)
            {
                var normalizedPath = requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    ? requestPath.Substring(5)
                    : requestPath;
                var match = catalogEntry.RouteRegex.Match(normalizedPath);
                var routeTenantId = match.Groups["tenantId"].Value;

                if (!string.IsNullOrEmpty(routeTenantId))
                {
                    targetTenantId = routeTenantId;

                    if (!isGlobalAdmin && !string.Equals(routeTenantId, jwtTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[PolicyEnforcement] BLOCKED cross-tenant access: user={User} jwtTenant={JwtTenant} routeTenant={RouteTenant} path={Path}",
                            decision.UserIdentifier, jwtTenantId, routeTenantId, requestPath);
                        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        httpContext.Response.ContentType = "application/json";
                        await httpContext.Response.WriteAsJsonAsync(new
                        {
                            error = "CrossTenantAccessDenied",
                            message = "Access denied. You can only access your own tenant's resources."
                        });
                        return;
                    }
                }
            }

            // Cross-tenant enforcement for routes with optional ?tenantId= query parameter
            if (catalogEntry.TenantScoping == TenantScoping.QueryParam)
            {
                var queryTenantId = httpContext.Request.Query["tenantId"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(queryTenantId))
                {
                    targetTenantId = queryTenantId;

                    if (!isGlobalAdmin && !string.Equals(queryTenantId, jwtTenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[PolicyEnforcement] BLOCKED cross-tenant access (query): user={User} jwtTenant={JwtTenant} queryTenant={QueryTenant} path={Path}",
                            decision.UserIdentifier, jwtTenantId, queryTenantId, requestPath);
                        httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                        httpContext.Response.ContentType = "application/json";
                        await httpContext.Response.WriteAsJsonAsync(new
                        {
                            error = "CrossTenantAccessDenied",
                            message = "Access denied. You can only access your own tenant's resources."
                        });
                        return;
                    }
                }
            }

            context.Items[RequestContext.ItemsKey] = new RequestContext
            {
                TenantId = jwtTenantId,
                TargetTenantId = targetTenantId,
                UserPrincipalName = decision.UserIdentifier,
                IsGlobalAdmin = isGlobalAdmin,
                IsTenantAdmin = decision.UserRole == Constants.TenantRoles.Admin,
                UserRole = decision.UserRole
            };

            _logger.LogDebug("[PolicyEnforcement] ALLOW {Method} {Path} policy={Policy} user={User} role={Role}",
                httpMethod, requestPath, catalogEntry.Policy, decision.UserIdentifier, decision.UserRole);
            await next(context);
            return;
        }

        // Denied — determine 401 vs 403
        var statusCode = decision.Reason is "NoJWT" or "MissingClaims"
            ? HttpStatusCode.Unauthorized
            : HttpStatusCode.Forbidden;

        _logger.LogWarning("[PolicyEnforcement] DENIED {Method} {Path} policy={Policy} status={Status} user={User} role={Role} reason={Reason}",
            httpMethod, requestPath, catalogEntry.Policy, (int)statusCode,
            decision.UserIdentifier, decision.UserRole, decision.Reason);

        httpContext.Response.StatusCode = (int)statusCode;
        httpContext.Response.ContentType = "application/json";

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "AuthenticationRequired",
                message = "Authentication required. Please provide a valid JWT token."
            });
        }
        else
        {
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = "InsufficientPermissions",
                message = "Access denied. You do not have permission to access this resource."
            });
        }
    }

    private async Task<CatalogDecisionResult> EvaluateCatalogPolicyAsync(
        FunctionContext context, EndpointPolicyEntry entry)
    {
        // Get the ClaimsPrincipal set by AuthenticationMiddleware (may be null for anonymous routes)
        ClaimsPrincipal? principal = null;
        if (context.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal cp
            && cp.Identity?.IsAuthenticated == true)
        {
            principal = cp;
        }

        var tenantId = principal?.GetTenantId();
        var upn = principal?.GetUserPrincipalName();
        var userIdentifier = upn ?? "anonymous";

        switch (entry.Policy)
        {
            case EndpointPolicy.PublicAnonymous:
            case EndpointPolicy.DeviceOrBootstrapAuth:
                // These are always allowed at the middleware level
                // (device auth is enforced in functions via ValidateSecurityAsync)
                return CatalogDecisionResult.Allow(userIdentifier, "N/A", "PolicyDoesNotRequireJWT");

            case EndpointPolicy.AuthenticatedUser:
                if (principal != null)
                    return CatalogDecisionResult.Allow(userIdentifier, "Authenticated", "ValidJWT");
                return CatalogDecisionResult.Deny(userIdentifier, "N/A", "NoJWT");

            case EndpointPolicy.MemberRead:
                return await EvaluateMemberReadAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.TenantAdminOrGA:
                return await EvaluateTenantAdminOrGAAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.BootstrapManagerOrGA:
                return await EvaluateBootstrapManagerOrGAAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.GlobalAdminOnly:
                return await EvaluateGlobalAdminOnlyAsync(upn, userIdentifier);

            default:
                return CatalogDecisionResult.Deny(userIdentifier, "N/A", $"UnknownPolicy:{entry.Policy}");
        }
    }

    private async Task<CatalogDecisionResult> EvaluateMemberReadAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _globalAdminService.IsGlobalAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GlobalAdmin", "GABypass");

        var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
        if (role == null)
            return CatalogDecisionResult.Deny(userIdentifier, "NonMember", "NotInTenant");

        // MemberRead allows Admin, Operator, AND Viewer
        return CatalogDecisionResult.Allow(userIdentifier, role.Role, "TenantMember");
    }

    private async Task<CatalogDecisionResult> EvaluateTenantAdminOrGAAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _globalAdminService.IsGlobalAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GlobalAdmin", "GABypass");

        var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
        if (role?.Role == Constants.TenantRoles.Admin)
            return CatalogDecisionResult.Allow(userIdentifier, Constants.TenantRoles.Admin, "TenantAdmin");

        return CatalogDecisionResult.Deny(userIdentifier, role?.Role ?? "NonMember", "NotAdminOrGA");
    }

    private async Task<CatalogDecisionResult> EvaluateBootstrapManagerOrGAAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _globalAdminService.IsGlobalAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GlobalAdmin", "GABypass");

        var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
        var canManageBootstrap = role != null &&
            (role.Role == Constants.TenantRoles.Admin ||
             (role.Role == Constants.TenantRoles.Operator && role.CanManageBootstrapTokens));

        if (canManageBootstrap)
            return CatalogDecisionResult.Allow(userIdentifier, "BootstrapManager", "CanManageBootstrap");

        return CatalogDecisionResult.Deny(userIdentifier, role?.Role ?? "NonMember", "NoBootstrapPermission");
    }

    /// <summary>
    /// Resolves the effective tenant member role: the TenantAdmins table entry if present,
    /// otherwise — when the tenant has Entra app-roles enabled — the role derived from the token's
    /// "roles" claim. Table always wins (manual override). The tenant-config lookup is only
    /// performed when there is no table entry, so the common (table member) path stays cheap.
    /// </summary>
    private async Task<MemberRoleInfo?> ResolveEffectiveRoleAsync(
        string tenantId, string upn, ClaimsPrincipal? principal)
    {
        var (state, tableRole) = await _tenantAdminsService.GetTableMembershipAsync(tenantId, upn);

        // Table-first: an enabled row wins, a disabled row is an explicit deny. Both skip the
        // claim path entirely — and avoid the tenant-config lookup. Only when no row exists do
        // we consult the Entra app-role claim (gated by the per-tenant opt-in flag).
        if (state != TableMemberState.NotPresent)
            return EntraAppRoleResolver.Resolve(state, tableRole, appRoles: null, appRolesEnabled: false);

        if (principal == null)
            return null;

        var config = await _tenantConfigService.GetConfigurationAsync(tenantId);
        return EntraAppRoleResolver.Resolve(state, tableRole, principal.GetAppRoles(), config.EntraAppRolesEnabled);
    }

    private async Task<CatalogDecisionResult> EvaluateGlobalAdminOnlyAsync(
        string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _globalAdminService.IsGlobalAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, "GlobalAdmin", "IsGA");

        return CatalogDecisionResult.Deny(userIdentifier, "NonGA", "NotGlobalAdmin");
    }

    /// <summary>
    /// Result of evaluating a catalog policy against the current request context.
    /// </summary>
    private sealed class CatalogDecisionResult
    {
        public bool IsAllowed { get; private init; }
        public string UserIdentifier { get; private init; } = "anonymous";
        public string UserRole { get; private init; } = "N/A";
        public string Reason { get; private init; } = "";

        public static CatalogDecisionResult Allow(string user, string role, string reason)
            => new() { IsAllowed = true, UserIdentifier = user, UserRole = role, Reason = reason };

        public static CatalogDecisionResult Deny(string user, string role, string reason)
            => new() { IsAllowed = false, UserIdentifier = user, UserRole = role, Reason = reason };
    }
}
