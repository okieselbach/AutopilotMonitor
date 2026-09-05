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
    /// Pure helpers for <c>/api/global/raw/tables/{tableName}</c>. Generic OData
    /// passthrough for the admin diagnostic tool — fingerprint covers tableName,
    /// PK/RK constraints, and the custom OData filter so a token from
    /// <c>Sessions</c> can't be replayed against <c>Events</c> and a token bound
    /// to one filter expression can't reseek under a different one.
    /// </summary>
    public static class RawTablePagination
    {
        public const int DefaultPageSize = 200;

        public static string Fingerprint(
            string callerTenantId,
            string tableName,
            string? partitionKey,
            string? rowKeyPrefix,
            string? customFilter)
        {
            return ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", "raw-table"),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("tableName", tableName),
                new KeyValuePair<string, string?>("partitionKey", partitionKey),
                new KeyValuePair<string, string?>("rowKeyPrefix", rowKeyPrefix),
                new KeyValuePair<string, string?>("filter", customFilter),
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
            string callerTenantId,
            string tableName,
            string? partitionKey,
            string? rowKeyPrefix,
            string? customFilter,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(callerTenantId, tableName, partitionKey, rowKeyPrefix, customFilter);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(
            string tableName,
            int pageSize,
            string wireContinuation,
            NameValueCollection originalQuery)
        {
            var sb = new StringBuilder("/api/global/raw/tables/");
            sb.Append(System.Uri.EscapeDataString(tableName));
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));

            // Echo every filter param the caller sent so the bookmark is
            // self-contained. Drop pagination-owned + legacy keys.
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
