using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance
{
    /// <summary>
    /// <c>POST /api/global/session-deletions/maintenance/trigger</c> — manual GA trigger for a
    /// one-off session-deletion maintenance run (GC sweeps + retention fanout), the on-demand
    /// counterpart to the 12h <see cref="SessionDeletionMaintenanceFunction"/> timer. GA-only via
    /// <c>EndpointAccessPolicyCatalog</c>; authentication + authorization are enforced by
    /// <c>PolicyEnforcementMiddleware</c>, the body does not re-check.
    /// <para>
    /// Semantics (mirrors <c>TriggerCriticalTableBackupFunction</c>'s fail-hard producer): a
    /// lease probe first rejects with <c>409 RunAlreadyActive</c> while a run is in flight; on
    /// successful enqueue we return <c>202 Accepted</c>; on enqueue failure <c>500</c> so the
    /// operator never sees a hollow success. Run progress surfaces via the
    /// <c>SessionDeletionMaintenance*</c> OpsEvents (Session Cleanup banner + Ops Events page).
    /// </para>
    /// </summary>
    public class TriggerSessionDeletionMaintenanceFunction
    {
        private readonly ISessionDeletionMaintenanceTriggerProducer _producer;
        private readonly SessionDeletionMaintenanceLockStore _lockStore;
        private readonly ILogger<TriggerSessionDeletionMaintenanceFunction> _logger;

        public TriggerSessionDeletionMaintenanceFunction(
            ISessionDeletionMaintenanceTriggerProducer producer,
            SessionDeletionMaintenanceLockStore lockStore,
            ILogger<TriggerSessionDeletionMaintenanceFunction> logger)
        {
            _producer = producer;
            _lockStore = lockStore;
            _logger = logger;
        }

        [Function("TriggerSessionDeletionMaintenance")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/session-deletions/maintenance/trigger")] HttpRequestData req)
        {
            var actor = TenantHelper.GetUserIdentifier(req) ?? "GlobalAdmin";
            var ct = req.FunctionContext.CancellationToken;

            // Acquire+release probe: a held lease means a run is active RIGHT NOW — tell the
            // operator instead of queuing a message that would only produce a SkippedLocked
            // event. Racing the probe (run starts between probe and worker pickup) is benign:
            // the worker's own lease acquire lands on the SkippedLocked path.
            try
            {
                var lease = await _lockStore.AcquireLeaseAsync(ct: ct);
                try { await lease.ReleaseAsync(cancellationToken: ct); }
                catch (Exception ex)
                {
                    // Release failure is harmless — the 60s lease auto-expires; the worker's
                    // acquire retries land within the queue's poll cadence.
                    _logger.LogWarning(ex, "TriggerSessionDeletionMaintenance: probe-lease release failed (auto-expires in 60s)");
                }
            }
            catch (LeaseHeldException)
            {
                return await WriteJsonAsync(req, HttpStatusCode.Conflict, new
                {
                    error = "RunAlreadyActive",
                    message = "A session-deletion maintenance run is already active — check the Session Cleanup page for progress.",
                });
            }

            try
            {
                await _producer.EnqueueAsync(new SessionDeletionMaintenanceTriggerEnvelope { TriggeredBy = actor }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TriggerSessionDeletionMaintenance: enqueue failed (triggeredBy={TriggeredBy})", actor);
                return await WriteJsonAsync(req, HttpStatusCode.InternalServerError, new
                {
                    error = "EnqueueFailed",
                    message = "Failed to enqueue the maintenance run — please retry.",
                });
            }

            _logger.LogInformation("TriggerSessionDeletionMaintenance: run queued (triggeredBy={TriggeredBy})", actor);
            return await WriteJsonAsync(req, HttpStatusCode.Accepted, new
            {
                message = "Maintenance run queued — progress surfaces as SessionDeletionMaintenance* ops events.",
                triggeredBy = actor,
            });
        }

        private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(JsonSerializer.Serialize(body));
            return response;
        }
    }
}
