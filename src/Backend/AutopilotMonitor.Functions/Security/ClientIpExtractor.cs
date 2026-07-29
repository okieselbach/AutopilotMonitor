using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
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
            if (req.Headers.TryGetValues("X-Forwarded-For", out var values))
            {
                // If a request carries multiple X-Forwarded-For headers (rare but legal),
                // concatenate them so the rightmost entry across all headers wins.
                var hop = ExtractTrustedHop(string.Join(",", values));
                if (hop != Unknown) return hop;
            }

            // Flex Consumption strips the X-Forwarded-* family from the HTTP request the
            // host proxies to the isolated worker (Azure/azure-functions-host#10253/#11254;
            // observed 2026-07-28: every anonymous caller keyed as "unknown"). The host does
            // pass the ORIGINAL header set out-of-band as trigger metadata, so recover the
            // rightmost XFF hop from there — same trust model, different transport.
            var bindingHop = ExtractTrustedHop(
                GetHeaderFromBindingDataJson(TryGetBindingHeadersJson(req), "X-Forwarded-For"));
            if (bindingHop != Unknown) return bindingHop;

            // Last resort: the transport-level peer address — unspoofable, and equal to the
            // rightmost XFF hop when no proxy chain exists. On Flex the worker's peer is the
            // host's local proxy, so loopback means "not a client" and must not be stored or
            // used as a bucket key (a stored 127.0.0.1 is forensically misleading).
            var remote = req.FunctionContext.GetHttpContext()?.Connection.RemoteIpAddress;
            return RemoteIpToString(remote);
        }

        /// <summary>
        /// Normalizes a transport-level peer address into a client-IP string:
        /// null and loopback (the Functions host's local proxy on Flex) yield
        /// <see cref="Unknown"/>; IPv4-mapped IPv6 is folded back to IPv4.
        /// </summary>
        internal static string RemoteIpToString(IPAddress? remote)
        {
            if (remote == null) return Unknown;
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            if (IPAddress.IsLoopback(remote)) return Unknown;
            return remote.ToString();
        }

        private static string? TryGetBindingHeadersJson(HttpRequestData req)
        {
            try
            {
                return req.FunctionContext.BindingContext.BindingData.TryGetValue("Headers", out var raw)
                    ? raw as string
                    : null;
            }
            catch
            {
                // Fail-soft: absent/odd trigger metadata must never break request handling.
                return null;
            }
        }

        /// <summary>
        /// Reads one header (case-insensitive) from the JSON object the Functions host
        /// places in BindingData["Headers"] — the request headers as the HOST saw them,
        /// before the worker-bound proxy hop drops the X-Forwarded-* family.
        /// Returns null for missing/malformed input.
        /// </summary>
        internal static string? GetHeaderFromBindingDataJson(string? headersJson, string headerName)
        {
            if (string.IsNullOrEmpty(headersJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(headersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(prop.Name, headerName, StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        return prop.Value.GetString();
                    }
                }
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Finest-grained trustworthy client IP — used both as the rate-limit key for
        /// endpoints reached through Azure Front Door (e.g. go.autopilotmonitor.com →
        /// /api/bootstrap/go) and as the stored forensic SourceIp (distress reports).
        /// Behind AFD the trusted hop is AFD's egress IP, so all callers would share a
        /// handful of buckets — one abuser could starve every legitimate technician. AFD
        /// carries the true client IP in X-Azure-ClientIP; it is honored ONLY when the
        /// request provably traversed OUR profile: X-Azure-FDID (which AFD overwrites on
        /// pass-through, so a client cannot inject it across AFD) must equal the
        /// FrontDoorProfileId app setting. The same gate keeps stored forensic IPs
        /// unspoofable when the Function App is reached directly, where anyone could
        /// send an X-Azure-ClientIP header. Fail-closed: unset setting,
        /// missing/mismatched/multi-valued FDID, or empty client IP all fall back to
        /// <see cref="GetTrustedClientIp"/>.
        /// </summary>
        public static string GetRateLimitClientIp(HttpRequestData? req)
        {
            if (req == null) return Unknown;

            var expectedFdid = Environment.GetEnvironmentVariable("FrontDoorProfileId");
            if (!string.IsNullOrWhiteSpace(expectedFdid)
                && req.Headers.TryGetValues("X-Azure-FDID", out var fdidValues)
                && FdidMatches(fdidValues, expectedFdid)
                && req.Headers.TryGetValues("X-Azure-ClientIP", out var clientIpValues))
            {
                var ip = ExtractTrustedHop(string.Join(",", clientIpValues));
                if (ip != Unknown) return ip;
            }

            return GetTrustedClientIp(req);
        }

        /// <summary>
        /// Exactly one FDID value, equal to the configured profile ID. Multiple values
        /// mean someone appended to what AFD set — treated as untrusted.
        /// </summary>
        internal static bool FdidMatches(IEnumerable<string> headerValues, string expected)
        {
            string? single = null;
            foreach (var raw in headerValues)
            {
                foreach (var part in raw.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length == 0) continue;
                    if (single != null) return false;
                    single = trimmed;
                }
            }
            return single != null && string.Equals(single, expected, StringComparison.OrdinalIgnoreCase);
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
