using System.Threading;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Default <see cref="IDeletionProgressDrainProbe"/> backed by
    /// <see cref="BlobStorageService.DownloadDeletionProgressAsync"/>. Plan §7.4 step 5:
    /// drain is satisfied for a session expectation when its progress blob has
    /// <c>CompletedAt != null AND TombstoneStarted == true</c>.
    /// </summary>
    public class DeletionProgressDrainProbe : IDeletionProgressDrainProbe
    {
        private readonly BlobStorageService _blobs;
        private readonly ILogger<DeletionProgressDrainProbe> _logger;

        public DeletionProgressDrainProbe(BlobStorageService blobs, ILogger<DeletionProgressDrainProbe> logger)
        {
            _blobs = blobs;
            _logger = logger;
        }

        public async Task<bool> IsCascadeCompletedAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken ct = default)
        {
            try
            {
                var (progress, _) = await _blobs.DownloadDeletionProgressAsync(tenantId, sessionId, manifestId, ct);
                return progress.CompletedAt != null && progress.TombstoneStarted;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Progress blob not yet written — cascade still running. NOT a failure: the
                // producer creates the progress blob before the queue message lands, but the
                // tombstone step happens at the end of the cascade.
                _logger.LogDebug(
                    "Drain probe: progress blob missing for tenant={Tenant} session={Session} manifest={Manifest} — cascade not done",
                    tenantId, sessionId, manifestId);
                return false;
            }
        }
    }
}
