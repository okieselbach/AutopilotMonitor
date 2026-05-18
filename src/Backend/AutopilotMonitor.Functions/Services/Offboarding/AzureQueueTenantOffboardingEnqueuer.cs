using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Queues;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Azure Storage Queue producer for <c>tenant-offboarding</c>. Mirrors the existing
    /// session-deletion / analyze producers but **fail-loud** (plan §7 + memory:
    /// feedback_storage_helpers_fail_soft). Caller (<c>TenantOffboardFunction</c>) catches
    /// and translates to HTTP 500; the History + Pointer + Marker rows already exist by then
    /// so the operator can retry by re-issuing the offboard click (or via maintenance tooling).
    /// </summary>
    public sealed class AzureQueueTenantOffboardingEnqueuer : ITenantOffboardingEnqueuer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueTenantOffboardingEnqueuer> _logger;

        private int _queueEnsured;

        public AzureQueueTenantOffboardingEnqueuer(
            IConfiguration configuration,
            ILogger<AzureQueueTenantOffboardingEnqueuer> logger)
        {
            _logger = logger;

            var options = new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.Base64,
            };

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var queueUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.TenantOffboarding}");
                _queueClient = new QueueClient(queueUri, new DefaultAzureCredential(), options);
                _logger.LogInformation(
                    "TenantOffboarding enqueuer initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _queueClient = new QueueClient(
                    connectionString, Constants.QueueNames.TenantOffboarding, options);
                _logger.LogInformation("TenantOffboarding enqueuer initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>Test seam: bind directly to a (possibly mocked) <see cref="QueueClient"/>.</summary>
        internal AzureQueueTenantOffboardingEnqueuer(
            QueueClient queueClient,
            ILogger<AzureQueueTenantOffboardingEnqueuer> logger)
        {
            _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task EnqueueAsync(
            TenantOffboardingEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.TenantId)) throw new ArgumentException("envelope.TenantId required", nameof(envelope));
            if (string.IsNullOrEmpty(envelope.HistoryRowKey)) throw new ArgumentException("envelope.HistoryRowKey required", nameof(envelope));

            await EnsureQueueExistsAsync(ct).ConfigureAwait(false);

            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(
                body,
                visibilityTimeout: visibilityDelay,
                timeToLive: null,
                cancellationToken: ct).ConfigureAwait(false);

            _logger.LogInformation(
                "TenantOffboarding enqueued tenant={Tenant} history={History} drainPollCount={Polls} visibilityDelay={Delay}",
                envelope.TenantId, envelope.HistoryRowKey, envelope.DrainPollCount, visibilityDelay);
        }

        private async Task EnsureQueueExistsAsync(CancellationToken ct)
        {
            if (_queueEnsured == 1) return;
            await _queueClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _queueEnsured, 1);
        }
    }
}
