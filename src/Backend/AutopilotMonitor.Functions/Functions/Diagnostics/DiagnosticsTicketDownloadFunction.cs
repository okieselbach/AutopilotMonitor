using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Diagnostics;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Diagnostics
{
    /// <summary>
    /// Ticket-gated diagnostics download for callers that hold no JWT (MCP/AI clients).
    /// The endpoint is <c>AuthorizationLevel.Anonymous</c> at the middleware tier and is registered
    /// as <c>PublicAnonymous</c> in the policy catalog: authorization was already performed when the
    /// ticket was minted (<see cref="DiagnosticsDownloadTicketFunction"/>, MemberRead). The ONLY
    /// authority here is the HMAC-signed ticket — tenantId + blobName are read FROM the ticket, never
    /// from the query string, so a ticket cannot be retargeted.
    /// <para>
    /// Replay is bounded by the 10-min ticket TTL, the admin size cap, and the fact that the ticket
    /// only ever points at the minting caller's own authorized blob; HMAC forgery is infeasible. On top
    /// of that the route carries a per-client-IP limit as defense in depth — it is unauthenticated and
    /// streams whole blobs, so a valid ticket replayed in a loop would otherwise be unbounded egress.
    /// </para>
    /// </summary>
    public class DiagnosticsTicketDownloadFunction
    {
        // Bounds replay of a still-valid ticket. A real client downloads a package once; the ceiling
        // only has to stay clear of a legitimate retry-after-timeout.
        private const int MaxRequestsPerMinutePerIp = 30;

        private readonly ILogger<DiagnosticsTicketDownloadFunction> _logger;
        private readonly DiagnosticsBlobStreamer _streamer;
        private readonly RateLimitService _rateLimitService;

        public DiagnosticsTicketDownloadFunction(
            ILogger<DiagnosticsTicketDownloadFunction> logger,
            DiagnosticsBlobStreamer streamer,
            RateLimitService rateLimitService)
        {
            _logger = logger;
            _streamer = streamer;
            _rateLimitService = rateLimitService;
        }

        [Function("DiagnosticsTicketDownload")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "diagnostics/download")] HttpRequestData req)
        {
            try
            {
                // Rightmost X-Forwarded-For hop only — leftmost entries are caller-controlled.
                var clientIp = ClientIpExtractor.GetTrustedClientIp(req);
                var rateLimitResult = _rateLimitService.CheckRateLimit(
                    $"diag_ticket_download_{clientIp}", MaxRequestsPerMinutePerIp);

                if (!rateLimitResult.IsAllowed)
                {
                    _logger.LogWarning("DiagnosticsTicketDownload rate limit exceeded for IP {ClientIp} ({Count} requests)",
                        clientIp, rateLimitResult.RequestsInWindow);

                    var tooMany = req.CreateResponse(HttpStatusCode.TooManyRequests);
                    if (rateLimitResult.RetryAfter.HasValue)
                        tooMany.Headers.Add("Retry-After", ((int)rateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                    await tooMany.WriteAsJsonAsync(new { success = false, message = "Rate limit exceeded." });
                    return tooMany;
                }

                var ticket = HttpUtility.ParseQueryString(req.Url.Query)["t"];
                if (string.IsNullOrEmpty(ticket))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = "Missing download ticket." });
                    return bad;
                }

                if (!DiagnosticsDownloadTicket.TryDecode(ticket, out var tenantId, out var blobName, out _, out var reason))
                {
                    _logger.LogWarning("DiagnosticsTicketDownload: rejecting ticket ({Reason})", reason);
                    var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauth.WriteAsJsonAsync(new { success = false, message = "Invalid or expired download ticket." });
                    return unauth;
                }

                return await _streamer.ProxyDownloadAsync(
                    req, tenantId, blobName,
                    new Dictionary<string, string> { ["Source"] = "mcp-ticket" });
            }
            catch (ArgumentException)
            {
                // Malformed blob name inside a (signed) ticket — should not happen, fail closed.
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                return bad;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("DiagnosticsTicketDownload: Blob not found");
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { success = false, message = "Diagnostics package not found." });
                return notFound;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DiagnosticsTicketDownload: timed out streaming blob");
                var timeout = req.CreateResponse(HttpStatusCode.GatewayTimeout);
                await timeout.WriteAsJsonAsync(new { success = false, message = "Diagnostics download timed out." });
                return timeout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ticket-gated diagnostics download");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return err;
            }
        }
    }
}
