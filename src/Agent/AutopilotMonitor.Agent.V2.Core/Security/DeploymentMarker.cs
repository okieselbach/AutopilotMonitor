#nullable enable
using System;
using System.Globalization;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// The install-time deployment marker: <c>HKLM\SOFTWARE\AutopilotMonitor\Deployed</c>, an
    /// ISO-8601 UTC instant written by <c>--install</c> right after the runtime was handed off
    /// (writer: <c>Program.InstallMode</c> in the runtime project). It is the bootstrap script's
    /// re-entry lock and — since 2026-09-04 — the one instant the agent knows in UTC that also
    /// appears in the IME logs: the Intune Management Extension logs the bootstrap script's
    /// result a few hundred milliseconds later, which anchors the timezone of the pre-agent log
    /// era (<c>ImeLogEraPreScan</c>). Read-only here; never written from Core.
    /// </summary>
    public static class DeploymentMarker
    {
        public const string RegistryKeyPath = @"SOFTWARE\AutopilotMonitor";
        public const string RegistryValueName = "Deployed";

        /// <summary>The marker's UTC instant, or <c>null</c> when absent or unparseable.</summary>
        public static DateTime? TryReadDeployedUtc()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath))
                {
                    var raw = key?.GetValue(RegistryValueName) as string;
                    return Parse(raw);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Pure parse of the marker text (round-trip "O" format); exposed for tests.</summary>
        public static DateTime? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return null;
            return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }
    }
}
