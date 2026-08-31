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
        /// Non-global callers additionally only see versions that <see cref="IsFleetConfirmed"/>:
        /// a row is created by whatever string a device reports, and one tenant's devices must
        /// not be able to publish an unverified "version" into every other tenant's rollout view.
        /// Global scope sees every row, unconfirmed ones included — that is where a bogus claim
        /// is investigated.
        ///
        /// Returned as <c>IReadOnlyList&lt;object&gt;</c> — System.Text.Json serializes each
        /// element using its RUNTIME type, so both branches keep the exact wire format they
        /// had while the projection was inline. Both element types are manifest-exported
        /// (<see cref="ImeVersionHistoryEntry"/> / <see cref="ImeVersionHistoryLeanEntry"/>).
        /// </summary>
        public static IReadOnlyList<object> BuildResponsePayload(
            IEnumerable<ImeVersionHistoryEntry> versions, bool hasGlobalScope)
        {
            if (hasGlobalScope)
            {
                return versions.ToList();
            }

            return versions
                .Where(IsFleetConfirmed)
                .Select(v => new ImeVersionHistoryLeanEntry
                {
                    Version = v.Version,
                    FirstSeenAt = v.FirstSeenAt,
                    LastSeenAt = v.LastSeenAt,
                    SessionCount = v.SessionCount,
                })
                .ToList();
        }

        /// <summary>
        /// Rows first seen before this instant predate both the version guard and the
        /// corroboration stamp. The whole table was reviewed on 2026-08-30 (16 rows, all
        /// genuine Microsoft builds), so they are trusted as-is instead of vanishing from the
        /// tenant view until a second tenant happens to report a version nobody runs any more.
        /// </summary>
        public static readonly DateTime LegacyTrustCutoffUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// A version is confirmed for the tenant view when evidence beyond the first-seen
        /// tenant's own claim exists: the installer was archived with a matching MSI
        /// ProductVersion (<c>ImeMsiArchiver</c>), or a SECOND tenant reported it
        /// (<see cref="ImeVersionHistoryEntry.CorroboratedAt"/>), or the row predates the guard.
        /// </summary>
        public static bool IsFleetConfirmed(ImeVersionHistoryEntry entry) =>
            string.Equals(entry.MsiArchiveStatus, Services.Ime.ImeMsiArchiver.Statuses.Archived, StringComparison.Ordinal)
            || entry.CorroboratedAt.HasValue
            || entry.FirstSeenAt < LegacyTrustCutoffUtc;
    }
}
