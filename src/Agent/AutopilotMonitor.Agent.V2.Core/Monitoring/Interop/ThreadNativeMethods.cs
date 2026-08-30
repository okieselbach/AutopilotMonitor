using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Win32 P/Invoke for per-thread CPU accounting. Used by ImeLogTracker to charge the
    /// per-line match budget with cycles the matching thread actually executed instead of
    /// wall-clock time: on a CPU-saturated guest the wall clock keeps running while the
    /// thread is descheduled, so a wall-based budget measures the scheduler, not the regex
    /// (sessions 946ccbd6 / b9f1d134, 2026-08-30).
    /// </summary>
    internal static class ThreadNativeMethods
    {
        // Pseudo handle (-2): always refers to the calling thread, never needs closing.
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        // Cycles the thread has executed, accumulated by the kernel at context switches.
        // Excludes time the thread spent descheduled by the GUEST scheduler; hypervisor-level
        // vCPU preemption is invisible to the guest kernel and still gets charged — the
        // measurement is a strict improvement over wall clock, not perfect steal accounting.
        [DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr threadHandle, out ulong cycleTime);

        /// <summary>
        /// Cycle count of the calling thread, or 0 if the call fails. A 0 makes the caller's
        /// delta accounting charge nothing for the enclosed work — the safe direction (no
        /// spurious skips); persistent unavailability is ruled out by <see cref="Probe"/>.
        /// </summary>
        public static long GetCurrentThreadCycles()
        {
            try
            {
                ulong cycles;
                return QueryThreadCycleTime(GetCurrentThread(), out cycles) ? unchecked((long)cycles) : 0L;
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        /// <summary>One-shot availability check (missing export, denied call).</summary>
        public static bool Probe()
        {
            try
            {
                ulong cycles;
                return QueryThreadCycleTime(GetCurrentThread(), out cycles);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
