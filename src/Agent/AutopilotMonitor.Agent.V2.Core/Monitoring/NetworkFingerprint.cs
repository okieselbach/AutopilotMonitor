#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Persistence;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring
{
    /// <summary>
    /// Short local fingerprint of the network a start ran on: the first <see cref="Length"/> hex
    /// characters of SHA-256 over the active interface's connection type and default gateway, as
    /// <see cref="NetworkChangeDetector"/> captures them. Stored beside the failed-registration
    /// record and compared by the next successful registration; only that comparison (changed or
    /// not) leaves the device, never a fingerprint.
    /// </summary>
    internal static class NetworkFingerprint
    {
        internal const int Length = 16;

        /// <summary>Fingerprint of the current network; null when it is unknown. Never throws.</summary>
        public static string? TryCapture()
        {
            try
            {
                return Of(NetworkChangeDetector.CaptureInterfaceState());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Fingerprint of a captured state; null without an active interface, type or gateway.</summary>
        internal static string? Of(NetworkChangeDetector.NetworkStateSnapshot? state)
        {
            if (state == null || !state.HasNetwork
                || string.IsNullOrEmpty(state.ConnectionType) || string.IsNullOrEmpty(state.Gateway))
            {
                return null;
            }

            return StartupEventGate.HashString(state.ConnectionType + "|" + state.Gateway).Substring(0, Length);
        }

        /// <summary>True/false when both fingerprints are known, null when either is unknown.</summary>
        public static bool? Differs(string? before, string? after) =>
            before == null || after == null ? (bool?)null : !string.Equals(before, after, StringComparison.Ordinal);
    }
}
