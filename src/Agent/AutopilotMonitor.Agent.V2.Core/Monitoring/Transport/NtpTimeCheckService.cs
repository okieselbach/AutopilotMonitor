using System;
using System.Net;
using System.Net.Sockets;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    public class NtpCheckResult
    {
        public bool Success { get; set; }
        public double OffsetSeconds { get; set; }
        public string NtpServer { get; set; }
        public string Error { get; set; }
        public DateTime? NtpTime { get; set; }
        public DateTime? LocalTime { get; set; }
    }

    public static class NtpTimeCheckService
    {
        private static readonly int ReceiveTimeoutMs = 3000;

        /// <summary>
        /// Queries an NTP server and returns the time offset between local clock and NTP time.
        /// Based on NTP v3 client mode (LI=0, VN=3, Mode=3).
        /// </summary>
        public static NtpCheckResult CheckTime(string ntpServer, AgentLogger logger)
        {
            var result = new NtpCheckResult { NtpServer = ntpServer };

            try
            {
                logger.Debug($"NTP time check: querying {ntpServer}");

                // NTP request packet: 48 bytes, first byte = 0x1B (LI=0, VN=3, Mode=3)
                var ntpData = new byte[48];
                ntpData[0] = 0x1B;

                var addresses = Dns.GetHostEntry(ntpServer).AddressList;
                if (addresses.Length == 0)
                {
                    result.Error = $"DNS resolution returned no addresses for {ntpServer}";
                    logger.Warning($"NTP time check failed: {result.Error}");
                    return result;
                }

                var ipEndPoint = new IPEndPoint(addresses[0], 123);
                using (var socket = new Socket(addresses[0].AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.ReceiveTimeout = ReceiveTimeoutMs;
                    socket.SendTimeout = ReceiveTimeoutMs;
                    socket.Connect(ipEndPoint);
                    socket.Send(ntpData);
                    var received = socket.Receive(ntpData);

                    // Validate NTP response: must be at least 48 bytes
                    if (received < 48)
                    {
                        result.Error = $"Invalid NTP response: received {received} bytes (expected 48)";
                        logger.Warning($"NTP time check failed: {result.Error}");
                        return result;
                    }

                    // Check Leap Indicator (bits 6-7 of first byte): value 3 means unsynchronized
                    var leapIndicator = (ntpData[0] >> 6) & 3;
                    if (leapIndicator == 3)
                    {
                        result.Error = "NTP server is unsynchronized (Leap Indicator = 3)";
                        logger.Warning($"NTP time check failed: {result.Error}");
                        return result;
                    }
                }

                // Parse transmit timestamp from bytes 40-47 (NTP epoch: 1900-01-01)
                ulong intPart = (ulong)ntpData[40] << 24
                              | (ulong)ntpData[41] << 16
                              | (ulong)ntpData[42] << 8
                              | (ulong)ntpData[43];

                ulong fractPart = (ulong)ntpData[44] << 24
                                | (ulong)ntpData[45] << 16
                                | (ulong)ntpData[46] << 8
                                | (ulong)ntpData[47];

                var milliseconds = (intPart * 1000) + ((fractPart * 1000) / 0x100000000L);
                var ntpTime = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);
                var localTime = DateTime.UtcNow;
                var offset = ntpTime.Subtract(localTime);

                result.Success = true;
                result.NtpTime = ntpTime;
                result.LocalTime = localTime;
                result.OffsetSeconds = offset.TotalSeconds;

                logger.Info($"NTP time check: offset={offset.TotalSeconds:F2}s (NTP={ntpTime:o}, Local={localTime:o})");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                logger.Warning($"NTP time check failed for {ntpServer}: {ex.Message}");
                return result;
            }
        }
    }
}
