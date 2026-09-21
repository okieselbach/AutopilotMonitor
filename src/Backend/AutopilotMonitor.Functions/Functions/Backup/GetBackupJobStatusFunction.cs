using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
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
                return await req.NotFoundAsync($"Backup job {jobId} not found.", Constants.BackupErrorCodes.JobNotFound);
            }
            return await req.OkAsync(job);
        }
    }
}
