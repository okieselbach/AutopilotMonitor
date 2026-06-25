using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Resolved request context populated by PolicyEnforcementMiddleware after authentication and policy evaluation.
/// Eliminates redundant IsGlobalAdminAsync / IsTenantAdminAsync service calls in function handlers.
/// Retrieved via <c>req.GetRequestContext()</c> or <c>context.GetRequestContext()</c>.
/// </summary>
public sealed record RequestContext
{
    internal const string ItemsKey = "RequestContext";

    /// <summary>The user's Azure AD tenant ID (from JWT tid claim).</summary>
    public string TenantId { get; init; } = string.Empty;

    /// <summary>The user's UPN (from JWT upn/preferred_username claim).</summary>
    public string UserPrincipalName { get; init; } = string.Empty;

    /// <summary>True if the user is a Global Admin of the platform (full read + write).</summary>
    public bool IsGlobalAdmin { get; init; }

    /// <summary>
    /// True if the user holds the Global Reader platform role: cross-tenant read visibility equal to a
    /// Global Admin but conferring no platform write/settings capability. Mutually exclusive with
    /// <see cref="IsGlobalAdmin"/>. Semantics are ADDITIVE — this role grants read and never removes a
    /// user's independent tenant-role rights, so a UPN that is both a Global Reader and a Tenant Admin of
    /// its own tenant still passes the tenant-scoped write tiers there (the write evaluators resolve the
    /// tenant role after the GA check). A pure Global Reader (no tenant role) is read-only everywhere.
    /// </summary>
    public bool IsGlobalReader { get; init; }

    /// <summary>
    /// True when the caller has platform-wide (cross-tenant) read scope — Global Admin OR Global Reader.
    /// Use for read/visibility decisions and cross-tenant gating; use <see cref="IsGlobalAdmin"/> for writes.
    /// </summary>
    public bool HasGlobalScope => IsGlobalAdmin || IsGlobalReader;

    /// <summary>
    /// True if the caller accessed THIS request's target tenant via a delegated-admin assignment at the
    /// read tier (the "scoped global" / MSP tier — read of a SUBSET of tenants). Set only when the target
    /// tenant is in the caller's delegated scope and the route is a tenant-scoped read. Mutually exclusive
    /// with <see cref="IsDelegatedAdmin"/> for this request. Phase 1: delegation only ever grants READ.
    /// </summary>
    public bool IsDelegatedReader { get; init; }

    /// <summary>
    /// True if the caller holds the DelegatedAdmin role for this request's target tenant. Reserved for the
    /// later scoped-write phase; in the read-only phase this still confers only read (no write tier admits
    /// a delegated caller), so treat it as read for now and gate writes explicitly when wiring Phase 5.
    /// </summary>
    public bool IsDelegatedAdmin { get; init; }

    /// <summary>True when the caller reached the target via any delegated assignment (reader or admin).</summary>
    public bool IsDelegated => IsDelegatedReader || IsDelegatedAdmin;

    /// <summary>
    /// True when the caller can see a fleet of tenants — full platform scope (GA/Reader) OR a delegated
    /// subset. Visibility/nav helper; does NOT itself authorize a specific tenant (the middleware gates that).
    /// </summary>
    public bool HasFleetScope => HasGlobalScope || IsDelegated;

    /// <summary>
    /// The set of tenant IDs (lowercase) the caller is authorized for via delegated assignments, or null
    /// when the caller is not a delegated admin (or full platform scope, which is unbounded = "all tenants").
    /// Populated on cross-tenant delegated reads; consumed by cross-tenant aggregation to bound the fan-out
    /// to the delegated subset (Phase 2). Null ⇒ no delegated bound applies.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedTenantIds { get; init; }

    /// <summary>True if the user is a Tenant Admin of their own tenant.</summary>
    public bool IsTenantAdmin { get; init; }

    /// <summary>The resolved role string (e.g. "GlobalAdmin", "Admin", "Operator", "Viewer").</summary>
    public string UserRole { get; init; } = string.Empty;

    /// <summary>
    /// The validated target tenant ID for this request.
    /// For RouteParam routes: the {tenantId} from the URL (after cross-tenant validation by middleware).
    /// For all other routes: equals TenantId (the JWT tenant).
    /// Handlers should use this for data access instead of extracting tenantId from route params manually.
    /// </summary>
    public string TargetTenantId { get; init; } = string.Empty;
}

/// <summary>
/// Extension methods for accessing the resolved RequestContext.
/// </summary>
public static class RequestContextExtensions
{
    /// <summary>
    /// Gets the resolved RequestContext from FunctionContext.Items.
    /// Populated by PolicyEnforcementMiddleware for authenticated requests.
    /// Returns an empty context (all defaults) for device/anonymous routes.
    /// </summary>
    public static RequestContext GetRequestContext(this FunctionContext context)
    {
        if (context.Items.TryGetValue(RequestContext.ItemsKey, out var ctx) && ctx is RequestContext requestCtx)
            return requestCtx;
        return new RequestContext();
    }

    /// <summary>
    /// Gets the resolved RequestContext via the HTTP request's FunctionContext.
    /// </summary>
    public static RequestContext GetRequestContext(this HttpRequestData req)
        => req.FunctionContext.GetRequestContext();

    /// <summary>
    /// True if the caller is a tenant member (any Admin/Operator/Viewer role) or a Global Admin.
    /// Only meaningful when the request was evaluated under a role-resolving policy tier
    /// (MemberRead / TenantAdminOrGA / AuthenticatedUserWithRole / …); under plain AuthenticatedUser
    /// the role is not resolved and this returns false.
    /// </summary>
    public static bool IsTenantMemberOrGlobalAdmin(this RequestContext ctx)
        => ctx.HasGlobalScope
            || ctx.UserRole == Constants.TenantRoles.Admin
            || ctx.UserRole == Constants.TenantRoles.Operator
            || ctx.UserRole == Constants.TenantRoles.Viewer;

    /// <summary>Gets the correlation ID for this request (set by CorrelationIdMiddleware).</summary>
    public static string GetCorrelationId(this FunctionContext context)
    {
        if (context.Items.TryGetValue("CorrelationId", out var id) && id is string correlationId)
            return correlationId;
        return string.Empty;
    }
}
