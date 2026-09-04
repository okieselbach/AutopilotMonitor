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
    /// Fleet-context deep link: the session IDs where a rule produced a result within the
    /// window. Powers the dashboard's ?ruleId= filter reached from the per-rule
    /// "Fired in N enrollments" sentence on the session-detail analysis cards. Reads the
    /// same RuleResults hit set the regression radar uses (partition-range scan, capped,
    /// fail-soft) — only loaded on an explicit user click, never on session-detail render.
    /// </summary>
    public class RuleHitSessionsFunction
    {
        /// <summary>Matches the GetRuleHitSessionIdsAsync default cap; used to report truncation.</summary>
        internal const int MaxSessionIds = 2000;

        private readonly ILogger<RuleHitSessionsFunction> _logger;
        private readonly IRuleRepository _ruleRepo;

        public RuleHitSessionsFunction(
            ILogger<RuleHitSessionsFunction> logger,
            IRuleRepository ruleRepo)
        {
            _logger = logger;
            _ruleRepo = ruleRepo;
        }

        [Function("GetRuleHitSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/rule-hit-sessions")] HttpRequestData req)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware;
                // cross-tenant access via TargetTenantId (TenantScoping.QueryParam).
                var requestCtx = req.GetRequestContext();
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var ruleId = query["ruleId"];

                if (string.IsNullOrWhiteSpace(ruleId))
                {
                    return await req.BadRequestAsync("ruleId is required");
                }

                var days = ParseDays(query["days"]);
                var sinceUtc = DateTime.UtcNow.AddDays(-days);
                var sessionIds = await _ruleRepo.GetRuleHitSessionIdsAsync(
                    requestCtx.TargetTenantId, ruleId, sinceUtc, MaxSessionIds);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new GetRuleHitSessionsResponse
                {
                    RuleId = ruleId,
                    Days = days,
                    SessionIds = sessionIds,
                    Truncated = sessionIds.Count >= MaxSessionIds
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "RuleHitSessions");
            }
        }

        /// <summary>
        /// Window in days: default 14, clamped to 1..90 (RuleResults follow the session
        /// retention cascade, so anything past 90 days cannot widen the result).
        /// </summary>
        internal static int ParseDays(string? raw)
        {
            if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var parsed))
                return 14;
            return Math.Clamp(parsed, 1, 90);
        }
    }
}
