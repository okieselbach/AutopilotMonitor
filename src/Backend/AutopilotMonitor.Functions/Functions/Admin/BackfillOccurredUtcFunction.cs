using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Global-Admin trigger for the one-time OccurredUtc backfill on AuditLogs / OpsEvents
    /// (business timestamps lost to the system-Timestamp reset of the 2026-07-18 storage
    /// migration; values are recovered from the reverse-tick RowKeys). DRY-RUN BY DEFAULT —
    /// a real run requires the explicit <c>dryRun=false</c> query parameter, so the operator
    /// always sees counts and samples before anything is written.
    /// </summary>
    public class BackfillOccurredUtcFunction
    {
        private readonly ILogger<BackfillOccurredUtcFunction> _logger;
        private readonly OccurredUtcBackfillService _service;

        private const int DefaultMaxRows = 250;
        private const int MaxMaxRows = 1000;

        public BackfillOccurredUtcFunction(
            ILogger<BackfillOccurredUtcFunction> logger,
            OccurredUtcBackfillService service)
        {
            _logger = logger;
            _service = service;
        }

        /// <summary>
        /// POST /api/maintenance/backfill-occurred-utc
        /// Query parameters:
        /// - table: "audit" | "ops" (required).
        /// - dryRun: "false" to actually write; anything else (or absent) reports only.
        /// - maxRows: rows examined per invocation (default 250, max 1000). Follow
        ///   nextContinuation to continue; null nextContinuation = table done.
        /// - continuation: opaque token from the previous response (URL-encode it).
        /// </summary>
        [Function("BackfillOccurredUtc")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/backfill-occurred-utc")] HttpRequestData req,
            FunctionContext context)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var userEmail = TenantHelper.GetUserIdentifier(req);

            var table = (req.Query["table"] ?? string.Empty).Trim().ToLowerInvariant();
            if (table != OccurredUtcBackfillService.TableAudit && table != OccurredUtcBackfillService.TableOps)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new
                {
                    error = $"table must be '{OccurredUtcBackfillService.TableAudit}' or '{OccurredUtcBackfillService.TableOps}'"
                });
                return bad;
            }

            var dryRun = !string.Equals(req.Query["dryRun"], "false", StringComparison.OrdinalIgnoreCase);
            var continuation = req.Query["continuation"];

            var maxRows = DefaultMaxRows;
            if (!string.IsNullOrEmpty(req.Query["maxRows"]))
            {
                if (!int.TryParse(req.Query["maxRows"], out maxRows) || maxRows < 1)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = $"maxRows must be a positive integer (max {MaxMaxRows})" });
                    return bad;
                }
                maxRows = Math.Min(maxRows, MaxMaxRows);
            }

            _logger.LogInformation(
                "OccurredUtc backfill requested by {User}: table={Table} dryRun={DryRun} maxRows={MaxRows} hasContinuation={HasContinuation}",
                userEmail, table, dryRun, maxRows, !string.IsNullOrEmpty(continuation));

            try
            {
                var result = await _service.RunAsync(table, dryRun, maxRows, continuation);

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
                _logger.LogError(ex, "OccurredUtc backfill run failed");
                var error = req.CreateResponse(HttpStatusCode.InternalServerError);
                await error.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return error;
            }
        }
    }
}
