using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Progress;

/// <summary>
/// Dedicated Progress Portal endpoints.
/// These routes are exempt from MemberAuthorizationMiddleware so any authenticated tenant user
/// can monitor enrollment sessions without needing an Admin or Operator role.
/// </summary>
public class ProgressPortalFunction
{
    private readonly ILogger<ProgressPortalFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    public ProgressPortalFunction(
        ILogger<ProgressPortalFunction> logger,
        ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    /// <summary>
    /// GET /api/progress/sessions
    /// Returns sessions for the authenticated user's tenant.
    /// </summary>
    [Function("ProgressGetSessions")]
    public async Task<HttpResponseData> GetSessions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions")] HttpRequestData req)
    {
        _logger.LogInformation("ProgressGetSessions processing request");

        try
        {
            // Authentication + AuthenticatedUser authorization enforced by PolicyEnforcementMiddleware
            var tenantId = req.GetRequestContext().TenantId;

            _logger.LogInformation("ProgressGetSessions: Fetching sessions for tenant {TenantId}", tenantId);

            // Progress portal renders only the latest 100 sessions — single page is enough.
            var page = await _sessionRepo.GetSessionsPageAsync(tenantId, days: null, pageSize: 100, continuation: null);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                count = page.Items.Count,
                sessions = page.Items
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressGetSessions");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                count = 0,
                sessions = Array.Empty<object>()
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// GET /api/progress/sessions/{sessionId}/events
    /// Returns events for a specific session. Cross-tenant access only for Global Admins.
    /// </summary>
    [Function("ProgressGetSessionEvents")]
    public async Task<HttpResponseData> GetSessionEvents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions/{sessionId}/events")] HttpRequestData req,
        string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
            return badRequestResponse;
        }

        var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
        _logger.LogInformation("{SessionPrefix} ProgressGetSessionEvents: Fetching events", sessionPrefix);

        try
        {
            // Authentication, AuthenticatedUser authz, AND cross-tenant access enforced by
            // PolicyEnforcementMiddleware (catalog: TenantScoping.QueryParam).
            // requestCtx.TargetTenantId is the middleware-validated tenantId from the
            // ?tenantId= query param (GA bypass already applied).
            var requestCtx = req.GetRequestContext();
            var userIdentifier = requestCtx.UserPrincipalName;

            var query = HttpUtility.ParseQueryString(req.Url.Query);
            if (string.IsNullOrEmpty(query["tenantId"]))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "tenantId query parameter is required",
                    sessionId = sessionId,
                    count = 0,
                    events = Array.Empty<object>()
                });
                return badRequest;
            }

            var requestedTenantId = requestCtx.TargetTenantId;

            // Audit log for GA cross-tenant access (middleware allowed it; logged here for visibility).
            if (requestCtx.IsGlobalAdmin && !string.Equals(requestedTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{SessionPrefix} Global Admin {User} accessing cross-tenant progress events (tenant: {TenantId})",
                    sessionPrefix, userIdentifier, requestedTenantId);
            }

            var events = await _sessionRepo.GetSessionEventsAsync(requestedTenantId, sessionId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sessionId = sessionId,
                count = events.Count,
                events = events
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressGetSessionEvents for session {SessionId}", sessionId);

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                sessionId = sessionId,
                count = 0,
                events = Array.Empty<object>()
            });
            return errorResponse;
        }
    }
}
