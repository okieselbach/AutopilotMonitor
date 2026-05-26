using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Stamps <c>Cache-Control: no-store</c> + <c>Pragma: no-cache</c> on responses for
/// credential- or identity-bearing endpoints, preventing shared caches, browsers, and
/// HTTP/1.0 proxies from retaining bearer tokens, SAS URLs, or PII session payloads.
/// <para>
/// Allowlist over deny-all: a single centralized place to audit which endpoints are
/// considered sensitive. New credential-returning endpoints must be added here.
/// </para>
/// </summary>
public class NoStoreCacheMiddleware : IFunctionsWorkerMiddleware
{
    private const string CacheControlHeader = "Cache-Control";
    private const string CacheControlValue = "no-store";
    private const string PragmaHeader = "Pragma";
    private const string PragmaValue = "no-cache";

    /// <summary>
    /// Exact-match credential / identity endpoints. Bearer-token returning routes
    /// (bootstrap/validate, agent/upload-url) carry acute risk if cached. Session-
    /// list and raw-table surfaces are listed here for the literal path; their
    /// sub-routes (with {id}) are covered by the prefix table below.
    /// </summary>
    private static readonly string[] _exactRoutes =
    {
        "/api/auth/me",
        "/api/agent/config",
        "/api/agent/upload-url",     // returns long-lived diagnostics SAS URL
        "/api/bootstrap/config",
        // Session list / search / diagnostic-download surfaces — PII session payloads
        "/api/sessions",
        "/api/diagnostics/download-url",       // proxies diagnostics ZIP content
        // GA cross-tenant raw + global PII surfaces
        "/api/raw/sessions",
        "/api/raw/events",
        "/api/raw/events/search",
        "/api/global/sessions",
        "/api/global/distress-reports",
        "/api/global/session-reports",         // covers list; /download-url + /{id}/note via prefix
        // Critical-table backup feature — manifest + job-status responses can carry
        // property values from AdminConfiguration / TenantConfiguration (SAS URLs,
        // webhook secrets, API keys) and must never be cached. Trigger endpoint also
        // here so a returned jobId / statusUrl doesn't end up in proxy cache.
        "/api/global/backups",
        "/api/global/backups/trigger",
    };

    /// <summary>
    /// Prefix-match routes. Used for routes with path parameters where exact match
    /// is impossible. Anything beneath these prefixes is treated as sensitive.
    /// </summary>
    private static readonly string[] _prefixRoutes =
    {
        "/api/bootstrap/validate/",  // /api/bootstrap/validate/{code} — Bearer token!
        "/api/config/",              // /api/config/{tenantId} + sub-routes
        "/api/sessions/",            // /api/sessions/{id} + sub-routes (events, signals, …)
        "/api/search/",              // /api/search/{quick,sessions,sessions-by-event,sessions-by-cve}
        "/api/global/raw/",          // GA cross-tenant raw event/session/table surfaces
        "/api/global/search/",       // GA cross-tenant search results
        "/api/global/session-reports/", // /download-url (SAS) + /{reportId}/note
        "/api/global/backups/",         // covers /{backupId}, /jobs/{jobId} — same secret-bearing rationale
    };

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext != null)
        {
            var path = httpContext.Request.Path.Value ?? string.Empty;
            if (IsSensitive(path))
            {
                // Direct-write before next() — OnStarting() is not reliably triggered
                // in the .NET 8 isolated worker (the host bridges the worker's response,
                // so the hook fires on a shadow object that never reaches the wire).
                // Same pattern used by CorrelationIdMiddleware and TimingAllowOriginMiddleware.
                if (!httpContext.Response.Headers.ContainsKey(CacheControlHeader))
                    httpContext.Response.Headers[CacheControlHeader] = CacheControlValue;
                if (!httpContext.Response.Headers.ContainsKey(PragmaHeader))
                    httpContext.Response.Headers[PragmaHeader] = PragmaValue;
            }
        }

        await next(context);
    }

    internal static bool IsSensitive(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        foreach (var exact in _exactRoutes)
        {
            if (path.Equals(exact, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var prefix in _prefixRoutes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
