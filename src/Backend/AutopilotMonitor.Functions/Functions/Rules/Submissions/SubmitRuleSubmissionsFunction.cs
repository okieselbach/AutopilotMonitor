using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Rules.Submissions
{
    /// <summary>
    /// A tenant admin submits one or more of the tenant's custom rules for the community pool.
    /// The body carries only rule references; the service freezes the rules server-side.
    /// </summary>
    public class SubmitRuleSubmissionsFunction
    {
        private readonly ILogger<SubmitRuleSubmissionsFunction> _logger;
        private readonly RuleSubmissionService _service;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TelegramNotificationService _telegram;
        private readonly GlobalNotificationService _globalNotifications;

        public SubmitRuleSubmissionsFunction(
            ILogger<SubmitRuleSubmissionsFunction> logger,
            RuleSubmissionService service,
            IMaintenanceRepository maintenanceRepo,
            TelegramNotificationService telegram,
            GlobalNotificationService globalNotifications)
        {
            _logger = logger;
            _service = service;
            _maintenanceRepo = maintenanceRepo;
            _telegram = telegram;
            _globalNotifications = globalNotifications;
        }

        [Function("SubmitRuleSubmissions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "rules/submissions")] HttpRequestData req)
        {
            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware
                var requestCtx = req.GetRequestContext();
                var tenantId = requestCtx.TenantId;
                var userIdentifier = requestCtx.UserPrincipalName;

                var read = await req.ReadAsync<SubmitRuleSubmissionsRequest>(262_144);
                if (read.Error != null) return read.Error;
                var request = read.Value!;

                // Same tenant-tamper gate as the session report: non-GAs submit for their JWT
                // tenant only; a GA may submit on behalf of a foreign tenant and must name it.
                if (!requestCtx.IsGlobalAdmin)
                {
                    if (!string.IsNullOrEmpty(request.TenantId)
                        && !string.Equals(request.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "SubmitRuleSubmissions: BLOCKED cross-tenant body for non-GA user={User} jwtTenant={JwtTenant} bodyTenant={BodyTenant}",
                            userIdentifier, tenantId, request.TenantId);
                        return await req.ForbiddenAsync("Body tenantId must match your authenticated tenant.");
                    }
                    request.TenantId = tenantId;
                }
                else if (string.IsNullOrEmpty(request.TenantId))
                {
                    return await req.BadRequestAsync("tenantId is required in body for Global Admin submissions.");
                }

                if (!SecurityValidator.IsValidGuid(request.TenantId))
                    return await req.BadRequestAsync("Invalid tenantId.");

                var displayName = TenantHelper.GetUserDisplayName(req) ?? userIdentifier;
                List<RuleSubmission> created;
                try
                {
                    created = await _service.SubmitAsync(request.TenantId, request, userIdentifier, displayName);
                }
                catch (ArgumentException ex)
                {
                    return await req.BadRequestAsync(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return await req.ConflictAsync(ex.Message);
                }

                if (!requestCtx.IsGlobalAdmin)
                {
                    foreach (var s in created)
                    {
                        await _maintenanceRepo.LogAuditEntryAsync(
                            request.TenantId, "CREATE", "RuleSubmission", s.SubmissionId, userIdentifier,
                            new Dictionary<string, string>
                            {
                                { "Action", "SubmitRuleSubmission" },
                                { "RuleKind", s.RuleKind },
                                { "RuleId", s.SourceRuleId },
                                { "BatchId", s.BatchId },
                                { "AttributionMode", s.AttributionMode },
                                { "HasComment", (s.Comment != null).ToString() },
                            });
                    }
                }

                // Operator notifications — best effort, one per rule so every id has its own deep link.
                foreach (var s in created)
                {
                    _ = _telegram.SendRuleSubmissionAsync(s.SubmissionId, request.TenantId, userIdentifier, s.RuleKind, s.SourceRuleId, s.Title, s.Comment ?? string.Empty);
                    _ = _globalNotifications.CreateNotificationAsync(
                        "rule_submission",
                        $"Rule submission {s.SubmissionId}",
                        $"{s.SourceRuleId} \"{s.Title}\" ({s.RuleKind}) — {userIdentifier} (Tenant: {request.TenantId})",
                        href: $"/admin/reports/rule-submissions?submissionId={Uri.EscapeDataString(s.SubmissionId)}");
                }

                _logger.LogInformation("Rule submissions stored: {Count} rule(s), batch {BatchId}, tenant {TenantId}, by {User}",
                    created.Count, created[0].BatchId, request.TenantId, userIdentifier);

                var items = await _service.ToItemsAsync(created);
                return await req.OkAsync(new SubmitRuleSubmissionsResponse
                {
                    Success = true,
                    Message = created.Count == 1 ? "Rule submitted for review." : $"{created.Count} rules submitted for review.",
                    BatchId = created[0].BatchId,
                    Submissions = items,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "SubmitRuleSubmissions");
            }
        }
    }
}
