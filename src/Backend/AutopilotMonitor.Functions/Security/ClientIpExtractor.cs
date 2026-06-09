using System;
using System.Linq;
using Microsoft.Azure.Functions.Worker.Http;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Extracts a trustworthy client IP from an <see cref="HttpRequestData"/> for
    /// rate-limiting and audit-logging purposes.
    ///
    /// Security model:
    ///   Azure App Service appends "&lt;client-ip&gt;:&lt;port&gt;" to the X-Forwarded-For
    ///   header when it forwards the request to the Functions worker. Anything already
    ///   present in XFF was written by the (untrusted) client or upstream proxies.
    ///   Therefore the RIGHTMOST entry is the only hop we can trust; using the leftmost
    ///   lets any caller bypass per-IP throttles by rotating spoofed XFF prefixes.
    ///
    ///   With Front Door / another reverse proxy in front of App Service, the rightmost
    ///   hop will be that proxy's egress IP (still trusted, but coarser-grained — out of
    ///   scope for this helper; per-tenant + global circuit-breaker layers cover that).
    /// </summary>
    internal static class ClientIpExtractor
    {
        public const string Unknown = "unknown";

        public static string GetTrustedClientIp(HttpRequestData? req)
        {
            if (req == null) return Unknown;
            if (!req.Headers.TryGetValues("X-Forwarded-For", out var values)) return Unknown;
            // If a request carries multiple X-Forwarded-For headers (rare but legal),
            // concatenate them so the rightmost entry across all headers wins.
            return ExtractTrustedHop(string.Join(",", values));
        }

        /// <summary>
        /// Best-effort REAL client egress IP for forensic/diagnostic STORAGE (e.g. distress-report
        /// SourceIp, audit fields). NOT for rate-limit keys — use <see cref="GetTrustedClientIp"/>
        /// there, because the value below is spoofable when no trusted proxy populates it.
        /// <para>
        /// Behind Azure Front Door the trusted rightmost X-Forwarded-For hop is Front Door's own
        /// egress IP, not the device — so a stored SourceIp taken from it is useless for identifying
        /// origin. Front Door sets the true client IP in <c>X-Azure-ClientIP</c>; prefer that and
        /// fall back to the trusted hop when absent (no Front Door in front).
        /// </para>
        /// </summary>
        public static string GetClientEgressIp(HttpRequestData? req)
        {
            if (req == null) return Unknown;
            if (req.Headers.TryGetValues("X-Azure-ClientIP", out var azValues))
            {
                var ip = ExtractTrustedHop(string.Join(",", azValues));
                if (ip != Unknown) return ip;
            }
            return GetTrustedClientIp(req);
        }

        /// <summary>
        /// Takes the rightmost non-empty entry from an X-Forwarded-For header value and
        /// strips any port suffix or IPv6 brackets. Returns <see cref="Unknown"/> when
        /// the header is missing or contains only whitespace.
        /// </summary>
        internal static string ExtractTrustedHop(string? forwardedFor)
        {
            if (string.IsNullOrWhiteSpace(forwardedFor)) return Unknown;

            var hops = forwardedFor.Split(',');
            string? hop = null;
            for (int i = hops.Length - 1; i >= 0; i--)
            {
                var trimmed = hops[i].Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    hop = trimmed;
                    break;
                }
            }
            if (string.IsNullOrEmpty(hop)) return Unknown;

            return StripPort(hop!);
        }

        private static string StripPort(string ip)
        {
            // Bracketed IPv6 with optional port: "[::1]:12345" -> "::1"
            if (ip.StartsWith('['))
            {
                var closeBracket = ip.IndexOf(']');
                if (closeBracket > 0)
                    return ip.Substring(1, closeBracket - 1);
                return ip;
            }

            // Bare IPv6 (multiple colons) — no port suffix to strip.
            if (ip.IndexOf(':') != ip.LastIndexOf(':'))
                return ip;

            // IPv4 with optional ":port"
            var colonIdx = ip.LastIndexOf(':');
            if (colonIdx > 0)
                return ip.Substring(0, colonIdx);

            return ip;
        }
    }
}
