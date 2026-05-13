using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>POST /api/admin/sessions/{sessionId}/restore</c> — GA-only cascade-delete restore
    /// endpoint (plan §13, PR4b). Body: <c>{ "manifestId": "...", "dryRun": false }</c>.
    /// Dispatches into <see cref="SessionRestoreService"/> which auto-selects full vs
    /// partial-poisoned-recovery mode based on the (Sessions row state, progress.CompletedAt)
    /// tuple. Status mapping:
    /// <list type="bullet">
    ///   <item>200 OK — restore (or dry-run) succeeded, body carries the row counts.</item>
    ///   <item>400 Bad Request — missing/invalid body, missing manifestId.</item>
    ///   <item>404 Not Found — manifest blob missing (GC'd past 33-day window) or session unknown.</item>
    ///   <item>409 Conflict — every <c>Reject*</c> outcome (state mismatch, manifest corruption, already at None, manifestId mismatch, CAS conflict on clear).</item>
    /// </list>
    /// Route registration in <c>EndpointAccessPolicyCatalog</c> with
    /// <c>EndpointPolicy.GlobalAdminOnly</c> is what makes this GA-only; unregistered routes
    /// fail-closed → 403 (per memory <c>feedback_route_policy_catalog</c>).
    /// </summary>
    public class RestoreSessionFunction
    {
        private readonly ILogger<RestoreSessionFunction> _logger;
        private readonly SessionRestoreService _restoreService;
        private readonly ISessionRepository _sessionRepo;

        public RestoreSessionFunction(
            ILogger<RestoreSessionFunction> logger,
            SessionRestoreService restoreService,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _restoreService = restoreService;
            _sessionRepo = sessionRepo;
        }

        [Function("RestoreSession")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/sessions/{sessionId}/restore")] HttpRequestData req,
            string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest, new { success = false, message = "sessionId is required" });
            }

            // Parse body.
            RestoreRequestBody? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<RestoreRequestBody>(
                    req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "RestoreSession: malformed body for session {SessionId}", sessionId);
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                    new { success = false, message = "Request body is not valid JSON. Expected: { \"manifestId\": \"...\", \"dryRun\": false }" });
            }
            if (body == null || string.IsNullOrWhiteSpace(body.ManifestId))
            {
                return await WriteJsonAsync(req, HttpStatusCode.BadRequest,
                    new { success = false, message = "manifestId is required in the request body." });
            }

            // Tenant resolution. Codex F1 review: an explicit body.TenantId from a GA caller is
            // the only path that lets a Global Admin restore a session in a tenant other than
            // their JWT home tenant. Order:
            //   1. Explicit body.TenantId on GA calls (cross-tenant restore — the common case
            //      when restoring from the Session Cleanup admin page).
            //   2. JWT TargetTenantId (single-tenant operators).
            //   3. SessionsIndex lookup keyed by sessionId (rare but preserved).
            //   4. body.TenantId again — last-resort fallback for the "Sessions row gone" path
            //      where neither the index nor the JWT tenant can resolve the target.
            var requestCtx = req.GetRequestContext();
            var actorEmail = TenantHelper.GetUserIdentifier(req);
            string tenantId;
            if (requestCtx.IsGlobalAdmin
                && !string.IsNullOrWhiteSpace(body.TenantId)
                && Guid.TryParse(body.TenantId, out _))
            {
                tenantId = body.TenantId!;
            }
            else
            {
                tenantId = requestCtx.TargetTenantId;
                if (string.IsNullOrEmpty(tenantId) || string.Equals(tenantId, "global", StringComparison.OrdinalIgnoreCase))
                {
                    tenantId = await _sessionRepo.FindSessionTenantIdAsync(sessionId) ?? string.Empty;
                }
                if (string.IsNullOrEmpty(tenantId))
                {
                    // Sessions row is gone — but a full restore IS legal in that case. The
                    // manifest blob path encodes tenantId, but the operator only gives us
                    // sessionId + manifestId. Require the operator to scope the call by passing
                    // the tenantId in the request body (or via the X-Target-Tenant header).
                    if (string.IsNullOrWhiteSpace(body.TenantId))
                    {
                        return await WriteJsonAsync(req, HttpStatusCode.NotFound, new
                        {
                            success = false,
                            message = $"Session {sessionId} not found in any active SessionsIndex entry. " +
                                      "If the cascade completed (Sessions row gone), pass the tenantId explicitly in the request body."
                        });
                    }
                    tenantId = body.TenantId!;
                }
            }

            _logger.LogInformation(
                "RestoreSession: tenant={TenantId} session={SessionId} manifestId={ManifestId} dryRun={DryRun} actor={Actor}",
                tenantId, sessionId, body.ManifestId, body.DryRun, actorEmail);

            // Trim + cap operator-supplied reason so it can't bloat the audit-row PartitionKey
            // payload or be used to smuggle large blobs through. 1024 chars matches the cap the
            // handler uses for LastFailureMessage in DeletionProgress (same compliance bucket).
            var reason = body.Reason;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                reason = reason!.Trim();
                if (reason.Length > 1024) reason = reason.Substring(0, 1024);
            }
            else
            {
                reason = null;
            }

            SessionRestoreResult result;
            try
            {
                result = await _restoreService.RestoreAsync(
                    tenantId, sessionId, body.ManifestId,
                    body.DryRun, actorEmail, reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "RestoreSession: unhandled exception for tenant={TenantId} session={SessionId} manifestId={ManifestId}",
                    tenantId, sessionId, body.ManifestId);
                return await WriteJsonAsync(req, HttpStatusCode.InternalServerError,
                    new { success = false, message = "Internal error during restore — see audit log + telemetry for details.", exceptionType = ex.GetType().Name });
            }

            return await WriteResultAsync(req, result);
        }

        /// <summary>
        /// Pure status-mapping function for the restore outcome → HTTP status code translation.
        /// Extracted as static + public so unit tests can verify it without spinning up the full
        /// HttpRequestData mocking machinery.
        /// </summary>
        public static HttpStatusCode MapOutcomeToStatus(SessionRestoreOutcome outcome) => outcome switch
        {
            SessionRestoreOutcome.Restored => HttpStatusCode.OK,
            SessionRestoreOutcome.DryRunOk => HttpStatusCode.OK,
            SessionRestoreOutcome.RejectManifestNotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.Conflict,
        };

        private static async Task<HttpResponseData> WriteResultAsync(HttpRequestData req, SessionRestoreResult result)
        {
            var status = MapOutcomeToStatus(result.Outcome);

            var body = new
            {
                success = status == HttpStatusCode.OK,
                outcome = result.Outcome.ToString(),
                mode = result.Mode,
                message = result.Message,
                currentState = result.CurrentState,
                pendingManifestId = result.PendingManifestId,
                rowsRestoredByTable = result.RowsRestoredByTable,
                rowsSkippedByTable = result.RowsSkippedByTable,
                wouldRestoreByTable = result.WouldRestoreByTable,
                inventoryReIncrements = result.InventoryReIncrements,
                durationMs = result.DurationMs,
            };

            return await WriteJsonAsync(req, status, body);
        }

        private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            await response.WriteAsJsonAsync(body);
            return response;
        }

        private sealed class RestoreRequestBody
        {
            public string ManifestId { get; set; } = string.Empty;
            public bool DryRun { get; set; }
            /// <summary>
            /// Optional: when the Sessions row is already gone (full-restore case after a completed
            /// cascade), there's no SessionsIndex entry to look up the tenant from. Operators
            /// should provide tenantId explicitly in this scenario.
            /// </summary>
            public string? TenantId { get; set; }
            /// <summary>
            /// Optional free-text justification. Persisted into the <c>deletion_restored</c> audit
            /// row's <c>reason</c> detail so the operator's intent (customer ticket, compliance
            /// request, …) is recoverable from the tenant audit trail. Codex follow-up to the
            /// PR-B audit consolidation: previously the UI sent this but the backend dropped it.
            /// Trimmed + capped at 1024 chars by the function before being passed to the service.
            /// </summary>
            public string? Reason { get; set; }
        }
    }
}
