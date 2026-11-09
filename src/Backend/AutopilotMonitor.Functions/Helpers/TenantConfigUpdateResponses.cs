using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>HTTP answers for a <see cref="TenantConfigurationService.UpdateAsync"/> outcome.</summary>
    internal static class TenantConfigUpdateResponses
    {
        /// <summary>
        /// The response for an outcome that wrote nothing for a reason other than the caller's own
        /// decision; null for Updated and Declined.
        /// </summary>
        internal static async Task<HttpResponseData?> FailureAsync(HttpRequestData req, TenantConfigUpdate update)
        {
            switch (update.Status)
            {
                case TenantConfigUpdateStatus.NotFound:
                    return await req.NotFoundAsync("Tenant not found");
                case TenantConfigUpdateStatus.OffboardingInProgress:
                    // The answer the suspension gate gives once its cache has caught up with the tombstone.
                    return await req.ErrorAsync(HttpStatusCode.Forbidden,
                        Constants.ApiErrorCodes.TenantSuspended, TenantOffboardFunction.OffboardingDisabledReason);
                case TenantConfigUpdateStatus.Conflict:
                    return await req.ErrorAsync(HttpStatusCode.Conflict,
                        Constants.ApiErrorCodes.Conflict, "The tenant configuration was changed concurrently. Retry shortly.");
                default:
                    return null;
            }
        }
    }
}
