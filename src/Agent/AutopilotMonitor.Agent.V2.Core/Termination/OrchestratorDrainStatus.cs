#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Orchestration;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Production <see cref="IDrainStatus"/> over the live <see cref="EnrollmentOrchestrator"/>.
    /// Both drain surfaces are observable in production; the drain budget is the V1-parity
    /// default (the old optional <c>spoolDrainPeriod</c> constructor parameter was never set
    /// by the runtime host).
    /// </summary>
    public sealed class OrchestratorDrainStatus : IDrainStatus
    {
        /// <summary>V1 parity: up to 20 × 500 ms drains before shutdown.exe.</summary>
        private static readonly TimeSpan DefaultSpoolDrain = TimeSpan.FromMilliseconds(10000);

        private readonly EnrollmentOrchestrator _orchestrator;

        public OrchestratorDrainStatus(EnrollmentOrchestrator orchestrator)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        }

        public TimeSpan SpoolDrainPeriod => DefaultSpoolDrain;

        public bool CanObserveIngress => true;

        public long IngressPendingSignalCount => _orchestrator.IngressPendingSignalCount;

        public bool CanObserveSpool => true;

        public int SpoolPendingItemCount => _orchestrator.PendingItemCount;
    }
}
