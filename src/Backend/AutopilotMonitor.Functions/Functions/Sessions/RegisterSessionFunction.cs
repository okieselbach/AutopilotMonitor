using AutopilotMonitor.Shared;
using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
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
        private readonly CloudPcDeviceValidator _cloudPcDeviceValidator;
        private readonly IntuneDeviceBindingValidator _intuneDeviceBindingValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;
        private readonly WebhookNotificationService _webhookNotificationService;
        private readonly SessionDeletionGuard _deletionGuard;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly SessionOwnerBindingObserver _ownerBinding;
        private readonly OpsEventService _opsEvents;

        public RegisterSessionFunction(
            ILogger<RegisterSessionFunction> logger,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            CloudPcDeviceValidator cloudPcDeviceValidator,
            IntuneDeviceBindingValidator intuneDeviceBindingValidator,
            BootstrapSessionService bootstrapSessionService,
            WebhookNotificationService webhookNotificationService,
            SessionDeletionGuard deletionGuard,
            SessionOwnerBindingObserver ownerBinding,
            OpsEventService opsEvents)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _cloudPcDeviceValidator = cloudPcDeviceValidator;
            _intuneDeviceBindingValidator = intuneDeviceBindingValidator;
            _bootstrapSessionService = bootstrapSessionService;
            _webhookNotificationService = webhookNotificationService;
            _deletionGuard = deletionGuard;
            _ownerBinding = ownerBinding;
            _opsEvents = opsEvents;
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
                    _adminConfigService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    registration.SessionId,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator,
                    cloudPcDeviceValidator: _cloudPcDeviceValidator,
                    intuneDeviceBindingValidator: _intuneDeviceBindingValidator
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

            // Server-populated: record which device-validation path accepted this request
            // (AutopilotV1 / CorporateIdentifier / DeviceAssociation / Bootstrap) so the
            // session row shows HOW the backend admitted the device. Overwrites any
            // agent-sent value unconditionally — this is the server's verdict, not input.
            registration.ValidatedBy = validation.ValidatedBy;

            // Device-supplied session anchor: clamp before ANY consumer sees it. StartedAt drives the
            // SessionsIndex RowKey (list order), the retention filter ("StartedAt lt cutoff"), the
            // TotalToday window and the supersede-predecessor comparison below — a far-future value
            // would pin the row at the head of every list, inflate today's counters forever and
            // never become a retention candidate. Same skew policy as event timestamps
            // (EventTimestampValidator: +24h / -168h, clamped to server time, not rejected).
            ClampStartedAt(registration, DateTime.UtcNow, _logger);

            // Cascade-delete guard (Codex F2/F3 + bootstrap follow-up): live HERE in the shared
            // core so BOTH the cert-auth /agent/register-session entry AND the bootstrap wrapper
            // (BootstrapRegisterSessionFunction) hit it before the StoreSessionAsync below.
            // Refuses with 410 Gone on lock states (Preparing/Queued/Running/Poisoned) and on a
            // fresh tombstone marker — without this gate the bootstrap path could still mutate a
            // mid-cascade row or Add-revive a session past the tombstone.
            Azure.Data.Tables.TableEntity? guardSessionRow;
            try
            {
                guardSessionRow = await _deletionGuard.EnsureWritableAndGetRowAsync(
                    registration.TenantId, registration.SessionId, "RegisterSession");
            }
            catch (SessionDeletionLockedException locked)
            {
                _logger.LogInformation(
                    "RegisterSession refused: cascade in flight or recent tombstone — tenant={Tenant} session={Session} state={State} manifestId={ManifestId}",
                    registration.TenantId, registration.SessionId, locked.CurrentState, locked.ManifestId);
                return new RegisterSessionOutput
                {
                    HttpResponse = await WriteSessionLockedAsync(req, locked),
                };
            }

            // Pre-read the session row so we can distinguish three lifecycle entries:
            //   - existing == null      → first-time registration (fresh enrollment)
            //   - existing.Pending      → WhiteGlove Part 2 resume (user-driven phase)
            //   - everything else       → agent restart / terminal re-register (no start signal)
            // Used below to scope the opt-in "enrollment started" webhook to real start events.
            var preExistingSession = await _sessionRepo.GetSessionAsync(registration.TenantId, registration.SessionId);
            bool isFreshRegistration = preExistingSession == null;
            bool isWhiteGloveResume = preExistingSession?.Status == SessionStatus.Pending;

            // SESSION-OWNER-BINDING-SHADOW: compare the caller's validated identity against the
            // owner already on the row (reusing the guard's read). Stage 1 never refuses — the
            // decision only records the outcome and yields the owner to stamp (fresh bind,
            // legacy claim, cert rotation, bootstrap→cert handoff). Enforcement (stage 2) will turn
            // a would-reject here into 403 + AgentErrorCodes.SessionOwnerMismatch, on which the
            // agent rotates its session id.
            var ownerDecision = _ownerBinding.Observe(
                req, registration.TenantId, registration.SessionId, guardSessionRow, validation, "agent/register-session");

            // Store session in Azure Table Storage. The sessionId → tenantId lookup claim inside is
            // first-writer-wins: a sessionId already owned by another tenant is refused with 409 so
            // no tenant can poison the cross-tenant resolver (SessionTenantLookup) that Global
            // Admin/Reader session reads and GA writes trust.
            bool stored;
            try
            {
                stored = await _sessionRepo.StoreSessionAsync(registration, ownerDecision.OwnerToStamp);
            }
            catch (SessionTenantConflictException conflict)
            {
                _logger.LogWarning(
                    "RegisterSession refused: session {SessionId} is owned by another tenant — requested tenant={TenantId} thumbprint={Thumbprint}",
                    registration.SessionId, registration.TenantId, validation.CertificateThumbprint);
                _ = _opsEvents.RecordSessionTenantConflictAsync(
                        registration.TenantId, registration.SessionId, conflict.OwningTenantId,
                        validation.CertificateThumbprint, registration.AgentVersion, "agent/register-session")
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget RecordSessionTenantConflictAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
                return new RegisterSessionOutput { HttpResponse = await WriteSessionTenantConflictAsync(req) };
            }

            if (!stored)
            {
                var errorResponse = await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Failed to store session");
                return new RegisterSessionOutput { HttpResponse = errorResponse };
            }

            // Enrollment counters (fire-and-forget, non-blocking). Only first-time registrations
            // count: agent restarts and WhiteGlove Part 2 resumes re-register the same session and
            // must not inflate either counter. The platform stat previously incremented on every
            // re-register — corrected daily by the recompute, but visibly overcounting in between.
            // The per-tenant counter has no corrective recompute at all (only a raise-only floor),
            // so gating on fresh registrations is load-bearing there.
            if (isFreshRegistration)
            {
                _ = _metricsRepo.IncrementPlatformStatAsync("TotalEnrollments")
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementPlatformStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);

                _ = _metricsRepo.IncrementTenantStatAsync(registration.TenantId, "TotalEnrollments")
                    .ContinueWith(t => _logger.LogWarning(t.Exception?.InnerException, "Fire-and-forget IncrementTenantStatAsync failed"), TaskContinuationOptions.OnlyOnFaulted);
            }

            // Retrieve the stored session to include full data in SignalR message
            var session = await _sessionRepo.GetSessionAsync(registration.TenantId, registration.SessionId);

            // Supersede pass (misclassification audit 2026-07-16): when a device registers a FRESH
            // session while older non-terminal sessions of the same device still exist (e.g.
            // WhiteGlove Part 2 running under a new session id leaves the Part-1 row Pending
            // forever, or a wiped/re-enrolled device abandons its old InProgress run), resolve
            // those predecessors as Incomplete("Superseded by ..."). One physical device can only
            // ever run one enrollment. Best-effort — never blocks the registration.
            var supersededMessages = isFreshRegistration
                ? await SupersedeOrphanedPredecessorsAsync(registration)
                : Array.Empty<SignalRMessageAction>();

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
            var tenantMessage = new SignalRMessageAction(Constants.SignalRMessages.NewSession)
            {
                GroupName = $"tenant-{registration.TenantId}",
                Arguments = new[] { messagePayload }
            };

            // 2. Global Admins notification (cross-tenant visibility)
            var globalAdminMessage = new SignalRMessageAction(Constants.SignalRMessages.NewSession)
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
                    .Concat(supersededMessages)
                    .ToArray()
            };
        }

        /// <summary>
        /// Resolves older non-terminal sessions (Pending/InProgress/Stalled/AwaitingUser) of the
        /// same device to Incomplete("Superseded by session …") when a fresh registration proves
        /// the device has moved on. Returns portal-live SignalR updates for each resolved row
        /// (same "newevents" sessionUpdate shape the ingest path uses). Fail-soft: any error
        /// returns an empty array and the registration proceeds untouched.
        /// </summary>
        private async Task<SignalRMessageAction[]> SupersedeOrphanedPredecessorsAsync(SessionRegistration registration)
        {
            try
            {
                if (!Helpers.SerialNumberHeuristics.IsUsableSerialNumber(registration.SerialNumber))
                    return Array.Empty<SignalRMessageAction>();

                var openSessions = await _sessionRepo.GetOpenSessionsForDeviceAsync(
                    registration.TenantId, registration.SerialNumber.Trim());
                if (openSessions.Count == 0)
                    return Array.Empty<SignalRMessageAction>();

                var messages = new List<SignalRMessageAction>();
                foreach (var stale in openSessions)
                {
                    if (string.Equals(stale.SessionId, registration.SessionId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Only strictly older runs — a clock-skewed or concurrent row is left alone.
                    if (stale.StartedAt >= registration.StartedAt)
                        continue;

                    var reason = $"Superseded by session {registration.SessionId}: the device registered a new " +
                                 $"enrollment session at {registration.StartedAt:yyyy-MM-dd HH:mm} UTC before this one " +
                                 "reached a terminal state.";
                    var transitioned = await _sessionRepo.UpdateSessionStatusAsync(
                        registration.TenantId, stale.SessionId, SessionStatus.Incomplete, VerdictPaths.RegisterSuperseded,
                        failureReason: reason, failureSource: "superseded_by_reregistration");
                    if (!transitioned)
                        continue;

                    _logger.LogInformation(
                        "Superseded stale {StaleStatus} session {StaleSessionId} (started {StaleStartedAt:yyyy-MM-dd}) with new session {SessionId} for device serial (tenant {TenantId})",
                        stale.Status, stale.SessionId, stale.StartedAt, registration.SessionId, registration.TenantId);

                    messages.Add(new SignalRMessageAction(Constants.SignalRMessages.NewEvents)
                    {
                        GroupName = $"tenant-{registration.TenantId}",
                        Arguments = new object[] { new {
                            sessionId = stale.SessionId,
                            tenantId = registration.TenantId,
                            eventCount = 0,
                            sessionUpdate = new
                            {
                                Status = SessionStatus.Incomplete,
                                FailureReason = reason,
                            }
                        } }
                    });
                }

                return messages.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Supersede pass failed for tenant {TenantId} session {SessionId}; registration proceeds",
                    registration.TenantId, registration.SessionId);
                return Array.Empty<SignalRMessageAction>();
            }
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
                var startChannels = tenantConfig.GetNotificationChannels()
                    .Where(c => c.Enabled && c.NotifyOnStart)
                    .ToList();
                if (startChannels.Count == 0)
                    return;

                var sessionUrl = Constants.PortalSessionUrl(sessionId);
                var alert = NotificationAlertBuilder.BuildEnrollmentStartedAlert(
                    session.DeviceName,
                    session.SerialNumber,
                    session.Manufacturer,
                    session.Model,
                    isResume: isResume,
                    sessionUrl: sessionUrl);

                await _webhookNotificationService.SendToChannelsAsync(startChannels, alert);
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

        /// <summary>
        /// 410 Gone response for the cascade-delete guard. Mirrors the V2 ingest 410 contract so
        /// the agent treats register and telemetry uniformly when a session is being deleted.
        /// </summary>
        /// <summary>
        /// Clamps <see cref="SessionRegistration.StartedAt"/> to the same plausibility window
        /// events get (<see cref="EventTimestampValidator.SanitizeTimestamp"/>): outside
        /// [utcNow − 168h, utcNow + 24h] the anchor becomes the server receive time. Runs in the
        /// shared core so the cert-auth and the bootstrap entry are covered alike. A re-registration
        /// of an existing session keeps the earlier stored anchor regardless (StoreSessionAsync
        /// takes the minimum), so a legitimately old WhiteGlove Part-2 resume is not moved.
        /// </summary>
        internal static void ClampStartedAt(SessionRegistration registration, DateTime utcNow, ILogger logger)
        {
            var original = registration.StartedAt;
            var sanitized = EventTimestampValidator.SanitizeTimestamp(original, utcNow);
            if (sanitized == EventTimestampValidator.EnsureUtc(original))
            {
                registration.StartedAt = sanitized;
                return;
            }

            logger.LogWarning(
                "Session {SessionId} (tenant {TenantId}): registration StartedAt {OriginalStartedAt:o} is outside the plausible window around server time {UtcNow:o} — clamped to server time",
                registration.SessionId, registration.TenantId, original, utcNow);
            registration.StartedAt = sanitized;
        }

        /// <summary>
        /// 409 for a sessionId already owned by another tenant. Carries
        /// <see cref="Constants.AgentErrorCodes.SessionOwnerMismatch"/> — the agent's correct reaction
        /// is the same as for an owner-binding refusal: rotate the SessionId and register afresh.
        /// The owning tenant is deliberately NOT disclosed to the caller.
        /// </summary>
        private static async Task<HttpResponseData> WriteSessionTenantConflictAsync(HttpRequestData req)
        {
            var response = req.CreateResponse(HttpStatusCode.Conflict);
            await response.WriteAsJsonAsync(new RegisterSessionResponse
            {
                Success = false,
                Message = "Session id is already registered to another tenant; register with a new session id.",
                RegisteredAt = DateTime.UtcNow,
                ErrorCode = Constants.AgentErrorCodes.SessionOwnerMismatch,
            });
            return response;
        }

        private static async Task<HttpResponseData> WriteSessionLockedAsync(HttpRequestData req, SessionDeletionLockedException locked)
        {
            var response = req.CreateResponse(HttpStatusCode.Gone);
            await response.WriteAsJsonAsync(new RegisterSessionResponse
            {
                Success = false,
                Message = $"Session is being deleted by an administrator (state={locked.CurrentState}); registration is rejected.",
                RegisteredAt = DateTime.UtcNow,
            });
            return response;
        }
    }

    public class RegisterSessionOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = SignalRGroupHelper.HubName)]
        public SignalRMessageAction[]? SignalRMessages { get; set; }
    }
}
