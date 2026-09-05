using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Enqueues a channel notification for durable delivery by
    /// <c>NotificationDispatchQueueFunction</c>. Implementations must be fail-soft: callers sit
    /// on the agent's hot path (ingest, distress) and must never observe an exception.
    /// </summary>
    public interface INotificationDispatchProducer
    {
        Task EnqueueAsync(NotificationDispatchEnvelope envelope, CancellationToken cancellationToken = default);
    }
}
