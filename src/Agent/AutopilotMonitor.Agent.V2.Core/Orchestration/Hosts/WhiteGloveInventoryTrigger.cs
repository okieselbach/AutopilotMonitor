#nullable enable
using System;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Subscribes to <see cref="EspAndHelloHost.WhiteGloveCompleted"/> and triggers a single
    /// inventory snapshot tagged with <c>whiteGlovePart=1</c> while the OOBE "Continue / Reseal"
    /// dialog is shown — i.e. BEFORE the admin clicks Reseal and Sysprep reboots the device.
    /// This is the only window in which the agent can stage a Part-1 software-inventory event
    /// for downstream vulnerability correlation; once Sysprep starts the agent is killed
    /// (<c>previousExitType=reboot_kill</c> on the next start) and any pending Part-1 work is lost
    /// from the live spool (it would only replay on the Part-2 agent start, by which point the
    /// inventory snapshot has missed its WhiteGlove-Part-1 phase tag).
    /// <para>
    /// <b>Lifecycle:</b> Constructor subscribes; <see cref="Dispose"/> unsubscribes. The trigger
    /// is single-fire — a defensive <see cref="Interlocked.Exchange(ref int, int)"/> guards
    /// against duplicate <c>WhiteGloveCompleted</c> emissions (the V2 tracker emits at most
    /// once today, but the guard makes the contract explicit).
    /// </para>
    /// <para>
    /// <b>Action injection:</b> The trigger calls a caller-supplied <see cref="Action"/> rather
    /// than holding a direct reference to <c>AgentAnalyzerManager</c>. This keeps the trigger
    /// trivially testable (no concrete-class mock needed) and avoids a Core→Runtime coupling.
    /// In production wiring the action is <c>() =&gt; analyzerManager.RunWhiteGlovePart1InventorySnapshot()</c>.
    /// </para>
    /// <para>
    /// <b>Routing separation:</b> This trigger is intentionally orthogonal to the
    /// <see cref="SignalAdapters.EspAndHelloTrackerAdapter"/> that maps the same WG-success
    /// event into a <c>WhiteGloveShellCoreSuccess</c> decision signal. Both subscribers see
    /// the event independently — single-responsibility, easy to remove.
    /// </para>
    /// </summary>
    internal sealed class WhiteGloveInventoryTrigger : IDisposable
    {
        private readonly IWhiteGloveCompletedSource _host;
        private readonly Action _onTrigger;
        private readonly AgentLogger _logger;

        private int _fired;     // 0 = not yet fired, 1 = fired
        private int _disposed;  // 0 = live, 1 = disposed

        public WhiteGloveInventoryTrigger(
            IWhiteGloveCompletedSource host,
            Action onTrigger,
            AgentLogger logger)
        {
            _host       = host       ?? throw new ArgumentNullException(nameof(host));
            _onTrigger  = onTrigger  ?? throw new ArgumentNullException(nameof(onTrigger));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));

            _host.WhiteGloveCompleted += OnWhiteGloveCompleted;
        }

        private void OnWhiteGloveCompleted(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _fired, 1) == 1)
            {
                _logger.Debug("WhiteGloveInventoryTrigger: duplicate WhiteGloveCompleted ignored (already fired)");
                return;
            }

            _logger.Info("WhiteGloveInventoryTrigger: WhiteGloveCompleted observed — running Part-1 inventory snapshot");

            try
            {
                _onTrigger();
            }
            catch (Exception ex)
            {
                // Trigger action failures must not propagate into the tracker's event loop.
                // Inventory missing is a degraded outcome but the agent must keep running.
                _logger.Error("WhiteGloveInventoryTrigger: trigger action threw", ex);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _host.WhiteGloveCompleted -= OnWhiteGloveCompleted; } catch { }
        }
    }
}
