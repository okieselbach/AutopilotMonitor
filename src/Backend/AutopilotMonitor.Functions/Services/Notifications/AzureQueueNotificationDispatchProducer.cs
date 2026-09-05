using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="INotificationDispatchProducer"/>. Mirrors
    /// <see cref="Analyze.AzureQueueAnalyzeOnEnrollmentEndProducer"/>: Managed Identity preferred,
    /// connection-string fallback, Base64 encoding (matches the host's queue trigger default).
    /// <para>
    /// <b>Fault tolerance:</b> queue-side exceptions are logged but never rethrown — the agent's
    /// HTTP 200 must not be blocked. A missed enqueue means the alert is not delivered; the
    /// warning names tenant and event type so an operator can see it.
    /// </para>
    /// </summary>
    public sealed class AzureQueueNotificationDispatchProducer : INotificationDispatchProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueNotificationDispatchProducer> _logger;

        private int _queueEnsured; // 0 = not yet ensured, 1 = CreateIfNotExistsAsync has run

        public AzureQueueNotificationDispatchProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueNotificationDispatchProducer> logger)
        {
            _logger = logger;
            _queueClient = queueFactory.Create(Constants.QueueNames.NotificationDispatch);
        }

        /// <summary>Test seam: construct directly with a mock <see cref="QueueClient"/>.</summary>
        internal AzureQueueNotificationDispatchProducer(
            QueueClient queueClient,
            ILogger<AzureQueueNotificationDispatchProducer> logger)
        {
            _queueClient = queueClient;
            _logger = logger;
        }

        public async Task EnqueueAsync(NotificationDispatchEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null) return;
            if (string.IsNullOrEmpty(envelope.TenantId) || envelope.Alert is null || envelope.ChannelIds.Count == 0)
            {
                _logger.LogWarning(
                    "NotificationDispatch enqueue skipped — missing TenantId/Alert/ChannelIds (tenant={Tenant} event={EventType})",
                    envelope.TenantId, envelope.Alert?.EventType);
                return;
            }

            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var body = JsonConvert.SerializeObject(envelope);
                await _queueClient.SendMessageAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "NotificationDispatch enqueue failed (tenant={Tenant} session={Session} event={EventType} channels={Channels}) — alert not delivered",
                    envelope.TenantId, envelope.SessionId, envelope.Alert.EventType, envelope.ChannelIds.Count);
            }
        }

        private async Task EnsureQueueExistsAsync(CancellationToken cancellationToken)
        {
            if (_queueEnsured == 1) return;

            try
            {
                await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _queueEnsured, 1);
            }
            catch (Exception ex)
            {
                // Leave _queueEnsured=0 so a later call retries.
                _logger.LogWarning(ex, "NotificationDispatch queue CreateIfNotExists failed — will retry next enqueue");
            }
        }
    }
}
