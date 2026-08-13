using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Annotations
{
    /// <summary>
    /// Returns the annotations (all lanes visible to the caller) for one session.
    /// The <c>globaladmin</c> lane is platform-internal and filtered out for callers
    /// without global scope.
    /// </summary>
    public class GetSessionAnnotationsFunction
    {
        private readonly ILogger<GetSessionAnnotationsFunction> _logger;
        private readonly ISessionAnnotationRepository _annotationRepo;
        private readonly ISessionRepository _sessionRepo;

        public GetSessionAnnotationsFunction(
            ILogger<GetSessionAnnotationsFunction> logger,
            ISessionAnnotationRepository annotationRepo,
            ISessionRepository sessionRepo)
        {
            _logger = logger;
            _annotationRepo = annotationRepo;
            _sessionRepo = sessionRepo;
        }

        [Function("GetSessionAnnotations")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/annotations")] HttpRequestData req,
            string sessionId)
        {
            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware.
                // Global-scope (GA or read-only GlobalReader) callers resolve the session's actual
                // tenant upfront so the read works cross-tenant.
                var requestCtx = req.GetRequestContext();
                var effectiveTenantId = await requestCtx.ResolveSessionScopeAsync(_sessionRepo, sessionId);

                var annotations = await _annotationRepo.GetForSessionAsync(effectiveTenantId, sessionId);
                var visible = FilterLanesForCaller(annotations, requestCtx.HasGlobalScope);

                // Server-computed write matrix: the SAME function the PUT re-gates with,
                // fed with the same own-tenant binding. The web renders lanes writable
                // exactly when this list says so — it holds no copy of the matrix.
                var isOwnTenant = string.Equals(effectiveTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase);
                var writableLanes = AnnotationLanes.All
                    .Where(l => UpsertSessionAnnotationFunction.IsLaneWritableByCaller(
                        l,
                        isOwnTenant ? requestCtx.UserRole : null,
                        requestCtx.IsTenantAdmin && isOwnTenant,
                        requestCtx.IsGlobalAdmin))
                    .ToList();

                return await req.OkAsync(new
                {
                    success = true,
                    sessionId,
                    tenantId = effectiveTenantId,
                    annotations = visible.Select(AnnotationWire.ToWire).ToList(),
                    writableLanes,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching annotations for session {SessionId}", sessionId);
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }

        /// <summary>
        /// The globaladmin lane is a platform-internal quality label — callers without
        /// global scope (tenant members and delegated admins) never see it.
        /// </summary>
        internal static List<SessionAnnotation> FilterLanesForCaller(
            List<SessionAnnotation> annotations, bool hasGlobalScope)
        {
            if (hasGlobalScope) return annotations;
            return annotations
                .Where(a => !string.Equals(a.Lane, AnnotationLanes.GlobalAdmin, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>
    /// Explicit camelCase wire shape shared by the annotation endpoints so the JSON
    /// contract does not depend on serializer naming configuration.
    /// </summary>
    internal static class AnnotationWire
    {
        internal static object ToWire(SessionAnnotation a) => new
        {
            lane = a.Lane,
            verdict = a.Verdict,
            note = a.Note,
            authorUpn = a.AuthorUpn,
            authorDisplayName = a.AuthorDisplayName,
            createdByUpn = a.CreatedByUpn,
            createdAtUtc = a.CreatedAtUtc,
            updatedAtUtc = a.UpdatedAtUtc,
            ruleIds = a.RuleIds,
        };

        internal static object ToWireWithScope(SessionAnnotation a) => new
        {
            tenantId = a.TenantId,
            sessionId = a.SessionId,
            lane = a.Lane,
            verdict = a.Verdict,
            note = a.Note,
            authorUpn = a.AuthorUpn,
            authorDisplayName = a.AuthorDisplayName,
            createdByUpn = a.CreatedByUpn,
            createdAtUtc = a.CreatedAtUtc,
            updatedAtUtc = a.UpdatedAtUtc,
            ruleIds = a.RuleIds,
        };
    }
}
