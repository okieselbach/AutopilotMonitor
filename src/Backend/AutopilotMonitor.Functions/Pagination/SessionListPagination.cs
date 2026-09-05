using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the session-list pagination surface
    /// (<c>/api/sessions</c> + <c>/api/global/sessions</c>). Replaces the
    /// legacy <c>?limit=</c> + <c>?cursor=</c> wire shape with the rollout
    /// plan's <c>?pageSize=</c> + <c>?continuation=</c> + <c>nextLink</c>
    /// contract.
    /// </summary>
    public static class SessionListPagination
    {
        public const int DefaultPageSize = 100;
        /// <summary>Scan-cost cap on <c>days</c>; a wider window is a 400, never a silent narrowing.</summary>
        public const int MaxDays = 365;

        /// <summary>
        /// Fingerprint binding the token to <c>(scope, callerTenantId, days, filterTenantId)</c>.
        /// <paramref name="filterTenantId"/> is for the global endpoint only.
        /// </summary>
        public static string Fingerprint(string scope, string callerTenantId, int? days, string? filterTenantId = null) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("days", days?.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
            });

        public sealed class Parsed
        {
            public int PageSize { get; init; }
            public string? Continuation { get; init; }
            public int? Days { get; init; }
            public string? FilterTenantId { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query, bool acceptFilterTenantId)
        {
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];
            var daysRaw = query?["days"];
            var filterTenantIdRaw = acceptFilterTenantId ? query?["tenantId"] : null;

            if (!QueryParams.TryPageSize(pageSizeRaw, out var pageSizeOrNull, out var pageSizeError))
                return new Parsed { PageSize = DefaultPageSize, Error = pageSizeError };
            var pageSize = pageSizeOrNull ?? DefaultPageSize;

            if (!QueryParams.TryInt(daysRaw, "days", 1, MaxDays, out var days, out var daysError))
                return new Parsed { PageSize = pageSize, Error = daysError };

            return new Parsed
            {
                PageSize = pageSize,
                Continuation = string.IsNullOrEmpty(continuationRaw) ? null : continuationRaw,
                Days = days,
                FilterTenantId = string.IsNullOrEmpty(filterTenantIdRaw) ? null : filterTenantIdRaw,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string scope,
            string callerTenantId,
            int? days,
            string? filterTenantId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, days, filterTenantId);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(
            string basePath,
            int pageSize,
            string wireContinuation,
            int? days,
            string? filterTenantId)
        {
            var sb = new StringBuilder(basePath);
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            if (days.HasValue)
            {
                sb.Append("&days=").Append(days.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(filterTenantId))
            {
                sb.Append("&tenantId=").Append(System.Uri.EscapeDataString(filterTenantId!));
            }
            return sb.ToString();
        }
    }
}
