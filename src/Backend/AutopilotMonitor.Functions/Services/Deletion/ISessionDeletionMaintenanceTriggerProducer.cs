using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Envelope for a single manual session-deletion-maintenance trigger message. Tiny on
    /// purpose — the run has no per-job state row; the OpsEvents lifecycle
    /// (Started / BudgetExceeded / SkippedLocked / Completed / Failed) is the status surface.
    /// </summary>
    public sealed class SessionDeletionMaintenanceTriggerEnvelope
    {
        /// <summary>Identifier of the requesting Global Admin (UPN) — flows into the Started OpsEvent.</summary>
        public string TriggeredBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Producer for the <c>session-deletion-maintenance</c> queue. <b>Fail-hard</b>:
    /// SendMessageAsync exceptions propagate so the HTTP trigger can return 5xx — silent
    /// enqueue loss would leave the operator with a 202 and no run ever starting.
    /// </summary>
    public interface ISessionDeletionMaintenanceTriggerProducer
    {
        Task EnqueueAsync(SessionDeletionMaintenanceTriggerEnvelope envelope, CancellationToken ct = default);
    }
}
