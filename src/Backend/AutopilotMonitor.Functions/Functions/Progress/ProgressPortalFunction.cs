using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Progress;

/// <summary>
/// Dedicated Progress Portal endpoints — an END-USER feature: employees tracking their own device
/// are authenticated (JWT tid = their tenant) but typically hold NO member role, so these routes
/// must not be Member-gated (regression history c4dabeee: elevating the portal's plumbing to
/// MemberRead silently froze it for its actual audience).
///
/// Authorization model instead: serial-number knowledge proof. The tenant-wide session list is
/// never shipped to the browser; the caller looks a session up by its exact serial (or device
/// name) and every follow-up read re-presents that serial (<see cref="SerialKnowledgeProof"/>).
/// Members/GA additionally keep substring search — for them the same data is MemberRead at REST.
/// </summary>
public class ProgressPortalFunction
{
    private readonly ILogger<ProgressPortalFunction> _logger;
    private readonly ISessionRepository _sessionRepo;

    /// <summary>How many latest sessions the lookup scans — the portal's pre-existing horizon.</summary>
    internal const int LookupPageSize = 100;

    public ProgressPortalFunction(
        ILogger<ProgressPortalFunction> logger,
        ISessionRepository sessionRepo)
    {
        _logger = logger;
        _sessionRepo = sessionRepo;
    }

    /// <summary>
    /// GET /api/progress/sessions/lookup?tenantId={tid}&amp;search={serialOrDeviceName}
    /// Resolves at most ONE session from the search term, server-side. Roleless callers need an
    /// exact serial/device-name match (the knowledge proof); members/GA keep substring matching.
    /// </summary>
    [Function("ProgressLookupSession")]
    public async Task<HttpResponseData> LookupSession(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions/lookup")] HttpRequestData req)
    {
        try
        {
            // Authentication, AuthenticatedUserWithRole authz (role flags resolved), AND cross-tenant
            // access enforced by PolicyEnforcementMiddleware (catalog: TenantScoping.QueryParam).
            var requestCtx = req.GetRequestContext();

            var query = HttpUtility.ParseQueryString(req.Url.Query);
            if (string.IsNullOrEmpty(query["tenantId"]))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "tenantId query parameter is required" });
                return badRequest;
            }

            var search = query["search"];
            if (string.IsNullOrWhiteSpace(search))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { success = false, message = "search query parameter is required" });
                return badRequest;
            }

            var tenantId = requestCtx.TargetTenantId;

            // Audit log for GA cross-tenant access (middleware allowed it; logged here for visibility).
            if (requestCtx.IsGlobalAdmin && !string.Equals(tenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Global Admin {User} performing cross-tenant progress lookup (tenant: {TenantId})",
                    requestCtx.UserPrincipalName, tenantId);
            }

            var page = await _sessionRepo.GetSessionsPageAsync(tenantId, days: null, pageSize: LookupPageSize, continuation: null);
            var match = FindBestMatch(page.Items, search, allowSubstring: requestCtx.IsTenantMemberOrGlobalAdmin());

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                found = match != null,
                session = match
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressLookupSession");

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                found = false
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// Picks the newest session matching the search term. Roleless callers (allowSubstring=false)
    /// must present the EXACT serial number or device name (trimmed, case-insensitive) — anything
    /// looser would let a roleless user fish through the tenant one character at a time. Members/GA
    /// (allowSubstring=true) keep the portal's original fuzzy matching.
    /// </summary>
    internal static SessionSummary? FindBestMatch(
        IEnumerable<SessionSummary> sessions, string search, bool allowSubstring)
    {
        var query = search.Trim();
        if (query.Length == 0)
            return null;

        bool ExactMatch(SessionSummary s) =>
            string.Equals(s.SerialNumber?.Trim(), query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.DeviceName?.Trim(), query, StringComparison.OrdinalIgnoreCase);

        bool SubstringMatch(SessionSummary s) =>
            (!string.IsNullOrEmpty(s.SerialNumber) && s.SerialNumber.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(s.DeviceName) && s.DeviceName.Contains(query, StringComparison.OrdinalIgnoreCase));

        return sessions
            .Where(s => allowSubstring ? SubstringMatch(s) : ExactMatch(s))
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// GET /api/progress/sessions/{sessionId}/events?tenantId={tid}&amp;serial={serial}
    /// Returns events for a specific session. The caller must re-present the session's serial
    /// number (knowledge proof) — session IDs travel in URLs/logs and must not act as an eternal
    /// bearer capability. Session missing and serial mismatch answer the SAME 404 (no existence
    /// oracle). Cross-tenant access only for Global Admins.
    /// </summary>
    [Function("ProgressGetSessionEvents")]
    public async Task<HttpResponseData> GetSessionEvents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "progress/sessions/{sessionId}/events")] HttpRequestData req,
        string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "SessionId is required" });
            return badRequestResponse;
        }

        var sessionPrefix = $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
        _logger.LogInformation("{SessionPrefix} ProgressGetSessionEvents: Fetching events", sessionPrefix);

        try
        {
            // Authentication, AuthenticatedUser authz, AND cross-tenant access enforced by
            // PolicyEnforcementMiddleware (catalog: TenantScoping.QueryParam).
            // requestCtx.TargetTenantId is the middleware-validated tenantId from the
            // ?tenantId= query param (GA bypass already applied).
            var requestCtx = req.GetRequestContext();
            var userIdentifier = requestCtx.UserPrincipalName;

            var query = HttpUtility.ParseQueryString(req.Url.Query);
            if (string.IsNullOrEmpty(query["tenantId"]))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "tenantId query parameter is required",
                    sessionId = sessionId,
                    count = 0,
                    events = Array.Empty<object>()
                });
                return badRequest;
            }

            var serial = query["serial"];
            if (string.IsNullOrWhiteSpace(serial))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "serial query parameter is required",
                    sessionId = sessionId,
                    count = 0,
                    events = Array.Empty<object>()
                });
                return badRequest;
            }

            var requestedTenantId = requestCtx.TargetTenantId;

            // Audit log for GA cross-tenant access (middleware allowed it; logged here for visibility).
            if (requestCtx.IsGlobalAdmin && !string.Equals(requestedTenantId, requestCtx.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("{SessionPrefix} Global Admin {User} accessing cross-tenant progress events (tenant: {TenantId})",
                    sessionPrefix, userIdentifier, requestedTenantId);
            }

            var session = await _sessionRepo.GetSessionAsync(requestedTenantId, sessionId);
            if (session == null || !SerialKnowledgeProof.Matches(session.SerialNumber, serial))
            {
                if (session != null)
                {
                    _logger.LogWarning("{SessionPrefix} Progress events denied: serial proof mismatch (user: {User})",
                        sessionPrefix, userIdentifier);
                }

                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Session not found",
                    sessionId = sessionId,
                    count = 0,
                    events = Array.Empty<object>()
                });
                return notFound;
            }

            var events = await _sessionRepo.GetSessionEventsAsync(requestedTenantId, sessionId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sessionId = sessionId,
                count = events.Count,
                events = events
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProgressGetSessionEvents for session {SessionId}", sessionId);

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                message = "Internal server error",
                sessionId = sessionId,
                count = 0,
                events = Array.Empty<object>()
            });
            return errorResponse;
        }
    }
}
