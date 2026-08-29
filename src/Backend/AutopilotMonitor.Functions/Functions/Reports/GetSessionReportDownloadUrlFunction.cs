using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Returns a short-lived SAS download URL for a session report blob stored in central blob storage.
    /// Global Admin only.
    /// </summary>
    public class GetSessionReportDownloadUrlFunction
    {
        private readonly ILogger<GetSessionReportDownloadUrlFunction> _logger;
        private readonly BlobStorageService _blobStorage;
        private const string ContainerName = "session-reports";

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
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "blobName query parameter is required." });
                    return badRequest;
                }

                // Prevent path traversal
                if (!BlobNameGuard.IsFlat(blobName))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new { success = false, message = "Invalid blob name." });
                    return badRequest;
                }

                var containerClient = _blobStorage.GetContainerClient(ContainerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                if (!await blobClient.ExistsAsync())
                {
                    var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundResponse.WriteAsJsonAsync(new { success = false, message = "Blob not found." });
                    return notFoundResponse;
                }

                // Generate time-limited download URL (15 minutes)
                var downloadUrl = await _blobStorage.GetDownloadUrlAsync(ContainerName, blobName, TimeSpan.FromMinutes(15));

                _logger.LogInformation("Generated session report download URL for blob {BlobName}", blobName);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { success = true, downloadUrl });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating session report download URL");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error." });
                return errorResponse;
            }
        }
    }
}
