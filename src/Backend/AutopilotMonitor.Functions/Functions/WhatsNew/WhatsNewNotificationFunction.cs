using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.WhatsNew;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.WhatsNew
{
    /// <summary>
    /// Hourly timer: diffs the portal's live <c>/whats-new.json</c> against the platform watermark
    /// and sends one digest of the newly published entries to every tenant channel that opted in
    /// (<c>NotificationChannel.NotifyOnWhatsNew</c>). Hourly — not on deploy — because the payload
    /// only changes with a web deploy and a delay of up to an hour is fine for product news.
    /// Offset to :20 so it does not collide with the :00 maintenance/SLA timers or the :30 sweep.
    /// </summary>
    public class WhatsNewNotificationFunction
    {
        private readonly WhatsNewNotificationService _service;
        private readonly ILogger<WhatsNewNotificationFunction> _logger;

        public WhatsNewNotificationFunction(
            WhatsNewNotificationService service,
            ILogger<WhatsNewNotificationFunction> logger)
        {
            _service = service;
            _logger = logger;
        }

        [Function("WhatsNewNotification")]
        public async Task Run([TimerTrigger("0 20 * * * *")] object timer)
        {
            _logger.LogInformation("What's new notification timer fired at {Time}", System.DateTime.UtcNow);
            await _service.RunAsync();
        }
    }
}
