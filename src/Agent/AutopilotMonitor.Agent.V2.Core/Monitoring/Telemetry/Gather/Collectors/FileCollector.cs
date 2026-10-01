using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Shared.Models;

using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class FileCollector : IGatherRuleCollector
    {
        public string CollectorType => "file";

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

            // Guard: only allow enrollment-relevant file paths
            if (!GatherRuleGuards.IsFilePathAllowed(filePath, context.UnrestrictedMode, userProfilePath))
                return context.EmitSecurityWarning(rule, "file", filePath);

            // Optional conditional severity based on existence
            string severityIfExists = null;
            string severityIfNotExists = null;
            rule.Parameters?.TryGetValue("severityIfExists", out severityIfExists);
            rule.Parameters?.TryGetValue("severityIfNotExists", out severityIfNotExists);

            try
            {
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    data["exists"] = true;
                    data["path"] = filePath;
                    data["size_bytes"] = info.Length;
                    data["last_modified"] = info.LastWriteTimeUtc.ToString("o");
                    data["created"] = info.CreationTimeUtc.ToString("o");

                    // Read content if requested and file is small enough
                    string readContent;
                    if (rule.Parameters != null && rule.Parameters.TryGetValue("readContent", out readContent) &&
                        readContent == "true" && info.Length < 50000) // Max 50KB
                    {
                        // Read the last 4000 chars — most useful for log files where recent entries matter
                        const int maxChars = 4000;
                        string content;
                        bool truncated = false;
                        using (var reader = new StreamReader(filePath))
                        {
                            var full = reader.ReadToEnd();
                            if (full.Length > maxChars)
                            {
                                content = full.Substring(full.Length - maxChars);
                                truncated = true;
                            }
                            else
                            {
                                content = full;
                            }
                        }
                        data["content"] = truncated ? "(truncated) ..." + content : content;
                    }
                }
                else if (Directory.Exists(filePath))
                {
                    data["exists"] = true;
                    data["is_directory"] = true;
                    data["path"] = filePath;

                    var files = Directory.GetFiles(filePath);
                    data["file_count"] = files.Length;
                    data["files"] = string.Join(", ", files.Select(Path.GetFileName).Take(20));
                }
                else
                {
                    data["exists"] = false;
                    data["path"] = filePath;
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
            }

            // Apply conditional severity based on existence
            if (data.ContainsKey("exists"))
            {
                var exists = data["exists"] is bool b ? b : data["exists"]?.ToString() == "True";
                if (exists && !string.IsNullOrEmpty(severityIfExists))
                    data["_severityOverride"] = severityIfExists;
                else if (!exists && !string.IsNullOrEmpty(severityIfNotExists))
                    data["_severityOverride"] = severityIfNotExists;
            }

            return data;
        }
    }
}
