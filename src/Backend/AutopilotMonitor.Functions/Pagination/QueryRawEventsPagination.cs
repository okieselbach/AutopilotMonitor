using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for <c>/api/raw/events</c> + <c>/api/global/raw/events</c>.
    /// PR-6 of mcp-pagination-rollout: replaces the legacy <c>?limit=</c> wire
    /// shape and the hard-coded <c>limit:20</c> EventTypeIndex lookup with
    /// <c>?pageSize=</c> + <c>?continuation=</c> + <c>nextLink</c> over the
    /// underlying index walk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pagination unit is <em>EventTypeIndex rows scanned per page</em>, NOT
    /// events returned: a single indexed (session, eventType) pair may yield
    /// many events. Total events per page can therefore exceed
    /// <c>pageSize</c>; AI/clients should follow <c>nextLink</c> until absent
    /// for forensics-grade exact recall.
    /// </para>
    /// <para>
    /// Continuation tokens bind <c>(scope, callerTenantId, filterTenantId,
    /// eventType, source, severity, startedAfter, startedBefore)</c> via
    /// fingerprint — flipping any filter invalidates the cursor (no recall
    /// drift between pages).
    /// </para>
    /// </remarks>
    public static class QueryRawEventsPagination
    {
        public const int DefaultPageSize = 200;
        public const int MaxPageSize = 1000;

        /// <summary>
        /// Slack between an event's sanitized time and the write time of its EventTypeIndex
        /// row: ingest clamps agent times to at most <c>MaxFutureToleranceHours</c> ahead of
        /// receipt, plus one hour for request-internal ordering. Used to turn
        /// <c>startedAfter</c> into a server-side <c>Timestamp ge</c> pre-filter on the index
        /// that can never exclude a session holding a matching event.
        /// </summary>
        public static readonly TimeSpan IndexWriteTimeSlack =
            TimeSpan.FromHours(Services.EventTimestampValidator.MaxFutureToleranceHours + 1);

        /// <summary>Lower bound on index-row write time implied by <paramref name="startedAfterUtc"/>; null when no lower bound was given.</summary>
        public static DateTime? IndexWrittenAfterHint(DateTime? startedAfterUtc)
            => startedAfterUtc.HasValue ? startedAfterUtc.Value - IndexWriteTimeSlack : null;

        /// <summary>
        /// Parses an ISO-8601 query value as a UTC instant. Returns false only for a non-empty
        /// value that does not parse — an absent/empty value yields <c>null</c> and true.
        /// A bare (offset-less) value is taken as UTC, never as server-local time.
        /// </summary>
        public static bool TryParseUtc(string? raw, out DateTime? utc)
        {
            utc = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            utc = parsed;
            return true;
        }

        public static string Fingerprint(
            string scope,
            string callerTenantId,
            string? filterTenantId,
            string? sessionId,
            string? eventType,
            string? source,
            string? severity,
            string? startedAfter,
            string? startedBefore)
        {
            return ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                // sessionId is null on the cross-session path; on the single-session
                // path it MUST be in the fingerprint so a cursor from session A cannot
                // be replayed against session B by the same caller.
                new KeyValuePair<string, string?>("sessionId", sessionId),
                new KeyValuePair<string, string?>("eventType", eventType),
                new KeyValuePair<string, string?>("source", source),
                new KeyValuePair<string, string?>("severity", severity),
                new KeyValuePair<string, string?>("startedAfter", startedAfter),
                new KeyValuePair<string, string?>("startedBefore", startedBefore),
            });
        }

        public sealed class Parsed
        {
            public int PageSize { get; init; }
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParsePagination(NameValueCollection? query)
        {
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];

            int pageSize = DefaultPageSize;
            if (!string.IsNullOrEmpty(pageSizeRaw))
            {
                if (!int.TryParse(pageSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return new Parsed { PageSize = DefaultPageSize, Error = "pageSize must be an integer" };
                if (n < 1 || n > MaxPageSize)
                    return new Parsed { PageSize = DefaultPageSize, Error = $"pageSize must be between 1 and {MaxPageSize}" };
                pageSize = n;
            }

            return new Parsed
            {
                PageSize = pageSize,
                Continuation = string.IsNullOrEmpty(continuationRaw) ? null : continuationRaw,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string scope,
            string callerTenantId,
            string? filterTenantId,
            string? sessionId,
            string? eventType,
            string? source,
            string? severity,
            string? startedAfter,
            string? startedBefore,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, filterTenantId, sessionId, eventType, source, severity, startedAfter, startedBefore);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(
            string basePath,
            int pageSize,
            string wireContinuation,
            NameValueCollection originalQuery)
        {
            var sb = new StringBuilder(basePath);
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));

            // Echo every filter param the caller sent so the bookmark survives
            // round-trips. Drop pageSize/continuation/limit (legacy/owned).
            foreach (string? key in originalQuery.AllKeys)
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (string.Equals(key, "pageSize", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, "continuation", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase)) continue;
                var value = originalQuery[key];
                if (string.IsNullOrEmpty(value)) continue;
                sb.Append('&').Append(System.Uri.EscapeDataString(key!)).Append('=').Append(System.Uri.EscapeDataString(value!));
            }
            return sb.ToString();
        }
    }
}
