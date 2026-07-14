using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Maintenance;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Background worker for the <c>session-deletion-maintenance</c> queue — the manual-trigger
    /// counterpart to the 12h timer. Each message runs the full
    /// <see cref="SessionDeletionMaintenanceFunction.RunCoreAsync"/> body with the requesting
    /// admin as <c>triggeredBy</c>.
    /// <para>
    /// Concurrency against the timer (and a second host instance) is handled INSIDE
    /// <c>RunCoreAsync</c> via the session-deletion maintenance blob lease: a lease-held run
    /// returns normally (SkippedLocked OpsEvent), so the message is deleted rather than
    /// retried — the operator asked for "a run", and one is already active.
    /// </para>
    /// <para>
    /// BatchSize=1 + VisibilityTimeout=60min (run budget is 50min plus cushion — same rationale
    /// as <c>CriticalTableBackupQueueWorker</c>). Real exceptions rethrow → visibility-timeout
    /// retry → poison after <see cref="QueuePollingWorkerBase.MaxDequeueCount"/>.
    /// </para>
    /// </summary>
    public sealed class SessionDeletionMaintenanceQueueWorker : QueuePollingWorker<SessionDeletionMaintenanceTriggerEnvelope>
    {
        private readonly SessionDeletionMaintenanceFunction _maintenance;

        public SessionDeletionMaintenanceQueueWorker(
            QueueClientFactory queueFactory,
            SessionDeletionMaintenanceFunction maintenance,
            ILogger<SessionDeletionMaintenanceQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.SessionDeletionMaintenance, logger, Constants.QueueNames.SessionDeletionMaintenancePoison)
        {
            _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        }

        protected override int BatchSize => 1;
        protected override TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(60);

        protected override bool TryValidate(SessionDeletionMaintenanceTriggerEnvelope envelope)
            => !string.IsNullOrEmpty(envelope.TriggeredBy);

        protected override string DescribeForLog(SessionDeletionMaintenanceTriggerEnvelope envelope)
            => $"triggeredBy={envelope.TriggeredBy}";

        protected override Task HandleAsync(SessionDeletionMaintenanceTriggerEnvelope envelope, CancellationToken ct)
            => _maintenance.RunCoreAsync(envelope.TriggeredBy, ct);
    }
}
