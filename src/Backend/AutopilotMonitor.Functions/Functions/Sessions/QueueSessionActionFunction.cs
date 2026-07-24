using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// Queues a <see cref="ServerAction"/> for delivery to the agent via the next ingest call.
    /// Used for manual admin-triggered actions (e.g. "force rotate config", "request diagnostics").
    /// The RuleEngine queues its own actions directly via the repository.
    /// </summary>
    public class QueueSessionActionFunction
    {
        private readonly ILogger<QueueSessionActionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly TelemetryClient _telemetryClient;

        private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ServerActionTypes.TerminateSession,
            ServerActionTypes.RotateConfig,
            ServerActionTypes.RequestDiagnostics
        };

        /// <summary>
        /// Action types an Operator may queue. The route is TenantAdminOrOperator so troubleshooting
        /// staff can trigger an on-demand diagnostics collection; everything with config or lifecycle
        /// impact (terminate_session, rotate_config) stays Admin/GA-only via <see cref="IsTypeAllowedForCaller"/>.
        /// </summary>
        private static readonly HashSet<string> OperatorAllowedTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ServerActionTypes.RequestDiagnostics
        };

        /// <summary>
        /// Type-level authorization on top of the route policy: Admins and Global Admins may queue
        /// every allowed type, Operators only the types in <see cref="OperatorAllowedTypes"/>.
        /// Internal-static so tests can pin the matrix without an HTTP host.
        /// </summary>
        internal static bool IsTypeAllowedForCaller(string actionType, bool isTenantAdmin, bool isGlobalAdmin)
        {
            if (isTenantAdmin || isGlobalAdmin)
                return true;
            return OperatorAllowedTypes.Contains(actionType);
        }

        public QueueSessionActionFunction(
            ILogger<QueueSessionActionFunction> logger,
            ISessionRepository sessionRepo,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _telemetryClient = telemetryClient;
        }

        /// <summary>
        /// POST /api/sessions/{sessionId}/actions — queue an action for the given session.
        /// Body: { "type": "rotate_config", "reason": "...", "params": { "key": "value" } }
        /// </summary>
        [Function("QueueSessionAction")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/actions")] HttpRequestData req,
            string sessionId)
        {
            try
            {
                // Authentication + TenantAdminOrOperator authorization enforced by
                // PolicyEnforcementMiddleware; per-type re-gating for Operators happens below.
                var tenantId = TenantHelper.GetTenantId(req);
                var userIdentifier = TenantHelper.GetUserIdentifier(req);
                var requestCtx = req.GetRequestContext();

                string body;
                using (var reader = new StreamReader(req.Body))
                    body = await reader.ReadToEndAsync();

                ServerAction? action;
                try
                {
                    action = JsonConvert.DeserializeObject<ServerAction>(body);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid action payload for session {SessionId}", sessionId);
                    return await BadRequestAsync(req, "Invalid JSON body");
                }

                if (action == null || string.IsNullOrWhiteSpace(action.Type))
                    return await BadRequestAsync(req, "Action 'type' is required");

                if (!AllowedTypes.Contains(action.Type))
                    return await BadRequestAsync(req, $"Unknown action type '{action.Type}'. Allowed: {string.Join(", ", AllowedTypes)}");

                if (!IsTypeAllowedForCaller(action.Type, requestCtx.IsTenantAdmin, requestCtx.IsGlobalAdmin))
                {
                    _logger.LogWarning(
                        "Operator {User} denied queueing admin-only action '{Type}' for session {SessionId}",
                        userIdentifier, action.Type, sessionId);
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = $"Action type '{action.Type}' requires the Tenant Admin role"
                    });
                    return forbidden;
                }

                // Stamp server-side fields — never trust client timestamps, and never let the caller
                // forge a RuleId (that's reserved for the RuleEngine).
                action.QueuedAt = DateTime.UtcNow;
                action.RuleId = null;
                if (string.IsNullOrWhiteSpace(action.Reason))
                    action.Reason = $"Manual action queued by {userIdentifier}";

                var success = await _sessionRepo.QueueServerActionAsync(tenantId, sessionId, action);
                if (!success)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { success = false, message = $"Session {sessionId} not found for tenant" });
                    return notFound;
                }

                _telemetryClient.TrackEvent("ServerActionQueued", new Dictionary<string, string>
                {
                    { "tenantId", tenantId },
                    { "sessionId", sessionId },
                    { "actionType", action.Type },
                    { "source", "admin_endpoint" },
                    { "userIdentifier", userIdentifier ?? string.Empty },
                    { "reason", action.Reason ?? string.Empty }
                });

                _logger.LogInformation("Queued server action '{Type}' for session {SessionId} by {User}",
                    action.Type, sessionId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.Accepted);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Action '{action.Type}' queued for delivery",
                    queuedAt = action.QueuedAt
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queueing server action for session {SessionId}", sessionId);
                var response = req.CreateResponse(HttpStatusCode.InternalServerError);
                await response.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return response;
            }
        }

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { success = false, message });
            return response;
        }
    }
}
