using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// Cross-tenant dashboard stats (Global Admin). Optional <c>?tenantId=</c>
    /// restricts the window to a single tenant; absent means platform-wide.
    /// </summary>
    public class GetAllSessionStatsFunction
    {
        private readonly ILogger<GetAllSessionStatsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetAllSessionStatsFunction(ILogger<GetAllSessionStatsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        // Route moved to "global/stats/sessions" to keep symmetry with the
        // per-tenant /api/stats/sessions endpoint — see GetSessionStatsFunction
        // for the routing-collision rationale.
        [Function("GetAllSessionStats")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/stats/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware.
                var callerTenantId = TenantHelper.GetTenantId(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

                if (!GetSessionStatsFunction.TryParseDays(query["days"], out var days, out var error))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = error });
                    return bad;
                }

                var tenantIdFilterRaw = query["tenantId"];
                string? tenantIdFilter = null;
                if (!string.IsNullOrEmpty(tenantIdFilterRaw))
                {
                    if (!Guid.TryParse(tenantIdFilterRaw, out _))
                    {
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new { success = false, message = "Invalid tenantId format" });
                        return bad;
                    }
                    tenantIdFilter = tenantIdFilterRaw;
                }

                // Delegated ("MSP") callers carry AllowedTenantIds (subset tier) → the no-filter aggregate is
                // bounded to their managed tenants. Null for GA/Reader = all tenants; ignored when a single
                // ?tenantId= is named (middleware already validated it is in the caller's scope).
                var allowedTenantIds = req.GetRequestContext().AllowedTenantIds;

                _logger.LogInformation(
                    "Computing cross-tenant session stats (caller={Caller}, filter={Filter}, days={Days}, bounded={Bounded})",
                    callerTenantId, tenantIdFilter ?? "none", days, allowedTenantIds != null);

                var stats = await _sessionRepo.GetAllSessionStatsAsync(tenantIdFilter, days, allowedTenantIds);
                return await req.OkAsync(new { success = true, stats });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing cross-tenant session stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }
    }
}
