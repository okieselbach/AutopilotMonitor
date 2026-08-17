using System.Diagnostics;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance
{
    /// <summary>
    /// Hourly interleave for the stalled-session sweep
    /// (<see cref="MaintenanceService.MarkStalledSessionsAsTimedOutAsync"/>). The 2h
    /// <c>Maintenance</c> timer keeps running the sweep as its first step; this function adds an
    /// offset tick at minute 30, so between the two the sweep effectively runs every hour and the
    /// sweep-induced classification latency drops from ≤2h to ≤1h (a 5h-silent session is
    /// terminalized after ~6h worst case instead of ~7h). Thresholds (2h silence → Stalled,
    /// SessionTimeoutHours → terminal) are unchanged — only the sampling rate rises.
    /// <para>
    /// No lease/serialization on purpose (unlike <see cref="SessionDeletionMaintenanceFunction"/>):
    /// the sweep is idempotent — terminal states are never overwritten, the silence guard protects
    /// live sessions, and a doubled pass is a no-op — so overlapping with the 2h run is harmless.
    /// Extraction pattern per the cadence-split precedent documented in
    /// <c>MaintenanceService.RunAllAsync</c> (SessionDeletionMaintenanceFunction).
    /// </para>
    /// </summary>
    public class SessionSweepFunction
    {
        // Minute 30 — deliberately offset from the 2h Maintenance timer (minute 0) so the two
        // never fire together. const per repo convention (see IndexReconcileTimer.cs).
        private const string Cron = "0 30 * * * *";

        private readonly MaintenanceService _maintenanceService;
        private readonly OpsEventService _opsEventService;
        private readonly ILogger<SessionSweepFunction> _logger;

        public SessionSweepFunction(
            MaintenanceService maintenanceService,
            OpsEventService opsEventService,
            ILogger<SessionSweepFunction> logger)
        {
            _maintenanceService = maintenanceService;
            _opsEventService = opsEventService;
            _logger = logger;
        }

        [Function("SessionSweep")]
        public async Task Run([TimerTrigger(Cron)] object timer)
        {
            _logger.LogInformation("SessionSweep timer trigger fired");
            var sw = Stopwatch.StartNew();

            var result = await _maintenanceService.MarkStalledSessionsAsTimedOutAsync();
            sw.Stop();

            if (result.Error is null)
            {
                await _opsEventService.RecordSessionSweepCompletedAsync(
                    result.StalledMarked, result.TimedOut, (int)sw.ElapsedMilliseconds);
            }
            else
            {
                await _opsEventService.RecordSessionSweepFailedAsync(result.Error);
            }
        }
    }
}
