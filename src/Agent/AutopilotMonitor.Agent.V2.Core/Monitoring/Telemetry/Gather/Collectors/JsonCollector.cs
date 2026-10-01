using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;

using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class JsonCollector : IGatherRuleCollector
    {
        public string CollectorType => "json";

        public Dictionary<string, object> Execute(GatherRule rule, GatherRuleContext context)
        {
            var data = new Dictionary<string, object>();
            var filePath = rule.Target;

            if (string.IsNullOrEmpty(filePath))
                return data;

            // Expand custom tokens (%LOGGED_ON_USER_PROFILE%) and standard environment variables
            var userProfilePath = UserProfileResolver.ContainsUserProfileToken(filePath)
                ? UserProfileResolver.GetLoggedOnUserProfilePath() : null;
            filePath = UserProfileResolver.ExpandCustomTokens(filePath, userProfilePath);
            if (filePath == null)
                return data; // Token present but no user logged on — skip silently

            if (!GatherRuleGuards.IsFilePathAllowed(filePath, context.UnrestrictedMode, userProfilePath))
                return context.EmitSecurityWarning(rule, "json", filePath);

            string jsonPath;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("jsonpath", out jsonPath) ||
                string.IsNullOrEmpty(jsonPath))
            {
                context.Logger.Warning($"JSON rule {rule.RuleId} has no 'jsonpath' parameter");
                data["error"] = "Missing required 'jsonpath' parameter";
                return data;
            }

            int maxResults = 20;
            string maxResultsStr;
            if (rule.Parameters.TryGetValue("maxResults", out maxResultsStr))
                int.TryParse(maxResultsStr, out maxResults);
            maxResults = Math.Min(maxResults, 100);

            try
            {
                if (!File.Exists(filePath))
                {
                    data["exists"] = false;
                    data["path"] = filePath;
                    return data;
                }

                var info = new FileInfo(filePath);
                if (info.Length > 200 * 1024) // 200 KB limit
                {
                    data["error"] = $"File too large ({info.Length} bytes, max 200 KB)";
                    data["path"] = filePath;
                    return data;
                }

                string content;
                using (var reader = new StreamReader(filePath))
                {
                    content = reader.ReadToEnd();
                }

                var token = JToken.Parse(content);
                var results = token.SelectTokens(jsonPath).Take(maxResults).ToList();

                data["path"] = filePath;
                data["query"] = jsonPath;
                data["matchCount"] = results.Count;

                if (results.Count == 1)
                {
                    data["value"] = TruncateMessage(results[0].ToString(), 2000);
                }

                if (results.Count > 0)
                {
                    var matchStrings = results.Select(r => TruncateMessage(r.ToString(), 500)).ToList();
                    data["matches"] = string.Join("\n---\n", matchStrings);
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
                data["path"] = filePath;
                data["query"] = jsonPath;
            }

            return data;
        }

        private static string TruncateMessage(string message, int maxLength)
        {
            if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
                return message;
            return message.Substring(0, maxLength) + "...";
        }
    }
}
