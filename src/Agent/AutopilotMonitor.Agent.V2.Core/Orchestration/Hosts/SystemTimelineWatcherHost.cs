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
    /// Standalone host for the <see cref="SystemTimelineTracker"/> — watches the <c>System</c>
    /// event log for system clock steps (Kernel-General 1) and completed sleep/standby episodes
    /// (Power-Troubleshooter 1 / Kernel-Power 507) and forwards them via the shared single-rail
    /// ingress. Mirrors <see cref="WindowsUpdateWatcherHost"/>: builds its own
    /// <see cref="InformationalEventPost"/> and delegates Start/Stop to the tracker.
    /// </summary>
    internal sealed class SystemTimelineWatcherHost : ICollectorHost
    {
        public string Name => "SystemTimelineTracker";

        private readonly SystemTimelineTracker _tracker;
        private readonly AgentLogger _logger;
        private int _disposed;

        public SystemTimelineWatcherHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            int backfillLookbackMinutes,
            string stateDirectory)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var post = new InformationalEventPost(ingress, clock, logger);
            _tracker = new SystemTimelineTracker(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                backfillEnabled: backfillLookbackMinutes > 0,
                backfillLookbackMinutes: backfillLookbackMinutes,
                stateDirectory: stateDirectory);
        }

        public void Start()
        {
            _tracker.Start();
            _logger.Info("SystemTimelineWatcherHost: started.");
        }

        public void Stop()
        {
            try { _tracker.Stop(); }
            catch (Exception ex) { _logger.Warning($"SystemTimelineWatcherHost: stop failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }
    }
}
