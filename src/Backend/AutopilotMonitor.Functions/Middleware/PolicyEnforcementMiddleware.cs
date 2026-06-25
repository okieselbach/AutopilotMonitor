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
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly TenantAdminsService _tenantAdminsService;
    private readonly TenantConfigurationService _tenantConfigService;

    public PolicyEnforcementMiddleware(
        ILogger<PolicyEnforcementMiddleware> logger,
        GlobalAdminService globalAdminService,
        DelegatedAdminService delegatedAdminService,
        TenantAdminsService tenantAdminsService,
        TenantConfigurationService tenantConfigService)
    {
        _logger = logger;
        _globalAdminService = globalAdminService;
        _delegatedAdminService = delegatedAdminService;
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
        var queryTenantId = httpContext.Request.Query["tenantId"].FirstOrDefault();

        // Authenticated principal set by AuthenticationMiddleware (null on anonymous/device routes).
        // Read from FunctionContext.Items (reliable in the isolated worker) rather than httpContext.User.
        ClaimsPrincipal? principal = null;
        if (context.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal cp
            && cp.Identity?.IsAuthenticated == true)
        {
            principal = cp;
        }

        // All authorization + RequestContext construction is the transport-agnostic DecideAsync seam.
        // Fail-closed on service errors: return 503 (not 500) so clients can retry.
        PolicyResult result;
        try
        {
            result = await DecideAsync(httpMethod, requestPath, queryTenantId, principal);
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

        if (result.Allowed)
        {
            context.Items[RequestContext.ItemsKey] = result.Context!;
            _logger.LogDebug("[PolicyEnforcement] ALLOW {Method} {Path} user={User} role={Role}",
                httpMethod, requestPath, result.UserIdentifier, result.UserRole);
            await next(context);
            return;
        }

        _logger.LogWarning("[PolicyEnforcement] DENIED {Method} {Path} status={Status} user={User} role={Role} reason={Reason}",
            httpMethod, requestPath, result.StatusCode, result.UserIdentifier, result.UserRole, result.LogReason);
        httpContext.Response.StatusCode = result.StatusCode;
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(new { error = result.ErrorCode, message = result.ErrorMessage });
    }

    /// <summary>
    /// Transport-agnostic authorization decision: catalog lookup → policy evaluation → cross-tenant
    /// enforcement → RequestContext construction. Returns an allow (carrying the resolved RequestContext)
    /// or a deny (carrying the HTTP status + error payload). Extracted from <see cref="Invoke"/> so the
    /// full decision — including the additive tenant-role resolution that drives RequestContext.IsTenantAdmin —
    /// is unit-testable without faking the worker's HttpContext feature. <see cref="Invoke"/> is a thin
    /// adapter that reads the request, calls this, and writes the response.
    /// </summary>
    internal async Task<PolicyResult> DecideAsync(
        string httpMethod, string requestPath, string? queryTenantId, ClaimsPrincipal? principal)
    {
        var catalogEntry = EndpointAccessPolicyCatalog.FindPolicy(httpMethod, requestPath);
        if (catalogEntry == null)
        {
            // Fail-closed: unregistered route → 403
            _logger.LogError("[PolicyEnforcement] BLOCKED unregistered route: {Method} {Path} — not in catalog (fail-closed)",
                httpMethod, requestPath);
            return PolicyResult.Deny((int)HttpStatusCode.Forbidden, "Forbidden", "Access denied.", "anonymous", "N/A", "Unregistered");
        }

        var decision = await EvaluateCatalogPolicyAsync(principal, catalogEntry);

        var jwtTenantId = principal?.GetTenantId() ?? string.Empty;
        var upn = principal?.GetUserPrincipalName();
        var isGlobalAdmin = decision.IsAllowed && decision.UserRole == Constants.GlobalRoles.GlobalAdmin;
        var isGlobalReader = decision.IsAllowed && decision.UserRole == Constants.GlobalRoles.GlobalReader;
        // Platform-wide read scope (GA or Reader) governs cross-tenant visibility; write power is GA only.
        var hasGlobalScope = isGlobalAdmin || isGlobalReader;

        // Resolve the target tenant for tenant-scoped routes ({tenantId} in path, or ?tenantId= query).
        // crossTenant = the route names a tenant OTHER than the caller's JWT tenant.
        var (targetTenantId, namedTarget) = ResolveTarget(catalogEntry, requestPath, queryTenantId, jwtTenantId);
        var crossTenant = namedTarget != null
            && !string.Equals(namedTarget, jwtTenantId, StringComparison.OrdinalIgnoreCase);

        // Delegated ("scoped global" / MSP) resolution. A delegated admin reaches a SUBSET of tenants —
        // ONLY on tenant-scoped read routes where the named target is in its scope. We resolve lazily:
        // skip when the caller already has full platform scope, is anonymous, or the request is not a
        // cross-tenant scoped route (own-tenant + non-scoped routes never need a delegated grant). This
        // keeps the common authenticated path free of the extra table read.
        string? delegatedRole = null;
        IReadOnlyCollection<string>? allowedTenantIds = null;
        var isScopedRoute = catalogEntry.TenantScoping is TenantScoping.RouteParam or TenantScoping.QueryParam;
        if (!hasGlobalScope && !string.IsNullOrEmpty(upn) && isScopedRoute && crossTenant)
        {
            var scope = await _delegatedAdminService.GetScopeAsync(upn);
            delegatedRole = scope.RoleFor(namedTarget);
            if (delegatedRole != null)
                allowedTenantIds = scope.TenantIds;
        }
        // A delegated grant only applies to the READ tiers — never to write tiers (TenantAdminOrGA,
        // GlobalAdminOnly, …). This is the single fact that keeps delegation read-only in this phase and
        // prevents a delegated READER from crossing into a write on a tenant they merely read.
        var delegatedGrantsRead = delegatedRole != null && IsDelegatedReadTier(catalogEntry.Policy);

        // Policy-tier admission. If the evaluator denied a delegated-only caller (no own-tenant membership,
        // no platform role) but they ARE a delegated reader of this read route's target, rescue the denial.
        if (!decision.IsAllowed)
        {
            if (delegatedGrantsRead)
            {
                decision = CatalogDecisionResult.Allow(decision.UserIdentifier, Constants.DelegatedRoles.DelegatedReader, "DelegatedScope");
            }
            else
            {
                var status = decision.Reason is "NoJWT" or "MissingClaims"
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.Forbidden;
                var (code, message) = status == HttpStatusCode.Unauthorized
                    ? ("AuthenticationRequired", "Authentication required. Please provide a valid JWT token.")
                    : ("InsufficientPermissions", "Access denied. You do not have permission to access this resource.");
                return PolicyResult.Deny((int)status, code, message, decision.UserIdentifier, decision.UserRole, decision.Reason);
            }
        }

        // Cross-tenant enforcement: a caller may reach a tenant other than its own ONLY with full platform
        // scope OR a delegated READ grant for that exact target. Applies to RouteParam/QueryParam routes.
        if (crossTenant && !hasGlobalScope && !delegatedGrantsRead)
        {
            _logger.LogWarning("[PolicyEnforcement] BLOCKED cross-tenant access: user={User} jwtTenant={JwtTenant} target={Target} scoping={Scoping} path={Path}",
                decision.UserIdentifier, jwtTenantId, namedTarget, catalogEntry.TenantScoping, requestPath);
            return PolicyResult.Deny((int)HttpStatusCode.Forbidden, "CrossTenantAccessDenied",
                "Access denied. You can only access your own tenant's resources.",
                decision.UserIdentifier, decision.UserRole, "CrossTenant");
        }

        // Merge delegated state from the two delegated admission paths:
        //  • scoped-route (RouteParam/QueryParam): a single-tenant cross-tenant read — delegatedRole +
        //    allowedTenantIds were set above.
        //  • GlobalReadOrDelegatedSubset tier (aggregate, no single target): the evaluator carried the
        //    managed tenant set on the decision instead. Either way the caller is a delegated reader here.
        var admittedViaDelegatedSubset = decision.AllowedTenantIds != null;
        var effectiveAllowedTenantIds = allowedTenantIds ?? decision.AllowedTenantIds;

        var requestContext = new RequestContext
        {
            TenantId = jwtTenantId,
            TargetTenantId = targetTenantId,
            UserPrincipalName = decision.UserIdentifier,
            IsGlobalAdmin = isGlobalAdmin,
            IsGlobalReader = isGlobalReader,
            // Delegated flags reflect the caller's role for THIS request (null unless a delegated grant
            // applied — either a single-tenant scoped read or the bounded-subset aggregate tier). Read only:
            // no write tier admits a delegated caller.
            IsDelegatedReader = delegatedRole == Constants.DelegatedRoles.DelegatedReader || admittedViaDelegatedSubset,
            IsDelegatedAdmin = delegatedRole == Constants.DelegatedRoles.DelegatedAdmin,
            AllowedTenantIds = effectiveAllowedTenantIds,
            // Additive: a caller's tenant-admin status is its OWN-TENANT role, independent of any
            // platform role. Evaluators that admit via a platform-role branch still surface the
            // resolved tenant role on decision.TenantRole; pure-tenant branches leave it null and
            // carry the role in UserRole — so fall back to UserRole when TenantRole is unset.
            IsTenantAdmin = (decision.TenantRole ?? decision.UserRole) == Constants.TenantRoles.Admin,
            UserRole = decision.UserRole
        };

        return PolicyResult.Allow(requestContext, decision.UserIdentifier, decision.UserRole);
    }

    /// <summary>
    /// Resolves the request's target tenant. Returns the effective TargetTenantId (defaults to the JWT
    /// tenant) and the explicitly-named tenant from the route/query (null when the route does not name one,
    /// so the caller can distinguish "no target named" from "target == own tenant"). Pure — no auth.
    /// </summary>
    private static (string targetTenantId, string? namedTarget) ResolveTarget(
        EndpointPolicyEntry catalogEntry, string requestPath, string? queryTenantId, string jwtTenantId)
    {
        if (catalogEntry.TenantScoping == TenantScoping.RouteParam)
        {
            var normalizedPath = requestPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                ? requestPath.Substring(5)
                : requestPath;
            var routeTenantId = catalogEntry.RouteRegex.Match(normalizedPath).Groups["tenantId"].Value;
            if (!string.IsNullOrEmpty(routeTenantId))
                return (routeTenantId, routeTenantId);
        }
        else if (catalogEntry.TenantScoping == TenantScoping.QueryParam && !string.IsNullOrWhiteSpace(queryTenantId))
        {
            return (queryTenantId, queryTenantId);
        }

        return (jwtTenantId, null);
    }

    /// <summary>
    /// The READ policy tiers a delegated (scoped-global / MSP) assignment may satisfy for a tenant in its
    /// scope. Deliberately excludes every write tier (TenantAdminOrGA, BootstrapManagerOrGA, GlobalAdminOnly).
    ///
    /// GlobalReadOrAdmin is included (Phase 2a) BUT a delegated grant only ever fires on a tenant-SCOPED
    /// route (RouteParam / QueryParam) for a target in the caller's allowed set — see the
    /// <c>isScopedRoute &amp;&amp; crossTenant</c> guard in <see cref="DecideAsync"/>. A delegated caller therefore
    /// reaches a GlobalReadOrAdmin endpoint ONLY on its single-tenant (?tenantId=X / {tenantId}) path, never
    /// the no-tenantId AGGREGATE path (which fans out over ALL tenants). The catalog enforces the other half
    /// of this invariant: ONLY GlobalReadOrAdmin routes that strictly restrict to the named tenant carry
    /// QueryParam/RouteParam scoping; aggregate-only routes stay TenantScoping.None and are thus unreachable
    /// by delegated callers. Bounded cross-tenant aggregation for the fleet view is a separate later step.
    /// </summary>
    private static bool IsDelegatedReadTier(EndpointPolicy policy)
        => policy is EndpointPolicy.MemberRead
            or EndpointPolicy.TenantAdminOrGlobalReader
            or EndpointPolicy.GlobalReadOrAdmin;

    private async Task<CatalogDecisionResult> EvaluateCatalogPolicyAsync(
        ClaimsPrincipal? principal, EndpointPolicyEntry entry)
    {
        // principal is the authenticated ClaimsPrincipal (set by AuthenticationMiddleware), or null on
        // anonymous/device routes — resolved by the caller (Invoke / DecideAsync).
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

            case EndpointPolicy.AuthenticatedUserWithRole:
                return await EvaluateAuthenticatedUserWithRoleAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.MemberRead:
                return await EvaluateMemberReadAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.TenantAdminOrGlobalReader:
                return await EvaluateTenantAdminOrGlobalReaderAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.TenantAdminOrGA:
                return await EvaluateTenantAdminOrGAAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.BootstrapManagerOrGA:
                return await EvaluateBootstrapManagerOrGAAsync(tenantId, upn, principal, userIdentifier);

            case EndpointPolicy.GlobalReadOrAdmin:
                return await EvaluateGlobalReadOrAdminAsync(upn, userIdentifier);

            case EndpointPolicy.GlobalReadOrDelegatedSubset:
                return await EvaluateGlobalReadOrDelegatedSubsetAsync(upn, userIdentifier);

            case EndpointPolicy.GlobalAdminOnly:
                return await EvaluateGlobalAdminOnlyAsync(upn, userIdentifier);

            default:
                return CatalogDecisionResult.Deny(userIdentifier, "N/A", $"UnknownPolicy:{entry.Policy}");
        }
    }

    /// <summary>
    /// Allows ANY authenticated caller (valid JWT) but still resolves their Global-Admin status and
    /// effective tenant role so the downstream function can make fine-grained, per-resource decisions
    /// (the resolved values flow onto RequestContext.IsGlobalAdmin/IsTenantAdmin/UserRole). A caller
    /// with no special role is admitted as "Authenticated" (role flags stay false) — non-member end
    /// users are intentionally allowed here; the function gates the privileged resources.
    /// </summary>
    private async Task<CatalogDecisionResult> EvaluateAuthenticatedUserWithRoleAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (principal == null)
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "NoJWT");

        // A platform role (GlobalAdmin or read-only GlobalReader) is surfaced as the primary role so
        // per-resource gates inside the function can tell GA from Reader (e.g. cross-tenant SignalR
        // group joins). ADDITIVE: we ALSO resolve the caller's own-tenant role and carry it separately,
        // so a user who is both GlobalReader and TenantAdmin still has IsTenantAdmin=true in the context
        // (admin notification group, admin-audience notifications).
        if (!string.IsNullOrEmpty(upn))
        {
            var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
            if (globalRole != null)
            {
                var tenantRole = !string.IsNullOrEmpty(tenantId)
                    ? (await ResolveEffectiveRoleAsync(tenantId, upn, principal))?.Role
                    : null;
                return CatalogDecisionResult.Allow(userIdentifier, globalRole, "ValidJWT+GlobalScope", tenantRole);
            }
        }

        // Resolve the effective tenant role (may be null for non-members). A resolved role is
        // surfaced so the function can distinguish members from roleless end users; either way
        // the request is allowed at the middleware tier.
        if (!string.IsNullOrEmpty(tenantId) && !string.IsNullOrEmpty(upn))
        {
            var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
            if (role?.Role != null)
                return CatalogDecisionResult.Allow(userIdentifier, role.Role, "ValidJWT+Member", role.Role);
        }

        // Authenticated but not a member and not a GA — still allowed (the function decides which
        // groups, if any, this caller may join).
        return CatalogDecisionResult.Allow(userIdentifier, "Authenticated", "ValidJWT");
    }

    private async Task<CatalogDecisionResult> EvaluateMemberReadAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        // Any platform role (GlobalAdmin or read-only GlobalReader) satisfies a member-read. ADDITIVE:
        // also resolve the own-tenant role and carry it separately so IsTenantAdmin stays correct for a
        // GlobalReader who is also their tenant's admin (drives admin-audience on /api/notifications).
        var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
        if (globalRole != null)
        {
            var tenantRole = (await ResolveEffectiveRoleAsync(tenantId, upn, principal))?.Role;
            return CatalogDecisionResult.Allow(userIdentifier, globalRole, "GlobalScopeBypass", tenantRole);
        }

        var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
        if (role == null)
            return CatalogDecisionResult.Deny(userIdentifier, "NonMember", "NotInTenant");

        // MemberRead allows Admin, Operator, AND Viewer
        return CatalogDecisionResult.Allow(userIdentifier, role.Role, "TenantMember", role.Role);
    }

    /// <summary>
    /// Admin-tier tenant READ: own-tenant Admin, OR any platform role (GlobalAdmin / GlobalReader).
    /// Used on GET routes that expose admin-tier tenant data (e.g. config) so the read-only GlobalReader
    /// gains visibility without write power, while tenant Operators/Viewers remain excluded. ADDITIVE:
    /// when admitted via the platform-role branch the caller's own-tenant role is ALSO resolved and
    /// carried, so a GlobalReader who is also their tenant's Admin keeps IsTenantAdmin=true (config GET
    /// then serves them the FULL config for their own tenant, redacting only the read-only-reader view).
    /// </summary>
    private async Task<CatalogDecisionResult> EvaluateTenantAdminOrGlobalReaderAsync(
        string? tenantId, string? upn, ClaimsPrincipal? principal, string userIdentifier)
    {
        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
        if (globalRole != null)
        {
            var tenantRole = (await ResolveEffectiveRoleAsync(tenantId, upn, principal))?.Role;
            return CatalogDecisionResult.Allow(userIdentifier, globalRole, "GlobalScopeBypass", tenantRole);
        }

        var role = await ResolveEffectiveRoleAsync(tenantId, upn, principal);
        if (role?.Role == Constants.TenantRoles.Admin)
            return CatalogDecisionResult.Allow(userIdentifier, Constants.TenantRoles.Admin, "TenantAdmin", Constants.TenantRoles.Admin);

        return CatalogDecisionResult.Deny(userIdentifier, role?.Role ?? "NonMember", "NotAdminOrGlobalScope");
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

        // Side-effect-free read: TryGetConfigurationAsync does NOT persist a default row for a missing
        // tenant (GetConfigurationAsync would). Authorization role resolution must never create config as
        // a side effect — otherwise an external delegated/MSP user whose own home tenant is not onboarded
        // would get a phantom TenantConfiguration row written on their first cross-tenant read. A missing
        // config simply means EntraAppRolesEnabled = false (the default), so the role result is unchanged.
        var (config, _) = await _tenantConfigService.TryGetConfigurationAsync(tenantId);
        return EntraAppRoleResolver.Resolve(state, tableRole, principal.GetAppRoles(), config.EntraAppRolesEnabled);
    }

    /// <summary>
    /// Platform-wide cross-tenant READ: admits GlobalAdmin and the read-only GlobalReader. The
    /// resolved role string flows onto RequestContext so the function still distinguishes GA from
    /// Reader (e.g. for any in-function write gate).
    /// </summary>
    private async Task<CatalogDecisionResult> EvaluateGlobalReadOrAdminAsync(
        string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
        if (globalRole != null)
            return CatalogDecisionResult.Allow(userIdentifier, globalRole, "GlobalScope");

        return CatalogDecisionResult.Deny(userIdentifier, "NonGlobal", "NotGlobalScope");
    }

    /// <summary>
    /// Cross-tenant READ that is BOUNDABLE to a subset. A Global Admin / Global Reader is admitted unbounded
    /// (no AllowedTenantIds — the handler sees all tenants). A delegated ("MSP") admin with a non-empty scope
    /// is admitted too, but carries its managed tenant set on the result so the middleware can publish it as
    /// RequestContext.AllowedTenantIds; the HANDLER is then responsible for restricting the aggregate
    /// response to that set. A pure delegated reader of nothing, or any non-global non-delegated caller, is
    /// denied. Used by aggregate endpoints whose body can be filtered per tenant (e.g. config/all).
    /// </summary>
    private async Task<CatalogDecisionResult> EvaluateGlobalReadOrDelegatedSubsetAsync(
        string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        var globalRole = await _globalAdminService.GetGlobalRoleAsync(upn);
        if (globalRole != null)
            return CatalogDecisionResult.Allow(userIdentifier, globalRole, "GlobalScope");

        var scope = await _delegatedAdminService.GetScopeAsync(upn);
        if (!scope.IsEmpty)
            return CatalogDecisionResult.Allow(
                userIdentifier, Constants.DelegatedRoles.DelegatedReader, "DelegatedSubset",
                allowedTenantIds: scope.TenantIds);

        return CatalogDecisionResult.Deny(userIdentifier, "NonGlobal", "NotGlobalOrDelegated");
    }

    private async Task<CatalogDecisionResult> EvaluateGlobalAdminOnlyAsync(
        string? upn, string userIdentifier)
    {
        if (string.IsNullOrEmpty(upn))
            return CatalogDecisionResult.Deny(userIdentifier, "N/A", "MissingClaims");

        if (await _globalAdminService.IsGlobalAdminAsync(upn))
            return CatalogDecisionResult.Allow(userIdentifier, Constants.GlobalRoles.GlobalAdmin, "IsGA");

        return CatalogDecisionResult.Deny(userIdentifier, "NonGA", "NotGlobalAdmin");
    }

    /// <summary>
    /// Outcome of <see cref="DecideAsync"/>: either an allow carrying the resolved
    /// <see cref="RequestContext"/>, or a deny carrying the HTTP status + error payload that
    /// <see cref="Invoke"/> writes. Internal so the middleware decision is unit-testable.
    /// </summary>
    internal sealed class PolicyResult
    {
        public bool Allowed { get; private init; }
        public RequestContext? Context { get; private init; }
        public int StatusCode { get; private init; }
        public string? ErrorCode { get; private init; }
        public string? ErrorMessage { get; private init; }
        public string UserIdentifier { get; private init; } = "anonymous";
        public string UserRole { get; private init; } = "N/A";
        public string LogReason { get; private init; } = "";

        public static PolicyResult Allow(RequestContext context, string user, string role)
            => new() { Allowed = true, Context = context, UserIdentifier = user, UserRole = role, LogReason = "Allow" };

        public static PolicyResult Deny(int statusCode, string errorCode, string errorMessage,
            string user, string role, string logReason)
            => new()
            {
                Allowed = false,
                StatusCode = statusCode,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                UserIdentifier = user,
                UserRole = role,
                LogReason = logReason,
            };
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

        /// <summary>
        /// The caller's independently-resolved tenant role ("Admin"/"Operator"/"Viewer"), when known.
        /// Carried SEPARATELY from <see cref="UserRole"/> so additive semantics survive: a caller admitted
        /// via a platform-role branch (e.g. GlobalReader) still surfaces their own-tenant role here, so
        /// RequestContext.IsTenantAdmin is correct for a user who is both GlobalReader and TenantAdmin.
        /// Null when not resolved (the allow-path then falls back to <see cref="UserRole"/>).
        /// </summary>
        public string? TenantRole { get; private init; }

        /// <summary>
        /// For the GlobalReadOrDelegatedSubset tier: a delegated caller's managed tenant set, which the
        /// middleware publishes as RequestContext.AllowedTenantIds so the handler can bound its aggregate
        /// response. Null for unbounded (Global Admin / Reader) admits and all other tiers.
        /// </summary>
        public IReadOnlyCollection<string>? AllowedTenantIds { get; private init; }

        public static CatalogDecisionResult Allow(string user, string role, string reason, string? tenantRole = null,
            IReadOnlyCollection<string>? allowedTenantIds = null)
            => new() { IsAllowed = true, UserIdentifier = user, UserRole = role, Reason = reason, TenantRole = tenantRole, AllowedTenantIds = allowedTenantIds };

        public static CatalogDecisionResult Deny(string user, string role, string reason)
            => new() { IsAllowed = false, UserIdentifier = user, UserRole = role, Reason = reason };
    }
}
