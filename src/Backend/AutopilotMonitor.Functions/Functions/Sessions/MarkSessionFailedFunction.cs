using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class MarkSessionFailedFunction
    {
        private readonly ILogger<MarkSessionFailedFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public MarkSessionFailedFunction(
            ILogger<MarkSessionFailedFunction> logger,
            ISessionRepository sessionRepo,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("MarkSessionFailed")]
        public async Task<MarkSessionFailedOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/mark-failed")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation($"MarkSessionFailed function processing request for session {sessionId}");

            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                _logger.LogInformation($"Marking session {sessionId} as failed for tenant {tenantId} by user {userIdentifier}");

                // Update session status to Failed with manual failure reason. AdminMarkedAction is
                // the authoritative trigger for the AdminAction response-field sent to agents.
                // FailureSource="manual" aligns this code path with the existing rule/agent
                // attribution scheme on the SessionSummary.
                var success = await _sessionRepo.UpdateSessionStatusAsync(
                    tenantId,
                    sessionId,
                    SessionStatus.Failed,
                    currentPhase: null, // Keep current phase
                    failureReason: "Manually marked as failed by administrator",
                    failureSource: "manual",
                    adminMarkedAction: "Failed"
                );

                if (success)
                {
                    // Log audit entry with actual user identifier
                    await _maintenanceRepo.LogAuditEntryAsync(
                        tenantId,
                        "UPDATE",
                        "Session",
                        sessionId,
                        userIdentifier,
                        new Dictionary<string, string>
                        {
                            { "Action", "MarkAsFailed" },
                            { "Reason", "Manually marked as failed" }
                        }
                    );

                    // Retrieve updated session data to include in SignalR message
                    var updatedSession = await _sessionRepo.GetSessionAsync(tenantId, sessionId);

                    _logger.LogInformation($"Successfully marked session {sessionId} as failed");
                    var response = req.CreateResponse(HttpStatusCode.OK);
                    await response.WriteAsJsonAsync(new
                    {
                        success = true,
                        message = $"Session {sessionId} marked as failed"
                    });

                    // Send SignalR notification to update all clients in the tenant
                    // Only sent to tenant-specific group (not global-admins) to avoid flooding
                    // Global Admins can refresh or view session details to see status changes
                    object? sessionDelta = updatedSession != null ? new {
                        updatedSession.CurrentPhase,
                        updatedSession.CurrentPhaseDetail,
                        updatedSession.Status,
                        updatedSession.FailureReason,
                        updatedSession.EventCount,
                        updatedSession.DurationSeconds,
                        updatedSession.CompletedAt,
                        updatedSession.DiagnosticsBlobName
                    } : null;

                    object messagePayload = new {
                        sessionId = sessionId,
                        tenantId = tenantId,
                        eventCount = 0,
                        sessionUpdate = sessionDelta
                    };

                    var tenantMessage = new SignalRMessageAction("newevents")
                    {
                        GroupName = $"tenant-{tenantId}",
                        Arguments = new[] { messagePayload }
                    };

                    // Also push to the per-session group: the tenant broadcast group is member-role
                    // gated, so a roleless Progress-Portal watcher of this session would otherwise
                    // never see the admin-marked failure.
                    var sessionMessage = new SignalRMessageAction("newevents")
                    {
                        GroupName = $"session-{tenantId}-{sessionId}",
                        Arguments = new[] { messagePayload }
                    };

                    return new MarkSessionFailedOutput
                    {
                        HttpResponse = response,
                        SignalRMessages = new[] { tenantMessage, sessionMessage }
                    };
                }
                else
                {
                    _logger.LogWarning($"Session {sessionId} not found");
                    var response = req.CreateResponse(HttpStatusCode.NotFound);
                    await response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"Session {sessionId} not found"
                    });
                    return new MarkSessionFailedOutput { HttpResponse = response };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error marking session {sessionId} as failed");

                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Internal server error"
                });

                return new MarkSessionFailedOutput { HttpResponse = errorResponse };
            }
        }
    }

    public class MarkSessionFailedOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
