using System;
using System.Collections;
using System.Reflection;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Reads the SSID of the current WLAN connection via WinRT
    /// <c>Windows.Networking.Connectivity.WlanConnectionProfileDetails.GetConnectedSsid</c>.
    /// <para>
    /// <b>Why this exists.</b> Windows 11 24H2 put the native WLAN API behind precise-location
    /// consent: <c>WlanQueryInterface</c> with <c>wlan_intf_opcode_current_connection</c> — the
    /// exact opcode <see cref="WifiInfoReader"/> needs — returns <c>ERROR_ACCESS_DENIED</c>
    /// without it, and <c>netsh wlan</c> is gated by the same rule, so
    /// <see cref="NetshWifiFallback"/> fails in lockstep. The consent prompt only appears for
    /// processes in the user's context outside <c>C:\Windows\System32</c>, so the agent — SYSTEM,
    /// during OOBE, before the user ever sees the privacy page — is denied silently. Microsoft
    /// names <c>GetConnectedSsid</c> as the replacement for exactly this case; it is not
    /// location-gated.
    /// </para>
    /// <para>
    /// Resolved through the CLR's built-in WinRT projection — no NuGet package, no winmd
    /// reference — the same mechanism <see cref="OobeStateReader"/> uses (validated from the
    /// SYSTEM service context). Available since Windows 10 1507. SSID only: signal quality,
    /// PHY type and channel have no ungated source, so they stay null on this path.
    /// </para>
    /// </summary>
    internal static class WinRtWifiSsidReader
    {
        // Resolved once per process. Benign race: concurrent first calls resolve the same
        // members and the assignments are idempotent — no lock needed.
        private static MethodInfo _getInternetConnectionProfile;
        private static MethodInfo _getConnectionProfiles;
        private static bool _initTried;

        /// <summary>
        /// Returns the SSID of the connected WLAN, or null when the device is not on WiFi, the
        /// WinRT contract is unavailable, or any part of the read fails. Never throws.
        /// <paramref name="diagnostics"/> carries the reason when the result is null.
        /// </summary>
        internal static string TryGetConnectedSsid(out string diagnostics)
        {
            try
            {
                EnsureInitialized();

                if (_getInternetConnectionProfile == null)
                {
                    diagnostics = "WinRT NetworkInformation unavailable";
                    return null;
                }

                // The internet-bearing profile is the one the device is actually enrolling over.
                var ssid = ReadSsid(_getInternetConnectionProfile.Invoke(null, null));
                if (ssid != null)
                {
                    diagnostics = null;
                    return ssid;
                }

                // No internet profile (captive portal, limited connectivity) or it is not WLAN —
                // fall back to the first connected profile that carries WLAN details.
                ssid = ScanConnectionProfiles();
                diagnostics = ssid == null ? "no connected WLAN profile" : null;
                return ssid;
            }
            catch (Exception ex)
            {
                diagnostics = $"WinRT exception {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static void EnsureInitialized()
        {
            if (_initTried) return;

            var type = Type.GetType(
                "Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime",
                throwOnError: false);

            _getInternetConnectionProfile = type?.GetMethod(
                "GetInternetConnectionProfile", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _getConnectionProfiles = type?.GetMethod(
                "GetConnectionProfiles", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

            _initTried = true;
        }

        /// <summary>
        /// Walks every known connection profile and returns the SSID of the first one that is
        /// both connected and WLAN. Returns null when none qualifies.
        /// </summary>
        private static string ScanConnectionProfiles()
        {
            if (_getConnectionProfiles == null)
                return null;

            if (!(_getConnectionProfiles.Invoke(null, null) is IEnumerable profiles))
                return null;

            foreach (var profile in profiles)
            {
                if (!IsConnected(profile))
                    continue;

                var ssid = ReadSsid(profile);
                if (ssid != null)
                    return ssid;
            }

            return null;
        }

        /// <summary>
        /// <c>ConnectionProfile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None</c>.
        /// A profile the device is not currently on would otherwise leak a stale SSID.
        /// Unreadable level counts as not connected.
        /// </summary>
        private static bool IsConnected(object profile)
        {
            var level = profile?.GetType()
                .GetMethod("GetNetworkConnectivityLevel", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?.Invoke(profile, null);

            return level != null && Convert.ToInt32(level) != 0; // NetworkConnectivityLevel.None
        }

        /// <summary>
        /// <c>ConnectionProfile.WlanConnectionProfileDetails.GetConnectedSsid()</c>, or null when
        /// the profile is not a WLAN profile or the SSID is empty (hidden network).
        /// </summary>
        private static string ReadSsid(object profile)
        {
            var details = profile?.GetType()
                .GetProperty("WlanConnectionProfileDetails", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(profile);

            var ssid = details?.GetType()
                .GetMethod("GetConnectedSsid", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?.Invoke(details, null) as string;

            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
    }
}
