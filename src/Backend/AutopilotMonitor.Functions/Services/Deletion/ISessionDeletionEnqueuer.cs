using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Producer-side enqueue surface for cascade deletions (plan §5 PR3 / PR5). Introduced in
    /// PR5 so HTTP-layer functions and timer-driven maintenance can depend on the contract
    /// rather than the sealed <see cref="SessionDeletionProducer"/> implementation — the
    /// existing producer is the production binding, tests substitute a fake.
    /// <para>
    /// Mirrors the sibling queue producers (<c>IAnalyzeOnEnrollmentEndProducer</c>,
    /// <c>IVulnerabilityCorrelateProducer</c>): one method
    /// returning an outcome enum so callers translate to HTTP statuses without coupling to
    /// internal CAS / blob mechanics.
    /// </para>
    /// </summary>
    public interface ISessionDeletionEnqueuer
    {
        /// <summary>
        /// Run the §2 producer sequence (CAS-Preparing → build manifest → upload snapshot
        /// + progress → audit → CAS-Queued → send envelope) and return the outcome. See
        /// <see cref="SessionDeletionEnqueueOutcome"/> for the full set of values.
        /// </summary>
        Task<SessionDeletionEnqueueResult> EnqueueAsync(
            string tenantId,
            string sessionId,
            string reason,
            DeletionActor actor,
            DeletionRetentionContext? retentionContext = null,
            CancellationToken cancellationToken = default);
    }
}
