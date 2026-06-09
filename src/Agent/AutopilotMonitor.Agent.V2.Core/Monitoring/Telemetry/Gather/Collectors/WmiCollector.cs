using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class WmiCollector : IGatherRuleCollector
    {
        public string CollectorType => "wmi";

        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var data = new Dictionary<string, object>();
            var query = rule.Target;

            if (string.IsNullOrEmpty(query))
                return data;

            // Guard: only allow known-safe WMI classes
            if (!GatherRuleGuards.IsWmiQueryAllowed(query, context.UnrestrictedMode))
                return context.EmitSecurityWarning(rule, "wmi", query);

            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    int index = 0;
                    foreach (ManagementObject obj in searcher.Get())
                    using (obj) // ManagementObject holds native WMI/COM memory — dispose per iteration
                    {
                        var item = new Dictionary<string, object>();
                        foreach (var prop in obj.Properties)
                        {
                            try
                            {
                                item[prop.Name] = prop.Value?.ToString();
                            }
                            catch { }
                        }

                        if (item.Count > 0)
                        {
                            // For single result, flatten; for multiple, use indexed keys
                            if (index == 0)
                            {
                                foreach (var kvp in item)
                                    data[kvp.Key] = kvp.Value;
                            }
                            else
                            {
                                data[$"item_{index}"] = string.Join(", ", item.Select(k => $"{k.Key}={k.Value}"));
                            }
                        }
                        index++;

                        if (index >= 20) break; // Limit results
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            return data;
        }
    }
}
