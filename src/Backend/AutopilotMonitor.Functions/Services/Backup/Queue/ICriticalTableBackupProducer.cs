using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Backup.Queue
{
    /// <summary>
    /// Envelope for a single backup-queue message. Tiny on purpose — all state lives
    /// in <c>BackupJobs</c>, the worker re-reads it on dequeue.
    /// </summary>
    public sealed class CriticalTableBackupEnvelope
    {
        public string JobId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Producer for the <c>critical-table-backup-jobs</c> queue. <b>Fail-hard</b>:
    /// SendMessageAsync exceptions propagate so the HTTP trigger can roll the
    /// BackupJobStatus row forward to Failed and return 5xx — silent enqueue loss
    /// would leave the job stuck in Queued forever (no Manual-Analyze-Now-style
    /// fallback exists here).
    /// </summary>
    public interface ICriticalTableBackupProducer
    {
        Task EnqueueAsync(CriticalTableBackupEnvelope envelope, CancellationToken ct = default);
    }
}
