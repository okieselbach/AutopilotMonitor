using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// Background worker for the <c>analyze-on-enrollment-end</c> queue. Replaces the previous
    /// in-function fire-and-forget <c>Task.Run</c> that ran the rule engine after session-terminal
    /// events — that pattern could be killed mid-flight by Azure Functions scale-in, leaving rule
    /// results un-persisted. Uses the shared <see cref="QueuePollingWorker{TEnvelope}"/> poll loop
    /// with vanilla defaults (batch 32, 5-min visibility, 5 attempts → poison).
    /// </summary>
    public sealed class AnalyzeOnEnrollmentEndQueueWorker : QueuePollingWorker<AnalyzeOnEnrollmentEndEnvelope>
    {
        private readonly AnalyzeOnEnrollmentEndHandler _handler;

        public AnalyzeOnEnrollmentEndQueueWorker(
            QueueClientFactory queueFactory,
            AnalyzeOnEnrollmentEndHandler handler,
            ILogger<AnalyzeOnEnrollmentEndQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.AnalyzeOnEnrollmentEnd, logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        protected override Task HandleAsync(AnalyzeOnEnrollmentEndEnvelope envelope, CancellationToken ct)
            => _handler.HandleAsync(envelope, ct);
    }
}
