using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Diagnostics;
using AutopilotMonitor.Shared;
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
    /// of that the route carries a per-client-IP limit as defense in depth (<see cref="TicketDownloadPrelude"/>).
    /// </para>
    /// </summary>
    public class DiagnosticsTicketDownloadFunction
    {
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
                var (reject, ticket) = await TicketDownloadPrelude.RunAsync(
                    req, _rateLimitService, _logger, "diag_ticket_download", DiagnosticsDownloadTicket.DiagnosticsPurpose);
                if (reject != null)
                    return reject;

                return await _streamer.ProxyDownloadAsync(
                    req, ticket.TenantId, ticket.BlobName,
                    new Dictionary<string, string> { ["Source"] = "mcp-ticket" });
            }
            catch (ArgumentException)
            {
                // Malformed blob name inside a (signed) ticket — should not happen, fail closed.
                return await req.BadRequestAsync("Invalid blob name.");
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("DiagnosticsTicketDownload: Blob not found");
                return await req.NotFoundAsync("Diagnostics package not found.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("DiagnosticsTicketDownload: timed out streaming blob");
                return await req.ErrorAsync(HttpStatusCode.GatewayTimeout, Constants.ApiErrorCodes.UpstreamTimeout, "Diagnostics download timed out.");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "DiagnosticsTicketDownload");
            }
        }
    }
}
