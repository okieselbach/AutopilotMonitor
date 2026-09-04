using System.Diagnostics;
using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Diagnostics;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// GET /api/global/session-reports/download?t=… — ticket-gated stream of one blob from the
    /// operator <c>session-reports</c> container for callers that hold no JWT (MCP/AI clients).
    /// <c>PublicAnonymous</c> in the catalog: authorization happened at mint time
    /// (<see cref="SessionReportDownloadTicketFunction"/>, GlobalReadOrAdmin); the HMAC ticket with
    /// <see cref="DiagnosticsDownloadTicket.SessionReportPurpose"/> is the sole authority here, and
    /// the blob name comes from the ticket alone. Bytes are proxied as-is (never unzipped); the
    /// storage SAS never leaves the backend. Per-IP limit + decode via <see cref="TicketDownloadPrelude"/>.
    /// </summary>
    public class SessionReportTicketDownloadFunction
    {
        private const string ContainerName = AutopilotMonitor.Shared.Constants.BlobContainers.SessionReports;
        /// <summary>Whole-transfer ceiling; report ZIPs are tens of MB at most.</summary>
        private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(120);

        private readonly ILogger<SessionReportTicketDownloadFunction> _logger;
        private readonly BlobStorageService _blobStorage;
        private readonly RateLimitService _rateLimitService;

        public SessionReportTicketDownloadFunction(
            ILogger<SessionReportTicketDownloadFunction> logger,
            BlobStorageService blobStorage,
            RateLimitService rateLimitService)
        {
            _logger = logger;
            _blobStorage = blobStorage;
            _rateLimitService = rateLimitService;
        }

        [Function("SessionReportTicketDownload")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/session-reports/download")] HttpRequestData req)
        {
            try
            {
                var (reject, ticket) = await TicketDownloadPrelude.RunAsync(
                    req, _rateLimitService, _logger, "report_ticket_download", DiagnosticsDownloadTicket.SessionReportPurpose);
                if (reject != null)
                    return reject;

                // Signed, but still: the container is flat and a nested name has no business here.
                Security.BlobNameGuard.EnsureFlat(ticket.BlobName, nameof(ticket.BlobName));

                using var cts = new CancellationTokenSource(StreamTimeout);
                var sw = Stopwatch.StartNew();
                var download = await _blobStorage.OpenReadAsync(ContainerName, ticket.BlobName, cts.Token);
                // The result owns the network stream; dispose it, not the Response wrapper.
                using var body = download.Value;
                var contentLength = body.Details.ContentLength;

                _logger.LogInformation(
                    "SessionReportTicketDownload: proxying {Blob}, {SizeBytes} bytes, fetch took {DurationMs}ms",
                    ticket.BlobName, contentLength, sw.ElapsedMilliseconds);

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/octet-stream");
                response.Headers.Add("Content-Disposition", $"attachment; filename=\"{ticket.BlobName}\"");
                if (contentLength > 0)
                    response.Headers.Add("Content-Length", contentLength.ToString());

                await body.Content.CopyToAsync(response.Body, cts.Token);
                return response;
            }
            catch (ArgumentException)
            {
                return await req.BadRequestAsync("Invalid blob name.");
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("SessionReportTicketDownload: blob not found");
                return await req.NotFoundAsync("Report blob not found.");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("SessionReportTicketDownload: timed out streaming blob");
                return await req.ErrorAsync(HttpStatusCode.GatewayTimeout, Constants.ApiErrorCodes.UpstreamTimeout, "Report download timed out.");
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "SessionReportTicketDownload");
            }
        }
    }
}
