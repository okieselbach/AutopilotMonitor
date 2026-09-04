using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class GetSessionFunction
    {
        private readonly ILogger<GetSessionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionFunction(
            ILogger<GetSessionFunction> logger,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return await req.BadRequestAsync("SessionId is required");
            }

            var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
                // Cross-tenant access check handled by middleware (TargetTenantId); global-scope
                // callers resolve the session's owning tenant upfront (point-read).
                var requestCtx = req.GetRequestContext();
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId);

                if (session == null)
                {
                    return await req.NotFoundAsync($"Session {sessionId} not found");
                }

                return await req.OkAsync(new GetSessionResponse
                {
                    Success = true,
                    Session = session
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get session '{sessionId}'");
            }
        }
    }
}
