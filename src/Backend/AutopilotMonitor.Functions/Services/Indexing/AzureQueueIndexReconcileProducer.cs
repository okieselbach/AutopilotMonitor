using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Indexing
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="IIndexReconcileProducer"/>
    /// (Plan §2.8, §M5.d). Writes one Base64-encoded JSON message per envelope onto
    /// the <c>telemetry-index-reconcile</c> queue.
    /// <para>
    /// <b>Gate:</b> feature flag <c>AdminConfiguration.EnableIndexDualWrite</c> is
    /// consulted via <see cref="AdminConfigurationService"/> (5-min memory cache).
    /// When false, the producer no-ops — the primary commit has already happened
    /// and pre-M5.d behaviour stays bit-exact.
    /// </para>
    /// <para>
    /// <b>Fault tolerance:</b> queue-side exceptions (network, throttling, queue
    /// missing) are logged but NOT rethrown. Primary rows are the source of truth;
    /// missed envelopes get recovered by the 2h <c>IndexReconcileTimer</c> (M5.d.4).
    /// </para>
    /// </summary>
    public sealed class AzureQueueIndexReconcileProducer : IIndexReconcileProducer
    {
        private readonly QueueClient _queueClient;
        private readonly AdminConfigurationService _adminConfig;
        private readonly ILogger<AzureQueueIndexReconcileProducer> _logger;

        private int _queueEnsured; // 0 = not yet ensured, 1 = CreateIfNotExistsAsync has run

        public AzureQueueIndexReconcileProducer(
            QueueClientFactory queueFactory,
            AdminConfigurationService adminConfig,
            ILogger<AzureQueueIndexReconcileProducer> logger)
        {
            _adminConfig = adminConfig;
            _logger = logger;
            // Base64 matches the Azure Functions QueueTrigger default so the M5.d.3 consumer
            // binding can decode our messages without an explicit override.
            _queueClient = queueFactory.Create(Constants.QueueNames.TelemetryIndexReconcile);
        }

        public async Task<int> EnqueueBatchAsync(
            IReadOnlyList<IndexReconcileEnvelope> envelopes,
            CancellationToken cancellationToken = default)
        {
            if (envelopes is null || envelopes.Count == 0) return 0;

            // Flag check — cached 5min inside AdminConfigurationService, cheap per call.
            var config = await _adminConfig.GetConfigurationAsync().ConfigureAwait(false);
            if (!config.EnableIndexDualWrite)
            {
                return 0;
            }

            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);

            var sent = 0;
            foreach (var envelope in envelopes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var body = JsonConvert.SerializeObject(envelope);
                    await _queueClient.SendMessageAsync(body, cancellationToken).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "IndexReconcile enqueue failed (source={Source} tenant={Tenant} session={Session}) — timer reconciliation will recover",
                        envelope.SourceKind, envelope.TenantId, envelope.SessionId);
                }
            }

            if (sent > 0)
            {
                _logger.LogDebug("IndexReconcile: enqueued {Count} envelope(s)", sent);
            }
            return sent;
        }

        private async Task EnsureQueueExistsAsync(CancellationToken cancellationToken)
        {
            // Cheap double-check: if CreateIfNotExistsAsync has already succeeded once we skip
            // the round trip. Interlocked flag keeps concurrent first-callers exclusive without
            // introducing a lock on the fast path.
            if (_queueEnsured == 1) return;

            try
            {
                await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                System.Threading.Interlocked.Exchange(ref _queueEnsured, 1);
            }
            catch (Exception ex)
            {
                // Leave _queueEnsured=0 so a later call retries — we'll still attempt SendMessageAsync
                // below; if the queue genuinely doesn't exist that call fails + gets logged per-envelope.
                _logger.LogWarning(ex, "IndexReconcile queue CreateIfNotExists failed — will retry next batch");
            }
        }
    }
}
