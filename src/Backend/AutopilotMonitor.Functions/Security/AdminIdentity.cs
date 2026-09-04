using System.Security.Claims;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// The complete caller identity that cross-tenant role resolution is keyed on: the mutable, display-grade
/// UPN (the key under which role rows are stored) PLUS the immutable Entra pair that must match the UPN's
/// <see cref="Shared.DataAccess.AdminIdentityBinding"/> — the validated JWT <c>tid</c> and <c>oid</c>.
/// <para>
/// The API deliberately accepts tokens from ANY Entra tenant, and <c>upn</c>/<c>preferred_username</c> can be
/// re-created in a foreign tenant (domain re-registration) or re-assigned inside the home tenant (UPN recycling).
/// A UPN string alone therefore never resolves a GlobalAdmin/GlobalReader or delegated (MSP) role; only this
/// triple does. Construction is total: a principal lacking any of the three claims yields <c>null</c>, which
/// every consumer treats as "no cross-tenant role" (fail-closed).
/// </para>
/// </summary>
/// <param name="Upn">The principal key (lowercase) — the row key of the role tables: a person's UPN, or
/// <c>app:&lt;client-id&gt;</c> for an application principal (<see cref="Shared.Constants.PrincipalKeys"/>).</param>
/// <param name="TenantId">The JWT <c>tid</c> (lowercase) — the caller's home tenant.</param>
/// <param name="ObjectId">The JWT <c>oid</c> (lowercase) — the caller's object id in that tenant (for an
/// application the service principal's object id, which differs per tenant).</param>
public sealed record AdminIdentity(string Upn, string TenantId, string ObjectId)
{
    /// <summary>
    /// True when the key names an application (app-only token). Applications are capped everywhere: never
    /// a platform role, never more than Viewer in a tenant, never more than DelegatedReader on a managed
    /// tenant — the caps live in the respective role services, this flag is what they switch on.
    /// </summary>
    public bool IsApplication => Shared.Constants.PrincipalKeys.IsApplication(Upn);

    /// <summary>Builds the identity from a validated principal, or null when upn, tid or oid is missing.</summary>
    public static AdminIdentity? FromPrincipal(ClaimsPrincipal? principal)
        => principal == null
            ? null
            : Create(principal.GetUserPrincipalName(), principal.GetTenantId(), principal.GetObjectId());

    /// <summary>Builds the identity from the middleware-resolved request context, or null when incomplete.</summary>
    public static AdminIdentity? FromRequestContext(RequestContext context)
        => Create(context.UserPrincipalName, context.TenantId, context.ObjectId);

    /// <summary>Normalizing factory: all three parts required (whitespace counts as missing), all lowercased.</summary>
    public static AdminIdentity? Create(string? upn, string? tenantId, string? objectId)
    {
        if (string.IsNullOrWhiteSpace(upn) || string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(objectId))
            return null;
        return new AdminIdentity(upn.ToLowerInvariant(), tenantId.ToLowerInvariant(), objectId.ToLowerInvariant());
    }
}
