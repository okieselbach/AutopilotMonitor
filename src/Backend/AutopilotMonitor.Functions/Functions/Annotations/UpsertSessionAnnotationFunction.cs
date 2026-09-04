using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Annotations
{
    /// <summary>
    /// Upserts (or clears) the annotation for one session + lane.
    /// Policy tier is TenantAdminOrOperator; the per-lane write matrix is re-gated here
    /// via <see cref="IsLaneWritableByCaller"/> (catalog multi-kind rule). Author fields
    /// are stamped from the JWT — a body-supplied author never wins.
    /// </summary>
    public class UpsertSessionAnnotationFunction
    {
        private readonly ILogger<UpsertSessionAnnotationFunction> _logger;
        private readonly ISessionAnnotationRepository _annotationRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IRuleRepository _ruleRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public UpsertSessionAnnotationFunction(
            ILogger<UpsertSessionAnnotationFunction> logger,
            ISessionAnnotationRepository annotationRepo,
            ISessionRepository sessionRepo,
            IRuleRepository ruleRepo,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _annotationRepo = annotationRepo;
            _sessionRepo = sessionRepo;
            _ruleRepo = ruleRepo;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("UpsertSessionAnnotation")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "sessions/{sessionId}/annotations/{lane}")] HttpRequestData req,
            string sessionId,
            string lane)
        {
            try
            {
                // Authentication + TenantAdminOrOperator authorization enforced by
                // PolicyEnforcementMiddleware; the lane matrix is re-gated below.
                var requestCtx = req.GetRequestContext();
                var userIdentifier = TenantHelper.GetUserIdentifier(req);

                var normalizedLane = lane?.ToLowerInvariant() ?? string.Empty;
                if (!AnnotationLanes.All.Contains(normalizedLane))
                {
                    return await req.BadRequestAsync($"lane must be one of: {string.Join(", ", AnnotationLanes.All)}");
                }

                // Tenant identity: non-GA callers are pinned to their middleware-validated
                // tenant (never a body value — prevents horizontal escalation). Global
                // Admins may annotate foreign-tenant sessions (the GA labeling flow), so
                // resolve the session's actual tenant like the other session reads do —
                // requireGlobalAdmin because this is a WRITE (a read-only GlobalReader
                // must never steer it cross-tenant). Resolution happens BEFORE the lane
                // gate: tenant-role lanes must bind to the caller's OWN tenant, so a GA
                // who is also an own-tenant admin cannot write another tenant's
                // operator/tenantadmin lanes.
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(
                    _sessionRepo, sessionId, requireGlobalAdmin: true);

                var isOwnTenant = string.Equals(effectiveTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase);
                if (!IsLaneWritableByCaller(
                        normalizedLane, isOwnTenant ? requestCtx.UserRole : null,
                        requestCtx.IsTenantAdmin && isOwnTenant, requestCtx.IsGlobalAdmin))
                {
                    _logger.LogWarning(
                        "UpsertSessionAnnotation: BLOCKED lane={Lane} for user={User} role={Role} ownTenant={OwnTenant}",
                        normalizedLane, userIdentifier, requestCtx.UserRole, isOwnTenant);
                    return await req.ForbiddenAsync($"Your role does not permit writing the '{normalizedLane}' annotation lane.");
                }

                // Annotations must not create junk rows for sessions that don't exist.
                var session = await _sessionRepo.GetSessionAsync(effectiveTenantId, sessionId);
                if (session == null)
                {
                    return await req.NotFoundAsync("Session not found.");
                }

                var body = await req.ReadAsStringAsync() ?? string.Empty;
                JObject json;
                try
                {
                    json = JObject.Parse(body);
                }
                catch (JsonException)
                {
                    return await req.BadRequestAsync("Invalid JSON body.");
                }

                var verdict = ReadOptionalString(json, "verdict")?.ToLowerInvariant();
                var note = ReadOptionalString(json, "note");

                if (verdict != null && !AnnotationVerdicts.All.Contains(verdict))
                {
                    return await req.BadRequestAsync($"verdict must be one of: {string.Join(", ", AnnotationVerdicts.All)}");
                }
                if (note != null && note.Length > SessionAnnotation.MaxNoteLength)
                {
                    return await req.BadRequestAsync($"note must be at most {SessionAnnotation.MaxNoteLength} characters.");
                }

                // Anti-spoof: author identity comes from the JWT, never from the body
                // (same stamp as the rule PUT-upserts).
                var authorDisplayName = TenantHelper.GetUserDisplayName(req) ?? "Autopilot Monitor";

                // Both fields empty = clear the lane.
                if (verdict == null && note == null)
                {
                    await _annotationRepo.DeleteAsync(effectiveTenantId, sessionId, normalizedLane);
                    await LogAuditAsync(requestCtx.IsGlobalAdmin, effectiveTenantId, "DELETE",
                        sessionId, normalizedLane, userIdentifier, verdict: null);
                    _logger.LogInformation(
                        "Annotation cleared for session {SessionId} lane {Lane} by {User}",
                        sessionId, normalizedLane, userIdentifier);
                    return await req.OkAsync(new UpsertSessionAnnotationDeletedResponse { Success = true, Deleted = true });
                }

                var existing = await _annotationRepo.GetAsync(effectiveTenantId, sessionId, normalizedLane);
                var now = DateTime.UtcNow;
                var annotation = new SessionAnnotation
                {
                    TenantId = effectiveTenantId,
                    SessionId = sessionId,
                    Lane = normalizedLane,
                    Verdict = verdict,
                    Note = note,
                    AuthorUpn = userIdentifier,
                    AuthorDisplayName = authorDisplayName,
                    CreatedByUpn = existing?.CreatedByUpn ?? userIdentifier,
                    CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                    UpdatedAtUtc = now,
                    RuleIds = await SnapshotRuleIdsAsync(effectiveTenantId, sessionId),
                };

                await _annotationRepo.UpsertAsync(annotation);
                await LogAuditAsync(requestCtx.IsGlobalAdmin, effectiveTenantId, "UPDATE",
                    sessionId, normalizedLane, userIdentifier, verdict);

                _logger.LogInformation(
                    "Annotation saved for session {SessionId} lane {Lane} by {User} (verdict={Verdict})",
                    sessionId, normalizedLane, userIdentifier, verdict ?? "none");

                return await req.OkAsync(new UpsertSessionAnnotationResponse
                {
                    Success = true,
                    Annotation = AnnotationWire.ToWire(annotation),
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "UpsertSessionAnnotation");
            }
        }

        /// <summary>
        /// Per-lane write matrix (policy tier already limited callers to tenant
        /// Admin/Operator or GA):
        ///   operator    → Operator or Tenant Admin (admins supervise operator notes)
        ///   tenantadmin → Tenant Admin only
        ///   globaladmin → Global Admin only (GA writes ONLY this lane — clean attribution;
        ///                 a GA who is also an own-tenant admin still passes IsTenantAdmin
        ///                 for that tenant's lanes)
        /// </summary>
        internal static bool IsLaneWritableByCaller(
            string lane, string? userRole, bool isTenantAdmin, bool isGlobalAdmin)
        {
            return lane switch
            {
                AnnotationLanes.GlobalAdmin => isGlobalAdmin,
                AnnotationLanes.TenantAdmin => isTenantAdmin,
                AnnotationLanes.Operator => isTenantAdmin
                    || string.Equals(userRole, Constants.TenantRoles.Operator, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        /// <summary>
        /// Snapshot of the rule ids that fired for this session, denormalized onto the
        /// annotation so rule-quality evaluation needs no join. Fail-soft: an empty list
        /// never blocks the write.
        /// </summary>
        private async Task<List<string>> SnapshotRuleIdsAsync(string tenantId, string sessionId)
        {
            try
            {
                var results = await _ruleRepo.GetRuleResultsAsync(tenantId, sessionId);
                return results
                    .Select(r => r.RuleId)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Annotation rule-id snapshot failed for session {SessionId}; storing empty list", sessionId);
                return new List<string>();
            }
        }

        // Audit entries land in the tenant-visible audit log; GA writes are skipped so
        // platform-internal labeling activity does not surface there (same convention as
        // session-report submissions by GAs).
        private async Task LogAuditAsync(
            bool isGlobalAdmin, string tenantId, string action,
            string sessionId, string lane, string userIdentifier, string? verdict)
        {
            if (isGlobalAdmin) return;
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                action,
                "SessionAnnotation",
                $"{sessionId}/{lane}",
                userIdentifier,
                new Dictionary<string, string>
                {
                    { "Action", "UpsertSessionAnnotation" },
                    { "SessionId", sessionId },
                    { "Lane", lane },
                    { "Verdict", verdict ?? string.Empty },
                });
        }

        private static string? ReadOptionalString(JObject json, string property)
        {
            var token = json[property];
            if (token == null || token.Type == JTokenType.Null) return null;
            var value = token.ToString().Trim();
            return value.Length == 0 ? null : value;
        }

    }
}
