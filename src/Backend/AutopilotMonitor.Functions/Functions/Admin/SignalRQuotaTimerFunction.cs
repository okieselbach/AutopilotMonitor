using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    /// <summary>
    /// Timer-triggered watcher for the Azure SignalR plan limits (Standard S1
    /// x1 unit: 1,000 concurrent connections / 1M messages per UTC day). Runs
    /// every full hour and emits OpsEvents when the configured thresholds are
    /// crossed so operators get a Telegram ping before clients start getting
    /// 429'd or message overage is billed.
    ///
    /// Separate from the 2h MaintenanceFunction: the SignalR limits can be
    /// breached by a brief connection spike, so the cadence is tighter. Cost
    /// impact is negligible (~720 extra executions/month, ~$0.04).
    /// </summary>
    public class SignalRQuotaTimerFunction
    {
        private readonly MaintenanceService _maintenanceService;
        private readonly ILogger<SignalRQuotaTimerFunction> _logger;

        public SignalRQuotaTimerFunction(
            MaintenanceService maintenanceService,
            ILogger<SignalRQuotaTimerFunction> logger)
        {
            _maintenanceService = maintenanceService;
            _logger = logger;
        }

        /// <summary>
        /// NCRONTAB "0 0 * * * *" - every hour on the minute. The watcher itself
        /// is idempotent (one OpsEvent per EventType per UTC day) and skips
        /// silently when SignalRResourceId is unset, so this trigger is safe to
        /// run in environments without managed-identity wiring.
        /// </summary>
        [Function("SignalRQuotaTimer")]
        public async Task Run([TimerTrigger("0 0 * * * *")] object timer, CancellationToken ct)
        {
            _logger.LogInformation("SignalR quota watcher fired");
            await _maintenanceService.CheckSignalRQuotaAsync(ct);
        }
    }
}
