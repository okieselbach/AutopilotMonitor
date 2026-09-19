using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// Queue envelope of a manually triggered maintenance run. There is no per-run state row —
    /// the Maintenance* ops events are the status surface.
    /// </summary>
    public sealed class MaintenanceTriggerEnvelope
    {
        public string TriggeredBy { get; set; } = string.Empty;

        /// <summary>Date to aggregate (UTC date); null = yesterday.</summary>
        public DateTime? TargetDate { get; set; }

        public bool AggregateOnly { get; set; }
    }

    /// <summary>
    /// Producer for the manual maintenance trigger. <b>Fail-hard</b>: send failures propagate so
    /// the HTTP trigger answers 5xx instead of a hollow 202.
    /// </summary>
    public interface IMaintenanceTriggerProducer
    {
        Task EnqueueAsync(MaintenanceTriggerEnvelope envelope, CancellationToken ct = default);
    }
}
