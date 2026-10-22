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
    /// Drives the <see cref="ConfigMgrClientDetector"/>: a first check shortly after start (the
    /// client may already be installed) and one per minute after that (it may arrive later, e.g.
    /// as a Win32 app). Always-on, no config gate — like the V1 check it replaces.
    /// </summary>
    internal sealed class ConfigMgrClientHost : ICollectorHost
    {
        public string Name => "ConfigMgrClientDetector";

        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        private readonly ConfigMgrClientDetector _detector;
        private readonly AgentLogger _logger;
        private Timer? _tickTimer;
        private int _checking;
        private int _disposed;

        public ConfigMgrClientHost(
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
            _detector = new ConfigMgrClientDetector(
                sessionId: sessionId,
                tenantId: tenantId,
                post: new InformationalEventPost(ingress, clock),
                logger: logger,
                clock: clock,
                startupGate: startupGate);
        }

        public void Start()
        {
            _tickTimer = new Timer(_ => SafeCheck(), state: null, dueTime: FirstCheckDelay, period: PollInterval);
            _logger.Info($"ConfigMgrClientHost: started (check every {PollInterval.TotalSeconds}s).");
        }

        public void Stop()
        {
            try
            {
                _tickTimer?.Dispose();
                _tickTimer = null;
            }
            catch (Exception ex) { _logger.Warning($"ConfigMgrClientHost: timer dispose failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Stop();
        }

        private void SafeCheck()
        {
            // A slow service query must not stack a second check on top of the first.
            if (Interlocked.Exchange(ref _checking, 1) == 1) return;
            try
            {
                _detector.Check();
            }
            catch (Exception ex)
            {
                _logger.Error("ConfigMgrClientHost: check failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _checking, 0);
            }
        }
    }
}
