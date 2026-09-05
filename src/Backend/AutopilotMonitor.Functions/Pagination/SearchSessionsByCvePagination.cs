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
    /// Pure helpers for <c>/api/search/sessions-by-cve</c> +
    /// <c>/api/global/search/sessions-by-cve</c>. Walks the CveIndex partition
    /// scan paged. The fingerprint binds the CVE id plus the optional CVSS /
    /// risk filters so a token from one CVE can't be replayed against another
    /// even with the same caller and tenant.
    /// </summary>
    public static class SearchSessionsByCvePagination
    {
        public const int DefaultPageSize = 200;

        public static string Fingerprint(
            string scope,
            string callerTenantId,
            string? filterTenantId,
            string cveId,
            double? minCvssScore,
            string? overallRisk)
        {
            return ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                new KeyValuePair<string, string?>("cveId", cveId),
                new KeyValuePair<string, string?>("minCvssScore", minCvssScore?.ToString("R", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("overallRisk", overallRisk),
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
            string cveId,
            double? minCvssScore,
            string? overallRisk,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, filterTenantId, cveId, minCvssScore, overallRisk);
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

            // Echo every filter param the caller sent (cveId, minCvssScore, overallRisk,
            // tenantId for global) so the bookmark is self-contained. Drop pagination-
            // owned + legacy keys.
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
