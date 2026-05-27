using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the opt-in pagination surface on <c>GET /api/config/all</c>.
    /// The endpoint is GlobalAdminOnly and has no filter parameters, so the
    /// continuation token only binds the caller's identity (defends against
    /// cross-caller token replay). When <c>pageSize</c> is absent the function
    /// returns the legacy unpaginated bare array; these helpers only apply once
    /// the caller opts into pagination.
    /// </summary>
    public static class TenantConfigPagination
    {
        public const int DefaultPageSize = 200;
        public const int MaxPageSize = 1000;

        public static string Fingerprint(string callerTenantId) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", "config-all"),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
            });

        public sealed class Parsed
        {
            /// <summary>Null when the caller did not opt into pagination (legacy bare-array mode).</summary>
            public int? PageSize { get; init; }
            public string? Continuation { get; init; }
            /// <summary>
            /// Raw comma-separated field subset (e.g. "tenantId,domainName"), or null for all
            /// safe fields. A projection, not a filter — it does NOT enter the continuation
            /// fingerprint (changing it mid-stream keeps the cursor valid).
            /// </summary>
            public string? Fields { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query)
        {
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];
            var fieldsRaw = query?["fields"];

            int? pageSize = null;
            if (!string.IsNullOrEmpty(pageSizeRaw))
            {
                if (!int.TryParse(pageSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return new Parsed { Error = "pageSize must be an integer" };
                if (n < 1 || n > MaxPageSize)
                    return new Parsed { Error = $"pageSize must be between 1 and {MaxPageSize}" };
                pageSize = n;
            }

            // continuation is meaningless without pageSize — silently drop.
            var continuation = pageSize.HasValue && !string.IsNullOrEmpty(continuationRaw)
                ? continuationRaw
                : null;

            return new Parsed
            {
                PageSize = pageSize,
                Continuation = continuation,
                Fields = string.IsNullOrEmpty(fieldsRaw) ? null : fieldsRaw,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string callerTenantId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(callerTenantId);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(int pageSize, string wireContinuation, string? fields)
        {
            var sb = new StringBuilder("/api/config/all");
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            // Echo the projection so the caller can follow nextLink verbatim and keep the
            // same lean column set across every page (fields is not in the token fingerprint).
            if (!string.IsNullOrEmpty(fields))
                sb.Append("&fields=").Append(System.Uri.EscapeDataString(fields!));
            return sb.ToString();
        }
    }
}
