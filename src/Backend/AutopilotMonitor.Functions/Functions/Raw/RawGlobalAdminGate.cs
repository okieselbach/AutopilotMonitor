using System.Net;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    /// <summary>
    /// DEFENSE IN DEPTH for the raw table/log proxies. The catalog (<c>GlobalAdminOnly</c>) and the
    /// policy middleware are the authorization gate; this re-check inside the function body only
    /// guarantees that a regression there (middleware unregistered or reordered, a catalog edit to a
    /// weaker tier) turns into a 403 instead of a secret-bearing dump. It reads the resolved
    /// <see cref="RequestContext"/>: an empty context (middleware never ran) has
    /// <c>IsGlobalAdmin == false</c>, so the gate fails closed by construction.
    /// </summary>
    internal static class RawGlobalAdminGate
    {
        /// <summary>Returns a 403 response to hand back, or null when the caller is a resolved Global Admin.</summary>
        public static async Task<HttpResponseData?> DenyUnlessGlobalAdminAsync(HttpRequestData req, FunctionContext context)
        {
            if (context.GetRequestContext().IsGlobalAdmin)
                return null;

            var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
            await forbidden.WriteAsJsonAsync(new { error = "Forbidden", message = "Global Admin role required." });
            return forbidden;
        }
    }
}
