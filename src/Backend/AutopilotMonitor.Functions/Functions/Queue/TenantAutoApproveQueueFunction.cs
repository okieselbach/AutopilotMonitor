using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Activation;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Queue
{
    /// <summary>
    /// Host-managed consumer of the <c>tenant-auto-approve</c> queue (lease, retry ladder and
    /// poison move owned by the Functions host — see <see cref="AnalyzeOnEnrollmentEndQueueFunction"/>).
    /// The producer's ~1-minute visibility delay still applies; every decision lives in
    /// <see cref="TenantAutoApproveHandler"/>.
    /// </summary>
    public sealed class TenantAutoApproveQueueFunction
    {
        private readonly TenantAutoApproveHandler _handler;
        private readonly ILogger<TenantAutoApproveQueueFunction> _logger;

        public TenantAutoApproveQueueFunction(
            TenantAutoApproveHandler handler,
            ILogger<TenantAutoApproveQueueFunction> logger)
        {
            _handler = handler;
            _logger = logger;
        }

        [Function("TenantAutoApproveQueue")]
        public Task Run(
            [QueueTrigger(Constants.QueueNames.TenantAutoApprove, Connection = Constants.QueueNames.TriggerConnection)]
            QueueMessage message,
            CancellationToken cancellationToken)
        {
            if (!QueueEnvelopeReader.TryRead<TenantAutoApproveEnvelope>(
                    message, _logger, out var envelope,
                    validate: e => !string.IsNullOrWhiteSpace(e.TenantId)))
                return Task.CompletedTask;

            return _handler.HandleAsync(envelope, cancellationToken);
        }
    }
}
