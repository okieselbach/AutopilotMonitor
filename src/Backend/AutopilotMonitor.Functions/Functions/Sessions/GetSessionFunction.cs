using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
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
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "SessionId is required"
                });
                return badRequest;
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
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Session not found",
                        sessionId
                    });
                    return notFound;
                }

                return await req.OkAsync(new
                {
                    success = true,
                    session
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, $"Get session '{sessionId}'");
            }
        }
    }
}
