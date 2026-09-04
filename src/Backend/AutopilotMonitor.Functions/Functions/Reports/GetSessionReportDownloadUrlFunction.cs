using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Returns a short-lived SAS download URL for a session report blob stored in central blob storage.
    /// Portal path (GlobalReadOrAdmin); the MCP uses the ticket-gated proxy instead
    /// (<see cref="SessionReportDownloadTicketFunction"/>) so a raw SAS never leaves the backend there.
    /// </summary>
    public class GetSessionReportDownloadUrlFunction
    {
        private readonly ILogger<GetSessionReportDownloadUrlFunction> _logger;
        private readonly BlobStorageService _blobStorage;
        private const string ContainerName = AutopilotMonitor.Shared.Constants.BlobContainers.SessionReports;

        public GetSessionReportDownloadUrlFunction(
            ILogger<GetSessionReportDownloadUrlFunction> logger,
            BlobStorageService blobStorage)
        {
            _logger = logger;
            _blobStorage = blobStorage;
        }

        [Function("GetSessionReportDownloadUrl")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/session-reports/download-url")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware

                var query = HttpUtility.ParseQueryString(req.Url.Query);
                var blobName = query["blobName"];

                if (string.IsNullOrEmpty(blobName))
                {
                    return await req.BadRequestAsync("blobName query parameter is required.");
                }

                // Prevent path traversal
                if (!BlobNameGuard.IsFlat(blobName))
                {
                    return await req.BadRequestAsync("Invalid blob name.");
                }

                var containerClient = _blobStorage.GetContainerClient(ContainerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                {
                    return await req.NotFoundAsync("Blob not found.");
                }

                // Generate time-limited download URL (15 minutes)
                var downloadUrl = await _blobStorage.GetDownloadUrlAsync(ContainerName, blobName, TimeSpan.FromMinutes(15));

                _logger.LogInformation("Generated session report download URL for blob {BlobName}", blobName);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SessionReportDownloadUrlResponse { Success = true, DownloadUrl = downloadUrl });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "GetSessionReportDownloadUrl");
            }
        }
    }
}
