using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/sessions/{sessionId}/signals</c> — returns the SignalLog for a V2 session
    /// (Plan §M5 read endpoint, consumed by the Inspector's SignalStream panel and the MCP
    /// <c>get_decision_signals</c> tool).
    /// <para>
    /// Tenant scoping is resolved upstream by <c>PolicyEnforcementMiddleware</c> via
    /// <see cref="RequestContext.TargetTenantId"/>; Global-Admin fallback mirrors the other
    /// session-detail read endpoints (resolve actual tenant via SessionsIndex when the
    /// effective tenant has no data).
    /// </para>
    /// </summary>
    public class GetSessionSignalsFunction
    {
        /// <summary>Hard upper bound on page size — protects against unbounded result sets.</summary>
        internal const int MaxResultsCap = 5000;
        /// <summary>Default result set size when the caller doesn't specify <c>?maxResults=</c>.</summary>
        internal const int DefaultMaxResults = 1000;

        private readonly ILogger<GetSessionSignalsFunction> _logger;
        private readonly ISignalRepository _signalRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionSignalsFunction(
            ILogger<GetSessionSignalsFunction> logger,
            ISignalRepository signalRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _signalRepo = signalRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionSignals")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/signals")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
                return badRequest;
            }

            var maxResults = ParseMaxResults(req.Url.Query);
            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            _logger.LogInformation("{Prefix} GetSessionSignals: maxResults={Max}", sessionPrefix, maxResults);

            try
            {
                // Global-scope (GA or read-only GlobalReader) callers resolve the session's owning
                // tenant upfront (point-read) — mirrors GetSessionEventsFunction; the former
                // "Count == 0 → scan" retry treated empty results as wrong-tenant evidence.
                var requestCtx = req.GetRequestContext();
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var signals = await _signalRepo.QueryBySessionAsync(
                    effectiveTenantId, sessionId, maxResults);

                return await req.OkAsync(new GetSessionSignalsResponse
                {
                    Success = true,
                    SessionId = sessionId,
                    Count = signals.Count,
                    Truncated = signals.Count >= maxResults
                        || SignalQueryLimits.IsPayloadBudgetExhausted(signals, SignalQueryLimits.DefaultMaxTotalPayloadChars),
                    Signals = signals,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get signals for session '{sessionId}'");
            }
        }

        /// <summary>
        /// Reads <c>?maxResults=</c> from the query string, clamping to [1, <see cref="MaxResultsCap"/>].
        /// Returns <see cref="DefaultMaxResults"/> when the parameter is missing or malformed.
        /// Takes the raw query string so the helper is unit-testable without mocking
        /// <c>HttpRequestData</c> (abstract, painful to fake).
        /// </summary>
        internal static int ParseMaxResults(string queryString)
        {
            var query = System.Web.HttpUtility.ParseQueryString(queryString ?? string.Empty);
            var raw = query["maxResults"];
            if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var parsed)) return DefaultMaxResults;
            if (parsed < 1) return 1;
            if (parsed > MaxResultsCap) return MaxResultsCap;
            return parsed;
        }
    }
}
