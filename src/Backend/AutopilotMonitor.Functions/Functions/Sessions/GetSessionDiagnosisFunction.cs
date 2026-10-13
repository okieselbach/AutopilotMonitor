using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Diagnosis;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// The session's verdict for the diagnosis page and the MCP summary: outcome, cause and the
    /// evidence behind them (<see cref="DiagnosisHeadlineBuilder"/>), built on demand from the row and
    /// the full event partition. The strict event read is deliberate: a storage error must surface as
    /// an error, never as a verdict built from no evidence.
    /// </summary>
    public class GetSessionDiagnosisFunction
    {
        private readonly ILogger<GetSessionDiagnosisFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionDiagnosisFunction(ILogger<GetSessionDiagnosisFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionDiagnosis")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/diagnosis")] HttpRequestData req,
            string sessionId)
        {
            if (!SecurityValidator.IsValidGuid(sessionId))
                return await req.BadRequestAsync("sessionId must be a GUID");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware;
                // cross-tenant access via TargetTenantId (TenantScoping.QueryParam), global-scope
                // callers resolve the owning tenant (point-read).
                var requestCtx = req.GetRequestContext();
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId);
                if (session == null)
                    return await req.NotFoundAsync($"Session {sessionId} not found");

                var events = await _sessionRepo.GetSessionEventsStrictAsync(effectiveTenantId, sessionId);
                return await req.OkAsync(new GetSessionDiagnosisResponse
                {
                    Success = true,
                    SessionId = sessionId,
                    Headline = DiagnosisHeadlineBuilder.Build(session, events),
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get session diagnosis '{sessionId}'");
            }
        }
    }
}
