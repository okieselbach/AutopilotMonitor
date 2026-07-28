using System;
using System.Runtime.InteropServices;
using System.Text;
using static AutopilotMonitor.Agent.V2.Core.Monitoring.Interop.WlanNativeMethods;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Current WiFi connection details as read from the native WLAN API.
    /// </summary>
    public sealed class WifiConnectionInfo
    {
        /// <summary>SSID of the connected network; null when the AP broadcasts an empty SSID.</summary>
        public string Ssid { get; internal set; }

        /// <summary>Signal quality 0–100 (same semantics as the netsh "Signal" percentage).</summary>
        public int SignalPercent { get; internal set; }

        /// <summary>Human-readable PHY generation ("802.11ax" …); null for exotic/unknown PHYs.</summary>
        public string RadioType { get; internal set; }

        /// <summary>Current 802.11 channel number; null when the channel query fails.</summary>
        public int? Channel { get; internal set; }
    }

    /// <summary>
    /// Fail-soft reader over <see cref="WlanNativeMethods"/>: returns the current WiFi
    /// connection or null — never throws, never blocks on a child process. Machines without
    /// the WLAN AutoConfig service (VMs, wired-only devices) simply return null.
    /// </summary>
    public static class WifiInfoReader
    {
        /// <summary>
        /// Reads the current WiFi connection. When <paramref name="preferredInterfaceId"/> is
        /// given (the adapter GUID from <c>NetworkInterface.Id</c>), that interface is queried
        /// first; otherwise — or when it is not connected — the first connected WLAN interface
        /// wins. Returns null when no interface has an active connection.
        /// </summary>
        public static WifiConnectionInfo TryGetCurrentConnection(Guid? preferredInterfaceId = null)
        {
            var clientHandle = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out clientHandle) != ERROR_SUCCESS)
                    return null;

                var interfaceList = IntPtr.Zero;
                try
                {
                    if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceList) != ERROR_SUCCESS)
                        return null;

                    var count = (uint)Marshal.ReadInt32(interfaceList);
                    var infoSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
                    // List header: DWORD dwNumberOfItems + DWORD dwIndex, then the info array.
                    var firstEntry = new IntPtr(interfaceList.ToInt64() + 8);

                    WifiConnectionInfo fallback = null;
                    for (var i = 0; i < count; i++)
                    {
                        var entryPtr = new IntPtr(firstEntry.ToInt64() + i * infoSize);
                        var info = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(entryPtr, typeof(WLAN_INTERFACE_INFO));

                        var connection = QueryCurrentConnection(clientHandle, info.InterfaceGuid);
                        if (connection == null)
                            continue;

                        if (preferredInterfaceId.HasValue && info.InterfaceGuid == preferredInterfaceId.Value)
                            return connection;

                        if (fallback == null)
                            fallback = connection;
                    }

                    return fallback;
                }
                finally
                {
                    if (interfaceList != IntPtr.Zero)
                        WlanFreeMemory(interfaceList);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (clientHandle != IntPtr.Zero)
                    WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }

        private static WifiConnectionInfo QueryCurrentConnection(IntPtr clientHandle, Guid interfaceGuid)
        {
            var dataPtr = IntPtr.Zero;
            try
            {
                if (WlanQueryInterface(clientHandle, ref interfaceGuid,
                        WLAN_INTF_OPCODE.wlan_intf_opcode_current_connection,
                        IntPtr.Zero, out _, out dataPtr, IntPtr.Zero) != ERROR_SUCCESS)
                    return null;

                var attributes = (WLAN_CONNECTION_ATTRIBUTES)Marshal.PtrToStructure(
                    dataPtr, typeof(WLAN_CONNECTION_ATTRIBUTES));

                if (attributes.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                    return null;

                var assoc = attributes.wlanAssociationAttributes;
                return new WifiConnectionInfo
                {
                    Ssid = DecodeSsid(assoc.dot11Ssid.ucSSID, assoc.dot11Ssid.uSSIDLength),
                    SignalPercent = (int)Math.Min(assoc.wlanSignalQuality, 100),
                    RadioType = PhyTypeToRadioType(assoc.dot11PhyType),
                    Channel = QueryChannel(clientHandle, interfaceGuid),
                };
            }
            finally
            {
                if (dataPtr != IntPtr.Zero)
                    WlanFreeMemory(dataPtr);
            }
        }

        private static int? QueryChannel(IntPtr clientHandle, Guid interfaceGuid)
        {
            var dataPtr = IntPtr.Zero;
            try
            {
                if (WlanQueryInterface(clientHandle, ref interfaceGuid,
                        WLAN_INTF_OPCODE.wlan_intf_opcode_channel_number,
                        IntPtr.Zero, out var dataSize, out dataPtr, IntPtr.Zero) != ERROR_SUCCESS)
                    return null;

                if (dataSize < sizeof(uint) || dataPtr == IntPtr.Zero)
                    return null;

                return Marshal.ReadInt32(dataPtr);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero)
                    WlanFreeMemory(dataPtr);
            }
        }

        /// <summary>
        /// Decodes a raw DOT11_SSID (UTF-8 by convention) into a string; null for empty SSIDs.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static string DecodeSsid(byte[] raw, uint length)
        {
            if (raw == null || length == 0)
                return null;

            var byteCount = (int)Math.Min(length, (uint)raw.Length);
            try
            {
                var ssid = Encoding.UTF8.GetString(raw, 0, byteCount);
                return string.IsNullOrEmpty(ssid) ? null : ssid;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps a DOT11 PHY type to the same strings netsh historically reported as
        /// "Radio type" — keeps event payloads consistent across the netsh→WLAN-API switch.
        /// Internal for direct unit-testing via InternalsVisibleTo.
        /// </summary>
        internal static string PhyTypeToRadioType(DOT11_PHY_TYPE phyType)
        {
            switch (phyType)
            {
                case DOT11_PHY_TYPE.dot11_phy_type_ofdm: return "802.11a";
                case DOT11_PHY_TYPE.dot11_phy_type_hrdsss: return "802.11b";
                case DOT11_PHY_TYPE.dot11_phy_type_erp: return "802.11g";
                case DOT11_PHY_TYPE.dot11_phy_type_ht: return "802.11n";
                case DOT11_PHY_TYPE.dot11_phy_type_vht: return "802.11ac";
                case DOT11_PHY_TYPE.dot11_phy_type_dmg: return "802.11ad";
                case DOT11_PHY_TYPE.dot11_phy_type_he: return "802.11ax";
                case DOT11_PHY_TYPE.dot11_phy_type_eht: return "802.11be";
                default: return null;
            }
        }
    }
}
