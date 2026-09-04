using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Diagnostics;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// The shared front half of every ticket-gated anonymous download route (diagnostics packages,
    /// session-report ZIPs): a per-client-IP limit as defense in depth — the route is unauthenticated
    /// and streams whole blobs, so a valid ticket replayed in a loop would otherwise be unbounded
    /// egress — followed by the HMAC ticket decode that is the route's SOLE authority. tenantId and
    /// blobName come FROM the ticket, never from the query string, so a ticket cannot be retargeted;
    /// the <paramref name="purpose"/> keeps a ticket minted for one surface from opening another.
    /// </summary>
    internal static class TicketDownloadPrelude
    {
        /// <summary>
        /// Bounds replay of a still-valid ticket. A real client downloads a package once; the ceiling
        /// only has to stay clear of a legitimate retry-after-timeout.
        /// </summary>
        public const int MaxRequestsPerMinutePerIp = 30;

        public readonly record struct Admitted(string TenantId, string BlobName, string Destination);

        /// <summary>
        /// Returns the response to hand back (429 / 400 / 401) or null with the ticket's bound values.
        /// </summary>
        public static async Task<(HttpResponseData? Reject, Admitted Ticket)> RunAsync(
            HttpRequestData req, RateLimitService rateLimitService, ILogger logger, string routeLabel, string purpose)
        {
            // Rightmost X-Forwarded-For hop only — leftmost entries are caller-controlled.
            var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
            var rateLimitResult = rateLimitService.CheckRateLimit($"{routeLabel}_{clientIp}", MaxRequestsPerMinutePerIp);
            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning("{Route} rate limit exceeded for IP {ClientIp} ({Count} requests)",
                    routeLabel, clientIp, rateLimitResult.RequestsInWindow);

                var tooMany = req.CreateResponse(HttpStatusCode.TooManyRequests);
                if (rateLimitResult.RetryAfter.HasValue)
                    tooMany.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                await tooMany.WriteAsJsonAsync(new { success = false, message = "Rate limit exceeded." });
                return (tooMany, default);
            }

            var ticket = HttpUtility.ParseQueryString(req.Url.Query)["t"];
            if (string.IsNullOrEmpty(ticket))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = "Missing download ticket." });
                return (bad, default);
            }

            if (!DiagnosticsDownloadTicket.TryDecode(ticket, out var tenantId, out var blobName, out var destination, out var reason, purpose: purpose))
            {
                logger.LogWarning("{Route}: rejecting ticket ({Reason})", routeLabel, reason);
                var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauth.WriteAsJsonAsync(new { success = false, message = "Invalid or expired download ticket." });
                return (unauth, default);
            }

            return (null, new Admitted(tenantId, blobName, destination));
        }
    }
}
