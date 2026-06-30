using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGeographicLocationSessionsFunction
    {
        private readonly ILogger<GetGeographicLocationSessionsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;

        public GetGeographicLocationSessionsFunction(
            ILogger<GetGeographicLocationSessionsFunction> logger,
            IMaintenanceRepository maintenanceRepo,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGeographicLocationSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/geographic/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var locationKey = query["locationKey"];
                var country = query["country"];
                // Accept either the legacy opaque locationKey (still used by the web UI,
                // which always pairs it with groupBy) or structured country/region/city
                // filters matched against the actual Geo* fields. The structured path is
                // robust to key formatting and partial (country-only) drilldowns.
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
                if (days < 1) days = 1;
                if (days > 365) days = 365;

                var groupBy = query["groupBy"] ?? "city";
                // Default response shape is lean (~15 fields/row) so MCP-driven callers fit
                // many sessions in a single response. UI sets ?full=1 to opt back into the
                // full LocationSessionRow shape.
                var full = string.Equals(query["full"], "1", StringComparison.Ordinal);

                _logger.LogInformation("Fetching sessions for location '{LocationKey}' tenant {TenantId} ({Days}d, groupBy={GroupBy}, country={Country}, full={Full})",
                    locationKey ?? "(none)", tenantId, days, groupBy, country ?? "(none)", full);

                var cutoff = DateTime.UtcNow.AddDays(-days);
                var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(cutoff, DateTime.UtcNow.AddDays(1), tenantId);
                var filtered = !string.IsNullOrEmpty(locationKey)
                    ? FilterSessionsByLocation(sessions, locationKey, groupBy)
                    : FilterSessionsByFields(sessions, country!, query["region"], query["city"]);

                // Same window as the sessions above (cutoff); BuildRows joins apps to those sessions.
                var appSummaries = await _metricsRepo.GetAppInstallSummariesByTenantAsync(tenantId, cutoff);
                var rows = BuildRows(filtered, appSummaries);

                var response = req.CreateResponse(HttpStatusCode.OK);
                if (full)
                {
                    await response.WriteAsJsonAsync(new { success = true, sessions = rows, totalCount = rows.Count });
                }
                else
                {
                    var lean = rows.Select(ToLeanRow).ToList();
                    await response.WriteAsJsonAsync(new { success = true, sessions = lean, totalCount = lean.Count });
                }
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching geographic location sessions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        internal static List<SessionSummary> FilterSessionsByLocation(List<SessionSummary> sessions, string locationKey, string groupBy)
        {
            return sessions
                .Where(s => !string.IsNullOrEmpty(s.GeoCountry))
                .Where(s => GetGeographicMetricsFunction.GetLocationKey(s, groupBy) == locationKey)
                .OrderByDescending(s => s.StartedAt)
                .ToList();
        }

        /// <summary>
        /// Matches sessions against the structured Geo* fields rather than a
        /// reconstructed delimited key. <paramref name="country"/> is required;
        /// <paramref name="region"/> and <paramref name="city"/> are optional
        /// wildcards that progressively narrow the result. Case-insensitive so a
        /// country code or region/city label from get_geographic_metrics resolves
        /// regardless of casing — and so a country-only drilldown returns every
        /// session in that country (the broken case the opaque-key path produced
        /// 0 results for).
        /// </summary>
        internal static List<SessionSummary> FilterSessionsByFields(
            List<SessionSummary> sessions, string country, string? region, string? city)
        {
            return sessions
                .Where(s => !string.IsNullOrEmpty(s.GeoCountry))
                .Where(s => string.Equals(s.GeoCountry, country, StringComparison.OrdinalIgnoreCase))
                .Where(s => string.IsNullOrEmpty(region) || string.Equals(s.GeoRegion, region, StringComparison.OrdinalIgnoreCase))
                .Where(s => string.IsNullOrEmpty(city) || string.Equals(s.GeoCity, city, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.StartedAt)
                .ToList();
        }

        internal static List<LocationSessionRow> BuildRows(
            List<SessionSummary> sessions, List<AppInstallSummary> appSummaries)
        {
            var appsBySession = appSummaries
                .GroupBy(a => a.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<LocationSessionRow>(sessions.Count);
            foreach (var s in sessions)
            {
                var apps = appsBySession.TryGetValue(s.SessionId, out var list) ? list : new List<AppInstallSummary>();
                var agg = DoAggregator.Compute(apps);
                rows.Add(LocationSessionRow.From(s, agg, apps.Count));
            }
            return rows;
        }

        /// <summary>
        /// Lean projection used as the default response shape. Fields chosen to support
        /// the typical MCP triage flow (which session, when, where, how big a deal, was
        /// DO active) while keeping per-row payload an order of magnitude smaller than
        /// the full <see cref="LocationSessionRow"/>. Callers needing the full shape
        /// pass <c>?full=1</c>.
        /// </summary>
        internal static object ToLeanRow(LocationSessionRow r) => new
        {
            sessionId = r.SessionId,
            tenantId = r.TenantId,
            serialNumber = r.SerialNumber,
            deviceName = r.DeviceName,
            manufacturer = r.Manufacturer,
            model = r.Model,
            startedAt = r.StartedAt,
            completedAt = r.CompletedAt,
            status = r.Status,
            failureReason = r.FailureReason,
            durationSeconds = r.DurationSeconds,
            enrollmentType = r.EnrollmentType,
            geoCountry = r.GeoCountry,
            geoCity = r.GeoCity,
            totalAppCount = r.TotalAppCount,
            hasDoTelemetry = r.HasDoTelemetry,
            doPercentPeerCaching = r.DoPercentPeerCaching,
        };
    }
}
