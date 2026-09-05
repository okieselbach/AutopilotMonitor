using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the <c>GET /api/global/session-reports</c> pagination
    /// surface. Stays GA-only at the policy layer; the optional
    /// <c>tenantId</c> here is a server-side <em>filter</em>, not an
    /// authorization scope, so the continuation token only binds the caller's
    /// identity + the active filter (so a token issued for tenantA's report
    /// view can't silently start serving tenantB's reports if the caller
    /// rewrites the query).
    /// </summary>
    public static class SessionReportsPagination
    {
        public const int DefaultPageSize = 200;

        public static string Fingerprint(string callerTenantId, string? filterTenantId) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", "session-reports"),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
            });

        public sealed class Parsed
        {
            public string? FilterTenantId { get; init; }
            public int? PageSize { get; init; }
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query)
        {
            var filterTenantIdRaw = query?["tenantId"];
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];

            if (!QueryParams.TryPageSize(pageSizeRaw, out var pageSize, out var pageSizeError))
                return new Parsed { Error = pageSizeError };

            // continuation is meaningless without pageSize — silently drop.
            var continuation = pageSize.HasValue && !string.IsNullOrEmpty(continuationRaw)
                ? continuationRaw
                : null;

            return new Parsed
            {
                FilterTenantId = string.IsNullOrEmpty(filterTenantIdRaw) ? null : filterTenantIdRaw,
                PageSize = pageSize,
                Continuation = continuation,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string callerTenantId,
            string? filterTenantId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(callerTenantId, filterTenantId);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(int pageSize, string wireContinuation, string? filterTenantId)
        {
            var sb = new StringBuilder("/api/global/session-reports");
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            if (!string.IsNullOrEmpty(filterTenantId))
            {
                sb.Append("&tenantId=").Append(System.Uri.EscapeDataString(filterTenantId!));
            }
            return sb.ToString();
        }
    }
}
