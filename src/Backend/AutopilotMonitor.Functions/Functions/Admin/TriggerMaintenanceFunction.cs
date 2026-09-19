using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Maintenance;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// <c>POST /api/maintenance/trigger</c> — manual Global Admin trigger of the platform
    /// maintenance run. The run takes minutes, so the request only queues it:
    /// <c>202 Accepted</c> when queued, <c>409</c> while a run (timer or manual) is active,
    /// <c>500</c> when the enqueue failed — never a hollow success. Progress and the run report
    /// surface as <c>Maintenance*</c> ops events.
    /// </summary>
    public class TriggerMaintenanceFunction
    {
        private readonly ILogger<TriggerMaintenanceFunction> _logger;
        private readonly IMaintenanceTriggerProducer _producer;
        private readonly MaintenanceRunGate _runGate;

        public TriggerMaintenanceFunction(
            ILogger<TriggerMaintenanceFunction> logger,
            IMaintenanceTriggerProducer producer,
            MaintenanceRunGate runGate)
        {
            _logger = logger;
            _producer = producer;
            _runGate = runGate;
        }

        /// <summary>
        /// Query parameters:
        /// - date: Optional date to aggregate (yyyy-MM-dd). If not provided, uses yesterday.
        /// - aggregateOnly: If true, only runs aggregation (skips timeout and cleanup)
        /// </summary>
        [Function("TriggerMaintenance")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/trigger")] HttpRequestData req,
            FunctionContext context)
        {
            // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
            var userEmail = TenantHelper.GetUserIdentifier(req) ?? "GlobalAdmin";
            var ct = context.CancellationToken;

            try
            {
                if (!QueryParams.TryUtcInstant(req.Query["date"], "date", out var dateInstant, out _))
                {
                    return await req.BadRequestAsync("Invalid date format. Use yyyy-MM-dd");
                }
                DateTime? targetDate = dateInstant?.Date;
                bool aggregateOnly = req.Query["aggregateOnly"]?.ToLower() == "true";

                if (await _runGate.IsRunActiveAsync(ct))
                {
                    return await req.ConflictAsync("A maintenance run is already active.");
                }

                await _producer.EnqueueAsync(new MaintenanceTriggerEnvelope
                {
                    TriggeredBy = userEmail,
                    TargetDate = targetDate,
                    AggregateOnly = aggregateOnly,
                }, ct);

                _logger.LogInformation("TriggerMaintenance: run queued (triggeredBy={TriggeredBy}, date={Date:yyyy-MM-dd}, aggregateOnly={AggregateOnly})",
                    userEmail, targetDate, aggregateOnly);

                return await req.JsonAsync(HttpStatusCode.Accepted, new TriggerMaintenanceResponse
                {
                    Message = "Maintenance run queued",
                    TriggeredBy = userEmail,
                    TriggeredAt = DateTime.UtcNow,
                    TargetDate = targetDate?.ToString("yyyy-MM-dd"),
                    AggregateOnly = aggregateOnly,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "TriggerMaintenance");
            }
        }
    }
}
