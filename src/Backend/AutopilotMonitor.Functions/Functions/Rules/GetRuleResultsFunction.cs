using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules
{
    /// <summary>
    /// Returns analysis results (rule evaluations) for a session.
    /// Supports on-demand re-analysis via ?reanalyze=true query parameter.
    /// Global admins can request analysis for any tenant by passing ?tenantId=...
    /// </summary>
    public class GetRuleResultsFunction
    {
        private readonly ILogger<GetRuleResultsFunction> _logger;
        private readonly IRuleRepository _ruleRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly AnalyzeRuleService _analyzeRuleService;

        public GetRuleResultsFunction(
            ILogger<GetRuleResultsFunction> logger,
            IRuleRepository ruleRepo,
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            AnalyzeRuleService analyzeRuleService)
        {
            _logger = logger;
            _ruleRepo = ruleRepo;
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _analyzeRuleService = analyzeRuleService;
        }

        [Function("GetRuleResults")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/analysis")] HttpRequestData req,
            string sessionId)
        {
            // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
            // Global-scope (GA or read-only GlobalReader) cross-tenant fallback: resolve actual tenant
            // upfront so the read works cross-tenant for any platform-scope caller.
            var requestCtx = req.GetRequestContext();
            var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

            var reanalyze = string.Equals(req.Query["reanalyze"], "true", StringComparison.OrdinalIgnoreCase);

            // Re-analysis deletes + rewrites rule results — an ACTION, not a view. The route is
            // MemberRead (Viewer included) so the read stays open, but the recompute trigger is
            // gated to write-capable callers (mirrors the UI hiding "Analyze now" for Viewer and
            // keeps a read-only GlobalReader from steering cross-tenant writes).
            if (reanalyze && !RecomputeTriggerGate.CanTriggerRecompute(requestCtx, effectiveTenantId))
            {
                var forbidden = req.CreateResponse(System.Net.HttpStatusCode.Forbidden);
                await forbidden.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Your role does not permit triggering re-analysis.",
                });
                return forbidden;
            }

            // Rules whose StoreRuleResultAsync returned false during the on-demand reanalyze.
            // Reported back to the caller so the UI can render a warning banner without losing
            // the rules that DID persist. Mirrors the queue-path's throw-on-false retry trigger,
            // but stays partial-success here because the user is on a synchronous HTTP wait
            // and a 500 would blank the page even when most rules persisted fine.
            var persistFailureRuleIds = new List<string>();

            if (reanalyze)
            {
                try
                {
                    var ruleEngine = new RuleEngine(_analyzeRuleService, _ruleRepo, _sessionRepo, _logger);
                    // Reanalyze context: every rule re-evaluated, but the engine merges the
                    // lifecycle markers (FirstDetectedAt, NotifiedAt) from the existing rows it
                    // loads BEFORE the delete below — so the rebuild can never re-arm a duplicate
                    // channel notification. Rules that no longer fire come back as ResolvedResults
                    // (kept for audit) instead of silently vanishing.
                    var outcome = await ruleEngine.AnalyzeSessionAsync(effectiveTenantId, sessionId, reanalyze: true);

                    // Delete existing results so stale entries (e.g. rows from since-deleted or
                    // disabled rules) don't persist after re-analysis
                    await _maintenanceRepo.DeleteSessionRuleResultsAsync(effectiveTenantId, sessionId);

                    foreach (var result in outcome.Results.Concat(outcome.ResolvedResults))
                    {
                        var stored = await _ruleRepo.StoreRuleResultAsync(result);
                        if (!stored)
                        {
                            persistFailureRuleIds.Add(result.RuleId);
                        }
                    }

                    if (persistFailureRuleIds.Count > 0)
                    {
                        _logger.LogError(
                            "On-demand re-analysis for session {SessionId}: {FailedCount} of {TotalCount} rule result(s) failed to persist: [{FailedRuleIds}]",
                            sessionId,
                            persistFailureRuleIds.Count,
                            outcome.Results.Count,
                            string.Join(", ", persistFailureRuleIds));
                    }
                    else
                    {
                        _logger.LogInformation($"On-demand re-analysis for session {sessionId}: {outcome.Results.Count} issue(s) detected");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"On-demand analysis failed for session {sessionId}");
                }
            }

            var results = await _ruleRepo.GetRuleResultsAsync(effectiveTenantId, sessionId);

            // Resolved findings (session healed / no longer firing) are kept for audit and still
            // returned in `results` (the UI hides them behind a toggle), but they no longer count
            // as issues.
            var openResults = results.Where(r => r.ResolvedAt == null).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                // success=false when any rule failed to persist during the reanalyze loop above.
                // UI keeps showing the persisted results; consumers can branch on persistFailureCount
                // to render a warning banner. persistFailureRuleIds is omitted entirely (null) when
                // there were no failures, so the existing UI contract stays unchanged for the happy path.
                success = persistFailureRuleIds.Count == 0,
                sessionId,
                results,
                totalIssues = openResults.Count,
                criticalCount = openResults.Count(r => r.Severity == "critical"),
                highCount = openResults.Count(r => r.Severity == "high"),
                warningCount = openResults.Count(r => r.Severity == "warning"),
                persistFailureCount = persistFailureRuleIds.Count,
                persistFailureRuleIds = persistFailureRuleIds.Count > 0 ? persistFailureRuleIds : null
            });
            return response;
        }
    }
}
