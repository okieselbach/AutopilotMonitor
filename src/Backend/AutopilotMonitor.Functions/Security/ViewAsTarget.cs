using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Functions.Security;

/// <summary>
/// "View as tenant": a Global Admin reads one tenant's portal exactly as a member of that tenant sees it,
/// read-only. The portal tab sends these headers on every API call; <c>PolicyEnforcementMiddleware</c>
/// admits the request only for a Global Admin, refuses every write, and builds the request context of the
/// emulated member.
/// </summary>
public static class ViewAsHeaders
{
    public const string Tenant = "X-View-As-Tenant";
    public const string Role = "X-View-As-Role";
}

/// <summary>A parsed view-as request: the tenant to view (lowercase GUID) and the tenant role to emulate.</summary>
public sealed record ViewAsTarget(string TenantId, string Role)
{
    /// <summary>Headers that are present but unusable: not a GUID, or not one of the three tenant roles.</summary>
    public static readonly ViewAsTarget Invalid = new(string.Empty, string.Empty);

    /// <summary>The one answer to anything that would change state in view-as mode (portal error text).</summary>
    public const string ReadOnlyMessage = "Read-only view: changes are disabled while you view this tenant.";

    public bool IsValid => TenantId.Length > 0;

    /// <summary>
    /// Null when no tenant header is sent (an ordinary request); <see cref="Invalid"/> when it is malformed.
    /// The role defaults to Admin.
    /// </summary>
    public static ViewAsTarget? FromHeaders(string? tenantHeader, string? roleHeader)
    {
        if (string.IsNullOrWhiteSpace(tenantHeader))
            return null;
        if (!Guid.TryParse(tenantHeader.Trim(), out var tenant) || tenant == Guid.Empty)
            return Invalid;

        var role = NormalizeRole(roleHeader);
        return role == null ? Invalid : new ViewAsTarget(tenant.ToString("D"), role);
    }

    private static string? NormalizeRole(string? roleHeader)
    {
        if (string.IsNullOrWhiteSpace(roleHeader))
            return Constants.TenantRoles.Admin;

        var value = roleHeader.Trim();
        foreach (var role in new[] { Constants.TenantRoles.Admin, Constants.TenantRoles.Operator, Constants.TenantRoles.Viewer })
        {
            if (string.Equals(value, role, StringComparison.OrdinalIgnoreCase))
                return role;
        }
        return null;
    }
}
