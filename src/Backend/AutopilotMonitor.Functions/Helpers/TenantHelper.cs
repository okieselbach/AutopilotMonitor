using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Security.Claims;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Helper class for extracting tenant information from authenticated requests
/// </summary>
public static class TenantHelper
{
    /// <summary>
    /// Extracts the tenant ID from the authenticated user's JWT token claims.
    /// Uses the Azure AD tenant ID claim which identifies which customer/organization owns the data.
    /// Supports both v1.0 and v2.0 tokens.
    ///
    /// Normal Users: Can only see sessions with their own tenant ID (from JWT token)
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>The tenant ID from the JWT token</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown when user is not authenticated or tenant ID is missing</exception>
    /// <summary>
    /// Resolves the authenticated ClaimsPrincipal for the request, or null.
    /// Azure Functions Isolated Worker: FunctionContext.Items first (set by
    /// AuthenticationMiddleware, more reliable than httpContext.User), then
    /// the HTTP context as fallback.
    /// </summary>
    private static ClaimsPrincipal? GetAuthenticatedPrincipal(HttpRequestData req)
    {
        if (req.FunctionContext.Items.TryGetValue("ClaimsPrincipal", out var principalObj)
            && principalObj is ClaimsPrincipal principal)
        {
            return principal;
        }

        var httpContext = req.FunctionContext.GetHttpContext();
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            return httpContext.User;
        }

        return null;
    }

    public static string GetTenantId(HttpRequestData req)
    {
        var user = GetAuthenticatedPrincipal(req);

        if (user?.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("User is not authenticated. JWT token required.");
        }

        // Extract tenant ID from JWT token
        // v2.0 tokens: "tid" claim
        // v1.0 tokens: "http://schemas.microsoft.com/identity/claims/tenantid" claim
        var tenantIdClaim = user.FindFirst("tid")?.Value ??
                           user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

        if (string.IsNullOrEmpty(tenantIdClaim))
        {
            throw new UnauthorizedAccessException("Tenant ID (tid) claim not found in token");
        }

        // Return the tenant ID from the JWT token
        return tenantIdClaim;
    }

    /// <summary>
    /// Gets the authenticated user's email or name for audit logging
    /// </summary>
    /// <param name="req">The HTTP request</param>
    /// <returns>User email or name, or "Anonymous" if not authenticated</returns>
    public static string GetUserIdentifier(HttpRequestData req)
    {
        var user = GetAuthenticatedPrincipal(req);

        if (user?.Identity?.IsAuthenticated != true)
        {
            return "Anonymous";
        }

        // Try to get UPN first (Azure AD User Principal Name - most reliable identifier)
        // Then fall back to email, preferred_username, and finally name
        return user.FindFirst("upn")?.Value ??
               user.FindFirst(ClaimTypes.Upn)?.Value ??
               user.FindFirst(ClaimTypes.Email)?.Value ??
               user.FindFirst("preferred_username")?.Value ??
               user.FindFirst(ClaimTypes.Name)?.Value ??
               user.FindFirst("name")?.Value ??
               "Unknown";
    }

    /// <summary>
    /// Gets the caller's human-readable name for attribution (e.g. rule Author):
    /// display name first, then UPN/email/preferred_username. Returns null when
    /// the request carries no user identity (anonymous or app-only token) so the
    /// caller can choose its own fallback.
    /// </summary>
    public static string? GetUserDisplayName(HttpRequestData req)
        => GetUserDisplayName(GetAuthenticatedPrincipal(req));

    /// <summary>
    /// Claim-precedence core of <see cref="GetUserDisplayName(HttpRequestData)"/>,
    /// separated so the precedence is unit-testable without an HttpRequestData.
    /// </summary>
    public static string? GetUserDisplayName(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return user.FindFirst("name")?.Value ??
               user.FindFirst(ClaimTypes.Name)?.Value ??
               user.FindFirst("upn")?.Value ??
               user.FindFirst(ClaimTypes.Upn)?.Value ??
               user.FindFirst(ClaimTypes.Email)?.Value ??
               user.FindFirst("preferred_username")?.Value;
    }
}
