#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Standalone host for the <see cref="WindowsUpdateTracker"/> — watches
    /// <c>Microsoft-Windows-WindowsUpdateClient/Operational</c> for quality/cumulative update
    /// activity during OOBE / ESP and forwards it via the shared single-rail ingress.
    /// Mirrors <see cref="StallProbeHost"/>: builds its own <see cref="InformationalEventPost"/>
    /// and delegates Start/Stop to the tracker.
    /// </summary>
    internal sealed class WindowsUpdateWatcherHost : ICollectorHost
    {
        public string Name => "WindowsUpdateTracker";

        private readonly WindowsUpdateTracker _tracker;
        private readonly AgentLogger _logger;
        private int _disposed;

        public WindowsUpdateWatcherHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int[]? targetedEventIds,
            int backfillLookbackMinutes,
            string stateDirectory)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var post = new InformationalEventPost(ingress, clock, logger);
            _tracker = new WindowsUpdateTracker(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                targetedEventIds: targetedEventIds,
                backfillEnabled: backfillLookbackMinutes > 0,
                backfillLookbackMinutes: backfillLookbackMinutes,
                stateDirectory: stateDirectory);
        }

        public void Start()
        {
            _tracker.Start();
            _logger.Info("WindowsUpdateWatcherHost: started.");
        }

        public void Stop()
        {
            try { _tracker.Stop(); }
            catch (Exception ex) { _logger.Warning($"WindowsUpdateWatcherHost: stop failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }
    }
}
