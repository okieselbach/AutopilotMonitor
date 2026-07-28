using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Native WLAN API interop (<c>wlanapi.dll</c>) used to read the current WiFi connection
    /// (SSID, signal quality, PHY type, channel) without spawning <c>netsh</c>.
    /// <para>
    /// The WLAN API is language-neutral — unlike parsing <c>netsh wlan show interfaces</c>
    /// output, whose labels follow the OS UI language and silently drop fields on
    /// non-English images. On machines without the WLAN AutoConfig service (VMs, servers,
    /// wired-only devices) <c>WlanOpenHandle</c>/<c>WlanEnumInterfaces</c> fail with a
    /// non-zero error code; callers treat that as "no WiFi" (see <see cref="WifiInfoReader"/>).
    /// </para>
    /// </summary>
    internal static class WlanNativeMethods
    {
        internal const uint ERROR_SUCCESS = 0;

        /// <summary>Client version 2 = Vista and later (the only relevant range).</summary>
        internal const uint WLAN_CLIENT_VERSION = 2;

        internal enum WLAN_INTF_OPCODE : uint
        {
            wlan_intf_opcode_current_connection = 7,
            wlan_intf_opcode_channel_number = 8,
        }

        internal enum WLAN_INTERFACE_STATE
        {
            wlan_interface_state_not_ready = 0,
            wlan_interface_state_connected = 1,
            wlan_interface_state_ad_hoc_network_formed = 2,
            wlan_interface_state_disconnecting = 3,
            wlan_interface_state_disconnected = 4,
            wlan_interface_state_associating = 5,
            wlan_interface_state_discovering = 6,
            wlan_interface_state_authenticating = 7,
        }

        internal enum DOT11_PHY_TYPE : uint
        {
            dot11_phy_type_unknown = 0,
            dot11_phy_type_fhss = 1,
            dot11_phy_type_dsss = 2,
            dot11_phy_type_irbaseband = 3,
            dot11_phy_type_ofdm = 4,    // 802.11a
            dot11_phy_type_hrdsss = 5,  // 802.11b
            dot11_phy_type_erp = 6,     // 802.11g
            dot11_phy_type_ht = 7,      // 802.11n
            dot11_phy_type_vht = 8,     // 802.11ac
            dot11_phy_type_dmg = 9,     // 802.11ad
            dot11_phy_type_he = 10,     // 802.11ax
            dot11_phy_type_eht = 11,    // 802.11be
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public int dot11BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public byte[] dot11Bssid;
            public DOT11_PHY_TYPE dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality; // 0..100, same semantics as netsh "Signal"
            public uint ulRxRate;
            public uint ulTxRate;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bOneXEnabled;
            public int dot11AuthAlgorithm;
            public int dot11CipherAlgorithm;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public int wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanCloseHandle(
            IntPtr hClientHandle,
            IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        internal static extern uint WlanQueryInterface(
            IntPtr hClientHandle,
            [In] ref Guid pInterfaceGuid,
            WLAN_INTF_OPCODE OpCode,
            IntPtr pReserved,
            out uint pdwDataSize,
            out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")]
        internal static extern void WlanFreeMemory(IntPtr pMemory);
    }
}
