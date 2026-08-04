using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Represents a single parsed line from a CMTrace-format log file
    /// </summary>
    public class CmTraceLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Message { get; set; }
        public string Component { get; set; }
        public int Type { get; set; } // 1=Info, 2=Warning, 3=Error
        public string Thread { get; set; }
    }

    /// <summary>
    /// Parses CMTrace/SCCM log format used by IME and other Microsoft components.
    /// Format: &lt;![LOG[{message}]LOG]!&gt;&lt;time="{time}" date="{date}" component="{comp}" context="" type="{type}" thread="{thread}" file=""&gt;
    /// </summary>
    public static class CmTraceLogParser
    {
        // Pre-compiled regex for the CMTrace format. The time field may carry a UTC-bias
        // suffix in minutes ("06:08:04.8834397+480", GetTimeZoneInformation convention:
        // UTC = local + bias) — without the optional bias group such lines would not match
        // at all and their content would be invisible to every pattern.
        private static readonly Regex CmTraceRegex = new Regex(
            @"<!\[LOG\[(?<message>.*)\]LOG\]!><time=""(?<time>[\d:.]+)(?<bias>[+-]\d{1,4})?""\s+date=""(?<date>[\d-]+)""\s+component=""(?<component>[^""]*)""\s+context=""[^""]*""\s+type=""(?<type>\d+)""\s+thread=""(?<thread>\d+)""\s+file=""[^""]*"">",
            RegexOptions.Compiled | RegexOptions.Singleline
        );

        /// <summary>
        /// Attempts to parse a single line of CMTrace-formatted log.
        /// Returns true if parsing succeeded, false if the line doesn't match the format.
        /// </summary>
        public static bool TryParseLine(string line, out CmTraceLogEntry entry)
        {
            entry = null;

            if (string.IsNullOrEmpty(line) || !line.StartsWith("<![LOG["))
                return false;

            var match = CmTraceRegex.Match(line);
            if (!match.Success)
                return false;

            var message = match.Groups["message"].Value;
            var timeStr = match.Groups["time"].Value;
            var biasStr = match.Groups["bias"].Value;
            var dateStr = match.Groups["date"].Value;
            var component = match.Groups["component"].Value;
            var typeStr = match.Groups["type"].Value;
            var thread = match.Groups["thread"].Value;

            // Parse timestamp: date is "M-d-yyyy", time is "HH:mm:ss.ticks"
            DateTime timestamp;
            if (!TryParseTimestamp(dateStr, timeStr, biasStr, out timestamp))
            {
                timestamp = DateTime.UtcNow;
            }

            int type;
            int.TryParse(typeStr, out type);

            entry = new CmTraceLogEntry
            {
                Timestamp = timestamp,
                Message = message,
                Component = component,
                Type = type,
                Thread = thread
            };

            return true;
        }

        private static bool TryParseTimestamp(string dateStr, string timeStr, string biasStr, out DateTime result)
        {
            result = DateTime.MinValue;

            // Date format: "M-d-yyyy" (e.g., "2-8-2026")
            // Time format: "HH:mm:ss.ticks" (e.g., "06:08:04.8834397")
            // Truncate time to 7 fractional digits max for DateTime parsing
            var timeParts = timeStr.Split('.');
            string normalizedTime;
            if (timeParts.Length == 2)
            {
                var fraction = timeParts[1];
                if (fraction.Length > 7)
                    fraction = fraction.Substring(0, 7);
                normalizedTime = timeParts[0] + "." + fraction;
            }
            else
            {
                normalizedTime = timeStr;
            }

            var combined = dateStr + " " + normalizedTime;

            // Try multiple date formats to handle varying date styles
            string[] formats = new[]
            {
                "M-d-yyyy H:mm:ss.fffffff",
                "M-d-yyyy H:mm:ss.ffffff",
                "M-d-yyyy H:mm:ss.fffff",
                "M-d-yyyy H:mm:ss.ffff",
                "M-d-yyyy H:mm:ss.fff",
                "M-d-yyyy H:mm:ss.ff",
                "M-d-yyyy H:mm:ss.f",
                "M-d-yyyy H:mm:ss",
                "M-d-yyyy HH:mm:ss.fffffff",
                "M-d-yyyy HH:mm:ss.ffffff",
                "M-d-yyyy HH:mm:ss.fffff",
                "M-d-yyyy HH:mm:ss.ffff",
                "M-d-yyyy HH:mm:ss.fff",
                "M-d-yyyy HH:mm:ss.ff",
                "M-d-yyyy HH:mm:ss.f",
                "M-d-yyyy HH:mm:ss"
            };

            // Bias-suffixed time ("+480"): the writer declared its own UTC offset in minutes
            // (GetTimeZoneInformation convention: UTC = written time + bias). Honor it instead
            // of assuming the agent's local timezone — writer and agent can disagree (mixed
            // components, mid-enrollment timezone change; session df1fcf47).
            if (!string.IsNullOrEmpty(biasStr)
                && int.TryParse(biasStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var biasMinutes))
            {
                if (DateTime.TryParseExact(combined, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out result))
                {
                    result = DateTime.SpecifyKind(result.AddMinutes(biasMinutes), DateTimeKind.Utc);
                    return true;
                }
                return false;
            }

            // No bias: CMTrace timestamps are in LOCAL time. Parse as local, then convert to UTC.
            if (DateTime.TryParseExact(combined, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out result))
            {
                result = result.ToUniversalTime();
                return true;
            }
            return false;
        }
    }
}
