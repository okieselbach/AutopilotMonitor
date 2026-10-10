#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// V1 parity (<c>CollectorCoordinator.StartOptionalCollectors:375-382</c>) — owns the
    /// lifecycle of <see cref="NetworkChangeDetector"/>. Without this host the detector class
    /// existed in V2 but was never instantiated, so WiFi / SSID / connectivity transitions
    /// were invisible to the backend.
    /// </summary>
    internal sealed class NetworkChangeHost : ICollectorHost
    {
        public string Name => "NetworkChangeDetector";

        private readonly NetworkChangeDetector _detector;
        private readonly AgentLogger _logger;
        private int _disposed;

        public NetworkChangeHost(
            string sessionId,
            string tenantId,
            ISignalIngressSink ingress,
            IClock clock,
            AgentLogger logger)
        {
            if (ingress == null) throw new ArgumentNullException(nameof(ingress));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            var post = new InformationalEventPost(ingress, clock);
            _detector = new NetworkChangeDetector(sessionId, tenantId, post, logger);
        }

        public void Start()
        {
            try { _detector.Start(); }
            catch (Exception ex) { _logger.Warning($"NetworkChangeHost: Start failed: {ex.Message}"); }
        }

        public void Stop()
        {
            try { _detector.Stop(); }
            catch (Exception ex) { _logger.Warning($"NetworkChangeHost: Stop failed: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _detector.Dispose(); } catch { }
        }
    }
}
