using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Azure-Storage-Queue producer for the manual maintenance trigger. Same shape as the
    /// session-deletion and backup trigger producers: <see cref="QueueClientFactory"/>
    /// (Managed Identity preferred), fail-hard.
    /// </summary>
    public sealed class AzureQueueMaintenanceTriggerProducer : IMaintenanceTriggerProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueMaintenanceTriggerProducer> _logger;
        private int _queueEnsured;

        public AzureQueueMaintenanceTriggerProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueMaintenanceTriggerProducer> logger)
        {
            _logger = logger;
            _queueClient = queueFactory.Create(Constants.QueueNames.MaintenanceTrigger);
        }

        public async Task EnqueueAsync(MaintenanceTriggerEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.TriggeredBy)) throw new ArgumentException("TriggeredBy required", nameof(envelope));

            await EnsureQueueExistsAsync(ct).ConfigureAwait(false);

            // Fail-hard: do NOT swallow. Caller (HTTP trigger) catches and returns 500.
            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(body, ct).ConfigureAwait(false);
            _logger.LogInformation("Maintenance trigger enqueued (triggeredBy={TriggeredBy})", envelope.TriggeredBy);
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
                _logger.LogError(ex, "Maintenance trigger queue CreateIfNotExists failed");
                throw;
            }
        }
    }
}
