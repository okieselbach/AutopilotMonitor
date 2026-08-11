using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the <c>GET /api/global/session-annotations</c> pagination
    /// surface. GA-only at the policy layer; every optional filter is a server-side
    /// <em>filter</em>, not an authorization scope, so the continuation token binds
    /// the caller's identity + the whole active filter set (a token issued for one
    /// filter view can't be replayed against another).
    /// </summary>
    public static class SessionAnnotationsPagination
    {
        public const int DefaultPageSize = 200;
        public const int MaxPageSize = 1000;

        public static string Fingerprint(
            string callerTenantId, string? filterTenantId, string? lane, string? verdict,
            string? ruleId, DateTime? dateFrom, DateTime? dateTo) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", "session-annotations"),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                new KeyValuePair<string, string?>("lane", lane),
                new KeyValuePair<string, string?>("verdict", verdict),
                new KeyValuePair<string, string?>("ruleId", ruleId),
                new KeyValuePair<string, string?>("dateFrom", dateFrom?.ToString("o", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("dateTo", dateTo?.ToString("o", CultureInfo.InvariantCulture)),
            });

        public sealed class Parsed
        {
            public string? FilterTenantId { get; init; }
            public string? Lane { get; init; }
            public string? Verdict { get; init; }
            public string? RuleId { get; init; }
            public DateTime? DateFrom { get; init; }
            public DateTime? DateTo { get; init; }
            public int PageSize { get; init; } = DefaultPageSize;
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query)
        {
            var pageSize = DefaultPageSize;
            var pageSizeRaw = query?["pageSize"];
            if (!string.IsNullOrEmpty(pageSizeRaw))
            {
                if (!int.TryParse(pageSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return new Parsed { Error = "pageSize must be an integer" };
                if (n < 1 || n > MaxPageSize)
                    return new Parsed { Error = $"pageSize must be between 1 and {MaxPageSize}" };
                pageSize = n;
            }

            var lane = Normalize(query?["lane"]);
            if (lane != null && !AnnotationLanes.All.Contains(lane))
                return new Parsed { Error = $"lane must be one of: {string.Join(", ", AnnotationLanes.All)}" };

            var verdict = Normalize(query?["verdict"]);
            if (verdict != null && !AnnotationVerdicts.All.Contains(verdict))
                return new Parsed { Error = $"verdict must be one of: {string.Join(", ", AnnotationVerdicts.All)}" };

            if (!TryParseDate(query?["dateFrom"], out var dateFrom))
                return new Parsed { Error = "dateFrom must be an ISO 8601 date" };
            if (!TryParseDate(query?["dateTo"], out var dateTo))
                return new Parsed { Error = "dateTo must be an ISO 8601 date" };

            return new Parsed
            {
                FilterTenantId = NullIfEmpty(query?["tenantId"]),
                Lane = lane,
                Verdict = verdict,
                RuleId = NullIfEmpty(query?["ruleId"]),
                DateFrom = dateFrom,
                DateTo = dateTo,
                PageSize = pageSize,
                Continuation = NullIfEmpty(query?["continuation"]),
            };
        }

        public static bool TryAcceptContinuation(
            Parsed parsed,
            string callerTenantId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(
                callerTenantId, parsed.FilterTenantId, parsed.Lane, parsed.Verdict,
                parsed.RuleId, parsed.DateFrom, parsed.DateTo);
            return ContinuationToken.TryDecode(parsed.Continuation!, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string EncodeContinuation(Parsed parsed, string callerTenantId, string rawAzureToken)
        {
            var fp = Fingerprint(
                callerTenantId, parsed.FilterTenantId, parsed.Lane, parsed.Verdict,
                parsed.RuleId, parsed.DateFrom, parsed.DateTo);
            return ContinuationToken.Encode(rawAzureToken, callerTenantId, fp);
        }

        public static string BuildNextLink(Parsed parsed, string wireContinuation)
        {
            var sb = new StringBuilder("/api/global/session-annotations");
            sb.Append("?pageSize=").Append(parsed.PageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(Uri.EscapeDataString(wireContinuation));
            AppendIfSet(sb, "tenantId", parsed.FilterTenantId);
            AppendIfSet(sb, "lane", parsed.Lane);
            AppendIfSet(sb, "verdict", parsed.Verdict);
            AppendIfSet(sb, "ruleId", parsed.RuleId);
            AppendIfSet(sb, "dateFrom", parsed.DateFrom?.ToString("o", CultureInfo.InvariantCulture));
            AppendIfSet(sb, "dateTo", parsed.DateTo?.ToString("o", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void AppendIfSet(StringBuilder sb, string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                sb.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value!));
        }

        private static bool TryParseDate(string? raw, out DateTime? value)
        {
            value = null;
            if (string.IsNullOrEmpty(raw)) return true;
            if (!DateTime.TryParse(
                    raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            value = parsed;
            return true;
        }

        private static string? Normalize(string? raw) =>
            string.IsNullOrEmpty(raw) ? null : raw.ToLowerInvariant();

        private static string? NullIfEmpty(string? raw) =>
            string.IsNullOrEmpty(raw) ? null : raw;
    }
}
