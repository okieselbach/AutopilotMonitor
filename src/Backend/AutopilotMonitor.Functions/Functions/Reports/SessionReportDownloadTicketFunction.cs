using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// POST /api/global/session-reports/download-ticket — mints a short-lived, self-authenticating
    /// download ticket for one blob of the operator <c>session-reports</c> container (a submitted
    /// report ZIP, a preserved diagnostics copy, or a diag-files report). Same tier as the portal's
    /// download-url route (GlobalReadOrAdmin, enforced by the policy middleware), but the client
    /// gets a ticket for the proxied stream (<see cref="SessionReportTicketDownloadFunction"/>)
    /// instead of a storage SAS — the MCP contract never hands out a SAS.
    /// <para>
    /// The ticket carries the caller's home tenant (JWT tid) as the audience it was issued to and
    /// <see cref="DiagnosticsDownloadTicket.SessionReportPurpose"/> as its purpose, so a diagnostics
    /// ticket can never open a report and vice versa.
    /// </para>
    /// </summary>
    public class SessionReportDownloadTicketFunction
    {
        private const string ContainerName = AutopilotMonitor.Shared.Constants.BlobContainers.SessionReports;
        public const string DestinationLabel = "SessionReports";

        private readonly ILogger<SessionReportDownloadTicketFunction> _logger;
        private readonly BlobStorageService _blobStorage;

        public SessionReportDownloadTicketFunction(
            ILogger<SessionReportDownloadTicketFunction> logger,
            BlobStorageService blobStorage)
        {
            _logger = logger;
            _blobStorage = blobStorage;
        }

        [Function("SessionReportDownloadTicket")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/session-reports/download-ticket")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalReadOrAdmin authorization enforced by PolicyEnforcementMiddleware.
                var requestCtx = req.GetRequestContext();

                string? blobName = null;
                try
                {
                    var body = await JsonSerializer.DeserializeAsync<TicketRequest>(
                        req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    blobName = body?.BlobName;
                }
                catch (JsonException) { /* fall through to the 400 below */ }

                if (string.IsNullOrEmpty(blobName))
                {
                    return await req.BadRequestAsync("blobName is required.");
                }

                // Report blobs are flat names; anything else is a traversal attempt.
                if (!BlobNameGuard.IsFlat(blobName))
                {
                    return await req.BadRequestAsync("Invalid blob name.");
                }

                var blobClient = _blobStorage.GetContainerClient(ContainerName).GetBlobClient(blobName);
                if (!await blobClient.ExistsAsync())
                {
                    return await req.NotFoundAsync("Blob not found.");
                }

                var audience = string.IsNullOrEmpty(requestCtx.TenantId) ? "operator" : requestCtx.TenantId;
                var issuedAt = DateTimeOffset.UtcNow;
                var ticket = DiagnosticsDownloadTicket.Encode(
                    audience, blobName, DestinationLabel, issuedAt, DiagnosticsDownloadTicket.SessionReportPurpose);
                var expiresAt = issuedAt.Add(DiagnosticsDownloadTicket.DefaultTtl).UtcDateTime;

                long? sizeBytes;
                using (var sizeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    sizeBytes = await _blobStorage.TryGetSizeAsync(ContainerName, blobName, sizeCts.Token);
                }

                _logger.LogInformation(
                    "SessionReportDownloadTicket: issued for blob {Blob} to {User}, expires {ExpiresAt}",
                    blobName, requestCtx.UserPrincipalName, expiresAt.ToString("O"));

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new DiagnosticsDownloadTicketResponse
                {
                    Success = true,
                    Url = $"/api/global/session-reports/download?t={Uri.EscapeDataString(ticket)}",
                    ExpiresAt = expiresAt,
                    BlobName = blobName,
                    Destination = DestinationLabel,
                    SizeBytes = sizeBytes,
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "SessionReportDownloadTicket");
            }
        }

        private sealed class TicketRequest
        {
            public string? BlobName { get; set; }
        }
    }
}
