using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Notifications;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Queue
{
    /// <summary>
    /// Host-managed consumer of the <c>notification-dispatch</c> queue (lease, retry ladder and
    /// poison move owned by the Functions host — see <see cref="AnalyzeOnEnrollmentEndQueueFunction"/>).
    /// Channel resolution and the send live in <see cref="NotificationDispatchHandler"/>.
    /// </summary>
    public sealed class NotificationDispatchQueueFunction
    {
        private readonly NotificationDispatchHandler _handler;
        private readonly ILogger<NotificationDispatchQueueFunction> _logger;

        public NotificationDispatchQueueFunction(
            NotificationDispatchHandler handler,
            ILogger<NotificationDispatchQueueFunction> logger)
        {
            _handler = handler;
            _logger = logger;
        }

        [Function("NotificationDispatchQueue")]
        public Task Run(
            [QueueTrigger(Constants.QueueNames.NotificationDispatch, Connection = Constants.QueueNames.TriggerConnection)]
            QueueMessage message,
            CancellationToken cancellationToken)
        {
            if (!QueueEnvelopeReader.TryRead<NotificationDispatchEnvelope>(
                    message, _logger, out var envelope,
                    validate: e => !string.IsNullOrEmpty(e.TenantId) && e.Alert is not null))
                return Task.CompletedTask;

            return _handler.HandleAsync(envelope, cancellationToken);
        }
    }
}
