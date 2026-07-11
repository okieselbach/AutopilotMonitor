using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGlobalGeographicLocationSessionsFunction
    {
        private readonly ILogger<GetGlobalGeographicLocationSessionsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalGeographicLocationSessionsFunction(
            ILogger<GetGlobalGeographicLocationSessionsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalGeographicLocationSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/geographic/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var locationKey = query["locationKey"];
                var country = query["country"];
                // Accept either the legacy opaque locationKey (web UI pairs it with groupBy)
                // or structured country/region/city filters matched against actual Geo*
                // fields — robust to key formatting and partial (country-only) drilldowns.
                if (string.IsNullOrEmpty(locationKey) && string.IsNullOrEmpty(country))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "locationKey or country parameter is required" });
                    return badRequest;
                }

                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;

                var groupBy = query["groupBy"] ?? "city";
                var full = string.Equals(query["full"], "1", StringComparison.Ordinal);
                // Optional tenantId filter so a GA can scope a single tenant's sessions
                // for a location without leaving the cross-tenant endpoint.
                var filterTenantId = query["tenantId"];

                _logger.LogInformation("Fetching global sessions for location '{LocationKey}' ({Days}d, groupBy={GroupBy}, country={Country}, full={Full}, tenantFilter={Tenant}) (User: {UserEmail})",
                    locationKey ?? "(none)", days, groupBy, country ?? "(none)", full, filterTenantId ?? "(none)", userEmail);

                // Sessions need the full row (LocationSessionRow returns nearly every column); the
                // app scan only feeds the per-session DO aggregate, so it uses the geo projection.
                // Both scans are independent — run them concurrently (same cutoff window).
                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-days);
                var tenantFilter = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                var sessionsTask = _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, now.AddDays(1), tenantFilter);
                var appSummariesTask = _metricsRepo.GetGeoAppInstallSummariesAsync(cutoff, tenantFilter);
                await Task.WhenAll(sessionsTask, appSummariesTask);

                var sessions = await sessionsTask;
                var filtered = !string.IsNullOrEmpty(locationKey)
                    ? GetGeographicLocationSessionsFunction.FilterSessionsByLocation(sessions, locationKey, groupBy)
                    : GetGeographicLocationSessionsFunction.FilterSessionsByFields(sessions, country!, query["region"], query["city"]);

                var rows = GetGeographicLocationSessionsFunction.BuildRows(filtered, await appSummariesTask);

                var response = req.CreateResponse(HttpStatusCode.OK);
                if (full)
                {
                    await response.WriteAsJsonAsync(new { success = true, sessions = rows, totalCount = rows.Count });
                }
                else
                {
                    var lean = rows.Select(GetGeographicLocationSessionsFunction.ToLeanRow).ToList();
                    await response.WriteAsJsonAsync(new { success = true, sessions = lean, totalCount = lean.Count });
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global geographic location sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
