using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Backup.Queue
{
    /// <summary>
    /// Azure-Storage-Queue producer for the critical-table backup feature.
    /// Mirrors <c>AzureQueueAnalyzeOnEnrollmentEndProducer</c>'s connection setup
    /// (Managed Identity preferred, connection string fallback) but flips the failure
    /// semantics: SendMessageAsync errors propagate so the HTTP trigger can mark
    /// the job Failed + return 5xx instead of leaving the operator with a 202 and
    /// no actual work scheduled.
    /// </summary>
    public sealed class AzureQueueCriticalTableBackupProducer : ICriticalTableBackupProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueCriticalTableBackupProducer> _logger;
        private int _queueEnsured;

        public AzureQueueCriticalTableBackupProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueCriticalTableBackupProducer> logger)
        {
            _logger = logger;
            _queueClient = queueFactory.Create(Constants.QueueNames.CriticalTableBackup);
        }

        public async Task EnqueueAsync(CriticalTableBackupEnvelope envelope, CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.JobId)) throw new ArgumentException("JobId required", nameof(envelope));

            await EnsureQueueExistsAsync(ct).ConfigureAwait(false);

            // Fail-hard: do NOT swallow. Caller (HTTP trigger) catches and rolls the
            // BackupJobStatus row to Failed + returns 500.
            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(body, ct).ConfigureAwait(false);
            _logger.LogInformation("CriticalTableBackup enqueued (jobId={JobId})", envelope.JobId);
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
                _logger.LogError(ex, "CriticalTableBackup queue CreateIfNotExists failed");
                throw;
            }
        }
    }
}
