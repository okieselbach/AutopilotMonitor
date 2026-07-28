using System;
using System.Diagnostics;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Fallback WiFi reader via <c>netsh wlan show interfaces</c> output parsing, used ONLY
    /// when <see cref="WifiInfoReader"/> (native WLAN API) yields nothing on a WiFi-connected
    /// device. Field evidence 2026-07-28: the WLAN API can return nothing in the OOBE/SYSTEM
    /// context on builds where inbox netsh still reports the connection — until that is fully
    /// root-caused (suspect: Wi-Fi location gating, 24H2+), netsh stays as the safety net.
    /// Caveats of this path (the reason it is only a fallback): labels are localized — on
    /// non-English images "Radio type"/"Channel" (sometimes "Signal") do not match — and it
    /// spawns a child process.
    /// </summary>
    internal static class NetshWifiFallback
    {
        private const int NetshTimeoutMs = 5000;

        /// <summary>
        /// Parses <c>netsh wlan show interfaces</c>. Returns a <see cref="WifiConnectionInfo"/>
        /// with the fields that could be parsed, or null when nothing was found (no WLAN, netsh
        /// failure, timeout, or fully localized labels). Never throws.
        /// </summary>
        internal static WifiConnectionInfo TryRead()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.Netsh,
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                string output;
                using (var process = Process.Start(psi))
                {
                    output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(NetshTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                }

                if (string.IsNullOrEmpty(output))
                    return null;

                string ssid = null, radioType = null;
                int? signal = null, channel = null;

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    var colonIndex = trimmed.IndexOf(':');
                    if (colonIndex < 0) continue;

                    var key = trimmed.Substring(0, colonIndex).Trim();
                    var value = trimmed.Substring(colonIndex + 1).Trim();

                    if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
                        ssid = value;
                    else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value.TrimEnd('%'), out var sig))
                            signal = sig;
                    }
                    else if (key.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
                        radioType = value;
                    else if (key.Equals("Channel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(value, out var ch))
                            channel = ch;
                    }
                }

                if (ssid == null && signal == null && radioType == null && channel == null)
                    return null;

                return new WifiConnectionInfo
                {
                    Ssid = ssid,
                    SignalPercent = signal,
                    RadioType = radioType,
                    Channel = channel,
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
