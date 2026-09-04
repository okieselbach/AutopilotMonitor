using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawEventsFunction
    {
        private readonly ILogger<QueryRawEventsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawEventsFunction(ILogger<QueryRawEventsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/events — Tenant-scoped raw event query (cross-session)
        /// </summary>
        [Function("QueryRawEvents")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/events")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QueryEvents(req, tenantId, scope: "raw-events:tenant", basePath: "/api/raw/events", filterTenantId: null);
            }
            catch (UnauthorizedAccessException)
            {
                return await req.UnauthorizedAsync("Unauthorized");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw events");
            }
        }

        /// <summary>
        /// GET /api/global/raw/events — Cross-tenant raw event query (GlobalAdminOnly)
        /// Omit tenantId to query across all tenants.
        /// </summary>
        [Function("QueryRawEventsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/events")] HttpRequestData req)
        {
            try
            {
                var filterTenantId = req.Query["tenantId"];
                var effectiveTenantId = string.IsNullOrEmpty(filterTenantId) ? null : filterTenantId;
                return await QueryEvents(req, effectiveTenantId, scope: "raw-events:global",
                    basePath: "/api/global/raw/events", filterTenantId: effectiveTenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw events");
            }
        }

        private async Task<HttpResponseData> QueryEvents(
            HttpRequestData req, string? tenantId, string scope, string basePath, string? filterTenantId)
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
            var sessionId = query["sessionId"];
            var eventType = query["eventType"];
            var severity = query["severity"];
            var source = query["source"];
            var startedAfter = query["startedAfter"];
            var startedBefore = query["startedBefore"];
            var fields = query["fields"];

            var pagination = QueryRawEventsPagination.ParsePagination(query);
            if (pagination.Error != null)
            {
                return await req.BadRequestAsync(pagination.Error);
            }

            // Date window: an unparsable value is an error, not a silently dropped filter —
            // a caller that believes it narrowed the window must never get the whole table.
            if (!QueryRawEventsPagination.TryParseUtc(startedAfter, out var afterUtc)
                || !QueryRawEventsPagination.TryParseUtc(startedBefore, out var beforeUtc))
            {
                return await req.BadRequestAsync("startedAfter/startedBefore must be ISO 8601 datetimes");
            }

            var callerTenantId = TenantHelper.GetTenantId(req);

            // Single-session path — paginated session-events walk so sessions
            // with more events than pageSize remain fully reachable across
            // multiple calls. Continuation token binds caller + sessionId so
            // a cursor from session A cannot be replayed for session B.
            if (!string.IsNullOrEmpty(sessionId))
            {
                // GA cross-tenant convenience: a sessionId query may omit tenantId; resolve it from
                // the session so the contract matches GetSessionEventsFunction. Only on the global
                // scope — the tenant-scoped path always has a JWT-bound tenantId (TenantHelper
                // throws when unauthenticated), so it never reaches this branch with an empty value.
                if (string.IsNullOrEmpty(tenantId) && scope == "raw-events:global")
                {
                    tenantId = await _sessionRepo.ResolveSessionTenantIdAsync(sessionId);
                }
                if (string.IsNullOrEmpty(tenantId))
                {
                    return await req.BadRequestAsync("tenantId is required when querying by sessionId (or the session was not found)");
                }

                string? singleAzureToken = null;
                if (pagination.Continuation != null)
                {
                    if (!QueryRawEventsPagination.TryAcceptContinuation(
                            pagination.Continuation, scope, callerTenantId, filterTenantId,
                            sessionId, eventType, source, severity, startedAfter, startedBefore,
                            out singleAzureToken, out var rejectReason))
                    {
                        _logger.LogWarning("QueryRawEvents: single-session continuation rejected ({Reason})", rejectReason);
                        return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                    }
                }

                var sessionPage = await _sessionRepo.GetSessionEventsRawPageAsync(
                    tenantId, sessionId, pagination.PageSize, singleAzureToken);

                var filtered = ApplyClientFilters(
                    sessionPage.Items.ToList(), eventType, severity, source, afterUtc, beforeUtc);
                filtered = filtered
                    .OrderBy(RawEventTime.Resolve)
                    .ThenBy(RawSequence)
                    .ToList();
                // No error-code enrichment here by design: the raw endpoint returns the stored
                // bytes verbatim (DataJson as the stored string). Decoded error meanings live on
                // the enriched get_session_events / search_events tools.

                string? singleNextLink = null;
                if (!string.IsNullOrEmpty(sessionPage.NextRawToken))
                {
                    var fp = QueryRawEventsPagination.Fingerprint(
                        scope, callerTenantId, filterTenantId,
                        sessionId, eventType, source, severity, startedAfter, startedBefore);
                    var wireToken = ContinuationToken.Encode(sessionPage.NextRawToken!, callerTenantId, fp);
                    singleNextLink = QueryRawEventsPagination.BuildNextLink(
                        basePath, pagination.PageSize, wireToken, query);
                }

                return await req.OkAsync(new QueryRawEventsResponse
                {
                    TenantId = tenantId,
                    Count = filtered.Count,
                    Events = RawEntityProjection.Project(filtered, fields),
                    NextLink = singleNextLink,
                });
            }

            if (string.IsNullOrEmpty(eventType))
            {
                return await req.BadRequestAsync("Either sessionId or eventType is required for raw event queries");
            }

            // Cross-session path — budgeted EventTypeIndex walk (see RawEventsScan): index rows
            // in chunks, bounded-parallel per-session fetches with the date window pushed into
            // the partition query, and a deadline between chunks. The cursor always sits after a
            // fully processed chunk, so a page ended by the budget is resumable without loss.
            string? azureToken = null;
            if (pagination.Continuation != null)
            {
                if (!QueryRawEventsPagination.TryAcceptContinuation(
                        pagination.Continuation, scope, callerTenantId, filterTenantId,
                        sessionId: null, eventType, source, severity, startedAfter, startedBefore,
                        out azureToken, out var rejectReason))
                {
                    _logger.LogWarning("QueryRawEvents: continuation rejected ({Reason})", rejectReason);
                    return await req.BadRequestAsync($"Invalid continuation token ({rejectReason}). Restart pagination from the first page.");
                }
            }

            var writtenAfterHint = QueryRawEventsPagination.IndexWrittenAfterHint(afterUtc);
            var scan = await RawEventsScan.RunAsync(
                fetchIndexPage: (chunkSize, token) => _sessionRepo.GetEventTypeIndexPageAsync(
                    tenantId, eventType, source, severity, writtenAfterHint, chunkSize, token),
                fetchSessionEvents: async entry =>
                {
                    var rows = await _sessionRepo.GetSessionEventsRawByTypeAsync(
                        entry.TenantId, entry.SessionId, eventType, maxResults: 200, afterUtc, beforeUtc);
                    return rows.Where(e => MatchesSeverity(e, severity) && MatchesSource(e, source)).ToList();
                },
                startToken: azureToken,
                options: new RawEventsScanOptions { PageSize = pagination.PageSize });

            // The RowKey range is millisecond-granular; keep the tick-exact window here.
            var events = ApplyDateFilters(scan.Events, afterUtc, beforeUtc)
                .OrderBy(RawEventTime.Resolve).ThenBy(RawSequence).ToList();
            // No enrichment — raw rows only (see single-session path).

            string? nextLink = null;
            if (!string.IsNullOrEmpty(scan.NextRawToken))
            {
                var fp = QueryRawEventsPagination.Fingerprint(
                    scope, callerTenantId, filterTenantId,
                    sessionId: null, eventType, source, severity, startedAfter, startedBefore);
                var wireToken = ContinuationToken.Encode(scan.NextRawToken!, callerTenantId, fp);
                nextLink = QueryRawEventsPagination.BuildNextLink(
                    basePath, pagination.PageSize, wireToken, query);
            }

            return await req.OkAsync(new QueryRawEventsResponse
            {
                TenantId = tenantId,
                Count = events.Count,
                Events = RawEntityProjection.Project(events, fields),
                NextLink = nextLink,
                Partial = scan.Partial ? true : null,
            });
        }

        // ── Raw-row filtering helpers ─────────────────────────────────────────
        // These operate on the literal stored columns (PascalCase) instead of the typed
        // EnrollmentEvent. They preserve the exact match semantics of the former enriched path:
        // eventType exact, severity by enum NAME (Severity is stored as an Int32), source substring.
        // The date window compares the row's BUSINESS time (OccurredUtc → RowKey → Timestamp),
        // never the bare system Timestamp — that is a write time every storage migration resets.

        private static List<IReadOnlyDictionary<string, object?>> ApplyClientFilters(
            List<IReadOnlyDictionary<string, object?>> events, string? eventType, string? severity, string? source,
            DateTime? afterUtc, DateTime? beforeUtc)
        {
            if (!string.IsNullOrEmpty(eventType))
                events = events.Where(e => string.Equals(RawString(e, "EventType"), eventType, StringComparison.Ordinal)).ToList();
            if (!string.IsNullOrEmpty(severity))
                events = events.Where(e => MatchesSeverity(e, severity)).ToList();
            if (!string.IsNullOrEmpty(source))
                events = events.Where(e => MatchesSource(e, source)).ToList();
            return ApplyDateFilters(events, afterUtc, beforeUtc);
        }

        private static List<IReadOnlyDictionary<string, object?>> ApplyDateFilters(
            List<IReadOnlyDictionary<string, object?>> events, DateTime? afterUtc, DateTime? beforeUtc)
        {
            if (afterUtc.HasValue)
                events = events.Where(e => RawEventTime.Resolve(e) >= afterUtc.Value).ToList();
            if (beforeUtc.HasValue)
                events = events.Where(e => RawEventTime.Resolve(e) <= beforeUtc.Value).ToList();
            return events;
        }

        private static bool MatchesSeverity(IReadOnlyDictionary<string, object?> row, string? severity)
            => string.IsNullOrEmpty(severity) ||
               string.Equals(RawSeverityName(row), severity, StringComparison.OrdinalIgnoreCase);

        private static bool MatchesSource(IReadOnlyDictionary<string, object?> row, string? source)
            => string.IsNullOrEmpty(source) ||
               (RawString(row, "Source") ?? string.Empty).Contains(source, StringComparison.OrdinalIgnoreCase);

        private static string? RawString(IReadOnlyDictionary<string, object?> row, string key)
            => row.TryGetValue(key, out var v) ? v?.ToString() : null;

        private static long RawSequence(IReadOnlyDictionary<string, object?> row)
            => row.TryGetValue("Sequence", out var v) && v != null && long.TryParse(v.ToString(), out var n) ? n : 0L;

        // Severity is stored as an Int32; map it back to the EventSeverity name so the severity=
        // filter keeps the same "by name" semantics the enriched path used. Unknown ints fall back
        // to the raw number string.
        private static string? RawSeverityName(IReadOnlyDictionary<string, object?> row)
        {
            if (!row.TryGetValue("Severity", out var v) || v == null) return null;
            if (int.TryParse(v.ToString(), out var n))
                return Enum.IsDefined(typeof(EventSeverity), n) ? ((EventSeverity)n).ToString() : n.ToString();
            return v.ToString();
        }
    }
}
