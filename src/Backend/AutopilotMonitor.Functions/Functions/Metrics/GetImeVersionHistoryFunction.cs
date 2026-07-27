using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Returns all known IME agent versions with first/last seen dates and session counts.
    /// Permanent archive that survives data retention — tracks Microsoft IME releases over time.
    /// Access: MemberRead (any tenant member can see version/dates/counts).
    /// FirstSeenTenantId and FirstSeenSessionId are only visible to Global Admins.
    /// </summary>
    public class GetImeVersionHistoryFunction
    {
        private readonly ILogger<GetImeVersionHistoryFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetImeVersionHistoryFunction(ILogger<GetImeVersionHistoryFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetImeVersionHistory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/ime-versions")] HttpRequestData req)
        {
            try
            {
                var requestCtx = req.GetRequestContext();
                var versions = await _sessionRepo.GetImeVersionHistoryAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(BuildResponsePayload(versions, requestCtx.HasGlobalScope));
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching IME version history");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Failed to retrieve IME version history" });
                return errorResponse;
            }
        }

        /// <summary>
        /// Projects the archive for the caller's scope.
        ///
        /// Extracted from <see cref="Run"/> so the cross-tenant redaction boundary is
        /// directly testable: this suite has no HTTP-level harness, so an inline projection
        /// is unreachable from a test — and an unguarded redaction is exactly what a later
        /// refactor opens silently.
        ///
        /// FirstSeenTenantId and FirstSeenSessionId name ANOTHER tenant's device and are
        /// dropped for non-global callers. Version, dates and SessionCount deliberately stay:
        /// the archive is a platform-wide view of Microsoft's IME rollout, and the route is
        /// MemberRead precisely so any tenant member can read that view.
        ///
        /// Returned as <see cref="object"/> on purpose — System.Text.Json serializes an
        /// `object` declared type using the RUNTIME type, so both branches keep the exact
        /// wire format they had while the projection was inline.
        /// </summary>
        public static object BuildResponsePayload(
            IEnumerable<ImeVersionHistoryEntry> versions, bool hasGlobalScope)
        {
            if (hasGlobalScope)
            {
                return versions.ToList();
            }

            return versions
                .Select(v => new { v.Version, v.FirstSeenAt, v.LastSeenAt, v.SessionCount })
                .ToList();
        }
    }
}
