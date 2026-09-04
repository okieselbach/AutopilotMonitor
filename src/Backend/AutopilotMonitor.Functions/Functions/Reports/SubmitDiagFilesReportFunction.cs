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
    /// <summary>
    /// Tenant-admin/-operator entry point for submitting diagnostic files (logs, JSON state
    /// files, screenshots) without a session context. Lands in the same SessionReports table +
    /// session-reports blob container as session reports, distinguished by
    /// <see cref="SessionReportMetadata.ReportType"/> = "diagFiles".
    /// </summary>
    public class SubmitDiagFilesReportFunction
    {
        private readonly ILogger<SubmitDiagFilesReportFunction> _logger;
        private readonly SessionReportService _sessionReportService;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TelegramNotificationService _telegramNotificationService;
        private readonly GlobalNotificationService _globalNotificationService;

        public SubmitDiagFilesReportFunction(
            ILogger<SubmitDiagFilesReportFunction> logger,
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

        [Function("SubmitDiagFilesReport")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "diagnostics/files")] HttpRequestData req)
        {
            _logger.LogInformation("SubmitDiagFilesReport processing request");

            try
            {
                // Authentication + TenantAdminOrOperator authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                // Body size limit (20 MB) — same as SubmitSessionReport. Diag-files payloads
                // are typically smaller (no events.csv/timeline.txt synthesis), but log
                // bundles + screenshots still benefit from the same upper bound.
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > 20_971_520)
                {
                    return await req.BadRequestAsync("Request body too large");
                }

                var request = await req.ReadFromJsonAsync<SubmitDiagFilesReportRequest>();
                if (request == null)
                {
                    return await req.BadRequestAsync("Invalid request body.");
                }

                // Tenant identity: enforce JWT tenantId for non-GAs (prevents body
                // tampering / horizontal escalation). Global Admins MAY submit reports
                // on behalf of foreign tenants and MUST specify the target tenantId.
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (!string.IsNullOrEmpty(request.TenantId)
                        && !string.Equals(request.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "SubmitDiagFilesReport: BLOCKED cross-tenant body for non-GA user={User} jwtTenant={JwtTenant} bodyTenant={BodyTenant}",
                            userIdentifier, tenantId, request.TenantId);
                        return await req.ForbiddenAsync("Body tenantId must match your authenticated tenant.");
                    }
                    request.TenantId = tenantId;
                }
                else if (string.IsNullOrEmpty(request.TenantId))
                {
                    return await req.BadRequestAsync("tenantId is required in body for Global Admin submissions.");
                }

                // TenantId becomes part of the blob name — must be a GUID, never path-shaped.
                if (!SecurityValidator.IsValidGuid(request.TenantId))
                {
                    return await req.BadRequestAsync("Invalid tenantId.");
                }

                var metadata = await _sessionReportService.SubmitDiagFilesReportAsync(request, userIdentifier);

                // Audit log — skip for Global Admins (their actions are not tenant-scoped)
                if (!requestCtx.IsGlobalAdmin)
                {
                    await _maintenanceRepo.LogAuditEntryAsync(
                        request.TenantId,
                        "CREATE",
                        "DiagFilesReport",
                        metadata.ReportId,
                        userIdentifier,
                        new Dictionary<string, string>
                        {
                            { "Action", "SubmitDiagFilesReport" },
                            { "BlobName", metadata.BlobName },
                            { "HasComment", (!string.IsNullOrEmpty(request.Comment)).ToString() },
                            { "HasEmail", (!string.IsNullOrEmpty(request.Email)).ToString() },
                            { "HasScreenshot", (!string.IsNullOrEmpty(request.ScreenshotBase64)).ToString() },
                            { "HasAgentLog", (!string.IsNullOrEmpty(request.AgentLogBase64)).ToString() }
                        }
                    );
                }

                // Telegram notification — best effort
                _ = _telegramNotificationService.SendDiagFilesReportAsync(
                    request.TenantId, userIdentifier, metadata.ReportId, request.Comment ?? string.Empty);

                // Persistent in-app notification for Global Admins — best effort
                _ = _globalNotificationService.CreateNotificationAsync(
                    "diag_files_report",
                    "New Diag Files Report",
                    $"{userIdentifier} (Tenant: {request.TenantId})",
                    href: $"/admin/reports/session-reports?reportId={Uri.EscapeDataString(metadata.ReportId)}");

                _logger.LogInformation("Diag-files report submitted: ReportId={ReportId}, By={User}",
                    metadata.ReportId, userIdentifier);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SubmitSessionReportResponse
                {
                    Success = true,
                    Message = "Diag files report submitted successfully.",
                    ReportId = metadata.ReportId
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "SubmitDiagFilesReport");
            }
        }
    }
}
