using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Shared.Logging
{
    /// <summary>
    /// Represents a single parsed line from a CMTrace-format log file.
    /// <para>
    /// Timestamps are deliberately NOT resolved to UTC here unless the writer declared its own
    /// offset. See <see cref="CmTraceLogParser"/> for why.
    /// </para>
    /// </summary>
    public class CmTraceLogEntry
    {
        /// <summary>
        /// The line's local time exactly as the writing process wrote it. Kind is
        /// <see cref="DateTimeKind.Unspecified"/> on purpose — this value is meaningless without
        /// knowing which zone the WRITER believed it was in, which the line itself does not say.
        /// </summary>
        public DateTime LocalTimestamp { get; set; }

        /// <summary>
        /// The writer-declared UTC bias in minutes, when the line carried one ("+480").
        /// <c>null</c> for the far more common bias-less form.
        /// </summary>
        public int? BiasMinutes { get; set; }

        /// <summary>
        /// UTC — populated ONLY when <see cref="BiasMinutes"/> was present, because a
        /// writer-declared offset is authoritative. <c>null</c> otherwise: the caller must resolve
        /// <see cref="LocalTimestamp"/> itself, and is the only party able to do so correctly.
        /// </summary>
        public DateTime? TimestampUtc { get; set; }

        /// <summary>
        /// Whether the line carried a parseable timestamp at all. When <c>false</c>,
        /// <see cref="LocalTimestamp"/> is <c>default</c> and callers must fall back to their own
        /// clock rather than treat the value as a real time.
        /// </summary>
        public bool HasTimestamp { get; set; }

        public string Message { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public int Type { get; set; } // 1=Info, 2=Warning, 3=Error
        public string Thread { get; set; } = string.Empty;
    }

    /// <summary>
    /// Parses CMTrace/SCCM log format used by IME and other Microsoft components.
    /// Format: &lt;![LOG[{message}]LOG]!&gt;&lt;time="{time}" date="{date}" component="{comp}" context="" type="{type}" thread="{thread}" file=""&gt;
    ///
    /// <para>
    /// Lives in Shared because it is the SINGLE parsing implementation for both sides: the
    /// agent's IME tracker / logparser gather collector (on-device) and the backend's
    /// rules/gather/test-pattern endpoint, which must reproduce the agent's matching semantics
    /// exactly so authors can test patterns without a device.
    /// </para>
    ///
    /// <para>
    /// <b>The parser does not guess timezones.</b> A bias-less CMTrace line carries local time
    /// and nothing else — IME 1.104 writes <c>DateTime.Now.TimeOfDay</c> in both of its trace
    /// listeners. Resolving that with the READER's <c>TimeZoneInfo.Local</c> silently assumes the
    /// writing and reading processes agree on the zone. They do not: each caches its zone at
    /// process start and neither follows a later <c>tzutil</c> or a Windows auto-timezone change.
    /// Field measurement over 11,068 sessions found 26 sessions where the two beliefs differed,
    /// from +1 h to -17 h, silently shifting every IME-derived event by that amount. So this
    /// parser hands back <see cref="CmTraceLogEntry.LocalTimestamp"/> plus
    /// <see cref="CmTraceLogEntry.BiasMinutes"/> and lets the caller — which alone can measure
    /// the writer's actual offset — do the conversion.
    /// </para>
    /// </summary>
    public static class CmTraceLogParser
    {
        /// <summary>
        /// Every CMTrace line has the shape <c>&lt;![LOG[message]LOG]!&gt;&lt;time=... file=""&gt;</c>. The
        /// parser deliberately does NOT express that as one regex with a greedy <c>.*</c> message
        /// group in front of the <c>]LOG]!&gt;&lt;time="</c> literal: on a line that never matches, such a
        /// regex re-tries every <c>&lt;![LOG[</c> occurrence as a fresh start and, for each, backtracks
        /// <c>.*</c> across every later character — Theta(k*m), quadratic in line length. Both sinks
        /// parse attacker-influenced input (tenant-supplied sample lines on the backend, tailed log
        /// files assembled into multi-line entries on the SYSTEM agent), so the cost must be linear.
        ///
        /// <para>
        /// The greedy regex's semantics are reproduced exactly with string search: the message
        /// starts after the FIRST <c>&lt;![LOG[</c> (a later start can only match when the first one
        /// does too, since <c>.*</c> spans the difference), and the message ends at the LAST
        /// <c>]LOG]!&gt;&lt;time="</c> occurrence whose trailer parses — earlier occurrences are tried in
        /// turn exactly as backtracking would. The trailer regex is anchored with <c>\G</c>, has no
        /// ambiguous quantifier, and additionally carries a match timeout as a backstop; a timeout
        /// is reported as "does not match", never thrown.
        /// </para>
        /// </summary>
        private const string LogOpen = "<![LOG[";
        private const string TrailerOpen = "]LOG]!><time=\"";

        // The time field may carry a UTC-bias suffix in minutes ("06:08:04.8834397+480",
        // GetTimeZoneInformation convention: UTC = local + bias) — without the optional bias
        // group such lines would not match at all and their content would be invisible to
        // every pattern.
        private static readonly Regex TrailerRegex = new Regex(
            @"\G\]LOG\]!><time=""(?<time>[\d:.]+)(?<bias>[+-]\d{1,4})?""\s+date=""(?<date>[\d-]+)""\s+component=""(?<component>[^""]*)""\s+context=""[^""]*""\s+type=""(?<type>\d+)""\s+thread=""(?<thread>\d+)""\s+file=""[^""]*"">",
            RegexOptions.Compiled,
            TimeSpan.FromSeconds(1)
        );

        /// <summary>
        /// Attempts to parse a single line of CMTrace-formatted log.
        /// Returns true if parsing succeeded, false if the line doesn't match the format.
        /// Cost is linear in the line length for arbitrary input; it never throws.
        /// </summary>
        public static bool TryParseLine(string? line, [NotNullWhen(true)] out CmTraceLogEntry? entry)
        {
            entry = null;

            // netstandard2.0's IsNullOrEmpty lacks [NotNullWhen(false)] — assert non-null after it.
            // StartsWith is culture-sensitive on purpose: a leading BOM (U+FEFF) is ignorable
            // there, so a freshly created log's first line still parses.
            if (string.IsNullOrEmpty(line) || !line!.StartsWith(LogOpen))
                return false;

            var open = line.IndexOf(LogOpen, StringComparison.Ordinal);
            if (open < 0)
                return false;
            var messageStart = open + LogOpen.Length;

            Match? match = null;
            var trailerPos = line.LastIndexOf(TrailerOpen, StringComparison.Ordinal);
            try
            {
                while (trailerPos >= messageStart)
                {
                    var candidate = TrailerRegex.Match(line, trailerPos);
                    if (candidate.Success)
                    {
                        match = candidate;
                        break;
                    }
                    if (trailerPos == messageStart)
                        break;
                    trailerPos = line.LastIndexOf(TrailerOpen, trailerPos - 1, StringComparison.Ordinal);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            if (match == null)
                return false;

            var message = line.Substring(messageStart, trailerPos - messageStart);
            var timeStr = match.Groups["time"].Value;
            var biasStr = match.Groups["bias"].Value;
            var dateStr = match.Groups["date"].Value;
            var component = match.Groups["component"].Value;
            var typeStr = match.Groups["type"].Value;
            var thread = match.Groups["thread"].Value;

            DateTime localTimestamp;
            var hasTimestamp = TryParseLocalTimestamp(dateStr, timeStr, out localTimestamp);

            int? biasMinutes = null;
            if (!string.IsNullOrEmpty(biasStr)
                && int.TryParse(biasStr, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedBias))
            {
                biasMinutes = parsedBias;
            }

            // A writer-declared bias is authoritative — it is the writer telling us its own
            // offset, which is exactly the fact we otherwise have to measure.
            // GetTimeZoneInformation convention: UTC = local + bias.
            DateTime? timestampUtc = null;
            if (hasTimestamp && biasMinutes.HasValue)
            {
                timestampUtc = DateTime.SpecifyKind(
                    localTimestamp.AddMinutes(biasMinutes.Value), DateTimeKind.Utc);
            }

            int type;
            int.TryParse(typeStr, out type);

            entry = new CmTraceLogEntry
            {
                LocalTimestamp = localTimestamp,
                BiasMinutes = biasMinutes,
                TimestampUtc = timestampUtc,
                HasTimestamp = hasTimestamp,
                Message = message,
                Component = component,
                Type = type,
                Thread = thread
            };

            return true;
        }

        /// <summary>
        /// Resolves a bias-less CMTrace local timestamp using the READING process's own timezone.
        ///
        /// <para>
        /// <b>This is a fallback with a known defect, not a correct conversion.</b> It is right
        /// only when the writing process happens to hold the same zone belief as this one. Use it
        /// solely where no better information exists — a log this process does not tail and
        /// therefore cannot measure an offset against — and mark the resulting value as not
        /// source-grounded so the inaccuracy stays visible.
        /// </para>
        ///
        /// <para>
        /// Callers that DO tail their log must instead measure the writer's offset from a freshly
        /// appended line (see the agent's <c>CmTraceOffsetCalibrator</c>).
        /// </para>
        /// </summary>
        public static DateTime ResolveUtcAssumingReaderZone(DateTime localTimestamp)
            => DateTime.SpecifyKind(localTimestamp, DateTimeKind.Local).ToUniversalTime();

        private static bool TryParseLocalTimestamp(string dateStr, string timeStr, out DateTime result)
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

            // DateTimeStyles.None: the value stays Unspecified. Attaching Local or Utc here would
            // be the very guess this parser refuses to make.
            if (DateTime.TryParseExact(combined, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out result))
            {
                return true;
            }

            result = default(DateTime);
            return false;
        }
    }
}
