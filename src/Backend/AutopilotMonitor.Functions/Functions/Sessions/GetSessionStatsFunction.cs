using System.Globalization;
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
    /// Per-tenant dashboard stats. Returns the same numbers regardless of how the
    /// client has paginated the session list — the aggregation runs server-side
    /// over a SessionsIndex window.
    /// </summary>
    public class GetSessionStatsFunction
    {
        private const int DefaultDays = 7;
        private const int MaxDays = 365;

        private readonly ILogger<GetSessionStatsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionStatsFunction(ILogger<GetSessionStatsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        // Route is "stats/sessions" — NOT "sessions/stats". Azure Functions' route
        // matcher does NOT honour literal-vs-parametric specificity the way ASP.NET
        // attribute routing does: a literal "sessions/stats" gets eaten by the
        // sibling GetSessionFunction's "sessions/{sessionId}" with sessionId="stats",
        // which 404s when "stats" fails GUID validation. Verified live via App
        // Insights (Functions.GetSession picked up /api/sessions/stats).
        [Function("GetSessionStats")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stats/sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
                var tenantId = TenantHelper.GetTenantId(req);
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);

                if (!TryParseDays(query["days"], out var days, out var error))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = error });
                    return bad;
                }

                _logger.LogInformation(
                    "Computing session stats (tenant={TenantId}, days={Days})",
                    tenantId, days);

                var stats = await _sessionRepo.GetSessionStatsAsync(tenantId, days);
                return await req.OkAsync(new { success = true, stats });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing session stats");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return errorResponse;
            }
        }

        internal static bool TryParseDays(string? raw, out int days, out string? error)
        {
            days = DefaultDays;
            error = null;
            if (string.IsNullOrEmpty(raw)) return true;

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1 || parsed > MaxDays)
            {
                error = $"days must be a positive integer between 1 and {MaxDays}";
                return false;
            }

            days = parsed;
            return true;
        }
    }
}
