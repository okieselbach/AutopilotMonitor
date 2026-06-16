using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Win32 power-availability interop (kernel32 <c>SetThreadExecutionState</c>).
    /// <para>
    /// Used to keep the device awake during the User-ESP (AccountSetup) phase so that app
    /// installs and account provisioning are not stalled by idle standby/sleep. The execution
    /// state only resets the system / display idle timers — it has <b>no</b> effect on explicit
    /// reboots or shutdowns, so ESP / Windows-Update reboots proceed normally.
    /// </para>
    /// <para>
    /// <b>Thread-affinity contract</b>: a continuous requirement set with
    /// <see cref="EXECUTION_STATE.ES_CONTINUOUS"/> is bound to the lifetime of the calling
    /// thread — it is automatically released when that thread exits (and, like all execution
    /// states, when the process exits or the machine reboots). Callers that need to hold the
    /// requirement therefore keep a dedicated long-lived thread alive (see
    /// <c>KeepAwakeController</c>); there is no way to leak a permanent "never sleep" state.
    /// </para>
    /// </summary>
    internal static class PowerNativeMethods
    {
        [Flags]
        internal enum EXECUTION_STATE : uint
        {
            /// <summary>Keep the requirement in effect until the next call that changes it.</summary>
            ES_CONTINUOUS = 0x80000000,

            /// <summary>Reset the system idle timer — prevents the system from sleeping.</summary>
            ES_SYSTEM_REQUIRED = 0x00000001,

            /// <summary>Reset the display idle timer — keeps the screen on.</summary>
            ES_DISPLAY_REQUIRED = 0x00000002,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        /// <summary>
        /// Engage a continuous system + display keep-awake on the <b>calling thread</b>. The hold
        /// stays in effect until <see cref="AllowSleep"/> is called on the same thread or the
        /// thread exits. Returns <c>false</c> if the OS rejected the request (the previous state
        /// is reported by Windows as <c>0</c>).
        /// </summary>
        public static bool PreventSleep()
        {
            var previous = SetThreadExecutionState(
                EXECUTION_STATE.ES_CONTINUOUS
                | EXECUTION_STATE.ES_SYSTEM_REQUIRED
                | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
            return previous != 0;
        }

        /// <summary>
        /// Clear the continuous keep-awake on the <b>calling thread</b>, restoring normal power
        /// policy. Must run on the same thread that called <see cref="PreventSleep"/>.
        /// </summary>
        public static bool AllowSleep()
        {
            var previous = SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            return previous != 0;
        }
    }
}
