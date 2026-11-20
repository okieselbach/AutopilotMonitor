using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Background worker for the <c>maintenance-trigger</c> queue — runs the manually triggered
    /// maintenance off the HTTP request, which would otherwise have to stay open for minutes
    /// and dies at the gateway limit.
    /// <para>
    /// A self-polling worker, not a queue-triggered function, because the run holds a lease
    /// for its whole lifetime. Concurrency against the 2h timer is handled inside
    /// <see cref="MaintenanceService.RunManualAsync"/> by the run gate: a lease-held run
    /// returns normally (SkippedLocked ops event), so the message is deleted rather than
    /// retried — the operator asked for "a run", and one is already active. A failing run
    /// reports MaintenanceFailed and returns as well: maintenance is not retried blindly.
    /// </para>
    /// </summary>
    public sealed class MaintenanceTriggerQueueWorker : QueuePollingWorker<MaintenanceTriggerEnvelope>
    {
        private readonly MaintenanceService _maintenance;
        private readonly TenantRegionMaintenance _tenantRegion;

        public MaintenanceTriggerQueueWorker(
            QueueClientFactory queueFactory,
            MaintenanceService maintenance,
            TenantRegionMaintenance tenantRegion,
            ILogger<MaintenanceTriggerQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.MaintenanceTrigger, logger, Constants.QueueNames.MaintenanceTriggerPoison)
        {
            _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
            _tenantRegion = tenantRegion ?? throw new ArgumentNullException(nameof(tenantRegion));
        }

        // The host functionTimeout (60min) is the upper bound of a run; the message must not
        // reappear while its run is still going.
        protected override TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(60);

        protected override bool TryValidate(MaintenanceTriggerEnvelope envelope)
            => !string.IsNullOrEmpty(envelope.TriggeredBy);

        protected override string DescribeForLog(MaintenanceTriggerEnvelope envelope)
            => $"triggeredBy={envelope.TriggeredBy} targetDate={envelope.TargetDate:yyyy-MM-dd} aggregateOnly={envelope.AggregateOnly}";

        protected override async Task HandleAsync(MaintenanceTriggerEnvelope envelope, CancellationToken ct)
        {
            // Like the 2-hour timer (MaintenanceFunction), the manual run fills the Entra region of tenants
            // that have none; here first, because the operator who pressed the button waits for it and the
            // rest of the run takes many minutes. Fail-soft inside. An aggregation-only run stays one.
            if (!envelope.AggregateOnly)
                await _tenantRegion.RunAsync();
            await _maintenance.RunManualAsync(envelope.TargetDate, envelope.AggregateOnly, envelope.TriggeredBy);
        }
    }
}
