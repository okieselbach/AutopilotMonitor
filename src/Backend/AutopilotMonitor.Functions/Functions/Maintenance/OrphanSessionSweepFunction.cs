using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Maintenance;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance
{
    /// <summary>
    /// Timer for the orphan-session sweep (<see cref="OrphanSessionSweeper"/>): rows of sessions
    /// that have no Sessions row — which no cascade can ever reach — are found through their
    /// EventSessionIndex handle and removed from every session table. Its own timer, lease and
    /// ops events: the sweep needs a slower cadence than the 2h <c>Maintenance</c> run, must never
    /// race the retention cascade's 00:00/12:00 tick, and its cost (two key drains instead of one
    /// Sessions point-read per handle) should stay visible on its own heartbeat.
    /// </summary>
    public sealed class OrphanSessionSweepFunction
    {
        // Minute 45 of every fourth hour: no other timer fires at :45 (Maintenance :00 of even
        // hours, SessionSweep :30, WhatsNew :20, backup watchdog :00/:30, retention 00:00/12:00).
        // const per repo convention (see SessionSweepFunction.cs).
        private const string Cron = "0 45 */4 * * *";

        private readonly OrphanSessionSweeper _sweeper;
        private readonly ILogger<OrphanSessionSweepFunction> _logger;

        public OrphanSessionSweepFunction(OrphanSessionSweeper sweeper, ILogger<OrphanSessionSweepFunction> logger)
        {
            _sweeper = sweeper;
            _logger = logger;
        }

        [Function("OrphanSessionSweep")]
        public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
        {
            _logger.LogInformation("OrphanSessionSweep timer trigger fired");
            // Lease, run and every ops event (Completed / Failed / SkippedLocked) live in the sweeper,
            // shared with the manual maintenance run.
            await _sweeper.RunAsync("Timer", cancellationToken);
        }
    }
}
