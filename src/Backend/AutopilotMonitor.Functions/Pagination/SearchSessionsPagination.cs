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
    /// Pure helpers for <c>/api/search/sessions</c> + <c>/api/global/search/sessions</c>.
    /// Continuation tokens bind the caller's tenantId, the endpoint scope, and a
    /// canonical fingerprint of the full filter — flipping any filter param
    /// (status, manufacturer, hardware filters, ...) invalidates the token, so a
    /// cursor produced by the scan path can't be replayed into the device-snapshot
    /// path or vice-versa.
    /// </summary>
    public static class SearchSessionsPagination
    {
        public const int DefaultPageSize = 50;
        public const int MaxPageSize = 1000;

        public static string Fingerprint(string scope, string callerTenantId, string? filterTenantId, SessionSearchFilter filter)
        {
            // Canonicalize the filter into a flat KV set the shared
            // ContinuationToken hasher already understands. "path" is derived
            // from filter.HasDeviceSnapshotFilters so a token can't bleed
            // between the two query backends.
            var pairs = new List<KeyValuePair<string, string?>>
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
                new KeyValuePair<string, string?>("path", filter.HasDeviceSnapshotFilters ? "snapshot" : "scan"),
                new KeyValuePair<string, string?>("status", filter.Status),
                new KeyValuePair<string, string?>("serialNumber", filter.SerialNumber),
                new KeyValuePair<string, string?>("agentVersion", filter.AgentVersion),
                new KeyValuePair<string, string?>("imeAgentVersion", filter.ImeAgentVersion),
                new KeyValuePair<string, string?>("agentVersionPrefix", filter.AgentVersionPrefix),
                new KeyValuePair<string, string?>("imeAgentVersionPrefix", filter.ImeAgentVersionPrefix),
                new KeyValuePair<string, string?>("deviceName", filter.DeviceName),
                new KeyValuePair<string, string?>("manufacturer", filter.Manufacturer),
                new KeyValuePair<string, string?>("model", filter.Model),
                new KeyValuePair<string, string?>("osBuild", filter.OsBuild),
                new KeyValuePair<string, string?>("enrollmentType", filter.EnrollmentType),
                new KeyValuePair<string, string?>("isPreProvisioned", filter.IsPreProvisioned?.ToString()),
                new KeyValuePair<string, string?>("isHybridJoin", filter.IsHybridJoin?.ToString()),
                new KeyValuePair<string, string?>("isSelfDeployingProfile", filter.IsSelfDeployingProfile?.ToString()),
                new KeyValuePair<string, string?>("geoCountry", filter.GeoCountry),
                new KeyValuePair<string, string?>("startedAfter", filter.StartedAfter?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("startedBefore", filter.StartedBefore?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("rebootCountMin", filter.RebootCountMin?.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("rebootCountMax", filter.RebootCountMax?.ToString(CultureInfo.InvariantCulture)),
            };
            if (filter.DeviceProperties != null)
            {
                foreach (var kv in filter.DeviceProperties.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    pairs.Add(new KeyValuePair<string, string?>($"prop.{kv.Key}", kv.Value));
                }
            }
            return ContinuationToken.ComputeFingerprint(pairs);
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
            SessionSearchFilter filter,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, filterTenantId, filter);
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

            // Echo back every filter param the caller sent so they don't have
            // to track it in their bookmark — drop pageSize/continuation/limit
            // (the new contract owns these).
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
