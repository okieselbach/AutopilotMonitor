using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="IPoisonQueueProbe"/>.
    /// Builds clients via the shared <see cref="QueueClientFactory"/> (Managed Identity or
    /// connection-string fallback). QueueClient instances are cached per queue name — they are
    /// thread-safe and cheap to keep around for the lifetime of the Functions host.
    /// <para>
    /// Probes only read <c>GetPropertiesAsync</c> message counts, so they request the factory's
    /// non-Base64 client (encoding is irrelevant for a metadata read).
    /// </para>
    /// </summary>
    public sealed class AzurePoisonQueueProbe : IPoisonQueueProbe
    {
        private readonly ConcurrentDictionary<string, QueueClient> _clients = new();
        private readonly QueueClientFactory _queueFactory;

        public AzurePoisonQueueProbe(QueueClientFactory queueFactory)
        {
            _queueFactory = queueFactory ?? throw new ArgumentNullException(nameof(queueFactory));
        }

        public async Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(queueName))
                throw new ArgumentException("Queue name must not be empty.", nameof(queueName));

            var client = _clients.GetOrAdd(queueName, name => _queueFactory.Create(name, base64: false));

            try
            {
                var props = await client.GetPropertiesAsync(ct).ConfigureAwait(false);
                return props.Value.ApproximateMessagesCount;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Poison queue is created lazily by the worker on first poison-move.
                // A 404 is the steady-state for queues that have never had a failure,
                // and is exactly the "healthy" signal we want to surface.
                return 0;
            }
        }
    }
}
