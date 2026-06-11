#nullable enable
namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Host-owned shutdown actions invoked by the <see cref="EnrollmentTerminationHandler"/>
    /// (ARCH-F2 grouping — replaces the <c>signalShutdown</c> / <c>triggerReboot</c> /
    /// <c>writeCleanExitMarker</c> / <c>tryClaimShutdownEvent</c> constructor parameters).
    /// Production implementation lives in the runtime host project
    /// (<c>RuntimeShutdownGate</c>) because every member touches host-process machinery
    /// (the shutdown <c>ManualResetEventSlim</c>, <c>Program.WriteCleanExitMarker</c>, the
    /// cross-path <c>agent_shutting_down</c> idempotency gate).
    /// </summary>
    public interface IShutdownGate
    {
        /// <summary>
        /// Releases the host's shutdown wait — the handler's very last act (invoked in the
        /// <c>finally</c> of <c>Handle</c>, best-effort, never allowed to prevent shutdown).
        /// </summary>
        void SignalShutdown();

        /// <summary>
        /// V1 parity — standalone-reboot flow (<c>RebootOnComplete</c> without self-destruct):
        /// queue <c>shutdown.exe /r /t &lt;delaySeconds&gt;</c> so the user gets a visible
        /// countdown. Tests record the call instead of rebooting the build machine.
        /// </summary>
        void TriggerReboot(int delaySeconds);

        /// <summary>
        /// Option 2 (WG Part 1 graceful-exit hardening, 2026-04-30): writes the
        /// <c>clean-exit.marker</c> file directly, before <see cref="SignalShutdown"/> hands
        /// control to the main thread. This wins the race against an admin-triggered
        /// reseal-reboot — without this hook the marker is only written by the
        /// AppDomain.ProcessExit handler, which Windows can pre-empt.
        /// </summary>
        void WriteCleanExitMarker();

        /// <summary>
        /// Shutdown-gap closure (2026-05-15): cross-path idempotency gate shared with the
        /// runtime host's gap emitters (Ctrl+C, ProcessExit, unhandled exception,
        /// runtime-host finally). The handler calls this before emitting
        /// <c>agent_shutting_down</c> so a Terminated event that races a Ctrl+C cannot
        /// produce two events on the wire. Returns <c>false</c> when another path already
        /// claimed the slot (the handler then skips its emit). Implementations without a
        /// shared gate return <c>true</c> (always-emit).
        /// </summary>
        bool TryClaimShutdownEvent();
    }
}
