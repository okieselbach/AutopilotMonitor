using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// CRUD over the per-tenant Expectations blob. Lives under
    /// <c>Constants.BlobContainers.OffboardingState/{tenantId}/{historyRowKey}.expectations.json</c>
    /// — separate container from <c>deletion-manifests</c> so Phase 2.E's blob wipe cannot
    /// orphan crash-recovery state (plan Rev-8-F1).
    /// </summary>
    public interface IOffboardingExpectationsStore
    {
        /// <summary>
        /// Idempotent initial upload via <c>If-None-Match=*</c>. Returns <c>true</c> on a fresh
        /// insert, <c>false</c> when a blob already exists for the same
        /// (tenantId, historyRowKey) — caller treats false as the resume path and re-reads.
        /// Any other Azure error propagates fail-loud.
        /// </summary>
        Task<bool> TryUploadInitialAsync(OffboardingExpectations payload, CancellationToken ct = default);

        /// <summary>
        /// Downloads the Expectations blob; returns <c>(null, null)</c> on 404. Caller's
        /// drain probe fails closed on 404 (<c>FailedPhase="expectations_missing"</c>).
        /// </summary>
        Task<(OffboardingExpectations? Payload, string? ETag)> TryDownloadAsync(
            string tenantId, string historyRowKey, CancellationToken ct = default);

        /// <summary>
        /// ETag-CAS update used by the drain loop to re-record retry counters for CasExhausted
        /// expectations. Throws <see cref="Azure.RequestFailedException"/> on 412 so the caller
        /// can re-read and retry.
        /// </summary>
        Task<string> UpdateWithEtagCasAsync(
            OffboardingExpectations payload, string ifMatchEtag, CancellationToken ct = default);

        /// <summary>
        /// Idempotent <c>DeleteIfExistsAsync</c>. Called as Phase 2.G's last step after the
        /// history row has been marked Completed, and again as defence-in-depth by
        /// <c>OffboardingMarkerCleanupFunction</c> when it removes a Completed marker.
        /// </summary>
        Task DeleteAsync(string tenantId, string historyRowKey, CancellationToken ct = default);
    }
}
