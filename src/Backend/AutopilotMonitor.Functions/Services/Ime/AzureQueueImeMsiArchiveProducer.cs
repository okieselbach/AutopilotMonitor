using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="IImeMsiArchiveProducer"/>. Mirrors
    /// <see cref="Analyze.AzureQueueAnalyzeOnEnrollmentEndProducer"/>: Managed Identity
    /// preferred, connection-string fallback, Base64 encoding (matches the consumer worker).
    /// <para>
    /// <b>Fault tolerance:</b> queue-side exceptions are logged but never rethrown — the
    /// caller is the ingest path's fire-and-forget continuation. A missed enqueue means the
    /// version's installer is not auto-archived; the operator-side blob backfill
    /// is the manual fallback.
    /// </para>
    /// </summary>
    public sealed class AzureQueueImeMsiArchiveProducer : IImeMsiArchiveProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueImeMsiArchiveProducer> _logger;

        private int _queueEnsured; // 0 = not yet ensured, 1 = CreateIfNotExistsAsync has run

        public AzureQueueImeMsiArchiveProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueImeMsiArchiveProducer> logger)
        {
            _logger = logger;
            // Base64 encoding matches the host's queue-trigger default so messages round-trip.
            _queueClient = queueFactory.Create(Constants.QueueNames.ImeMsiArchive);
        }

        /// <summary>
        /// Test seam: construct directly with a mock <see cref="QueueClient"/>. Mirrors
        /// <see cref="Deletion.SessionDeletionProducer"/>'s internal test ctor.
        /// </summary>
        internal AzureQueueImeMsiArchiveProducer(
            QueueClient queueClient,
            ILogger<AzureQueueImeMsiArchiveProducer> logger)
        {
            _queueClient = queueClient;
            _logger = logger;
        }

        public async Task EnqueueAsync(
            ImeMsiArchiveEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken cancellationToken = default)
        {
            if (envelope is null) return;
            if (string.IsNullOrEmpty(envelope.Version))
            {
                _logger.LogWarning("ImeMsiArchive enqueue skipped — missing Version");
                return;
            }

            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var body = JsonConvert.SerializeObject(envelope);
                await _queueClient.SendMessageAsync(
                    body,
                    visibilityTimeout: visibilityDelay,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "ImeMsiArchive enqueued (version={Version} urlPresent={UrlPresent} tenant={Tenant} session={Session} delay={Delay})",
                    envelope.Version, !string.IsNullOrEmpty(envelope.MsiDownloadUrl), envelope.TenantId, envelope.SessionId, visibilityDelay);
            }
            catch (Exception ex)
            {
                // Fail-soft — ingest continuation. Skill-side blob backfill is the fallback.
                _logger.LogWarning(ex,
                    "ImeMsiArchive enqueue failed (version={Version}) — operator-side blob backfill is the fallback",
                    envelope.Version);
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
                _logger.LogWarning(ex, "ImeMsiArchive queue CreateIfNotExists failed — will retry next enqueue");
            }
        }
    }
}
