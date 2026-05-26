using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Backup
{
    /// <summary>
    /// Drives <see cref="BackupJobWatchdog.SweepAsync"/> every 30 minutes so stuck
    /// Queued / Running rows in <c>BackupJobs</c> are eventually transitioned to
    /// Failed (with the watchdog's lease-probe + ETag-CAS safeguards). Independent
    /// timer because the cadence is shorter than the daily backup itself, and the
    /// watchdog is cheap (single filtered table query + 0..1 lease probe per sweep).
    /// </summary>
    public class BackupJobWatchdogTimerFunction
    {
        private readonly BackupJobWatchdog _watchdog;
        private readonly ILogger<BackupJobWatchdogTimerFunction> _logger;

        public BackupJobWatchdogTimerFunction(
            BackupJobWatchdog watchdog,
            ILogger<BackupJobWatchdogTimerFunction> logger)
        {
            _watchdog = watchdog;
            _logger = logger;
        }

        /// <summary>NCRONTAB "0 */30 * * * *" — every 30 minutes on the half-hour.</summary>
        [Function("BackupJobWatchdogTimer")]
        public async Task Run([TimerTrigger("0 */30 * * * *")] object timer, CancellationToken ct)
        {
            var transitioned = await _watchdog.SweepAsync(ct).ConfigureAwait(false);
            if (transitioned > 0)
            {
                _logger.LogWarning("BackupJobWatchdogTimer: {Count} stale job(s) transitioned to Failed", transitioned);
            }
        }
    }
}
