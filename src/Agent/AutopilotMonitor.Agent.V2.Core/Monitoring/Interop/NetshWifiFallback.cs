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
    /// <para>
    /// netsh labels follow the OS UI language, so the parser matches the label variants of
    /// the languages commonly seen in enrollments (EN/DE/FR/ES/PT/IT/NL/PL/CS/TR/RU/JA/
    /// ZH-CN/ZH-TW/KO + the Scandinavian ones that reuse EN/DE spellings). Unknown-language
    /// labels fall through harmlessly — the affected field is simply absent, same as before.
    /// "SSID" itself is untranslated in all known locales.
    /// </para>
    /// </summary>
    internal static class NetshWifiFallback
    {
        private const int NetshTimeoutMs = 5000;

        // Label variants per field, by OS UI language. Matched case-insensitively against the
        // full text left of the colon, so acronym-suffixed keys ("AP BSSID") never collide.
        private static readonly string[] SignalKeys =
        {
            "Signal",   // en, de, fr, sv, da, nb
            "Señal",    // es
            "Sinal",    // pt
            "Segnale",  // it
            "Signaal",  // nl
            "Sygnał",   // pl
            "Signál",   // cs
            "Sinyal",   // tr
            "Сигнал",   // ru
            "シグナル",  // ja
            "信号",      // zh-CN
            "訊號",      // zh-TW
            "신호",      // ko
        };

        private static readonly string[] RadioTypeKeys =
        {
            "Radio type",     // en
            "Funktyp",        // de
            "Type de radio",  // fr
            "Tipo de radio",  // es
            "Tipo de rádio",  // pt
            "Tipo di radio",  // it
            "Radiotype",      // nl
            "Typ radia",      // pl
            "Radyo türü",     // tr
            "Тип радио",      // ru
            "無線の種類",      // ja
            "无线电类型",      // zh-CN
            "無線電類型",      // zh-TW
            "라디오 종류",     // ko
            "무선 종류",       // ko (variant)
        };

        private static readonly string[] ChannelKeys =
        {
            "Channel",  // en
            "Kanal",    // de, sv, da, nb, tr
            "Canal",    // fr, es, pt
            "Canale",   // it
            "Kanaal",   // nl
            "Kanał",    // pl
            "Kanál",    // cs
            "Канал",    // ru
            "チャネル",  // ja
            "信道",      // zh-CN
            "通道",      // zh-TW / zh-CN (variant)
            "채널",      // ko
        };

        /// <summary>
        /// Runs <c>netsh wlan show interfaces</c> and parses its output. Returns a
        /// <see cref="WifiConnectionInfo"/> with the fields that could be parsed, or null when
        /// nothing was found (no WLAN, netsh failure, timeout, or unrecognized localized
        /// labels). Never throws.
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

                return Parse(output);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pure parser over the netsh output text. Internal for direct unit-testing via
        /// InternalsVisibleTo (TryRead itself spawns a process and stays untested).
        /// </summary>
        internal static WifiConnectionInfo Parse(string output)
        {
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
                {
                    ssid = value;
                }
                else if (MatchesAny(key, SignalKeys))
                {
                    // Value formats seen: "94%", "94% " (trailing space), "94 %".
                    if (int.TryParse(value.TrimEnd('%', ' '), out var sig))
                        signal = sig;
                }
                else if (MatchesAny(key, RadioTypeKeys))
                {
                    radioType = value;
                }
                else if (MatchesAny(key, ChannelKeys))
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

        private static bool MatchesAny(string key, string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (key.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
