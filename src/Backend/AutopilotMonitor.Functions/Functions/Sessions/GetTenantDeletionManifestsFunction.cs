using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// <c>GET /api/global/tenants/{tenantId}/deletion-manifests</c> — file-browser-style listing
    /// of every persisted cascade-delete snapshot for a single tenant. Powers the Session Cleanup
    /// admin page's "Restore Browser" tab so a Global Admin can pick a (session, manifest) pair
    /// without already knowing the manifestId.
    /// <para>
    /// Cross-tenant access is the design intent (Global Admins acting on cross-tenant recovery
    /// tickets); GA-only enforcement comes from <c>EndpointAccessPolicyCatalog</c>. Sessions are
    /// returned newest-first so a "just deleted" session surfaces immediately; manifests under a
    /// session are also newest-first because re-runs after partial poison are rare and the most
    /// recent is almost always the operator's target.
    /// </para>
    /// <para>
    /// No pagination on first cut: the 33-day blob TTL caps the per-tenant scale, typical tenants
    /// have a handful of deleted sessions per month. If a tenant ever exceeds a few hundred
    /// manifests the cross-partition list response itself stays reasonable (Azure pages internally
    /// at 5000 items), and a pagination follow-up can land before the UI starts paging on screen.
    /// </para>
    /// </summary>
    public class GetTenantDeletionManifestsFunction
    {
        private readonly ILogger<GetTenantDeletionManifestsFunction> _logger;
        private readonly BlobStorageService _blob;

        public GetTenantDeletionManifestsFunction(
            ILogger<GetTenantDeletionManifestsFunction> logger,
            BlobStorageService blob)
        {
            _logger = logger;
            _blob = blob;
        }

        [Function("GetTenantDeletionManifests")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/tenants/{tenantId}/deletion-manifests")]
            HttpRequestData req,
            string tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId, out _))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = "Path parameter 'tenantId' must be a GUID." });
                return bad;
            }

            // sessionId-scoped sub-tree probe — handy when the operator opens the browser by
            // following a "deletion_started" audit row (sessionId in hand, manifestId not).
            var sessionFilter = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty)["sessionId"];

            // Group entries by sessionId so the UI can render a two-level tree without extra
            // bookkeeping. SortedDictionary on the outer level keeps the order deterministic;
            // we then re-sort by recency before serializing.
            var bySession = new Dictionary<string, List<TenantDeletionManifestEntry>>(StringComparer.Ordinal);
            try
            {
                await foreach (var entry in _blob.EnumerateDeletionManifestsByTenantAsync(
                    tenantId, req.FunctionContext.CancellationToken))
                {
                    if (!string.IsNullOrEmpty(sessionFilter)
                        && !string.Equals(entry.SessionId, sessionFilter, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!bySession.TryGetValue(entry.SessionId, out var list))
                    {
                        list = new List<TenantDeletionManifestEntry>();
                        bySession[entry.SessionId] = list;
                    }
                    list.Add(entry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "GetTenantDeletionManifests: enumeration failed for tenant={TenantId}",
                    tenantId);
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { success = false, message = "Failed to enumerate manifest blobs." });
                return err;
            }

            var sessions = bySession
                .Select(kvp =>
                {
                    var manifests = kvp.Value
                        .OrderByDescending(e => e.LastModifiedUtc)
                        .Select(e => new TenantDeletionManifestItem
                        {
                            ManifestId = e.ManifestId,
                            SizeBytes = e.SizeBytes,
                            LastModifiedUtc = e.LastModifiedUtc.ToString("o"),
                        })
                        .ToList();
                    return new TenantDeletionManifestSessionNode
                    {
                        SessionId = kvp.Key,
                        ManifestCount = manifests.Count,
                        // Tree-view sort: newest manifest under a session wins for the session-level
                        // recency, so "just deleted" sessions float up.
                        LatestManifestUtc = kvp.Value.Max(e => e.LastModifiedUtc).ToString("o"),
                        Manifests = manifests,
                    };
                })
                .OrderByDescending(s => s.LatestManifestUtc)
                .ToList();

            return await req.OkAsync(new GetTenantDeletionManifestsResponse
            {
                Success = true,
                TenantId = tenantId,
                SessionFilter = sessionFilter,
                SessionCount = sessions.Count,
                ManifestCount = sessions.Sum(s => s.ManifestCount),
                Sessions = sessions,
            });
        }
    }
}
