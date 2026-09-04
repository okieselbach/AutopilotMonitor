using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Sessions
{
    /// <summary>
    /// Cross-tenant read-only listing of sessions currently in a cascade-deletion state.
    /// Feeds the Global-Admin "Session Cleanup" page (PR-C) — one call per tab:
    /// <list type="bullet">
    ///   <item><c>?state=Preparing|Queued|Running</c> — In-Flight tab.</item>
    ///   <item><c>?state=Poisoned</c> — Poisoned tab; rows here need an operator restore.</item>
    ///   <item><c>?state=Queued&amp;strandedSinceMinutes=30</c> — Stranded tab; matches the
    ///       <c>SessionDeletionStrandedQueued</c> OpsEvent watchdog (plan §10).</item>
    /// </list>
    /// <para>
    /// Cross-partition scan via <see cref="TableStorageService.GetSessionsByDeletionStateAsync"/>.
    /// Acceptable here for the same reason the 12h maintenance function uses it: the row count
    /// for any non-None state is typically &lt; 10 in steady state (poisoned/stranded peak higher
    /// during incidents). No pagination — page-size pagination would complicate the cross-state
    /// fan-out the UI needs and the absolute upper bound is bounded by per-tenant retention.
    /// </para>
    /// <para>
    /// GA-only enforcement comes from <c>EndpointAccessPolicyCatalog</c> (route registered as
    /// <c>GlobalAdminOnly</c>); the function body does not re-check.
    /// </para>
    /// </summary>
    public class GetSessionDeletionsListFunction
    {
        private readonly ILogger<GetSessionDeletionsListFunction> _logger;
        private readonly TableStorageService _storage;

        public GetSessionDeletionsListFunction(
            ILogger<GetSessionDeletionsListFunction> logger,
            TableStorageService storage)
        {
            _logger = logger;
            _storage = storage;
        }

        [Function("GetSessionDeletionsList")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/session-deletions")]
            HttpRequestData req)
        {
            try
            {
                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var state = query["state"] ?? string.Empty;
                if (!IsAllowedState(state))
                {
                    return await req.BadRequestAsync("Query parameter 'state' must be one of: Preparing, Queued, Running, Poisoned.");
                }

                int? strandedSinceMinutes = null;
                var strandedRaw = query["strandedSinceMinutes"];
                if (!string.IsNullOrEmpty(strandedRaw))
                {
                    if (state != SessionDeletionState.Queued)
                    {
                        return await req.BadRequestAsync("strandedSinceMinutes is only valid with state=Queued.");
                    }
                    if (!int.TryParse(strandedRaw, out var minutes) || minutes <= 0 || minutes > 10080)
                    {
                        return await req.BadRequestAsync("strandedSinceMinutes must be a positive integer up to 10080 (7 days).");
                    }
                    strandedSinceMinutes = minutes;
                }

                var cutoffUtc = strandedSinceMinutes.HasValue
                    ? (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-strandedSinceMinutes.Value)
                    : null;

                var now = DateTimeOffset.UtcNow;
                var sessions = new List<SessionDeletionListItem>();
                await foreach (var entity in _storage.GetSessionsByDeletionStateAsync(state, req.FunctionContext.CancellationToken))
                {
                    var ts = entity.Timestamp ?? now;
                    if (cutoffUtc.HasValue && ts > cutoffUtc.Value) continue;

                    var ageMinutes = (int)Math.Floor((now - ts).TotalMinutes);
                    sessions.Add(new SessionDeletionListItem
                    {
                        TenantId = entity.PartitionKey,
                        SessionId = entity.RowKey,
                        DeletionState = entity.GetString("DeletionState") ?? state,
                        ManifestId = entity.GetString("PendingDeletionManifestId") ?? string.Empty,
                        Timestamp = ts.UtcDateTime.ToString("o"),
                        AgeMinutes = ageMinutes,
                    });
                }

                return await req.OkAsync(new GetSessionDeletionsListResponse
                {
                    Success = true,
                    State = state,
                    StrandedSinceMinutes = strandedSinceMinutes,
                    Count = sessions.Count,
                    Sessions = sessions,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetSessionDeletionsList");
            }
        }

        private static bool IsAllowedState(string state) =>
            state == SessionDeletionState.Preparing
            || state == SessionDeletionState.Queued
            || state == SessionDeletionState.Running
            || state == SessionDeletionState.Poisoned;
    }
}
