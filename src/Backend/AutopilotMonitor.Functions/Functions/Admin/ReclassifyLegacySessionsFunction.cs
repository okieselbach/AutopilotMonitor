using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Global-Admin trigger for the one-time retro-reconciliation of misclassified historical
    /// sessions (misclassification audit 2026-07-16). DRY-RUN BY DEFAULT — a real run requires
    /// the explicit <c>dryRun=false</c> query parameter, so the operator always sees the counts
    /// and samples before anything is written.
    /// </summary>
    public class ReclassifyLegacySessionsFunction
    {
        private readonly ILogger<ReclassifyLegacySessionsFunction> _logger;
        private readonly LegacyReclassificationService _service;

        private const int DefaultMaxSessions = 200;
        private const int MaxMaxSessions = 2000;

        public ReclassifyLegacySessionsFunction(
            ILogger<ReclassifyLegacySessionsFunction> logger,
            LegacyReclassificationService service)
        {
            _logger = logger;
            _service = service;
        }

        /// <summary>
        /// POST /api/maintenance/reclassify-legacy
        /// Query parameters:
        /// - mode: "legacy_timeouts" (default) — re-classify pre-classifier "Session timed out" Failed rows;
        ///         "pending_orphans" — resolve Pending rows superseded by a later session of the same device.
        /// - dryRun: "false" to actually write; anything else (or absent) reports only.
        /// - tenantId: optional single-tenant scope (GUID); absent = all tenants.
        /// - maxSessions: per-invocation examination cap (default 200, max 2000). Re-run to continue.
        /// </summary>
        [Function("ReclassifyLegacySessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/reclassify-legacy")] HttpRequestData req,
            FunctionContext context)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var userEmail = TenantHelper.GetUserIdentifier(req);

            var mode = (req.Query["mode"] ?? LegacyReclassificationService.ModeLegacyTimeouts).Trim().ToLowerInvariant();
            var dryRun = !string.Equals(req.Query["dryRun"], "false", StringComparison.OrdinalIgnoreCase);
            var tenantIdScope = req.Query["tenantId"];

            if (!string.IsNullOrEmpty(tenantIdScope) && !Guid.TryParse(tenantIdScope, out _))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { error = "tenantId must be a GUID" });
                return bad;
            }

            var maxSessions = DefaultMaxSessions;
            if (!string.IsNullOrEmpty(req.Query["maxSessions"]))
            {
                if (!int.TryParse(req.Query["maxSessions"], out maxSessions) || maxSessions < 1)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = $"maxSessions must be a positive integer (max {MaxMaxSessions})" });
                    return bad;
                }
                maxSessions = Math.Min(maxSessions, MaxMaxSessions);
            }

            _logger.LogInformation(
                "Legacy reclassification requested by {User}: mode={Mode} dryRun={DryRun} tenantScope={TenantScope} maxSessions={MaxSessions}",
                userEmail, mode, dryRun, tenantIdScope ?? "(all)", maxSessions);

            try
            {
                var result = mode switch
                {
                    LegacyReclassificationService.ModeLegacyTimeouts =>
                        await _service.ReclassifyLegacyTimeoutsAsync(tenantIdScope, dryRun, maxSessions, userEmail),
                    LegacyReclassificationService.ModePendingOrphans =>
                        await _service.ResolvePendingOrphansAsync(tenantIdScope, dryRun, maxSessions, userEmail),
                    _ => null,
                };

                if (result == null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new
                    {
                        error = $"Unknown mode '{mode}'. Valid: {LegacyReclassificationService.ModeLegacyTimeouts}, {LegacyReclassificationService.ModePendingOrphans}"
                    });
                    return bad;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    result,
                    triggeredBy = userEmail,
                    triggeredAt = DateTime.UtcNow,
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy reclassification run failed");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return error;
            }
        }
    }
}
