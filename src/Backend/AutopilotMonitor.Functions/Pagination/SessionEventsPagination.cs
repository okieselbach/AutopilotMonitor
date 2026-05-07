using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the <c>GET /api/sessions/{sessionId}/events</c> pagination
    /// surface. Kept pure so they can be unit-tested without standing up the
    /// Functions HTTP runtime.
    /// </summary>
    public static class SessionEventsPagination
    {
        public const int DefaultPageSize = 200;
        public const int MaxPageSize = 1000;

        /// <summary>Fingerprint binding tokens to <c>(tenantId, sessionId)</c>.</summary>
        public static string Fingerprint(string tenantId, string sessionId) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("tenantId", tenantId),
                new KeyValuePair<string, string?>("sessionId", sessionId),
            });

        /// <summary>
        /// Result of <see cref="ParseQuery"/>. <c>PageSize</c> is null when the
        /// request did not opt in to pagination — the function then returns the
        /// full event list, no <c>nextLink</c> (legacy behavior).
        /// </summary>
        public sealed class Parsed
        {
            public int? PageSize { get; init; }
            public string? Continuation { get; init; }
            public string? Error { get; init; }
        }

        /// <summary>
        /// Parses <c>pageSize</c> + <c>continuation</c> from the URL query.
        /// Tolerant: missing pageSize means "unpaginated"; invalid pageSize is
        /// surfaced as <see cref="Parsed.Error"/> for the caller to 400 on.
        /// </summary>
        public static Parsed ParseQuery(NameValueCollection? query)
        {
            if (query == null)
            {
                return new Parsed { PageSize = null, Continuation = null };
            }

            var rawPageSize = query["pageSize"];
            var rawContinuation = query["continuation"];

            int? pageSize = null;
            if (!string.IsNullOrEmpty(rawPageSize))
            {
                if (!int.TryParse(rawPageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    return new Parsed { Error = "pageSize must be an integer" };
                }
                if (n < 1 || n > MaxPageSize)
                {
                    return new Parsed { Error = $"pageSize must be between 1 and {MaxPageSize}" };
                }
                pageSize = n;
            }

            // continuation without pageSize is meaningless — silently drop it
            // rather than 400, so a caller that toggles pageSize off does not get
            // a confusing rejection on the leftover token.
            var continuation = pageSize.HasValue && !string.IsNullOrEmpty(rawContinuation)
                ? rawContinuation
                : null;

            return new Parsed { PageSize = pageSize, Continuation = continuation };
        }

        /// <summary>
        /// Validates a wire-token + decodes the embedded Azure-Tables continuation.
        /// </summary>
        public static bool TryAcceptContinuation(
            string raw,
            string tenantId,
            string sessionId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(tenantId, sessionId);
            return ContinuationToken.TryDecode(raw, tenantId, fp, out azureToken, out rejectReason);
        }

        /// <summary>
        /// Builds the absolute(-on-host) <c>nextLink</c> URL the AI/UI should
        /// follow. Caller passes the tenantId the continuation was bound to so
        /// follow-up pages can re-bind to the same tenant — critical for GA
        /// cross-tenant reads where the JWT tenant differs from the session's
        /// actual tenant.
        /// </summary>
        public static string BuildNextLink(
            string sessionId,
            int pageSize,
            string wireContinuation,
            string? tenantId,
            IEnumerable<KeyValuePair<string, string?>>? extras = null)
        {
            var sb = new StringBuilder();
            sb.Append("/api/sessions/").Append(sessionId).Append("/events");
            sb.Append("?pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            if (!string.IsNullOrEmpty(tenantId))
            {
                sb.Append("&tenantId=").Append(System.Uri.EscapeDataString(tenantId));
            }
            if (extras != null)
            {
                foreach (var kv in extras)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    sb.Append('&').Append(System.Uri.EscapeDataString(kv.Key))
                      .Append('=').Append(System.Uri.EscapeDataString(kv.Value!));
                }
            }
            return sb.ToString();
        }
    }
}
