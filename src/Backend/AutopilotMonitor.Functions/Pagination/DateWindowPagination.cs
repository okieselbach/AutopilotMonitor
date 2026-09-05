using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Shared pagination helpers for date-windowed forensics endpoints
    /// (audit logs, ops events, session reports). These all share the same
    /// query-string surface (<c>dateFrom</c> / <c>dateTo</c> / <c>pageSize</c>
    /// / <c>continuation</c>) and the same default-window contract, so the
    /// parsing + fingerprint logic lives in one place.
    /// </summary>
    /// <remarks>
    /// Per the pagination rollout plan (PR-3):
    /// <list type="bullet">
    ///   <item><description>No <c>dateFrom</c>/<c>dateTo</c> ⇒ default to a
    ///   30-day window ending now.</description></item>
    ///   <item><description>Either bound supplied ⇒ honoured exactly; the
    ///   other bound stays open.</description></item>
    ///   <item><description><c>pageSize</c> opts in to pagination
    ///   (default <see cref="DefaultPageSize"/>, max
    ///   <see cref="MaxPageSize"/>); without it the endpoint returns the full
    ///   filtered window with no <c>nextLink</c> and no row cap.</description></item>
    ///   <item><description>Continuation tokens bind <c>scope</c>,
    ///   <c>category</c> (when applicable) and the resolved
    ///   <c>dateFrom</c>/<c>dateTo</c> via filter fingerprint, plus the caller's
    ///   tenantId.</description></item>
    /// </list>
    /// </remarks>
    public static class DateWindowPagination
    {
        public const int DefaultPageSize = 200;
        public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

        /// <summary>
        /// Parsed query-state. <c>PageSize == null</c> indicates the legacy
        /// unpaginated path; <c>Error</c> non-null indicates a 400 should be
        /// returned without inspecting the rest.
        /// </summary>
        public sealed class Parsed
        {
            public DateTime? DateFrom { get; init; }
            public DateTime? DateTo { get; init; }
            public int? PageSize { get; init; }
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        /// <summary>
        /// Parses <c>dateFrom</c> / <c>dateTo</c> / <c>pageSize</c> /
        /// <c>continuation</c> from the URL query and resolves the default
        /// 30-day window if both bounds are absent. Resolved bounds are
        /// returned in <see cref="Parsed.DateFrom"/> / <see cref="Parsed.DateTo"/>
        /// so the caller can fingerprint + emit them on <c>nextLink</c>
        /// unchanged across follow-up calls.
        /// </summary>
        public static Parsed ParseQuery(NameValueCollection? query, DateTimeOffset? now = null)
        {
            var dateFromRaw = query?["dateFrom"];
            var dateToRaw = query?["dateTo"];
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];

            if (!QueryParams.TryUtcInstant(dateFromRaw, "dateFrom", out var dateFrom, out var dateError))
                return new Parsed { Error = dateError };
            if (!QueryParams.TryUtcInstant(dateToRaw, "dateTo", out var dateTo, out dateError))
                return new Parsed { Error = dateError };
            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                return new Parsed { Error = "dateFrom must be <= dateTo" };
            }

            // Default window: when caller supplies neither bound, pick the last
            // 30 days ending at the resolution time. We resolve to a concrete
            // value here so the same window survives the cross-page nextLink
            // round trip — without this, follow-up pages would see a slightly
            // different "now" and blow the fingerprint check.
            if (!dateFrom.HasValue && !dateTo.HasValue)
            {
                var anchor = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
                dateFrom = anchor - DefaultWindow;
                dateTo = anchor;
            }

            if (!QueryParams.TryPageSize(pageSizeRaw, out var pageSize, out var pageSizeError))
                return new Parsed { Error = pageSizeError };

            // continuation is meaningless without pageSize — silently drop.
            var continuation = pageSize.HasValue && !string.IsNullOrEmpty(continuationRaw)
                ? continuationRaw
                : null;

            return new Parsed
            {
                DateFrom = dateFrom,
                DateTo = dateTo,
                PageSize = pageSize,
                Continuation = continuation,
            };
        }

        /// <summary>
        /// Builds a filter fingerprint over <c>(scope, callerTenantId, dateFrom, dateTo,
        /// extras...)</c> in canonical form. <paramref name="extras"/> is for
        /// per-endpoint discriminators (e.g. ops-events <c>category</c>).
        /// </summary>
        public static string Fingerprint(
            string scope,
            string callerTenantId,
            DateTime? dateFrom,
            DateTime? dateTo,
            IEnumerable<KeyValuePair<string, string?>>? extras = null)
        {
            var pairs = new List<KeyValuePair<string, string?>>
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("dateFrom", dateFrom?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("dateTo", dateTo?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
            };
            if (extras != null) pairs.AddRange(extras);
            return ContinuationToken.ComputeFingerprint(pairs);
        }

        /// <summary>
        /// Validates an incoming continuation against the recomputed fingerprint
        /// of the current request and the caller's tenantId.
        /// </summary>
        public static bool TryAcceptContinuation(
            string raw,
            string scope,
            string callerTenantId,
            DateTime? dateFrom,
            DateTime? dateTo,
            IEnumerable<KeyValuePair<string, string?>>? extras,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, dateFrom, dateTo, extras);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        /// <summary>
        /// Builds a relative-on-host nextLink URL preserving the resolved
        /// window and any extra query params the endpoint cares about.
        /// </summary>
        public static string BuildNextLink(
            string basePath,
            int pageSize,
            string wireContinuation,
            DateTime? dateFrom,
            DateTime? dateTo,
            IEnumerable<KeyValuePair<string, string?>>? extras = null)
        {
            var sb = new StringBuilder(basePath);
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(Uri.EscapeDataString(wireContinuation));
            if (dateFrom.HasValue)
            {
                sb.Append("&dateFrom=").Append(Uri.EscapeDataString(dateFrom.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            }
            if (dateTo.HasValue)
            {
                sb.Append("&dateTo=").Append(Uri.EscapeDataString(dateTo.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            }
            if (extras != null)
            {
                foreach (var kv in extras)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    sb.Append('&').Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value!));
                }
            }
            return sb.ToString();
        }
    }
}
