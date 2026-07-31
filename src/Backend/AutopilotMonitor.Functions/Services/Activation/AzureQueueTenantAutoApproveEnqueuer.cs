using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Activation
{
    /// <summary>
    /// Azure Storage Queue producer for <c>tenant-auto-approve</c>. Mirrors
    /// <c>AzureQueueTenantOffboardingEnqueuer</c>: fail-loud so the caller decides how to
    /// degrade — the signup site sends fire-and-forget, and a lost enqueue only means the
    /// tenant waits for manual approval (visible on the waitlist), never a wrong activation.
    /// </summary>
    public sealed class AzureQueueTenantAutoApproveEnqueuer : ITenantAutoApproveEnqueuer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueTenantAutoApproveEnqueuer> _logger;

        private int _queueEnsured;

        public AzureQueueTenantAutoApproveEnqueuer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueTenantAutoApproveEnqueuer> logger)
        {
            _logger = logger;
            _queueClient = queueFactory.Create(Constants.QueueNames.TenantAutoApprove);
        }

        /// <summary>Test seam: bind directly to a (possibly mocked) <see cref="QueueClient"/>.</summary>
        internal AzureQueueTenantAutoApproveEnqueuer(
            QueueClient queueClient,
            ILogger<AzureQueueTenantAutoApproveEnqueuer> logger)
        {
            _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task EnqueueAsync(
            TenantAutoApproveEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken ct = default)
        {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            if (string.IsNullOrEmpty(envelope.TenantId)) throw new ArgumentException("envelope.TenantId required", nameof(envelope));

            await EnsureQueueExistsAsync(ct).ConfigureAwait(false);

            var body = JsonConvert.SerializeObject(envelope);
            await _queueClient.SendMessageAsync(
                body,
                visibilityTimeout: visibilityDelay,
                timeToLive: null,
                cancellationToken: ct).ConfigureAwait(false);

            _logger.LogInformation(
                "TenantAutoApprove enqueued tenant={Tenant} upn={Upn} visibilityDelay={Delay}",
                envelope.TenantId, envelope.SignupUpn, visibilityDelay);
        }

        private async Task EnsureQueueExistsAsync(CancellationToken ct)
        {
            if (_queueEnsured == 1) return;
            await _queueClient.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _queueEnsured, 1);
        }
    }
}
