using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Indexing
{
    /// <summary>
    /// Background worker for the <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d.3).
    /// Replaces the earlier <c>IndexReconcileFunction</c> QueueTrigger (whose binding required a
    /// Functions-host-specific connection app-setting the project's storage access does not need)
    /// with the shared <see cref="QueuePollingWorker{TEnvelope}"/> poll loop. Vanilla defaults:
    /// batch 32, 5-min visibility, 5 attempts → poison.
    /// </summary>
    public sealed class IndexReconcileQueueWorker : QueuePollingWorker<IndexReconcileEnvelope>
    {
        private readonly IndexReconcileHandler _handler;

        public IndexReconcileQueueWorker(
            QueueClientFactory queueFactory,
            IndexReconcileHandler handler,
            ILogger<IndexReconcileQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.TelemetryIndexReconcile, logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        protected override Task HandleAsync(IndexReconcileEnvelope envelope, CancellationToken ct)
            => _handler.HandleAsync(envelope, ct);
    }
}
