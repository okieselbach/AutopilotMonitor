using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Azure-Storage-Queue producer for the manual session-deletion-maintenance trigger.
    /// Mirrors <c>AzureQueueCriticalTableBackupProducer</c>: Managed Identity preferred via
    /// <see cref="QueueClientFactory"/>, fail-hard semantics (SendMessageAsync errors propagate
    /// so the HTTP trigger returns 5xx instead of a hollow 202).
    /// </summary>
    public sealed class AzureQueueSessionDeletionMaintenanceTriggerProducer : ISessionDeletionMaintenanceTriggerProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueSessionDeletionMaintenanceTriggerProducer> _logger;
        private int _queueEnsured;

        public AzureQueueSessionDeletionMaintenanceTriggerProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueSessionDeletionMaintenanceTriggerProducer> logger)
        {
            _logger = logger;
            _queueClient = queueFactory.Create(Constants.QueueNames.SessionDeletionMaintenance);
        }

        public async Task EnqueueAsync(SessionDeletionMaintenanceTriggerEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.TriggeredBy)) throw new ArgumentException("TriggeredBy required", nameof(envelope));

            await EnsureQueueExistsAsync(ct).ConfigureAwait(false);

            // Fail-hard: do NOT swallow. Caller (HTTP trigger) catches and returns 500.
            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(body, ct).ConfigureAwait(false);
            _logger.LogInformation("SessionDeletionMaintenance trigger enqueued (triggeredBy={TriggeredBy})", envelope.TriggeredBy);
        }

        private async Task EnsureQueueExistsAsync(CancellationToken ct)
        {
            if (_queueEnsured == 1) return;
            try
            {
                await _queueClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _queueEnsured, 1);
            }
            catch (Exception ex)
            {
                // Ensure failure during enqueue is itself a fail-hard signal — surface to the caller.
                _logger.LogError(ex, "SessionDeletionMaintenance queue CreateIfNotExists failed");
                throw;
            }
        }
    }
}
