using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Apps;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Apps
{
    /// <summary>
    /// GET /api/apps/fleet-context?appName=&amp;source=&amp;appId=&amp;excludeSessionId=&amp;tenantId=
    /// How one app fared in the tenant's other enrollments of the last week: the diagnosis page's
    /// fleet line. MemberRead with the tenant from the query (TenantScoping.QueryParam), so the line
    /// also works in a cross-tenant view. The name is a query value, never a path segment.
    /// </summary>
    public class GetAppFleetContextFunction
    {
        private readonly ILogger<GetAppFleetContextFunction> _logger;
        private readonly AppFleetContextService _fleet;

        public GetAppFleetContextFunction(ILogger<GetAppFleetContextFunction> logger, AppFleetContextService fleet)
        {
            _logger = logger;
            _fleet = fleet;
        }

        [Function("GetAppFleetContext")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "apps/fleet-context")] HttpRequestData req)
        {
            var query = req.Query;
            var appName = query["appName"];
            if (string.IsNullOrWhiteSpace(appName))
                return await req.BadRequestAsync("appName is required");
            if (!AppsAnalyticsHelper.TryParseSourceQueryParam(query["source"], out var source))
                return await req.BadRequestAsync($"source must be one of: {string.Join(", ", AppInstallSources.All)}");
            var excludeSessionId = query["excludeSessionId"];
            if (!string.IsNullOrEmpty(excludeSessionId) && !SecurityValidator.IsValidGuid(excludeSessionId))
                return await req.BadRequestAsync("excludeSessionId must be a valid GUID");

            // Authentication and MemberRead are enforced by PolicyEnforcementMiddleware; a cross-tenant
            // read arrives as TargetTenantId, which the middleware does not check for shape.
            var tenantId = req.GetRequestContext().TargetTenantId;
            if (!SecurityValidator.IsValidGuid(tenantId))
                return await req.BadRequestAsync("tenantId must be a valid GUID");

            try
            {
                var appId = query["appId"];
                return await req.OkAsync(await _fleet.GetAsync(
                    tenantId, appName, source, string.IsNullOrWhiteSpace(appId) ? null : appId.Trim(), excludeSessionId));
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetAppFleetContext");
            }
        }
    }
}
