using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    public class SubmitSessionReportFunction
    {
        private readonly ILogger<SubmitSessionReportFunction> _logger;
        private readonly SessionReportService _sessionReportService;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TelegramNotificationService _telegramNotificationService;
        private readonly GlobalNotificationService _globalNotificationService;

        public SubmitSessionReportFunction(
            ILogger<SubmitSessionReportFunction> logger,
            SessionReportService sessionReportService,
            IMaintenanceRepository maintenanceRepo,
            TelegramNotificationService telegramNotificationService,
            GlobalNotificationService globalNotificationService)
        {
            _logger = logger;
            _sessionReportService = sessionReportService;
            _maintenanceRepo = maintenanceRepo;
            _telegramNotificationService = telegramNotificationService;
            _globalNotificationService = globalNotificationService;
        }

        [Function("SubmitSessionReport")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/report")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation("SubmitSessionReport processing request for session {SessionId}", sessionId);

            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                // Request body size limit (20 MB — must accommodate base64-encoded agent logs,
                // screenshots, plus CSV/TXT exports; base64 adds ~33% overhead)
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 20_971_520)
                {
                    return await req.BadRequestAsync("Request body too large");
                }

                // Parse request body
                var request = await req.ReadFromJsonAsync<SubmitSessionReportRequest>();
                if (request == null)
                {
                    return await req.BadRequestAsync("Invalid request body.");
                }

                // Ensure sessionId consistency
                request.SessionId = sessionId;

                // Tenant identity: enforce JWT tenantId for non-GAs (prevents body
                // tampering / horizontal escalation). Global Admins MAY submit reports
                // against foreign tenants — the UI sends the session's tenantId, which
                // for cross-tenant GA views is the foreign tenant.
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (!string.IsNullOrEmpty(request.TenantId)
                        && !string.Equals(request.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "SubmitSessionReport: BLOCKED cross-tenant body for non-GA user={User} jwtTenant={JwtTenant} bodyTenant={BodyTenant}",
                            userIdentifier, tenantId, request.TenantId);
                        return await req.ForbiddenAsync("Body tenantId must match your authenticated tenant.");
                    }
                    request.TenantId = tenantId;
                }
                else if (string.IsNullOrEmpty(request.TenantId))
                {
                    return await req.BadRequestAsync("tenantId is required in body for Global Admin submissions.");
                }

                // Identifier format gate: both values become part of the report blob name.
                // Route values are percent-decoded, so an encoded '/' or '..' would otherwise
                // reach the storage layer as a path segment.
                if (!SecurityValidator.IsValidGuid(request.SessionId) || !SecurityValidator.IsValidGuid(request.TenantId))
                {
                    _logger.LogWarning(
                        "SubmitSessionReport: BLOCKED non-GUID identifier user={User} tenant={TenantId} sessionIdLength={SessionIdLength}",
                        userIdentifier, request.TenantId, request.SessionId?.Length ?? 0);
                    return await req.BadRequestAsync("Invalid sessionId or tenantId.");
                }

                // Submit report
                var metadata = await _sessionReportService.SubmitReportAsync(request, userIdentifier);

                // Log audit entry — skip for Global Admins
                if (!requestCtx.IsGlobalAdmin)
                {
                    await _maintenanceRepo.LogAuditEntryAsync(
                        request.TenantId,
                        "CREATE",
                        "SessionReport",
                        metadata.ReportId,
                        userIdentifier,
                        new Dictionary<string, string>
                        {
                            { "Action", "SubmitSessionReport" },
                            { "SessionId", sessionId },
                            { "BlobName", metadata.BlobName },
                            { "HasComment", (!string.IsNullOrEmpty(request.Comment)).ToString() },
                            { "HasEmail", (!string.IsNullOrEmpty(request.Email)).ToString() },
                            { "HasScreenshot", (!string.IsNullOrEmpty(request.ScreenshotBase64)).ToString() },
                            { "HasAgentLog", (!string.IsNullOrEmpty(request.AgentLogBase64)).ToString() },
                            { "IncludeDiagnostics", request.IncludeDiagnostics.ToString() },
                            { "DiagnosticsCopyStatus", metadata.DiagnosticsCopyStatus ?? string.Empty }
                        }
                    );
                }

                // Telegram notification — best effort
                _ = _telegramNotificationService.SendSessionReportAsync(
                    request.TenantId, userIdentifier, sessionId, metadata.ReportId, request.Comment ?? string.Empty);

                // Persistent in-app notification for Global Admins — best effort
                _ = _globalNotificationService.CreateNotificationAsync(
                    "session_report",
                    "New Session Report",
                    $"{userIdentifier} — Session {sessionId} (Tenant: {request.TenantId})",
                    href: $"/admin/reports/session-reports?reportId={Uri.EscapeDataString(metadata.ReportId)}");

                _logger.LogInformation("Session report submitted: ReportId={ReportId}, Session={SessionId}, By={User}",
                    metadata.ReportId, sessionId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SubmitSessionReportResponse
                {
                    Success = true,
                    Message = "Session report submitted successfully.",
                    ReportId = metadata.ReportId
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "SubmitSessionReport");
            }
        }
    }
}
