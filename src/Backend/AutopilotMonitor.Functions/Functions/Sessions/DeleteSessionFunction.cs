using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// Admin-triggered session deletion. Plan §5 PR5 wires this function as a flag-gated
    /// dispatcher between the legacy direct-delete path (today's default) and the V2 cascade
    /// path (<see cref="ISessionDeletionEnqueuer"/>).
    /// <para>
    /// Order matters: kill-switch + <c>DeletionState != None</c> are checked <b>before</b> the
    /// per-tenant feature flag — both paths must respect them so a kill-switch flip or a
    /// half-completed V2 cascade is never stepped on by a legacy delete (plan §1 P8 / §6).
    /// </para>
    /// </summary>
    public class DeleteSessionFunction
    {
        private readonly ILogger<DeleteSessionFunction> _logger;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly AdminConfigurationService _adminConfig;
        private readonly TenantConfigurationService _tenantConfig;
        private readonly ISessionDeletionEnqueuer _enqueuer;

        public DeleteSessionFunction(
            ILogger<DeleteSessionFunction> logger,
            ISessionRepository sessionRepo,
            IMaintenanceRepository maintenanceRepo,
            AdminConfigurationService adminConfig,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
            _maintenanceRepo = maintenanceRepo;
            _adminConfig = adminConfig;
            _tenantConfig = tenantConfig;
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
                // into RequestContext.TargetTenantId. For non-GA users the query param is constrained
                // to their own JWT tenant; GAs may target any tenant. The old TenantHelper.GetTenantId
                // path read the JWT tenant only, which made every Global-Admin DELETE silently target
                // the GA's home tenant instead of the requested one (Codex-followup F1).
                var ctx = req.GetRequestContext();
                var tenantId = ctx.TargetTenantId;
                var userIdentifier = ctx.UserPrincipalName;

                _logger.LogInformation($"Deleting session {sessionId} for tenant {tenantId} by user {userIdentifier}");

                // ── Step 1: global kill-switch — applied to BOTH paths (plan §1 P8 / §9). ─────────
                // PR5 finding 1: must NOT go through the 5-minute-cached GetConfigurationAsync,
                // otherwise a flip-ON is not honored across scaled-out instances until each one's
                // cache expires independently. IsSessionDeletionKillSwitchActiveAsync is the
                // direct (uncached, fail-closed) read.
                if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync())
                {
                    _logger.LogWarning(
                        "DeleteSession rejected: SessionDeletionKillSwitch is active. tenant={TenantId} session={SessionId}",
                        tenantId, sessionId);
                    return await WriteResponse(req, HttpStatusCode.ServiceUnavailable, new
                    {
                        success = false,
                        message = "Session deletion is temporarily disabled by global kill-switch.",
                        hint = "kill_switch_active",
                    });
                }

                // ── Step 2: existence + cascade-lock check — applied to BOTH paths (plan §6). ────
                // Reads the Sessions row once to (a) detect 404 and (b) check DeletionState so the
                // legacy path can NEVER step on a half-completed V2 cascade (plan §1 P8 — last
                // sentence: "legacy direct-delete path **also** respects DeletionState != None").
                var session = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
                if (session == null)
                {
                    _logger.LogWarning($"Session {sessionId} not found");
                    return await WriteResponse(req, HttpStatusCode.NotFound, new
                    {
                        success = false,
                        message = $"Session {sessionId} not found",
                    });
                }

                if (SessionDeletionState.IsLocked(session.DeletionState))
                {
                    _logger.LogWarning(
                        "DeleteSession rejected: session already in cascade state {State} (manifestId={ManifestId}). tenant={TenantId} session={SessionId}",
                        session.DeletionState, session.PendingDeletionManifestId, tenantId, sessionId);

                    // Poisoned needs a different hint — operator must use the restore endpoint.
                    var poisonedHint = string.Equals(session.DeletionState, SessionDeletionState.Poisoned, StringComparison.Ordinal);
                    return await WriteResponse(req, HttpStatusCode.Conflict, new
                    {
                        success = false,
                        message = poisonedHint
                            ? "Cascade is poisoned; recover via POST /api/admin/sessions/{id}/restore before retrying delete."
                            : "Session is already being deleted by a cascade.",
                        deletionState = session.DeletionState,
                        manifestId = session.PendingDeletionManifestId,
                        hint = poisonedHint ? "cascade_poisoned_use_restore" : "cascade_already_in_flight",
                    });
                }

                // ── Step 3: per-tenant V2 cascade flag — selects path. ──────────────────────────
                var tenantCfg = await _tenantConfig.GetConfigurationAsync(tenantId);
                if (tenantCfg.EnableCascadeDeleteV2)
                {
                    return await RunV2CascadePathAsync(req, tenantId, sessionId, userIdentifier);
                }

                return await RunLegacyDirectDeletePathAsync(req, tenantId, sessionId, userIdentifier);
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
        /// V2 cascade path — enqueue a manifest-driven cascade and let the worker drain it.
        /// Returns 202 Accepted on success; translates the producer's enqueue outcomes to
        /// HTTP statuses per plan §5 PR5.
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
        /// Pure status mapping for the V2 enqueue outcomes (plan §5 PR5). Exposed as a
        /// public static so tests can verify the contract without HTTP plumbing — same
        /// pattern as <see cref="RestoreSessionFunction.MapOutcomeToStatus"/>.
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
            SessionDeletionEnqueueOutcome.Enqueued => new
            {
                success = true,
                status = "queued",
                manifestId = result.ManifestId,
                message = "Cascade deletion queued; worker will drain asynchronously.",
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
                message = "Cascade is poisoned; recover via POST /api/admin/sessions/{id}/restore before retrying delete.",
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

        /// <summary>
        /// Legacy direct-delete path — preserved for the flag-OFF default. Removed by
        /// plan §5 PR7 once V2 has been stable on all tenants for at least two weeks.
        /// <para>
        /// <b>Ordering</b> (Sessions-first): the ETag-CAS-guarded <c>DeleteSessionAsync</c> runs
        /// <i>before</i> any side-table delete. This closes the TOCTOU window where a V2
        /// cascade producer could CAS-claim <c>DeletionState=Preparing</c> after our pre-read
        /// but before our side-table mutations — leaving the producer's manifest snapshot
        /// inconsistent against an already-half-purged side-table state. Side-table cleanup
        /// after the Sessions row is tombstoned is safe: no producer can claim a missing row.
        /// </para>
        /// </summary>
        private async Task<HttpResponseData> RunLegacyDirectDeletePathAsync(
            HttpRequestData req, string tenantId, string sessionId, string userIdentifier)
        {
            // Sessions-first: tombstone (and release the writer-lock semantics) before any
            // side-table mutation. DeleteSessionAsync is ETag-CAS-guarded (PR5 finding 2)
            // against in-flight V2 cascades.
            var sessionDeleted = await _sessionRepo.DeleteSessionAsync(tenantId, sessionId);

            if (!sessionDeleted)
            {
                // DeleteSessionAsync returned false. Either 404 (already gone) or a concurrent
                // V2 producer CAS-claimed the row (ETag 412 / non-None DeletionState). Re-read
                // to disambiguate and pick the right status code.
                var refreshed = await _sessionRepo.GetSessionAsync(tenantId, sessionId);
                if (refreshed == null)
                {
                    _logger.LogWarning($"Session {sessionId} not found at final delete (race with concurrent caller)");
                    return await WriteResponse(req, HttpStatusCode.NotFound, new
                    {
                        success = false,
                        message = $"Session {sessionId} not found",
                    });
                }
                if (SessionDeletionState.IsLocked(refreshed.DeletionState))
                {
                    _logger.LogWarning(
                        "Legacy delete refused: V2 cascade owns the session. " +
                        "tenant={TenantId} session={SessionId} state={State}",
                        tenantId, sessionId, refreshed.DeletionState);
                    var poisonedHint = string.Equals(refreshed.DeletionState, SessionDeletionState.Poisoned, StringComparison.Ordinal);
                    return await WriteResponse(req, HttpStatusCode.Conflict, new
                    {
                        success = false,
                        message = poisonedHint
                            ? "Cascade is poisoned; recover via POST /api/admin/sessions/{id}/restore before retrying delete."
                            : "A V2 cascade owns this session; retry later or use the cascade-delete path.",
                        deletionState = refreshed.DeletionState,
                        manifestId = refreshed.PendingDeletionManifestId,
                        hint = poisonedHint ? "cascade_poisoned_use_restore" : "cascade_already_in_flight",
                    });
                }
                _logger.LogError($"Legacy delete failed for session {sessionId} without an identifiable race cause");
                return await WriteResponse(req, HttpStatusCode.InternalServerError, new
                {
                    success = false,
                    message = "Internal server error",
                });
            }

            // Sessions row is gone — no V2 producer can claim it now. Safe to cascade-clean
            // side tables without ordering races.
            var eventsDeleted = await _maintenanceRepo.DeleteSessionEventsAsync(tenantId, sessionId);
            _logger.LogInformation($"Deleted {eventsDeleted} events for session {sessionId}");

            var ruleResultsDeleted = await _maintenanceRepo.DeleteSessionRuleResultsAsync(tenantId, sessionId);
            _logger.LogInformation($"Deleted {ruleResultsDeleted} rule results for session {sessionId}");

            var appSummariesDeleted = await _maintenanceRepo.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId);
            _logger.LogInformation($"Deleted {appSummariesDeleted} app install summaries for session {sessionId}");

            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                "DELETE",
                "Session",
                sessionId,
                userIdentifier,
                new Dictionary<string, string>
                {
                    { "EventsDeleted", eventsDeleted.ToString() },
                    { "RuleResultsDeleted", ruleResultsDeleted.ToString() },
                    { "AppInstallSummariesDeleted", appSummariesDeleted.ToString() }
                });

            _logger.LogInformation($"Successfully deleted session {sessionId}");
            return await WriteResponse(req, HttpStatusCode.OK, new
            {
                success = true,
                message = $"Session {sessionId} deleted successfully",
                eventsDeleted,
                ruleResultsDeleted,
                appInstallSummariesDeleted = appSummariesDeleted,
            });
        }

        private static async Task<HttpResponseData> WriteResponse(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(body);
            return response;
        }
    }
}
