#nullable enable
using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Shared thresholds + computation for the "low observation coverage" assessment used by both
    /// the termination-time <c>agent_late_start</c> event (<see cref="EnrollmentTerminationHandler"/>)
    /// and the on-disk final-status.json (<see cref="FinalStatusBuilder"/>). Centralised so the
    /// two surfaces can never drift on what counts as a late start.
    /// <para>
    /// A session has low observation coverage when the agent started a long time after the device
    /// booted AND lived only briefly before terminating — i.e. it arrived after the enrollment had
    /// already decided its outcome, so its diagnosis is a post-mortem of the end-state rather than a
    /// live observation of the failure window. The canonical case: the Autopilot-Monitor bootstrap
    /// platform script is queued behind a hung customer script and only runs near the 30-min ESP
    /// timeout, so the agent starts ~34 min after boot and lives ~30 s.
    /// </para>
    /// </summary>
    internal static class ObservationCoverage
    {
        /// <summary>Boot-to-agent-start latency at/above which the start counts as "late".</summary>
        internal static readonly TimeSpan LateStartBootThreshold = TimeSpan.FromMinutes(10);

        /// <summary>Agent uptime at/below which coverage counts as "low" (barely observed).</summary>
        internal static readonly TimeSpan LowCoverageUptime = TimeSpan.FromMinutes(5);

        // net48 has no Environment.TickCount64 (Environment.TickCount is a 32-bit int that wraps at
        // ~24.9 days). GetTickCount64 returns 64-bit milliseconds since boot with no wrap, available
        // on every supported Windows. Monotonic → immune to wall-clock jumps / NTP corrections.
        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        /// <summary>
        /// Device boot time derived from the monotonic uptime counter (immune to wall-clock jumps /
        /// NTP corrections during enrollment, unlike a stored boot timestamp). Computed at call time.
        /// </summary>
        internal static DateTime DeviceBootUtc() =>
            DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());

        /// <summary>
        /// Evaluates the coverage gate. <paramref name="bootToStartSeconds"/> and
        /// <paramref name="uptimeSeconds"/> are always set (clamped at 0); the return value is true
        /// only when both thresholds are met.
        /// </summary>
        internal static bool IsLowObservationCoverage(
            DateTime agentStartUtc,
            DateTime terminatedUtc,
            DateTime deviceBootUtc,
            out double bootToStartSeconds,
            out double uptimeSeconds)
        {
            var bootToStart = agentStartUtc - deviceBootUtc;
            var uptime = terminatedUtc - agentStartUtc;
            bootToStartSeconds = Math.Max(0, bootToStart.TotalSeconds);
            uptimeSeconds = Math.Max(0, uptime.TotalSeconds);
            return bootToStart >= LateStartBootThreshold && uptime <= LowCoverageUptime;
        }
    }
}
