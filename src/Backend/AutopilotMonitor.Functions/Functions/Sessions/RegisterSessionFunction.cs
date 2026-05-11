using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    public class RegisterSessionFunction
    {
        // Set to false to suppress the start notification on WhiteGlove Part 2 resume
        // (user-driven phase after a pre-provisioned device is delivered to the end user).
        // The fresh-session start notification is unaffected.
        private const bool NotifyOnWhiteGloveResume = true;

        private readonly ILogger<RegisterSessionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly WebhookNotificationService _webhookNotificationService;

        public RegisterSessionFunction(
            ILogger<RegisterSessionFunction> logger,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService,
            WebhookNotificationService webhookNotificationService)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _configService = configService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _webhookNotificationService = webhookNotificationService;
        }

        [Function("RegisterSession")]
        public async Task<RegisterSessionOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/register-session")] HttpRequestData req)
        {
            _logger.LogInformation("RegisterSession function processing request");

            try
            {
                // Parse request
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 1_048_576) // 1 MB limit
                {
                    var errorResponse = await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Request body too large");
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RegisterSessionRequest>(requestBody);

                if (request?.Registration == null)
                {
                    var errorResponse = await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid request payload");
                    return new RegisterSessionOutput { HttpResponse = errorResponse };
                }

                var registration = request.Registration;

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse2) = await req.ValidateSecurityAsync(
                    registration.TenantId,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    registration.SessionId,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator
                );

                if (errorResponse2 != null)
                {
                    return new RegisterSessionOutput { HttpResponse = errorResponse2 };
                }

                return await ProcessRegisterAsync(req, registration, validation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering session");
                var errorResponse = await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Internal server error");
                return new RegisterSessionOutput { HttpResponse = errorResponse };
            }
        }

        /// <summary>
        /// Core registration logic: store session, emit SignalR notifications.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<RegisterSessionOutput> ProcessRegisterAsync(HttpRequestData req, SessionRegistration registration, SecurityValidationResult validation)
        {
            _logger.LogInformation($"Registering session {registration.SessionId} for tenant {registration.TenantId} (Device: {validation.CertificateThumbprint})");

            // Pre-read the session row so we can distinguish three lifecycle entries:
            //   - existing == null      → first-time registration (fresh enrollment)
            //   - existing.Pending      → WhiteGlove Part 2 resume (user-driven phase)
            //   - everything else       → agent restart / terminal re-register (no start signal)
            // Used below to scope the opt-in "enrollment started" webhook to real start events.
            var preExistingSession = await _sessionRepo.GetSessionAsync(registration.TenantId, registration.SessionId);
            bool isFreshRegistration = preExistingSession == null;
            bool isWhiteGloveResume = preExistingSession?.Status == SessionStatus.Pending;

            // Store session in Azure Table Storage
            var stored = await _sessionRepo.StoreSessionAsync(registration);

            if (!stored)
            {
                var errorResponse = await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Failed to store session");
                return new RegisterSessionOutput { HttpResponse = errorResponse };
            }

            // Increment platform stats (fire-and-forget, non-blocking)
            _ = _metricsRepo.IncrementPlatformStatAsync("TotalEnrollments")
                .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

            // Retrieve the stored session to include full data in SignalR message
            var session = await _sessionRepo.GetSessionAsync(registration.TenantId, registration.SessionId);

            // AdminAction on re-register is only the portal button signal. SessionSummary.AdminMarkedAction
            // is set exclusively by MarkSessionSucceededFunction / MarkSessionFailedFunction. An
            // agent that restarts after its own completion must NOT receive a phantom AdminAction —
            // previously inferred from "status is terminal", which applied to every agent-driven
            // Succeeded/Failed session too.
            string? adminAction = session?.AdminMarkedAction;
            if (!string.IsNullOrEmpty(adminAction))
            {
                _logger.LogInformation("Session {SessionId} was admin-marked as {AdminAction} — signaling agent on re-register: AdminAction={AdminAction}",
                    registration.SessionId, adminAction, adminAction);
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            var responseData = new RegisterSessionResponse
            {
                SessionId = registration.SessionId,
                Success = true,
                Message = "Session registered successfully",
                RegisteredAt = DateTime.UtcNow,
                AdminAction = adminAction,
                ValidatedBy = validation.ValidatedBy
            };

            await response.WriteAsJsonAsync(responseData);

            // Send SignalR notification for new session registration
            // This is sent to BOTH tenant-specific group AND global-admins group
            // so Global Admins can see new sessions from all tenants without being
            // flooded with every single event update
            var messagePayload = new {
                sessionId = registration.SessionId,
                tenantId = registration.TenantId,
                session = session
            };

            // 1. Tenant-specific notification (normal users)
            var tenantMessage = new SignalRMessageAction("newSession")
            {
                GroupName = $"tenant-{registration.TenantId}",
                Arguments = new[] { messagePayload }
            };

            // 2. Global Admins notification (cross-tenant visibility)
            var globalAdminMessage = new SignalRMessageAction("newSession")
            {
                GroupName = "global-admins",
                Arguments = new[] { messagePayload }
            };

            // Opt-in "Enrollment Started" webhook (Teams/Slack). Fired once per real start:
            // fresh registration OR WhiteGlove Part 2 resume (suppressible via NotifyOnWhiteGloveResume).
            // Agent restarts and re-registrations of terminal sessions never trigger this.
            if ((isFreshRegistration || (isWhiteGloveResume && NotifyOnWhiteGloveResume))
                && session != null)
            {
                _ = SendStartNotificationAsync(registration.TenantId, registration.SessionId, session, isWhiteGloveResume);
            }

            return new RegisterSessionOutput
            {
                HttpResponse = response,
                SignalRMessages = new[] { tenantMessage, globalAdminMessage }
            };
        }

        /// <summary>
        /// Fire-and-forget enrollment-started webhook. Loads tenant config, respects the
        /// opt-in <c>WebhookNotifyOnStart</c>/<c>TeamsNotifyOnStart</c> toggle, and dispatches
        /// through the same renderer pipeline (Teams Legacy / Workflow / Slack) as success/failure.
        /// </summary>
        private async Task SendStartNotificationAsync(string tenantId, string sessionId, SessionSummary session, bool isResume)
        {
            try
            {
                var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
                if (!tenantConfig.GetEffectiveNotifyOnStart())
                    return;

                var (webhookUrl, providerTypeInt) = tenantConfig.GetEffectiveWebhookConfig();
                if (string.IsNullOrEmpty(webhookUrl) || providerTypeInt == 0)
                    return;

                var sessionUrl = $"https://portal.autopilotmonitor.com/session/{tenantId}/{sessionId}";
                var alert = NotificationAlertBuilder.BuildEnrollmentStartedAlert(
                    session.DeviceName,
                    session.SerialNumber,
                    session.Manufacturer,
                    session.Model,
                    isResume: isResume,
                    sessionUrl: sessionUrl);

                await _webhookNotificationService.SendNotificationAsync(webhookUrl, (WebhookProviderType)providerTypeInt, alert);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fire-and-forget enrollment-started webhook failed for tenant {TenantId} session {SessionId}", tenantId, sessionId);
            }
        }

        private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
        {
            var response = req.CreateResponse(statusCode);
            var errorResponse = new RegisterSessionResponse
            {
                Success = false,
                Message = message,
                RegisteredAt = DateTime.UtcNow
            };
            await response.WriteAsJsonAsync(errorResponse);
            return response;
        }
    }

    public class RegisterSessionOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
