using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// One-shot read of the Schannel errors the System log recorded since a point in time:
    /// 36871 (creating the client credential failed), 36887 (fatal TLS alert received) and
    /// 36888 (fatal TLS alert sent). Only the terminal registration-failure path calls it.
    /// Another process's TLS failure in the same window shows up too, so the result is a hint.
    /// </summary>
    internal static class SchannelEventReader
    {
        internal const int MaxEvents = 2;

        /// <summary>The log was read and holds no matching event — a finding, unlike <see cref="Unreadable"/>.</summary>
        internal const string NoEvents = "none";

        /// <summary>The System log could not be queried; nothing is known about Schannel.</summary>
        internal const string Unreadable = "unreadable";

        /// <summary>
        /// Newest first, at most <see cref="MaxEvents"/> events as <see cref="FormatToken"/> tokens
        /// joined by commas (<c>36888:40/1205,36887:116</c>); <see cref="NoEvents"/> when there is
        /// none, <see cref="Unreadable"/> when the log cannot be read. Never throws.
        /// </summary>
        public static string ReadSince(DateTime sinceUtc, AgentLogger logger = null)
        {
            var tokens = new List<string>();
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, BuildXPath(sinceUtc)) { ReverseDirection = true };
                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while (tokens.Count < MaxEvents && (record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            tokens.Add(FormatToken(record.Id, ReadProperties(record)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"SchannelEventReader: System log not readable ({ex.Message}).");
                return Unreadable;
            }
            return tokens.Count == 0 ? NoEvents : string.Join(",", tokens);
        }

        internal static string BuildXPath(DateTime sinceUtc)
        {
            var since = sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            return "*[System[Provider[@Name='Schannel'] and (EventID=36871 or EventID=36887 or EventID=36888)" +
                   $" and TimeCreated[@SystemTime >= '{since}']]]";
        }

        /// <summary>
        /// <c>36888:40/1205</c>: the event id and its numeric properties (TLS alert code, Schannel
        /// error state). Text properties (target name, certificate subjects) are left out.
        /// </summary>
        internal static string FormatToken(int eventId, IEnumerable<string> properties)
        {
            var numbers = (properties ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrEmpty(p) && p.All(c => c >= '0' && c <= '9'))
                .Take(3)
                .ToList();
            return numbers.Count == 0
                ? eventId.ToString(CultureInfo.InvariantCulture)
                : $"{eventId.ToString(CultureInfo.InvariantCulture)}:{string.Join("/", numbers)}";
        }

        private static List<string> ReadProperties(EventRecord record)
        {
            var values = new List<string>();
            try
            {
                foreach (var property in record.Properties)
                    values.Add(Convert.ToString(property?.Value, CultureInfo.InvariantCulture));
            }
            catch { /* best-effort — keep what was read */ }
            return values;
        }
    }
}
