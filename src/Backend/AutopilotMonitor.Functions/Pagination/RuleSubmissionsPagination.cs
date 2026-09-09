using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the <c>GET /api/global/rule-submissions</c> pagination surface — the
    /// SessionReportsPagination contract with a second server-side filter (<c>status</c>). Both
    /// filters are bound into the continuation fingerprint so a token issued for one view cannot
    /// resume another.
    /// </summary>
    public static class RuleSubmissionsPagination
    {
        public const int DefaultPageSize = 200;

        public static string Fingerprint(string callerTenantId, string? filterTenantId, string? filterStatus) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", "rule-submissions"),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                new KeyValuePair<string, string?>("filterStatus", filterStatus),
            });

        public sealed class Parsed
        {
            public string? FilterTenantId { get; init; }
            public string? FilterStatus { get; init; }
            public int? PageSize { get; init; }
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query)
        {
            var filterTenantIdRaw = query?["tenantId"];
            var filterStatusRaw = query?["status"];
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];

            if (!QueryParams.TryPageSize(pageSizeRaw, out var pageSize, out var pageSizeError))
                return new Parsed { Error = pageSizeError };

            if (!string.IsNullOrEmpty(filterStatusRaw)
                && System.Array.IndexOf(Shared.Models.RuleSubmissionStatuses.Stored, filterStatusRaw) < 0)
            {
                return new Parsed { Error = $"status must be one of: {string.Join(", ", Shared.Models.RuleSubmissionStatuses.Stored)}" };
            }

            var continuation = pageSize.HasValue && !string.IsNullOrEmpty(continuationRaw)
                ? continuationRaw
                : null;

            return new Parsed
            {
                FilterTenantId = string.IsNullOrEmpty(filterTenantIdRaw) ? null : filterTenantIdRaw,
                FilterStatus = string.IsNullOrEmpty(filterStatusRaw) ? null : filterStatusRaw,
                PageSize = pageSize,
                Continuation = continuation,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string callerTenantId,
            string? filterTenantId,
            string? filterStatus,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(callerTenantId, filterTenantId, filterStatus);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(int pageSize, string wireContinuation, string? filterTenantId, string? filterStatus)
        {
            var sb = new StringBuilder("/api/global/rule-submissions");
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            if (!string.IsNullOrEmpty(filterTenantId))
                sb.Append("&tenantId=").Append(System.Uri.EscapeDataString(filterTenantId!));
            if (!string.IsNullOrEmpty(filterStatus))
                sb.Append("&status=").Append(System.Uri.EscapeDataString(filterStatus!));
            return sb.ToString();
        }
    }
}
