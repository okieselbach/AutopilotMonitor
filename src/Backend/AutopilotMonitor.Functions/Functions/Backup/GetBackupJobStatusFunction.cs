using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// <c>GET /api/global/backups/jobs/{jobId}</c> — polling endpoint for the
    /// 202-Accepted async backup workflow. GA-only via <c>EndpointAccessPolicyCatalog</c>.
    /// Returns the live <see cref="BackupJobStatus"/> DTO including
    /// <c>backupOutcome</c> (Success / Partial) and <c>lastHeartbeatUtc</c> so the
    /// UI can render Completed / Partial / Failed banners and the operator can spot
    /// stalled jobs at a glance.
    /// </summary>
    public class GetBackupJobStatusFunction
    {
        private readonly BackupJobsRepository _jobs;
        private readonly ILogger<GetBackupJobStatusFunction> _logger;

        public GetBackupJobStatusFunction(BackupJobsRepository jobs, ILogger<GetBackupJobStatusFunction> logger)
        {
            _jobs = jobs;
            _logger = logger;
        }

        [Function("GetBackupJobStatus")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/backups/jobs/{jobId}")] HttpRequestData req,
            string jobId)
        {
            var (job, _) = await _jobs.GetWithETagAsync(jobId, req.FunctionContext.CancellationToken);
            if (job is null)
            {
                return await WriteJsonAsync(req, HttpStatusCode.NotFound, new { error = "JobNotFound", jobId });
            }
            return await WriteJsonAsync(req, HttpStatusCode.OK, job);
        }

        private static async Task<HttpResponseData> WriteJsonAsync(HttpRequestData req, HttpStatusCode status, object body)
        {
            var response = req.CreateResponse(status);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            var json = JsonSerializer.Serialize(body, BackupManifestJson.SerializerOptions);
            await response.WriteStringAsync(json);
            return response;
        }
    }
}
