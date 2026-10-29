#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Subscribes to <see cref="IDeviceSetupCompletedSource.DeviceSetupProvisioningComplete"/> and
    /// triggers a single AutoLogon scan when the ESP device phase completes — the first point at
    /// which a device-targeted provisioning script / app could have written the Winlogon AutoLogon
    /// keys. The eventual final shutdown re-scans (via <c>AgentAnalyzerManager.RunShutdown</c>) to
    /// capture any AutoLogon written during the user phase.
    /// <para>
    /// <b>Lifecycle:</b> Constructor subscribes; <see cref="Dispose"/> unsubscribes. Single-fire —
    /// a defensive <see cref="Interlocked.Exchange(ref int, int)"/> guards against duplicate
    /// <c>DeviceSetupProvisioningComplete</c> emissions (the tracker fires at most once today, but
    /// the guard makes the contract explicit).
    /// </para>
    /// <para>
    /// <b>Action injection:</b> calls a caller-supplied <see cref="Action"/> rather than holding a
    /// direct reference to <c>AgentAnalyzerManager</c> — keeps the trigger trivially testable and
    /// avoids a Core→Runtime coupling. Production wires
    /// <c>() =&gt; analyzerManager.RunDeviceSetupCompleteAutoLogonCheck()</c>. Mirrors
    /// <see cref="WhiteGloveInventoryTrigger"/>.
    /// </para>
    /// </summary>
    internal sealed class AutoLogonDeviceSetupTrigger : IDisposable
    {
        private readonly IDeviceSetupCompletedSource _host;
        private readonly Action _onTrigger;
        private readonly AgentLogger _logger;

        private int _fired;     // 0 = not yet fired, 1 = fired
        private int _disposed;  // 0 = live, 1 = disposed

        public AutoLogonDeviceSetupTrigger(
            IDeviceSetupCompletedSource host,
            Action onTrigger,
            AgentLogger logger)
        {
            _host       = host       ?? throw new ArgumentNullException(nameof(host));
            _onTrigger  = onTrigger  ?? throw new ArgumentNullException(nameof(onTrigger));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));

            _host.DeviceSetupProvisioningComplete += OnDeviceSetupProvisioningComplete;
        }

        private void OnDeviceSetupProvisioningComplete(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 1)
            {
                _logger.Debug("AutoLogonDeviceSetupTrigger: duplicate DeviceSetupProvisioningComplete ignored (already fired)");
                return;
            }

            _logger.Info("AutoLogonDeviceSetupTrigger: DeviceSetupProvisioningComplete observed — running AutoLogon scan");

            try
            {
                _onTrigger();
            }
            catch (Exception ex)
            {
                // Trigger action failures must not propagate into the tracker's event loop.
                _logger.Error("AutoLogonDeviceSetupTrigger: trigger action threw", ex);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _host.DeviceSetupProvisioningComplete -= OnDeviceSetupProvisioningComplete; } catch { }
        }
    }
}
