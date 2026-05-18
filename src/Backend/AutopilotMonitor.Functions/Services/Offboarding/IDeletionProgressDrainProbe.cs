using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Per-session predicate over a cascade-deletion progress blob. Used by the
    /// tenant-offboarding handler to drain against each <see cref="Models.Offboarding.OffboardingExpectation"/>
    /// that ended up in an <c>Enqueued | AlreadyInFlight</c> outcome.
    /// </summary>
    public interface IDeletionProgressDrainProbe
    {
        /// <summary>
        /// Returns <c>true</c> iff the progress blob exists AND
        /// <c>CompletedAt != null AND TombstoneStarted == true</c>. 404 is treated as "not yet
        /// done" (false). Other RequestFailedExceptions propagate so the worker can retry.
        /// </summary>
        Task<bool> IsCascadeCompletedAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken ct = default);
    }
}
