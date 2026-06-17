using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;

namespace AutopilotMonitor.Functions.Extensions;

/// <summary>
/// Extension methods for extracting user information from JWT token claims
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Extracts the ClaimsPrincipal from the FunctionContext
    /// </summary>
    public static ClaimsPrincipal? GetUser(this FunctionContext context)
    {
        // Azure Functions Isolated Worker: Try FunctionContext.Items first
        // This is set by AuthenticationMiddleware and is more reliable than httpContext.User
        if (context.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal principal)
        {
            return principal;
        }

        // Fallback to HTTP context (may not work reliably in isolated worker)
        var httpContext = context.GetHttpContext();
        return httpContext?.User;
    }

    /// <summary>
    /// Gets the tenant ID (tid) from the token claims
    /// This is the tenant ID of the user's Azure AD tenant, not your tenant!
    /// Supports both v1.0 and v2.0 token formats
    /// </summary>
    public static string? GetTenantId(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("tid")?.Value
               ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
    }

    /// <summary>
    /// Gets the user's UPN (User Principal Name) from token claims
    /// Falls back to preferred_username if upn is not available
    /// Supports both v1.0 and v2.0 token formats
    /// </summary>
    public static string? GetUserPrincipalName(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("upn")?.Value
               ?? principal.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn")?.Value
               ?? principal.FindFirst("preferred_username")?.Value;
    }

    /// <summary>
    /// Gets the user's display name from token claims
    /// Supports both v1.0 and v2.0 token formats
    /// </summary>
    public static string? GetDisplayName(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("name")?.Value
               ?? principal.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name")?.Value;
    }

    /// <summary>
    /// Gets the user's email address from token claims
    /// Supports both v1.0 and v2.0 token formats
    /// </summary>
    public static string? GetEmail(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("email")?.Value
               ?? principal.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress")?.Value;
    }

    /// <summary>
    /// Gets the user's object ID (oid) from token claims
    /// This is the unique identifier for the user in their Azure AD tenant
    /// Supports both v1.0 and v2.0 token formats
    /// </summary>
    public static string? GetObjectId(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("oid")?.Value
               ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
    }

    /// <summary>
    /// Gets the application (client) ID that the token was issued for
    /// </summary>
    public static string? GetAudience(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("aud")?.Value;
    }

    /// <summary>
    /// Gets the issuer of the token
    /// </summary>
    public static string? GetIssuer(this ClaimsPrincipal principal)
    {
        return principal.FindFirst("iss")?.Value;
    }

    /// <summary>
    /// Checks if the principal has a valid tenant ID claim
    /// </summary>
    public static bool HasTenantId(this ClaimsPrincipal principal)
    {
        var tenantId = principal.GetTenantId();
        return !string.IsNullOrEmpty(tenantId);
    }

    /// <summary>
    /// Gets all Entra ID app-role values assigned to the user (the "roles" claim emitted by the
    /// application's Enterprise App). Returns an empty list when no app roles are present.
    /// Reads both the raw "roles"/"role" claims and the mapped <see cref="ClaimTypes.Role"/> URI
    /// to stay robust regardless of JwtSecurityTokenHandler inbound claim mapping. App roles are
    /// multi-valued; duplicates are removed case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> GetAppRoles(this ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(c => c.Type == "roles" || c.Type == "role" || c.Type == ClaimTypes.Role)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gets all claims as a dictionary for debugging purposes
    /// </summary>
    public static Dictionary<string, string> GetAllClaims(this ClaimsPrincipal principal)
    {
        return principal.Claims
            .GroupBy(c => c.Type)
            .ToDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(c => c.Value))
            );
    }
}
