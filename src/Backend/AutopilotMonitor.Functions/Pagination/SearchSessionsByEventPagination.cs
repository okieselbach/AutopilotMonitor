using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for <c>/api/search/sessions-by-event</c> +
    /// <c>/api/global/search/sessions-by-event</c>. Same paged
    /// EventTypeIndex walk as <see cref="QueryRawEventsPagination"/>, but
    /// returns sessions (not events) — so it gets its own scope tag in the
    /// fingerprint to keep tokens non-fungible across the two endpoints.
    /// </summary>
    public static class SearchSessionsByEventPagination
    {
        public const int DefaultPageSize = 50;

        public static string Fingerprint(
            string scope,
            string callerTenantId,
            string? filterTenantId,
            string eventType)
        {
            return ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                new KeyValuePair<string, string?>("eventType", eventType),
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

            if (!QueryParams.TryPageSize(pageSizeRaw, out var pageSizeOrNull, out var pageSizeError))
                return new Parsed { PageSize = DefaultPageSize, Error = pageSizeError };
            var pageSize = pageSizeOrNull ?? DefaultPageSize;

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
            string eventType,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, filterTenantId, eventType);
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

            // Echo every filter param the caller sent (eventType + filter tenantId)
            // so the bookmark is self-contained. Drop pagination-owned + legacy keys.
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
