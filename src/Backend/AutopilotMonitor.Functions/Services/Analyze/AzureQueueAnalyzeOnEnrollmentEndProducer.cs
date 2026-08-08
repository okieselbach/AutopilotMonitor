using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="IAnalyzeOnEnrollmentEndProducer"/>.
    /// Mirrors <c>AzureQueueIndexReconcileProducer</c>: Managed Identity preferred, connection
    /// string fallback, Base64 encoding (matches the consumer worker).
    /// <para>
    /// <b>Fault tolerance:</b> queue-side exceptions (network, throttling, queue missing) are
    /// logged but never rethrown. The agent's HTTP 200 must not be blocked. A missed enqueue
    /// means the session has no auto-analyze — the user can still trigger via the "Analyze Now"
    /// UI button (<c>GET /sessions/{id}/analysis?reanalyze=true</c>).
    /// </para>
    /// </summary>
    public sealed class AzureQueueAnalyzeOnEnrollmentEndProducer : IAnalyzeOnEnrollmentEndProducer
    {
        private readonly QueueClient _queueClient;
        private readonly ILogger<AzureQueueAnalyzeOnEnrollmentEndProducer> _logger;

        private int _queueEnsured; // 0 = not yet ensured, 1 = CreateIfNotExistsAsync has run

        /// <summary>
        /// Delay before the enqueued message becomes visible to the worker. The trigger event
        /// (enrollment_complete / enrollment_failed) is by design the FIRST event of the
        /// agent's terminal sequence — the terminal flush (stuck-app promotion's synthetic
        /// app_install_failed, app_tracking_summary, esp_apps_failure_correlation,
        /// agent_shutting_down) lands in separate uploads seconds later. An immediately
        /// visible message let the worker evaluate rules against a stream missing those rows
        /// (session 53d1e9f6: ANALYZE-ESP-004's optional app_install_failed condition never
        /// matched, {{appName}} stayed a literal placeholder — and the RuleResult dedup makes
        /// that permanent). 30 s covers the flush including the agent's late-event grace.
        /// </summary>
        private static readonly TimeSpan s_visibilityDelay = TimeSpan.FromSeconds(30);

        public AzureQueueAnalyzeOnEnrollmentEndProducer(
            QueueClientFactory queueFactory,
            ILogger<AzureQueueAnalyzeOnEnrollmentEndProducer> logger)
        {
            _logger = logger;
            // Base64 encoding matches the consumer (BackgroundService) so messages round-trip.
            _queueClient = queueFactory.Create(Constants.QueueNames.AnalyzeOnEnrollmentEnd);
        }

        public async Task EnqueueAsync(AnalyzeOnEnrollmentEndEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null) return;
            if (string.IsNullOrEmpty(envelope.TenantId) || string.IsNullOrEmpty(envelope.SessionId))
            {
                _logger.LogWarning(
                    "AnalyzeOnEnrollmentEnd enqueue skipped — missing TenantId/SessionId (reason={Reason})",
                    envelope.Reason);
                return;
            }

            await EnsureQueueExistsAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var body = JsonConvert.SerializeObject(envelope);
                await _queueClient.SendMessageAsync(
                    body,
                    visibilityTimeout: s_visibilityDelay,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Analyze enqueued (tenant={Tenant} session={Session} reason={Reason})",
                    envelope.TenantId, envelope.SessionId, envelope.Reason);
            }
            catch (Exception ex)
            {
                // Fail-soft — agent is on hot path. Manual "Analyze Now" remains the fallback.
                _logger.LogWarning(
                    ex,
                    "AnalyzeOnEnrollmentEnd enqueue failed (tenant={Tenant} session={Session} reason={Reason}) — manual Analyze Now is the fallback",
                    envelope.TenantId, envelope.SessionId, envelope.Reason);
            }
        }

        private async Task EnsureQueueExistsAsync(CancellationToken cancellationToken)
        {
            if (_queueEnsured == 1) return;

            try
            {
                await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                System.Threading.Interlocked.Exchange(ref _queueEnsured, 1);
            }
            catch (Exception ex)
            {
                // Leave _queueEnsured=0 so a later call retries.
                _logger.LogWarning(ex, "AnalyzeOnEnrollmentEnd queue CreateIfNotExists failed — will retry next enqueue");
            }
        }
    }
}
