using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// The single entry point every collector uses to read the current WiFi connection. Owns the
    /// tier order so the two callers (<c>DeviceInfoCollector</c>, <c>NetworkChangeDetector</c>)
    /// cannot drift apart:
    /// <list type="number">
    /// <item><see cref="WifiInfoReader"/> — native WLAN API. Full payload (SSID, signal, PHY,
    /// channel), but Windows 11 24H2+ requires precise-location consent for it.</item>
    /// <item><see cref="WinRtWifiSsidReader"/> — WinRT <c>GetConnectedSsid</c>. SSID only, but
    /// NOT location-gated; this is the tier that carries 24H2+ devices whose image has location
    /// services off (the common OOBE case — see that class for the mechanism).</item>
    /// <item><see cref="NetshWifiFallback"/> — process spawn, localized output parsing. Last
    /// resort: it hangs off the same location gate as tier 1, so it only helps when the native
    /// API failed for an unrelated reason.</item>
    /// </list>
    /// Never throws. Returns null when the device has no readable WiFi connection.
    /// </summary>
    internal static class WifiInfoProvider
    {
        /// <summary>
        /// <see cref="WifiConnectionInfo.DataLimitedReason"/> value for a payload cut down to the
        /// SSID by the Windows 11 24H2 location gate. Travels verbatim into the
        /// <c>wifi_signal_info</c> payload as <c>wifiDataLimitedReason</c> and is matched by the
        /// portal's WiFi card — a published contract, so do not rename it.
        /// </summary>
        internal const string LocationServicesOff = "location_services_off";

        /// <summary>
        /// True when the native tier reported <c>ERROR_ACCESS_DENIED</c> — "rc=5" but not
        /// "rc=50"/"rc=5023"; the diagnostics string ends the code with ';' or end-of-line.
        /// That is the 24H2 location gate, not a driver or hardware fault.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static bool IsLocationGateDenied(string nativeDiag)
            => !string.IsNullOrEmpty(nativeDiag) && Regex.IsMatch(nativeDiag, @"rc=5(?!\d)");

        /// <summary>
        /// Reads the current WiFi connection, walking the tiers until one yields something.
        /// <paramref name="preferredInterfaceId"/> is the adapter GUID from
        /// <c>NetworkInterface.Id</c> (tier 1 only). <paramref name="diagnostics"/> is null on
        /// success and otherwise explains what every tier reported — log it at Warning: this is
        /// the only field evidence for a missing <c>wifi_signal_info</c> event.
        /// </summary>
        internal static WifiConnectionInfo TryRead(Guid? preferredInterfaceId, out string diagnostics)
        {
            var native = WifiInfoReader.TryGetCurrentConnection(preferredInterfaceId, out var nativeDiag);
            if (native != null)
            {
                diagnostics = null;
                return native;
            }

            var ssid = WinRtWifiSsidReader.TryGetConnectedSsid(out var winRtDiag);
            if (ssid != null)
            {
                diagnostics = null;
                return new WifiConnectionInfo
                {
                    Ssid = ssid,
                    // Only claim the location gate when the native tier actually said so. Any
                    // other native failure (no WLAN service, no connected interface) leaves this
                    // null rather than blaming a setting we did not observe.
                    DataLimitedReason = IsLocationGateDenied(nativeDiag) ? LocationServicesOff : null,
                };
            }

            var netsh = NetshWifiFallback.TryRead();
            if (netsh != null)
            {
                diagnostics = null;
                return netsh;
            }

            diagnostics = BuildDiagnostics(nativeDiag, winRtDiag);
            return null;
        }

        /// <summary>
        /// Composes the all-tiers-failed message. A native <c>rc=5</c> (ERROR_ACCESS_DENIED) is
        /// spelled out: that is the 24H2 location gate, not a driver or hardware fault, and it
        /// takes the netsh tier down with it — without this hint the log reads like a mystery.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static string BuildDiagnostics(string nativeDiag, string winRtDiag)
        {
            var text = new StringBuilder("no WiFi info from any reader");
            text.Append(" — native: ").Append(string.IsNullOrEmpty(nativeDiag) ? "no connected WLAN interface" : nativeDiag);
            text.Append("; WinRT: ").Append(string.IsNullOrEmpty(winRtDiag) ? "no SSID" : winRtDiag);
            text.Append("; netsh: no parsable output");

            if (IsLocationGateDenied(nativeDiag))
            {
                text.Append(". rc=5 is ERROR_ACCESS_DENIED — Windows 11 24H2+ gates the current-connection "
                          + "opcode (and netsh wlan) behind precise-location consent, which is off in this image; "
                          + "enable Location services to restore signal/PHY/channel.");
            }

            return text.ToString();
        }
    }
}
