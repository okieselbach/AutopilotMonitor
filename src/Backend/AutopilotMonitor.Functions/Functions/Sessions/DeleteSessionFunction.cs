using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// Admin-triggered session deletion. Always dispatches to the V2 cascade producer.
    /// <para>
    /// One HTTP gate runs before the producer: the global kill-switch (returns 503 uniformly
    /// so no data-state info leaks while the operator override is active). The producer itself
    /// owns the existence check (<c>SessionMissing</c> → 404), lock-state mapping
    /// (<c>AlreadyInFlight</c>/<c>Poisoned</c> → 409), AND the intentional recovery resume
    /// for stranded <c>Queued</c> / <c>Preparing+Snapshot</c> rows (→ 202). Routing those
    /// through the producer is required so admin retries can recover crashed cascades —
    /// blocking them at an HTTP gate strands the session until retention age.
    /// </para>
    /// </summary>
    public class DeleteSessionFunction
    {
        private readonly ILogger<DeleteSessionFunction> _logger;
        private readonly AdminConfigurationService _adminConfig;
        private readonly ISessionDeletionEnqueuer _enqueuer;

        public DeleteSessionFunction(
            ILogger<DeleteSessionFunction> logger,
            AdminConfigurationService adminConfig,
            ISessionDeletionEnqueuer enqueuer)
        {
            _logger = logger;
            _adminConfig = adminConfig;
            _enqueuer = enqueuer;
        }

        [Function("DeleteSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}")] HttpRequestData req,
            string sessionId)
        {
            _logger.LogInformation($"DeleteSession function processing request for session {sessionId}");

            try
            {
                // Authentication + TenantAdminOrGA authorization enforced by PolicyEnforcementMiddleware.
                // Tenant scoping is TenantScoping.QueryParam (catalog) so middleware validates the
                // optional ?tenantId=... against the caller's role and writes the resolved tenant
                // into RequestContext.TargetTenantId.
                var ctx = req.GetRequestContext();
                var tenantId = ctx.TargetTenantId;
                var userIdentifier = ctx.UserPrincipalName;

                _logger.LogInformation($"Deleting session {sessionId} for tenant {tenantId} by user {userIdentifier}");

                // Gate — Kill-switch. Uncached, fail-closed read so a flip-ON is honored across
                // scaled-out instances within seconds (not up to 5 min cache TTL). Runs before
                // the producer so 503 wins over any data-state response and no info leaks.
                var gateResult = EvaluateAdminDeleteGates(
                    killSwitchActive: await _adminConfig.IsSessionDeletionKillSwitchActiveAsync());
                if (gateResult.HasValue)
                {
                    _logger.LogWarning(
                        "DeleteSession rejected: SessionDeletionKillSwitch is active. tenant={TenantId} session={SessionId}",
                        tenantId, sessionId);
                    return await WriteResponse(req, gateResult.Value.Status, gateResult.Value.Body);
                }

                // Gate passed — hand off to the V2 cascade producer (handles 404, locked states,
                // and the Queued/Preparing+Snapshot recovery resume paths).
                return await RunV2CascadePathAsync(req, tenantId, sessionId, userIdentifier);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deleting session {sessionId}");

                return await WriteResponse(req, HttpStatusCode.InternalServerError, new
                {
                    success = false,
                    message = "Internal server error",
                });
            }
        }

        /// <summary>
        /// Pure pre-dispatch gate evaluation. Returns the kill-switch 503 short-circuit, or
        /// <c>null</c> to hand off to the producer.
        /// <para>
        /// Everything else — 404 SessionMissing, 409 AlreadyInFlight, 409 Poisoned, and the
        /// intentional <c>Queued</c> / <c>Preparing+Snapshot</c> → 202 Resume recovery paths —
        /// is owned by the producer. Adding an HTTP-level lock-state gate here would block the
        /// producer's recovery and strand admin-triggered cascades that crashed mid-flight.
        /// </para>
        /// </summary>
        internal static (HttpStatusCode Status, object Body)? EvaluateAdminDeleteGates(bool killSwitchActive)
        {
            if (killSwitchActive)
            {
                return (HttpStatusCode.ServiceUnavailable, new
                {
                    success = false,
                    message = "Session deletion is temporarily disabled by global kill-switch.",
                    hint = "kill_switch_active",
                });
            }

            return null;
        }

        /// <summary>
        /// V2 cascade path — enqueue a manifest-driven cascade and let the worker drain it.
        /// Returns 202 Accepted on success; translates the producer's enqueue outcomes to HTTP
        /// statuses via <see cref="MapEnqueueOutcomeToStatus"/>.
        /// </summary>
        private async Task<HttpResponseData> RunV2CascadePathAsync(
            HttpRequestData req, string tenantId, string sessionId, string userIdentifier)
        {
            var actor = new DeletionActor { Type = "admin", Actor = userIdentifier };
            var result = await _enqueuer.EnqueueAsync(tenantId, sessionId, "admin_delete", actor);

            var status = MapEnqueueOutcomeToStatus(result.Outcome);
            var body = BuildV2ResponseBody(result, sessionId);

            switch (result.Outcome)
            {
                case SessionDeletionEnqueueOutcome.Enqueued:
                    _logger.LogInformation(
                        "V2 cascade enqueued. tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                        tenantId, sessionId, result.ManifestId);
                    break;
                case SessionDeletionEnqueueOutcome.CasExhausted:
                    _logger.LogWarning(
                        "V2 cascade enqueue exhausted CAS retries. tenant={TenantId} session={SessionId}",
                        tenantId, sessionId);
                    break;
                case SessionDeletionEnqueueOutcome.AlreadyInFlight:
                case SessionDeletionEnqueueOutcome.Poisoned:
                case SessionDeletionEnqueueOutcome.KillSwitchActive:
                case SessionDeletionEnqueueOutcome.SessionNotFound:
                    break;
                default:
                    _logger.LogError(
                        "V2 cascade enqueue returned unexpected outcome {Outcome}. tenant={TenantId} session={SessionId}",
                        result.Outcome, tenantId, sessionId);
                    break;
            }

            return await WriteResponse(req, status, body);
        }

        /// <summary>
        /// Pure status mapping for the V2 enqueue outcomes. Exposed as a public static so tests
        /// can verify the contract without HTTP plumbing.
        /// </summary>
        public static HttpStatusCode MapEnqueueOutcomeToStatus(SessionDeletionEnqueueOutcome outcome) => outcome switch
        {
            SessionDeletionEnqueueOutcome.Enqueued          => HttpStatusCode.Accepted,
            SessionDeletionEnqueueOutcome.AlreadyInFlight   => HttpStatusCode.Conflict,
            SessionDeletionEnqueueOutcome.Poisoned          => HttpStatusCode.Conflict,
            SessionDeletionEnqueueOutcome.KillSwitchActive  => HttpStatusCode.ServiceUnavailable,
            SessionDeletionEnqueueOutcome.CasExhausted      => HttpStatusCode.ServiceUnavailable,
            SessionDeletionEnqueueOutcome.SessionNotFound   => HttpStatusCode.NotFound,
            _                                               => HttpStatusCode.InternalServerError,
        };

        /// <summary>
        /// Builds the JSON body for a V2-enqueue response. Internal so the test project
        /// can assert the shape without going through HttpResponseData mock plumbing.
        /// </summary>
        internal static object BuildV2ResponseBody(SessionDeletionEnqueueResult result, string sessionId) => result.Outcome switch
        {
            SessionDeletionEnqueueOutcome.Enqueued => new SessionDeletionQueuedResponse
            {
                Success = true,
                Status = "queued",
                ManifestId = result.ManifestId,
                Message = "Cascade deletion queued; worker will drain asynchronously.",
            },
            SessionDeletionEnqueueOutcome.AlreadyInFlight => new
            {
                success = false,
                message = "A cascade for this session is already in flight.",
                deletionState = result.ExistingState,
                manifestId = result.ManifestId,
                hint = "cascade_already_in_flight",
            },
            SessionDeletionEnqueueOutcome.Poisoned => new
            {
                success = false,
                message = "Cascade is poisoned; recover via POST /api/global/sessions/{id}/restore before retrying delete.",
                deletionState = result.ExistingState,
                manifestId = result.ManifestId,
                hint = "cascade_poisoned_use_restore",
            },
            SessionDeletionEnqueueOutcome.KillSwitchActive => new
            {
                success = false,
                message = "Session deletion is temporarily disabled by global kill-switch.",
                hint = "kill_switch_active",
            },
            SessionDeletionEnqueueOutcome.CasExhausted => new
            {
                success = false,
                message = "Cascade enqueue exhausted retries; please retry shortly.",
                hint = "cas_exhausted_retry_later",
            },
            SessionDeletionEnqueueOutcome.SessionNotFound => new
            {
                success = false,
                message = $"Session {sessionId} not found",
            },
            _ => new
            {
                success = false,
                message = "Internal server error",
            },
        };

        private static async Task<HttpResponseData> WriteResponse(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(body);
            return response;
        }
    }
}
