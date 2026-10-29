using System;
using System.Collections.Generic;
using System.Diagnostics;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    public class TimezoneSetResult
    {
        public bool Success { get; set; }
        public string IanaTimezone { get; set; }
        public string WindowsTimezoneId { get; set; }
        public string PreviousTimezone { get; set; }
        public string Error { get; set; }
    }

    public static class TimezoneService
    {
        /// <summary>
        /// Attempts to set the Windows timezone based on an IANA timezone identifier.
        /// Uses tzutil /s to apply the timezone (works from SYSTEM context).
        /// </summary>
        public static TimezoneSetResult TrySetTimezone(string ianaTimezone, AgentLogger logger)
        {
            var result = new TimezoneSetResult { IanaTimezone = ianaTimezone };

            try
            {
                // Map IANA timezone to Windows timezone ID
                if (!IanaToWindowsMap.TryGetValue(ianaTimezone, out var windowsId))
                {
                    result.Error = $"No Windows timezone mapping found for IANA timezone '{ianaTimezone}'";
                    logger.Warning($"Timezone auto-set: {result.Error}");
                    return result;
                }

                result.WindowsTimezoneId = windowsId;
                result.PreviousTimezone = TimeZoneInfo.Local.Id;

                // Skip if already set to the target timezone
                if (string.Equals(result.PreviousTimezone, windowsId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = true;
                    logger.Info($"Timezone auto-set: already set to {windowsId} (IANA: {ianaTimezone})");
                    return result;
                }

                // Execute tzutil /s to set the timezone
                logger.Debug($"Timezone auto-set: setting timezone to {windowsId} (IANA: {ianaTimezone}, previous: {result.PreviousTimezone})");

                var psi = new ProcessStartInfo
                {
                    FileName = SystemPaths.TzUtil,
                    Arguments = $"/s \"{windowsId}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    var stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(10000); // 10s timeout

                    if (process.ExitCode != 0)
                    {
                        result.Error = $"tzutil exited with code {process.ExitCode}: {stderr.Trim()}";
                        logger.Warning($"Timezone auto-set failed: {result.Error}");
                        return result;
                    }
                }

                result.Success = true;
                logger.Info($"Timezone auto-set: successfully set to {windowsId}");
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                logger.Warning($"Timezone auto-set failed: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// IANA timezone to Windows timezone ID mapping.
        /// Sourced from Unicode CLDR / IANA timezone database.
        /// Covers all major regions and practical timezones.
        /// </summary>
        private static readonly Dictionary<string, string> IanaToWindowsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // UTC
            { "Etc/UTC", "UTC" },
            { "Etc/GMT", "UTC" },
            { "UTC", "UTC" },

            // Europe
            { "Europe/London", "GMT Standard Time" },
            { "Europe/Dublin", "GMT Standard Time" },
            { "Europe/Lisbon", "GMT Standard Time" },
            { "Europe/Berlin", "W. Europe Standard Time" },
            { "Europe/Amsterdam", "W. Europe Standard Time" },
            { "Europe/Zurich", "W. Europe Standard Time" },
            { "Europe/Vienna", "W. Europe Standard Time" },
            { "Europe/Rome", "W. Europe Standard Time" },
            { "Europe/Stockholm", "W. Europe Standard Time" },
            { "Europe/Oslo", "W. Europe Standard Time" },
            { "Europe/Copenhagen", "Romance Standard Time" },
            { "Europe/Paris", "Romance Standard Time" },
            { "Europe/Brussels", "Romance Standard Time" },
            { "Europe/Madrid", "Romance Standard Time" },
            { "Europe/Warsaw", "Central European Standard Time" },
            { "Europe/Prague", "Central Europe Standard Time" },
            { "Europe/Budapest", "Central Europe Standard Time" },
            { "Europe/Belgrade", "Central Europe Standard Time" },
            { "Europe/Bratislava", "Central Europe Standard Time" },
            { "Europe/Ljubljana", "Central Europe Standard Time" },
            { "Europe/Zagreb", "Central Europe Standard Time" },
            { "Europe/Athens", "GTB Standard Time" },
            { "Europe/Bucharest", "GTB Standard Time" },
            { "Europe/Helsinki", "FLE Standard Time" },
            { "Europe/Kiev", "FLE Standard Time" },
            { "Europe/Kyiv", "FLE Standard Time" },
            { "Europe/Riga", "FLE Standard Time" },
            { "Europe/Sofia", "FLE Standard Time" },
            { "Europe/Tallinn", "FLE Standard Time" },
            { "Europe/Vilnius", "FLE Standard Time" },
            { "Europe/Istanbul", "Turkey Standard Time" },
            { "Europe/Moscow", "Russian Standard Time" },
            { "Europe/Minsk", "Belarus Standard Time" },
            { "Europe/Samara", "Russia Time Zone 3" },

            // Americas
            { "America/New_York", "Eastern Standard Time" },
            { "America/Detroit", "Eastern Standard Time" },
            { "America/Toronto", "Eastern Standard Time" },
            { "America/Chicago", "Central Standard Time" },
            { "America/Winnipeg", "Central Standard Time" },
            { "America/Denver", "Mountain Standard Time" },
            { "America/Edmonton", "Mountain Standard Time" },
            { "America/Phoenix", "US Mountain Standard Time" },
            { "America/Los_Angeles", "Pacific Standard Time" },
            { "America/Vancouver", "Pacific Standard Time" },
            { "America/Anchorage", "Alaskan Standard Time" },
            { "Pacific/Honolulu", "Hawaiian Standard Time" },
            { "America/Halifax", "Atlantic Standard Time" },
            { "America/St_Johns", "Newfoundland Standard Time" },
            { "America/Mexico_City", "Central Standard Time (Mexico)" },
            { "America/Tijuana", "Pacific Standard Time (Mexico)" },
            { "America/Bogota", "SA Pacific Standard Time" },
            { "America/Lima", "SA Pacific Standard Time" },
            { "America/Santiago", "Pacific SA Standard Time" },
            { "America/Buenos_Aires", "Argentina Standard Time" },
            { "America/Argentina/Buenos_Aires", "Argentina Standard Time" },
            { "America/Sao_Paulo", "E. South America Standard Time" },
            { "America/Caracas", "Venezuela Standard Time" },

            // Asia
            { "Asia/Tokyo", "Tokyo Standard Time" },
            { "Asia/Seoul", "Korea Standard Time" },
            { "Asia/Shanghai", "China Standard Time" },
            { "Asia/Hong_Kong", "China Standard Time" },
            { "Asia/Taipei", "Taipei Standard Time" },
            { "Asia/Singapore", "Singapore Standard Time" },
            { "Asia/Kuala_Lumpur", "Singapore Standard Time" },
            { "Asia/Bangkok", "SE Asia Standard Time" },
            { "Asia/Jakarta", "SE Asia Standard Time" },
            { "Asia/Ho_Chi_Minh", "SE Asia Standard Time" },
            { "Asia/Kolkata", "India Standard Time" },
            { "Asia/Calcutta", "India Standard Time" },
            { "Asia/Colombo", "Sri Lanka Standard Time" },
            { "Asia/Karachi", "Pakistan Standard Time" },
            { "Asia/Dhaka", "Bangladesh Standard Time" },
            { "Asia/Kathmandu", "Nepal Standard Time" },
            { "Asia/Almaty", "Central Asia Standard Time" },
            { "Asia/Tashkent", "West Asia Standard Time" },
            { "Asia/Dubai", "Arabian Standard Time" },
            { "Asia/Muscat", "Arabian Standard Time" },
            { "Asia/Riyadh", "Arab Standard Time" },
            { "Asia/Kuwait", "Arab Standard Time" },
            { "Asia/Baghdad", "Arabic Standard Time" },
            { "Asia/Tehran", "Iran Standard Time" },
            { "Asia/Kabul", "Afghanistan Standard Time" },
            { "Asia/Baku", "Azerbaijan Standard Time" },
            { "Asia/Tbilisi", "Georgian Standard Time" },
            { "Asia/Yerevan", "Caucasus Standard Time" },
            { "Asia/Jerusalem", "Israel Standard Time" },
            { "Asia/Beirut", "Middle East Standard Time" },
            { "Asia/Yangon", "Myanmar Standard Time" },
            { "Asia/Vladivostok", "Vladivostok Standard Time" },
            { "Asia/Yakutsk", "Yakutsk Standard Time" },
            { "Asia/Krasnoyarsk", "North Asia Standard Time" },
            { "Asia/Novosibirsk", "N. Central Asia Standard Time" },
            { "Asia/Irkutsk", "North Asia East Standard Time" },

            // Oceania
            { "Australia/Sydney", "AUS Eastern Standard Time" },
            { "Australia/Melbourne", "AUS Eastern Standard Time" },
            { "Australia/Brisbane", "E. Australia Standard Time" },
            { "Australia/Perth", "W. Australia Standard Time" },
            { "Australia/Adelaide", "Cen. Australia Standard Time" },
            { "Australia/Darwin", "AUS Central Standard Time" },
            { "Australia/Hobart", "Tasmania Standard Time" },
            { "Pacific/Auckland", "New Zealand Standard Time" },
            { "Pacific/Fiji", "Fiji Standard Time" },
            { "Pacific/Guam", "West Pacific Standard Time" },
            { "Pacific/Port_Moresby", "West Pacific Standard Time" },

            // Africa
            { "Africa/Cairo", "Egypt Standard Time" },
            { "Africa/Johannesburg", "South Africa Standard Time" },
            { "Africa/Lagos", "W. Central Africa Standard Time" },
            { "Africa/Nairobi", "E. Africa Standard Time" },
            { "Africa/Casablanca", "Morocco Standard Time" },
            { "Africa/Windhoek", "Namibia Standard Time" },
        };
    }
}
