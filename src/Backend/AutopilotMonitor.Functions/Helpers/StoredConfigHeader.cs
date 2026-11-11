using Microsoft.AspNetCore.Http;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// The <c>X-Tenant-Config: stored</c> request header. A client sends it for a few minutes after it
/// wrote a tenant configuration, because a write clears only the cache of the instance that served
/// it: the instance that answers reads the stored row first. Caller-supplied, so it only ever costs
/// the caller's own request one read — it grants nothing.
/// </summary>
internal static class StoredConfigHeader
{
    public const string HeaderName = "X-Tenant-Config";
    public const string Stored = "stored";

    /// <summary>True when the request asks for the stored row instead of this instance's cached copy.</summary>
    public static bool IsSet(HttpContext? httpContext)
        => httpContext != null && string.Equals(
            httpContext.Request.Headers[HeaderName].FirstOrDefault(), Stored, StringComparison.OrdinalIgnoreCase);
}
