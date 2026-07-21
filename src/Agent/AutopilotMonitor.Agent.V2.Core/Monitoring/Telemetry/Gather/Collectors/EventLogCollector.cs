using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class EventLogCollector : IGatherRuleCollector
    {
        public string CollectorType => "eventlog";

        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var data = new Dictionary<string, object>();
            var logName = rule.Target;

            if (string.IsNullOrEmpty(logName))
                return data;

            // Guard: only allow enrollment-relevant channels
            if (!GatherRuleGuards.IsEventLogChannelAllowed(logName, context.UnrestrictedMode))
                return context.EmitSecurityWarning(rule, "eventlog", logName);

            // Optional: escalate severity when matching entries are found
            string severityIfFound = null;
            rule.Parameters?.TryGetValue("severityIfFound", out severityIfFound);

            try
            {
                int maxEntries = 10;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("maxEntries", out var maxEntriesStr))
                {
                    int.TryParse(maxEntriesStr, out maxEntries);
                    maxEntries = Math.Min(maxEntries, 50); // Cap at 50
                }

                // Build XPath query with optional filters
                var conditions = new List<string>();

                string sourceFilter = null;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("source", out sourceFilter)
                    && !string.IsNullOrEmpty(sourceFilter))
                {
                    conditions.Add($"Provider[@Name='{EscapeXPath(sourceFilter)}']");
                }

                string eventIdFilter = null;
                if (rule.Parameters != null && rule.Parameters.TryGetValue("eventId", out eventIdFilter)
                    && !string.IsNullOrEmpty(eventIdFilter))
                {
                    conditions.Add($"EventID={eventIdFilter}");
                }

                string xpath = conditions.Count > 0
                    ? $"*[System[{string.Join(" and ", conditions)}]]"
                    : "*";

                string messageFilter = null;
                rule.Parameters?.TryGetValue("messageFilter", out messageFilter);

                var query = new EventLogQuery(logName, PathType.LogName, xpath)
                {
                    ReverseDirection = true // newest first
                };

                data["log_name"] = logName;
                var entries = new List<string>();

                using (var reader = new EventLogReader(query))
                {
                    EventRecord record;
                    while ((record = reader.ReadEvent()) != null && entries.Count < maxEntries)
                    {
                        using (record)
                        {
                            var message = "";
                            try { message = record.FormatDescription() ?? ""; }
                            catch { /* Some events lack formatting resources */ }

                            // Apply message filter (wildcard contains)
                            if (!string.IsNullOrEmpty(messageFilter))
                            {
                                var pattern = messageFilter.Trim('*');
                                if (string.IsNullOrEmpty(pattern) ||
                                    message.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                            }

                            var level = "";
                            try { level = record.LevelDisplayName ?? record.Level?.ToString() ?? "Unknown"; }
                            catch { level = record.Level?.ToString() ?? "Unknown"; }

                            var truncMsg = message.Length > 500 ? message.Substring(0, 500) : message;
                            entries.Add($"[{record.TimeCreated:yyyy-MM-dd HH:mm:ss}] [{level}] [ID:{record.Id}] {truncMsg}");
                        }
                    }
                }

                for (int i = 0; i < entries.Count; i++)
                {
                    data[$"entry_{i + 1}"] = entries[i];
                }
                data["entries_returned"] = entries.Count;

                // Apply conditional severity when matching entries are found
                if (entries.Count > 0 && !string.IsNullOrEmpty(severityIfFound))
                    data["_severityOverride"] = severityIfFound;
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }

        private static string EscapeXPath(string value)
        {
            return value.Replace("'", "&apos;");
        }
    }
}
