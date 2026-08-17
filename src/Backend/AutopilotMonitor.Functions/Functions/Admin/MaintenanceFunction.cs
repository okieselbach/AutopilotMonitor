using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Timer-triggered Azure Function that runs periodic maintenance.
    /// Delegates all logic to MaintenanceService.
    /// </summary>
    public class MaintenanceFunction
    {
        private readonly MaintenanceService _maintenanceService;
        private readonly ILogger<MaintenanceFunction> _logger;

        public MaintenanceFunction(
            MaintenanceService maintenanceService,
            ILogger<MaintenanceFunction> logger)
        {
            _maintenanceService = maintenanceService;
            _logger = logger;
        }

        /// <summary>
        /// Timer trigger: Runs every 2 hours
        /// NCRONTAB format: {second} {minute} {hour} {day} {month} {day-of-week}
        /// Reduced from daily (2:00 UTC) to every 2 hours to catch stalled sessions faster.
        /// MarkStalledSessionsAsTimedOutAsync is idempotent (terminal states not overwritten).
        /// The stalled-session sweep additionally runs hourly at minute 30 via the dedicated
        /// SessionSweepFunction interleave — this chain keeps it as its first step regardless.
        /// </summary>
        [Function("Maintenance")]
        public async Task Run([TimerTrigger("0 0 */2 * * *")] object timer)
        {
            _logger.LogInformation("Maintenance timer trigger fired");
            await _maintenanceService.RunAllAsync();
        }
    }
}
