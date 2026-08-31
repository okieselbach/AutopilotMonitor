using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// F1 time-attribution read surfaces (insights spec §F1 "Surfaces", PR3). All three
    /// endpoints serve PRE-COMPUTED rows — the per-session breakdown written at the terminal
    /// transition and the aggregate rows the maintenance sweep maintains. No request-time
    /// derivation: what the calculator computed is what every consumer (web, MCP) sees.
    /// </summary>
    public class GetSessionTimeAttributionFunction
    {
        private readonly ILogger<GetSessionTimeAttributionFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionTimeAttributionFunction(
            ILogger<GetSessionTimeAttributionFunction> logger,
            IMetricsRepository metricsRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// Per-session breakdown for the session-detail attribution lane. A missing row is a
        /// NORMAL outcome (pre-feature session, non-terminal, Incomplete — no wall clock) and
        /// returns success with breakdown=null so the UI simply omits the lane.
        /// </summary>
        [Function("GetSessionTimeAttribution")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/time-attribution")] HttpRequestData req,
            string sessionId)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware;
                // cross-tenant access via TargetTenantId (TenantScoping.QueryParam). Global-scope
                // callers resolve the session's owning tenant upfront (point-read; mirrors
                // GetSessionEventsFunction).
                var requestCtx = req.GetRequestContext();
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var breakdown = await _metricsRepo.GetSessionTimeBreakdownAsync(effectiveTenantId, sessionId);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetSessionTimeAttributionResponse { Success = true, Breakdown = breakdown });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching time attribution for session {SessionId}", sessionId);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Tenant fleet view: the rolling 30-day range statistics per enrollment class (the panel's
    /// stacked medians + top blocking apps) plus the daily rows for the per-day trend. The range
    /// window is FIXED at the sweep's 30 days — daily medians cannot be merged into an arbitrary
    /// range honestly, so no days parameter is offered.
    /// </summary>
    public class GetTimeAttributionMetricsFunction
    {
        private readonly ILogger<GetTimeAttributionMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetTimeAttributionMetricsFunction(
            ILogger<GetTimeAttributionMetricsFunction> logger,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetTimeAttributionMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/time-attribution")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                var tenantId = TenantHelper.GetTenantId(req);
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(await TimeAttributionResponseBuilder.BuildAsync(_metricsRepo, tenantId));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching time attribution metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>Global-admin variant: tenantId filters to one tenant; absent = cross-tenant "global" rows.</summary>
    public class GetGlobalTimeAttributionMetricsFunction
    {
        private readonly ILogger<GetGlobalTimeAttributionMetricsFunction> _logger;
        private readonly IMetricsRepository _metricsRepo;

        public GetGlobalTimeAttributionMetricsFunction(
            ILogger<GetGlobalTimeAttributionMetricsFunction> logger,
            IMetricsRepository metricsRepo)
        {
            _logger = logger;
            _metricsRepo = metricsRepo;
        }

        [Function("GetGlobalTimeAttributionMetrics")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/metrics/time-attribution")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantIdFilter = query["tenantId"];
                var partition = string.IsNullOrWhiteSpace(tenantIdFilter) ? "global" : tenantIdFilter!;

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(await TimeAttributionResponseBuilder.BuildAsync(_metricsRepo, partition));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching global time attribution metrics");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }

    /// <summary>
    /// Shared response builder for the tenant and global variants (mirrors the MetricsMath
    /// single-source pattern). The envelope DTO (<see cref="TimeAttributionMetricsResponse"/>)
    /// lives in Shared so the manifest exports it.
    /// </summary>
    internal static class TimeAttributionResponseBuilder
    {
        internal const int WindowDays = 30;

        /// <summary>
        /// Rolling rows are rewritten by every 2h sweep as long as the partition has window
        /// sessions; a partition that went fully quiet stops being rewritten, so its rows would
        /// otherwise serve arbitrarily old numbers as "last 30 days" (Codex review). 48h
        /// tolerates a full day of sweep outage before the panel honestly goes empty.
        /// </summary>
        internal static readonly TimeSpan RollingMaxAge = TimeSpan.FromHours(48);

        /// <summary>
        /// "Last N days" = exactly N calendar day keys including today — both range ends are
        /// inclusive, so subtracting the full N would return N+1 days (Codex review).
        /// </summary>
        internal static DateTime InclusiveWindowStart(DateTime today, int days) => today.AddDays(-(days - 1));

        internal static async Task<TimeAttributionMetricsResponse> BuildAsync(IMetricsRepository metricsRepo, string partition)
        {
            var freshCutoff = DateTime.UtcNow - RollingMaxAge;
            var rolling = (await metricsRepo.GetRollingTimeAttributionAggregatesAsync(partition))
                .Where(r => r.ComputedAt >= freshCutoff)
                .ToList();
            var today = DateTime.UtcNow.Date;
            var daily = await metricsRepo.GetTimeAttributionAggregatesAsync(partition, InclusiveWindowStart(today, WindowDays), today);

            return new TimeAttributionMetricsResponse
            {
                Success = true,
                WindowDays = WindowDays,
                // Range statistics per enrollment class (never mixed) — the UI gates rendering
                // at cleanSessionCount >= 20 and shows "insufficient data (n=…)" below it.
                Classes = rolling.OrderBy(r => r.EnrollmentClass, StringComparer.Ordinal).ToList(),
                Daily = daily.OrderBy(d => d.Date, StringComparer.Ordinal).ToList(),
            };
        }
    }
}
