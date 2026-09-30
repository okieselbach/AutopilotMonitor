using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Resolves the <c>{{token}}</c> placeholders of a rule's explanation/remediation text from a
    /// result's <see cref="RuleResult.MatchedConditions"/>. A stored <see cref="RuleResult"/> keeps
    /// the rule's raw template; the web (<c>lib/interpolateRuleTemplate.ts</c>) and the MCP server
    /// (<c>src/interpolate-rule-template.ts</c>) resolve it when they render. This is the backend
    /// copy for text that leaves without passing either of them (notifications).
    /// <para>
    /// Resolution order, identical in all three and pinned by
    /// <c>tests/fixtures/rule-template-interpolation/cases.json</c>:
    /// </para>
    /// <list type="number">
    /// <item>an evidence entry whose <c>field</c> equals the token (the last such entry wins);</item>
    /// <item>a same-event auto-field named like the token (<see cref="RuleEngine.EvidenceAutoFields"/>;
    /// the first non-empty one wins, which pins it to the rule's earliest matched condition);</item>
    /// <item>an evidence entry whose signal name equals the token;</item>
    /// <item>otherwise the token stays as written.</item>
    /// </list>
    /// </summary>
    public static class RuleTemplateInterpolator
    {
        /// <summary>Shown in a notification where running text names a value that was not recorded.</summary>
        internal const string NotRecorded = "–";

        private static readonly Regex TokenRegex = new Regex(
            @"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ListItemRegex = new Regex(
            @"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex SentenceBreakRegex = new Regex(
            @"(?<=[.!?])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex BlankRunRegex = new Regex(
            @"\n{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Replaces every resolvable token; an unresolved one stays literal, exactly as the web
        /// and the MCP server render it.
        /// </summary>
        public static string Interpolate(string? text, IReadOnlyDictionary<string, object>? matchedConditions)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var lookup = Lookup.From(matchedConditions);
            return TokenRegex.Replace(text, m => lookup.Resolve(m.Groups[1].Value) ?? m.Value);
        }

        /// <summary>
        /// Interpolation for text that leaves the backend as a notification. The recipient has no
        /// renderer and no footnote explaining a literal placeholder, so none may survive: a list
        /// item whose token was not recorded is dropped, a token in running text becomes
        /// <see cref="NotRecorded"/>, and a sentence that talks about literal placeholders is removed.
        /// </summary>
        public static string InterpolateForNotification(string? text, IReadOnlyDictionary<string, object>? matchedConditions)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var lookup = Lookup.From(matchedConditions);
            var lines = new List<string>();

            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var unresolved = false;
                var rendered = TokenRegex.Replace(line, m =>
                {
                    var value = lookup.Resolve(m.Groups[1].Value);
                    if (value != null)
                        return value;
                    unresolved = true;
                    return NotRecorded;
                });

                if (unresolved && ListItemRegex.IsMatch(line))
                    continue;

                if (rendered.Contains("{{", StringComparison.Ordinal))
                {
                    rendered = string.Join(" ", SentenceBreakRegex.Split(rendered)
                        .Where(sentence => !sentence.Contains("{{", StringComparison.Ordinal)));
                    if (rendered.Length == 0)
                        continue;
                }

                lines.Add(rendered);
            }

            return BlankRunRegex.Replace(string.Join("\n", lines), "\n\n").Trim();
        }

        private sealed class Lookup
        {
            private readonly Dictionary<string, string> _byField = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _byAutoField = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _bySignal = new Dictionary<string, string>(StringComparer.Ordinal);

            public string? Resolve(string token)
            {
                if (_byField.TryGetValue(token, out var value)) return value;
                if (_byAutoField.TryGetValue(token, out value)) return value;
                if (_bySignal.TryGetValue(token, out value)) return value;
                return null;
            }

            public static Lookup From(IReadOnlyDictionary<string, object>? matchedConditions)
            {
                var lookup = new Lookup();
                if (matchedConditions == null || matchedConditions.Count == 0)
                    return lookup;

                // Evidence arrives in two shapes: nested dictionaries straight from the rule engine,
                // or whatever the table read produced. Going through JSON gives both the form the
                // web and the MCP server see. JsonSerializer.Create() ignores the process-wide
                // default settings, so a naming strategy configured there cannot rename the keys.
                JObject root;
                try
                {
                    root = JObject.FromObject(matchedConditions, JsonSerializer.Create());
                }
                catch (Exception)
                {
                    // Evidence that cannot be serialized resolves nothing; the text is still sent.
                    return lookup;
                }

                foreach (var property in root.Properties())
                {
                    if (property.Value is not JObject evidence)
                    {
                        if (property.Value is JValue scalar && scalar.Type != JTokenType.Null && scalar.Type != JTokenType.Undefined)
                            lookup._bySignal[property.Name] = Format(scalar);
                        continue;
                    }

                    var hasValue = evidence.TryGetValue("value", out var valueToken);
                    if (hasValue)
                    {
                        if (evidence["field"] is JValue { Type: JTokenType.String } field)
                            lookup._byField[(string)field.Value!] = Format(valueToken);
                        lookup._bySignal[property.Name] = Format(valueToken);
                    }

                    foreach (var autoField in RuleEngine.EvidenceAutoFields)
                    {
                        if (lookup._byAutoField.ContainsKey(autoField))
                            continue;
                        var formatted = Format(evidence[autoField]);
                        if (formatted.Length > 0)
                            lookup._byAutoField[autoField] = formatted;
                    }
                }

                return lookup;
            }

            // Mirrors the TypeScript formatValue: strings as they are, numbers and booleans the
            // way JavaScript prints them, anything structured as compact JSON.
            private static string Format(JToken? token)
            {
                if (token == null)
                    return string.Empty;

                switch (token.Type)
                {
                    case JTokenType.Null:
                    case JTokenType.Undefined:
                        return string.Empty;
                    case JTokenType.String:
                        return (string)((JValue)token).Value!;
                    case JTokenType.Boolean:
                        return (bool)token ? "true" : "false";
                    case JTokenType.Integer:
                        return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    case JTokenType.Float:
                        return Convert.ToDouble(((JValue)token).Value, CultureInfo.InvariantCulture)
                            .ToString(CultureInfo.InvariantCulture);
                    case JTokenType.Object:
                    case JTokenType.Array:
                        return token.ToString(Formatting.None);
                    default:
                        // Date, Guid, TimeSpan, Uri: their JSON string form without the quotes.
                        return token.ToString(Formatting.None).Trim('"');
                }
            }
        }
    }
}
