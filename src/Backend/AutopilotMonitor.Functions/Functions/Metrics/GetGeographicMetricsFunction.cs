using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    public class GetGeographicMetricsFunction
    {
        private readonly ILogger<GetGeographicMetricsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _configService;

        public GetGeographicMetricsFunction(ILogger<GetGeographicMetricsFunction> logger, IMaintenanceRepository maintenanceRepo, IMetricsRepository metricsRepo, TenantConfigurationService configService)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
            _metricsRepo = metricsRepo;
            _configService = configService;
        }

        [Function("GetGeographicMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/geographic")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                _logger.LogInformation("Fetching geographic metrics for tenant {TenantId}", tenantId);

                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var daysParam = query["days"];
                int days = 30;
                if (!string.IsNullOrEmpty(daysParam) && int.TryParse(daysParam, out var parsedDays) && parsedDays > 0)
                    days = parsedDays;
                if (days < 1) days = 1;
                if (days > 365) days = 365;

                var groupBy = query["groupBy"] ?? "city";

                // The three sources are independent — run them concurrently so wall-clock cost is
                // the slowest scan, not the sum. Both scans are column-projected: the aggregation
                // reads only Geo*/status/duration from sessions and join-key/throughput/DO counters
                // from apps (see GeoMetricsSessionProjection / GeoAppInstallProjection).
                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-days);
                var configTask = _configService.GetConfigurationAsync(tenantId);
                var sessionsTask = _maintenanceRepo.GetGeoWindowSessionsAsync(cutoff, now.AddDays(1), tenantId);
                var summariesTask = _metricsRepo.GetGeoAppInstallSummariesAsync(cutoff, tenantId);
                await Task.WhenAll(configTask, sessionsTask, summariesTask);

                var geoEnabled = (await configTask)?.EnableGeoLocation ?? true;
                var sessions = await sessionsTask;
                // The window is pushed server-side; the in-memory Where stays as the exact trim
                // (the OData StartedAt filter is second-granular).
                var summaries = (await summariesTask).Where(s => s.StartedAt >= cutoff).ToList();

                var result = ComputeGeographicMetrics(sessions, summaries, groupBy);
                result.GeoLocationEnabled = geoEnabled;

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(result);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching geographic metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        internal static GeographicMetricsResponse ComputeGeographicMetrics(
            List<SessionSummary> sessions, List<AppInstallSummary> appSummaries, string groupBy)
        {
            // Filter to sessions with geo data
            var geoSessions = sessions.Where(s => !string.IsNullOrEmpty(s.GeoCountry)).ToList();

            // Build app install lookup by session
            var appsBySession = appSummaries
                .GroupBy(a => a.SessionId)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Group sessions by location key
            var groups = geoSessions.GroupBy(s => GetLocationKey(s, groupBy)).ToList();

            // The grouping level decides which structured fields are meaningful.
            // A country-grouped row aggregates many cities, so carrying the first
            // session's GeoCity/GeoRegion would mislabel a nationwide figure with
            // an arbitrary sample city (e.g. "Washington, DC" for all of "US").
            // Blank the finer-than-group fields; keep Loc as a representative
            // point so the map can still position a pin for the group.
            var level = (groupBy ?? "city").ToLowerInvariant();

            var locations = new List<LocationMetrics>();

            foreach (var group in groups)
            {
                var first = group.First();
                var succeeded = group.Where(s => s.Status == SessionStatus.Succeeded).ToList();
                var failed = group.Where(s => s.Status == SessionStatus.Failed).ToList();
                // Success rate is an outcome quota over finished enrollments only
                // (Succeeded + Failed), matching the SLA convention. In-flight sessions
                // (InProgress/Pending/Stalled/AwaitingUser) and Incomplete (terminal,
                // non-failure) must not dilute it — a site mid-rollout would otherwise
                // show a poor rate despite zero actual failures. 0 finished → 0; the
                // frontend renders "—" off Succeeded+Failed instead of a 0% badge.
                var finished = succeeded.Count + failed.Count;

                // Duration stats from succeeded sessions with valid duration
                var durations = succeeded
                    .Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0)
                    .Select(s => s.DurationSeconds!.Value / 60.0)
                    .OrderBy(d => d)
                    .ToList();

                var avgDuration = durations.Count > 0 ? durations.Average() : 0;
                var medianDuration = durations.Count > 0 ? durations[durations.Count / 2] : 0;
                var p95Duration = durations.Count > 0 ? durations[(int)(durations.Count * 0.95)] : 0;

                // App count per session
                var sessionAppCounts = group.Select(s =>
                    appsBySession.ContainsKey(s.SessionId) ? appsBySession[s.SessionId].Count : 0
                ).Where(c => c > 0).ToList();
                var avgAppCount = sessionAppCounts.Count > 0 ? sessionAppCounts.Average() : 0;
                var minutesPerApp = avgAppCount > 0 ? avgDuration / avgAppCount : 0;

                // Network throughput from app install summaries
                var locationApps = group
                    .Where(s => appsBySession.ContainsKey(s.SessionId))
                    .SelectMany(s => appsBySession[s.SessionId])
                    .Where(a => a.DownloadDurationSeconds > 0 && a.DownloadBytes > 0)
                    .ToList();
                var totalBytes = locationApps.Sum(a => a.DownloadBytes);
                var totalDownloadSecs = locationApps.Sum(a => (double)a.DownloadDurationSeconds);
                var avgThroughput = totalDownloadSecs > 0 ? totalBytes / totalDownloadSecs : 0;

                // Delivery Optimization metrics — use ALL apps with DO telemetry, not just those
                // with DownloadDurationSeconds > 0 (small/fast apps have valid DO data but < 1s duration)
                var allLocationApps = group
                    .Where(s => appsBySession.ContainsKey(s.SessionId))
                    .SelectMany(s => appsBySession[s.SessionId])
                    .ToList();
                var doAgg = DoAggregator.Compute(allLocationApps);
                var doSessionCount = group
                    .Where(s => appsBySession.ContainsKey(s.SessionId))
                    .Count(s => appsBySession[s.SessionId].Any(a => a.DoDownloadMode >= 0));

                locations.Add(new LocationMetrics
                {
                    LocationKey = group.Key,
                    Country = first.GeoCountry,
                    Region = level == "country" ? string.Empty : first.GeoRegion,
                    City = (level == "country" || level == "region") ? string.Empty : first.GeoCity,
                    Loc = first.GeoLoc,
                    SessionCount = group.Count(),
                    Succeeded = succeeded.Count,
                    Failed = failed.Count,
                    SuccessRate = finished > 0 ? Math.Round((double)succeeded.Count / finished * 100, 1) : 0,
                    AvgDurationMinutes = Math.Round(avgDuration, 1),
                    MedianDurationMinutes = Math.Round(medianDuration, 1),
                    P95DurationMinutes = Math.Round(p95Duration, 1),
                    AvgAppCount = Math.Round(avgAppCount, 1),
                    MinutesPerApp = Math.Round(minutesPerApp, 2),
                    AvgThroughputBytesPerSec = Math.Round(avgThroughput, 0),
                    TotalDownloadBytes = totalBytes,
                    // Delivery Optimization
                    DoSessionCount = doSessionCount,
                    AvgDoPercentPeerCaching = Math.Round(doAgg.PercentPeerCaching, 1),
                    TotalDoBytesFromPeers = doAgg.BytesFromPeers,
                    TotalDoBytesFromHttp = doAgg.BytesFromHttp,
                    TotalDoBytesFromLanPeers = doAgg.BytesFromLanPeers,
                    TotalDoBytesFromGroupPeers = doAgg.BytesFromGroupPeers,
                    TotalDoBytesFromInternetPeers = doAgg.BytesFromInternetPeers,
                    TotalDoBytesFromLinkLocalPeers = doAgg.BytesFromLinkLocalPeers,
                    TotalDoBytesFromCacheServer = doAgg.BytesFromCacheServer
                });
            }

            // Compute global averages
            var allDurations = locations.Where(l => l.SessionCount >= 3 && l.AvgDurationMinutes > 0)
                .Select(l => l.AvgDurationMinutes).ToList();
            var globalAvgDuration = allDurations.Count > 0 ? allDurations.Average() : 0;
            var globalMedianDuration = allDurations.Count > 0 ? allDurations.OrderBy(d => d).ToList()[allDurations.Count / 2] : 0;
            var globalStdDev = allDurations.Count > 1
                ? Math.Sqrt(allDurations.Sum(d => Math.Pow(d - globalAvgDuration, 2)) / (allDurations.Count - 1))
                : 0;

            var allMinutesPerApp = locations.Where(l => l.MinutesPerApp > 0).Select(l => l.MinutesPerApp).ToList();
            var globalAvgMinutesPerApp = allMinutesPerApp.Count > 0 ? allMinutesPerApp.Average() : 0;
            var globalMedianMinutesPerApp = allMinutesPerApp.Count > 0
                ? allMinutesPerApp.OrderBy(m => m).ToList()[allMinutesPerApp.Count / 2] : 0;

            var allThroughputs = locations.Where(l => l.AvgThroughputBytesPerSec > 0)
                .Select(l => l.AvgThroughputBytesPerSec).ToList();
            var globalAvgThroughput = allThroughputs.Count > 0 ? allThroughputs.Average() : 0;

            // Global DO metrics (weighted by bytes, not simple average)
            var globalDoPeers = locations.Sum(l => l.TotalDoBytesFromPeers);
            var globalDoHttp = locations.Sum(l => l.TotalDoBytesFromHttp);
            var globalDoTotal = globalDoPeers + globalDoHttp;
            var globalDoPct = globalDoTotal > 0 ? (double)globalDoPeers / globalDoTotal * 100 : 0;

            // Compute per-location comparisons and outlier detection
            foreach (var loc in locations)
            {
                // Duration vs global
                loc.DurationVsGlobalPct = globalAvgDuration > 0
                    ? Math.Round((loc.AvgDurationMinutes - globalAvgDuration) / globalAvgDuration * 100, 1) : 0;

                // Throughput vs global
                loc.ThroughputVsGlobalPct = globalAvgThroughput > 0
                    ? Math.Round((loc.AvgThroughputBytesPerSec - globalAvgThroughput) / globalAvgThroughput * 100, 1) : 0;

                // AppLoadScore: normalize minutesPerApp to global median = 100
                loc.AppLoadScore = globalMedianMinutesPerApp > 0
                    ? Math.Round(loc.MinutesPerApp / globalMedianMinutesPerApp * 100, 0) : 0;

                // Outlier detection (minimum 3 sessions)
                if (loc.SessionCount >= 3 && globalStdDev > 0 && loc.AvgDurationMinutes > 0)
                {
                    var zScore = (loc.AvgDurationMinutes - globalAvgDuration) / globalStdDev;
                    loc.IsOutlier = Math.Abs(zScore) > 2;
                    loc.OutlierDirection = zScore > 2 ? "slow" : zScore < -2 ? "fast" : null;
                }
            }

            return new GeographicMetricsResponse
            {
                Success = true,
                Locations = locations.OrderByDescending(l => l.SessionCount).ToList(),
                GlobalAverages = new GlobalAverages
                {
                    AvgDurationMinutes = Math.Round(globalAvgDuration, 1),
                    MedianDurationMinutes = Math.Round(globalMedianDuration, 1),
                    AvgMinutesPerApp = Math.Round(globalAvgMinutesPerApp, 2),
                    AvgThroughputBytesPerSec = Math.Round(globalAvgThroughput, 0),
                    StdDevDurationMinutes = Math.Round(globalStdDev, 1),
                    AvgDoPercentPeerCaching = Math.Round(globalDoPct, 1),
                    TotalDoBytesFromPeers = globalDoPeers,
                    TotalDoBytesFromHttp = globalDoHttp
                },
                ComputedAt = DateTime.UtcNow,
                TotalSessions = sessions.Count,
                LocationsWithData = geoSessions.Count
            };
        }

        internal static string GetLocationKey(SessionSummary session, string groupBy)
        {
            return groupBy.ToLower() switch
            {
                "country" => session.GeoCountry,
                "region" => $"{session.GeoRegion}, {session.GeoCountry}",
                _ => !string.IsNullOrEmpty(session.GeoCity)
                    ? $"{session.GeoCity}, {session.GeoRegion}, {session.GeoCountry}"
                    : $"{session.GeoRegion}, {session.GeoCountry}"
            };
        }
    }
}
