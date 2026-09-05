using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Queue
{
    /// <summary>
    /// Host-managed consumer of the <c>telemetry-index-reconcile</c> queue (Plan §M5.d.3; lease,
    /// retry ladder and poison move owned by the Functions host — see
    /// <see cref="AnalyzeOnEnrollmentEndQueueFunction"/>). Index writes live unchanged in
    /// <see cref="IndexReconcileHandler"/>; the 2h <c>IndexReconcileTimer</c> remains the safety net.
    /// </summary>
    public sealed class IndexReconcileQueueFunction
    {
        private readonly IndexReconcileHandler _handler;
        private readonly ILogger<IndexReconcileQueueFunction> _logger;

        public IndexReconcileQueueFunction(
            IndexReconcileHandler handler,
            ILogger<IndexReconcileQueueFunction> logger)
        {
            _handler = handler;
            _logger = logger;
        }

        [Function("IndexReconcileQueue")]
        public Task Run(
            [QueueTrigger(Constants.QueueNames.TelemetryIndexReconcile, Connection = Constants.QueueNames.TriggerConnection)]
            QueueMessage message,
            CancellationToken cancellationToken)
        {
            if (!QueueEnvelopeReader.TryRead<IndexReconcileEnvelope>(message, _logger, out var envelope))
                return Task.CompletedTask;

            return _handler.HandleAsync(envelope, cancellationToken);
        }
    }
}
