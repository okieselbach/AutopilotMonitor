#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Production <see cref="IAppTrackingReadModel"/> over the live
    /// <see cref="EnrollmentOrchestrator"/>: decision state from the processor, app-tracking
    /// surfaces from <see cref="EnrollmentOrchestrator.CollectorSurfaces"/> (ARCH-F4). All
    /// members delegate lazily — the surfaces only exist after <c>Start</c> step 14, while the
    /// termination handler is constructed earlier inside the <c>onIngressReady</c> hook.
    /// </summary>
    public sealed class OrchestratorAppTrackingReadModel : IAppTrackingReadModel
    {
        private readonly EnrollmentOrchestrator _orchestrator;

        public OrchestratorAppTrackingReadModel(EnrollmentOrchestrator orchestrator)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        }

        public DecisionState CurrentState => _orchestrator.CurrentState;

        public IReadOnlyList<AppPackageState>? PackageStates =>
            _orchestrator.CollectorSurfaces?.AllKnownPackageStates;

        public IReadOnlyDictionary<string, AppInstallTiming>? AppTimings =>
            _orchestrator.CollectorSurfaces?.ImeAppTimings;

        public int IgnoredCount => _orchestrator.CollectorSurfaces?.ImeIgnoredCount ?? 0;

        public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode) =>
            _orchestrator.CollectorSurfaces?.PromoteActiveInstallsToStuck(failureType, message, errorCode)
            ?? Array.Empty<string>();

        public EspTerminalFailureSnapshot? LastEspTerminalFailure =>
            _orchestrator.CollectorSurfaces?.LastEspTerminalFailure;

        public IReadOnlyList<AppPackageState>? GetStarvedUserEspApps() =>
            _orchestrator.CollectorSurfaces?.GetStarvedUserEspApps();

        public bool TryClaimStarvedUserEspAppReport(string appId) =>
            _orchestrator.CollectorSurfaces?.TryClaimStarvedUserEspAppReport(appId) ?? true;
    }
}
