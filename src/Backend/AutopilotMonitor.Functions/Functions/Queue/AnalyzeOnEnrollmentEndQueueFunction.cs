using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Analyze;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Queue
{
    /// <summary>
    /// Host-managed consumer of the <c>analyze-on-enrollment-end</c> queue. The Functions host
    /// owns the lease (renewed while the handler runs), the retry ladder (throw ⇒ redelivery after
    /// <c>extensions.queues.visibilityTimeout</c>) and the poison move after
    /// <c>maxDequeueCount</c> attempts to <c>analyze-on-enrollment-end-poison</c>. On Flex
    /// Consumption the queue length is the scale signal, so a message enqueued while no instance
    /// is running wakes one — the property the earlier self-polling BackgroundService lacked.
    /// The rule-engine work lives unchanged in <see cref="AnalyzeOnEnrollmentEndHandler"/>.
    /// </summary>
    public sealed class AnalyzeOnEnrollmentEndQueueFunction
    {
        private readonly AnalyzeOnEnrollmentEndHandler _handler;
        private readonly ILogger<AnalyzeOnEnrollmentEndQueueFunction> _logger;

        public AnalyzeOnEnrollmentEndQueueFunction(
            AnalyzeOnEnrollmentEndHandler handler,
            ILogger<AnalyzeOnEnrollmentEndQueueFunction> logger)
        {
            _handler = handler;
            _logger = logger;
        }

        [Function("AnalyzeOnEnrollmentEndQueue")]
        public Task Run(
            [QueueTrigger(Constants.QueueNames.AnalyzeOnEnrollmentEnd, Connection = Constants.QueueNames.TriggerConnection)]
            QueueMessage message,
            CancellationToken cancellationToken)
        {
            if (!QueueEnvelopeReader.TryRead<AnalyzeOnEnrollmentEndEnvelope>(message, _logger, out var envelope))
                return Task.CompletedTask;

            return _handler.HandleAsync(envelope, cancellationToken);
        }
    }
}
