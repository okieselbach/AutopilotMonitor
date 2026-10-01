using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using AutopilotMonitor.Shared.Models;

using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors
{
    public class XmlCollector : IGatherRuleCollector
    {
        public string CollectorType => "xml";

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
                return context.EmitSecurityWarning(rule, "xml", filePath);

            string xpath;
            if (rule.Parameters == null || !rule.Parameters.TryGetValue("xpath", out xpath) ||
                string.IsNullOrEmpty(xpath))
            {
                context.Logger.Warning($"XML rule {rule.RuleId} has no 'xpath' parameter");
                data["error"] = "Missing required 'xpath' parameter";
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

                // Parse XML with DTD processing disabled (XXE prevention)
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                XDocument doc;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var xmlReader = XmlReader.Create(stream, settings))
                {
                    doc = XDocument.Load(xmlReader);
                }

                // Set up namespace resolver if namespaces are provided
                XmlNamespaceManager nsManager = null;
                string namespacesStr;
                if (rule.Parameters.TryGetValue("namespaces", out namespacesStr) &&
                    !string.IsNullOrEmpty(namespacesStr))
                {
                    nsManager = new XmlNamespaceManager(new NameTable());
                    foreach (var ns in namespacesStr.Split(';'))
                    {
                        var parts = ns.Split(new[] { '=' }, 2);
                        if (parts.Length == 2 && !string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
                        {
                            nsManager.AddNamespace(parts[0].Trim(), parts[1].Trim());
                        }
                    }
                }

                // Execute XPath — try element selection first, fall back to evaluate for scalar/attribute results
                var elements = nsManager != null
                    ? doc.XPathSelectElements(xpath, nsManager).Take(maxResults).ToList()
                    : doc.XPathSelectElements(xpath).Take(maxResults).ToList();

                data["path"] = filePath;
                data["query"] = xpath;

                if (elements.Count > 0)
                {
                    data["matchCount"] = elements.Count;
                    if (elements.Count == 1)
                    {
                        data["value"] = TruncateMessage(elements[0].ToString(), 2000);
                    }
                    var matchStrings = elements.Select(e => TruncateMessage(e.ToString(), 500)).ToList();
                    data["matches"] = string.Join("\n---\n", matchStrings);
                }
                else
                {
                    // Try XPathEvaluate for scalar results (attributes, text(), count(), etc.)
                    var evalResult = nsManager != null
                        ? doc.XPathEvaluate(xpath, nsManager)
                        : doc.XPathEvaluate(xpath);

                    if (evalResult is IEnumerable<object> enumerable)
                    {
                        var items = enumerable.Take(maxResults).ToList();
                        data["matchCount"] = items.Count;
                        if (items.Count == 1)
                        {
                            data["value"] = TruncateMessage(items[0].ToString(), 2000);
                        }
                        if (items.Count > 0)
                        {
                            data["matches"] = string.Join("\n---\n",
                                items.Select(i => TruncateMessage(i.ToString(), 500)));
                        }
                    }
                    else if (evalResult != null)
                    {
                        data["matchCount"] = 1;
                        data["value"] = TruncateMessage(evalResult.ToString(), 2000);
                    }
                    else
                    {
                        data["matchCount"] = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                data["error"] = ex.Message;
                data["path"] = filePath;
                data["query"] = xpath;
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
