using System;
using System.Diagnostics;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Termination;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Production <see cref="IShutdownGate"/> for <see cref="AgentRuntimeHost"/> (ARCH-F2).
    /// Wraps the host-process shutdown machinery that only exists as locals inside
    /// <c>AgentRuntimeHost.Run</c>: the shutdown <c>ManualResetEventSlim</c>, the early
    /// <c>clean-exit.marker</c> write (<c>Program.WriteCleanExitMarker</c>), and the
    /// cross-path <c>agent_shutting_down</c> idempotency gate shared with the Ctrl+C /
    /// ProcessExit / unhandled-exception gap emitters.
    /// </summary>
    internal sealed class RuntimeShutdownGate : IShutdownGate
    {
        private readonly Action _signalShutdown;
        private readonly Action _writeCleanExitMarker;
        private readonly Func<bool> _tryClaimShutdownEvent;

        public RuntimeShutdownGate(
            Action signalShutdown,
            Action writeCleanExitMarker,
            Func<bool> tryClaimShutdownEvent)
        {
            _signalShutdown = signalShutdown ?? throw new ArgumentNullException(nameof(signalShutdown));
            _writeCleanExitMarker = writeCleanExitMarker ?? throw new ArgumentNullException(nameof(writeCleanExitMarker));
            _tryClaimShutdownEvent = tryClaimShutdownEvent ?? throw new ArgumentNullException(nameof(tryClaimShutdownEvent));
        }

        public void SignalShutdown() => _signalShutdown();

        public void WriteCleanExitMarker() => _writeCleanExitMarker();

        public bool TryClaimShutdownEvent() => _tryClaimShutdownEvent();

        /// <summary>
        /// V1 parity — queues <c>shutdown.exe /r /t &lt;delaySeconds&gt;</c> with a visible
        /// countdown message. (Formerly <c>EnrollmentTerminationHandler.DefaultTriggerReboot</c>.)
        /// </summary>
        public void TriggerReboot(int delaySeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
                Arguments = $"/r /t {delaySeconds} /c \"Autopilot enrollment completed - rebooting\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
    }
}
