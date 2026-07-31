using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Activation
{
    /// <summary>
    /// Background poll-loop for the <c>tenant-auto-approve</c> queue. Thin queue shell —
    /// all decisions live in <see cref="TenantAutoApproveHandler"/>. Defaults of
    /// <see cref="QueuePollingWorkerBase"/> (batch 32, 5-min visibility, 10-s poll,
    /// poison after 5 attempts) fit this tiny workload.
    /// </summary>
    public sealed class TenantAutoApproveQueueWorker : QueuePollingWorker<TenantAutoApproveEnvelope>
    {
        private readonly TenantAutoApproveHandler _handler;

        public TenantAutoApproveQueueWorker(
            QueueClientFactory queueFactory,
            TenantAutoApproveHandler handler,
            ILogger<TenantAutoApproveQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.TenantAutoApprove, logger)
        {
            _handler = handler;
        }

        /// <summary>Test seam: inject pre-built (possibly in-memory) queues.</summary>
        internal TenantAutoApproveQueueWorker(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            TenantAutoApproveHandler handler,
            ILogger logger,
            TimeSpan? pollIntervalOverride = null)
            : base(mainQueue, poisonQueue, logger, pollIntervalOverride)
        {
            _handler = handler;
        }

        protected override bool TryValidate(TenantAutoApproveEnvelope envelope)
            => !string.IsNullOrWhiteSpace(envelope.TenantId);

        protected override string DescribeForLog(TenantAutoApproveEnvelope envelope)
            => $"tenant={envelope.TenantId}";

        protected override Task HandleAsync(TenantAutoApproveEnvelope envelope, CancellationToken ct)
            => _handler.HandleAsync(envelope, ct);
    }
}
