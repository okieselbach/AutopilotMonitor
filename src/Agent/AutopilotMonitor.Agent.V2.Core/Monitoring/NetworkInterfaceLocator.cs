#nullable enable
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring
{
    /// <summary>
    /// Shared "which NIC is the device actually using" heuristic: Up, not Loopback/Tunnel,
    /// carries a non-0.0.0.0 default gateway. Fail-soft — any enumeration error yields null.
    /// </summary>
    internal static class NetworkInterfaceLocator
    {
        /// <summary>
        /// Finds the active network interface: Up, not Loopback/Tunnel, has a non-0.0.0.0 gateway.
        /// </summary>
        internal static NetworkInterface? FindActiveNetworkInterface()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.ToString() != "0.0.0.0")
                            return nic;
                    }
                }
            }
            catch
            {
                // Caller handles null
            }
            return null;
        }

        /// <summary>First IPv4 default gateway of the interface (0.0.0.0 excluded), or null.</summary>
        internal static IPAddress? GetIpv4Gateway(NetworkInterface nic)
        {
            try
            {
                foreach (var gw in nic.GetIPProperties().GatewayAddresses)
                {
                    var address = gw?.Address;
                    if (address != null
                        && address.AddressFamily == AddressFamily.InterNetwork
                        && address.ToString() != "0.0.0.0")
                    {
                        return address;
                    }
                }
            }
            catch
            {
                // Caller handles null
            }
            return null;
        }
    }
}
