#nullable enable
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// ARP resolution via <c>iphlpapi!SendARP</c> — resolves the MAC address of an on-link IPv4
    /// host (in practice: the default gateway). IPv4 only by API design; the OS answers from its
    /// ARP cache or sends a real ARP request, so the target must be on the local subnet.
    /// </summary>
    internal static class ArpNativeMethods
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIP, uint srcIP, byte[] macAddress, ref uint macAddressLength);

        /// <summary>
        /// Fail-soft MAC resolution. Returns false (with a diagnostic in <paramref name="error"/>)
        /// for non-IPv4 addresses, SendARP failures, or unexpected reply lengths — never throws.
        /// </summary>
        internal static bool TryResolveMac(IPAddress? ipv4Address, out byte[] mac, out string? error)
        {
            mac = new byte[6];
            error = null;
            try
            {
                if (ipv4Address == null || ipv4Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    error = "not an IPv4 address";
                    return false;
                }

                // SendARP takes the IPv4 address as a uint in network byte order (first octet in
                // the lowest byte).
                var addressBytes = ipv4Address.GetAddressBytes();
                uint dest = ((uint)addressBytes[3] << 24)
                          | ((uint)addressBytes[2] << 16)
                          | ((uint)addressBytes[1] << 8)
                          | addressBytes[0];

                uint len = (uint)mac.Length;
                var err = SendARP(dest, 0, mac, ref len);
                if (err != 0)
                {
                    error = $"SendARP returned {err}";
                    return false;
                }
                if (len != 6)
                {
                    error = $"SendARP returned unexpected MAC length {len}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
