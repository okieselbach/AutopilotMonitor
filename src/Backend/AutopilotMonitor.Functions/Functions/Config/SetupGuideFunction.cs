using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// The setup guide's markers on the tenant row. The guide applies settings through the field
    /// PATCH like any settings card; this endpoint only stamps what the PATCH cannot: that an admin
    /// finished a step, or hid the guide for the whole tenant. Server time and the caller's UPN.
    /// </summary>
    public class SetupGuideFunction
    {
        internal const string StepSettings = "settings";
        internal const string StepHardware = "hardware";
        internal const string StepDismiss = "dismiss";
        internal const string StepRestore = "restore";

        private readonly ILogger<SetupGuideFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public SetupGuideFunction(
            ILogger<SetupGuideFunction> logger,
            TenantConfigurationService configService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _configService = configService;
            _maintenanceRepo = maintenanceRepo;
        }

        /// <summary>
        /// POST /api/config/{tenantId}/setup-guide — TenantAdminOrGA (catalog-enforced), refused in
        /// view-as. Body <c>{ "step": "settings" | "hardware" | "dismiss" | "restore" }</c>. A step that
        /// is already recorded writes nothing and answers with the current markers.
        /// </summary>
        [Function("RecordSetupGuideStep")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/setup-guide")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                var requestCtx = req.GetRequestContext();
                var read = await req.ReadAsync<SetupGuideStepRequest>();
                if (read.Error != null) return read.Error;

                var step = NormalizeStep(read.Value!.Step);
                if (step == null)
                    return await req.BadRequestAsync("step must be one of: settings, hardware, dismiss, restore.");

                var caller = requestCtx.UserPrincipalName ?? "Unknown";
                var nowUtc = DateTime.UtcNow;
                var update = await _configService.UpdateAsync(
                    requestCtx.TargetTenantId,
                    row => ApplyStep(row, step, caller, nowUtc),
                    "setup-guide",
                    $"setup guide: {step}");

                if (await TenantConfigUpdateResponses.FailureAsync(req, update) is { } failure)
                    return failure;
                var config = update.Config!;

                if (update.Status == TenantConfigUpdateStatus.Updated)
                {
                    await _maintenanceRepo.LogAuditEntryAsync(
                        requestCtx.TargetTenantId,
                        "UPDATE",
                        "SetupGuide",
                        requestCtx.TargetTenantId,
                        caller,
                        new Dictionary<string, string> { { "Step", step } });
                }

                return await req.OkAsync(new SetupGuideStateResponse
                {
                    Success = true,
                    SettingsReviewedAt = config.SetupGuideSettingsReviewedAt,
                    HardwareReviewedAt = config.SetupGuideHardwareReviewedAt,
                    DismissedAt = config.SetupGuideDismissedAt,
                    DismissedBy = config.SetupGuideDismissedBy,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "RecordSetupGuideStep");
            }
        }

        /// <summary>The known step in lower case, or null.</summary>
        internal static string? NormalizeStep(string? step)
        {
            var value = step?.Trim().ToLowerInvariant();
            return value is StepSettings or StepHardware or StepDismiss or StepRestore ? value : null;
        }

        /// <summary>
        /// Stamps <paramref name="step"/> on the fresh row inside the conditional write. False (nothing to
        /// write) when the step is already recorded — the first completion stays the one on record.
        /// </summary>
        internal static bool ApplyStep(TenantConfiguration row, string step, string caller, DateTime nowUtc)
        {
            switch (step)
            {
                case StepSettings:
                    if (row.SetupGuideSettingsReviewedAt != null) return false;
                    row.SetupGuideSettingsReviewedAt = nowUtc;
                    break;
                case StepHardware:
                    if (row.SetupGuideHardwareReviewedAt != null) return false;
                    row.SetupGuideHardwareReviewedAt = nowUtc;
                    break;
                case StepDismiss:
                    if (row.SetupGuideDismissedAt != null) return false;
                    row.SetupGuideDismissedAt = nowUtc;
                    row.SetupGuideDismissedBy = caller;
                    break;
                case StepRestore:
                    if (row.SetupGuideDismissedAt == null) return false;
                    row.SetupGuideDismissedAt = null;
                    row.SetupGuideDismissedBy = null;
                    break;
                default:
                    return false;
            }
            row.UpdatedBy = caller;
            return true;
        }
    }
}
