#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Drives the <see cref="EspPolicyProviderStallDetector"/> from a 60-s wall-clock tick.
    /// Deliberately NOT idle-coupled (a policy-provider stall coexists with a chatty session —
    /// <see cref="StallProbeHost"/>'s idle clock would keep resetting) and NOT idle-stopped
    /// (<see cref="PeriodicCollectorLifecycleHost"/> would sleep through exactly the dormant
    /// window the dwell must observe). Always-on kernel host, no config gate — precedent:
    /// the <c>disk_space_low</c> tripwire.
    /// <para>
    /// First tick fires after 60 s, not at Start: the EnrollmentStatusTracking key legitimately
    /// doesn't exist at agent start, and nothing can be 15 min stale at t=0.
    /// </para>
    /// </summary>
    internal sealed class EspPolicyProviderStallHost : ICollectorHost
    {
        public string Name => "EspPolicyProviderStallDetector";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        private readonly EspPolicyProviderStallDetector _detector;
        private readonly AgentLogger _logger;
        private Timer? _tickTimer;
        private int _disposed;

        public EspPolicyProviderStallHost(
            string sessionId,
            string tenantId,
            AgentLogger logger,
            ISignalIngressSink ingress,
            IClock clock,
            StartupEventGate? startupGate)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            _detector = new EspPolicyProviderStallDetector(
                sessionId: sessionId,
                tenantId: tenantId,
                post: post,
                logger: logger,
                clock: clock,
                startupGate: startupGate);
        }

        public void Start()
        {
            _tickTimer = new Timer(
                _ => SafeTick(),
                state: null,
                dueTime: PollInterval,
                period: PollInterval);
            _logger.Info($"EspPolicyProviderStallHost: started (tick every {PollInterval.TotalSeconds}s).");
        }

        public void Stop()
        {
            try
            {
                _tickTimer?.Dispose();
                _tickTimer = null;
            }
            catch (Exception ex) { _logger.Warning($"EspPolicyProviderStallHost: timer dispose failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }

        private void SafeTick()
        {
            try
            {
                _detector.Tick();
            }
            catch (Exception ex)
            {
                _logger.Error("EspPolicyProviderStallHost: tick failed.", ex);
            }
        }
    }
}
