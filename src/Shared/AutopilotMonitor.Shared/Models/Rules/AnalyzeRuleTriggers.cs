using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Trigger grammar for <see cref="AnalyzeRule.EvaluateOn"/> and the helpers every
    /// consumer (engine rule filter, ingest trigger registry, CRUD validation) shares so
    /// the matching semantics cannot drift apart. An absent/empty EvaluateOn list means
    /// <see cref="EnrollmentEnd"/> only — the historical terminal-only behavior, bit-for-bit.
    /// See docs/rules/analyze-rule-triggers.md for the full design.
    /// </summary>
    public static class AnalyzeRuleTriggers
    {
        /// <summary>Terminal analyze run (enrollment complete / failed / sweep-terminalized). Default.</summary>
        public const string EnrollmentEnd = "enrollment_end";

        /// <summary>First genuine whiteglove_complete seal (session → Pending, isPreProvisioned).</summary>
        public const string WhitegloveSealed = "whiteglove_sealed";

        /// <summary>Prefix for event-driven interim triggers: <c>on_event:&lt;eventType&gt;</c>.</summary>
        public const string OnEventPrefix = "on_event:";

        /// <summary>
        /// The rule's effective trigger list: EvaluateOn, or the enrollment_end default when
        /// the field is absent/empty (legacy rules and rows predating the feature).
        /// </summary>
        public static IReadOnlyList<string> EffectiveTriggers(AnalyzeRule rule)
        {
            return rule.EvaluateOn is { Count: > 0 }
                ? rule.EvaluateOn
                : new[] { EnrollmentEnd };
        }

        /// <summary>True when the rule participates in terminal (enrollment-end) runs.</summary>
        public static bool RunsAtEnrollmentEnd(AnalyzeRule rule)
            => EffectiveTriggers(rule).Any(t => string.Equals(t, EnrollmentEnd, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the rule participates in the whiteglove_sealed interim run.</summary>
        public static bool RunsAtWhitegloveSealed(AnalyzeRule rule)
            => EffectiveTriggers(rule).Any(t => string.Equals(t, WhitegloveSealed, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The event types this rule wants an interim run for (empty when it has no
        /// on_event triggers). Types are returned lower-cased for set intersection.
        /// </summary>
        public static IReadOnlyList<string> OnEventTypes(AnalyzeRule rule)
        {
            return EffectiveTriggers(rule)
                .Where(t => t != null && t.StartsWith(OnEventPrefix, StringComparison.OrdinalIgnoreCase)
                            && t.Length > OnEventPrefix.Length)
                .Select(t => t.Substring(OnEventPrefix.Length).Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .ToList();
        }

        /// <summary>
        /// True when the rule matches an on_event interim run for any of the batch's
        /// trigger event types.
        /// </summary>
        public static bool MatchesOnEvent(AnalyzeRule rule, IReadOnlyCollection<string> triggerEventTypes)
        {
            if (triggerEventTypes == null || triggerEventTypes.Count == 0)
                return false;
            var wanted = OnEventTypes(rule);
            if (wanted.Count == 0)
                return false;
            return triggerEventTypes.Any(t => wanted.Contains(t?.Trim().ToLowerInvariant() ?? string.Empty));
        }

        /// <summary>
        /// Grammar validation for a single trigger token. Used by the rule CRUD endpoints so a
        /// tenant custom rule cannot persist an unparseable trigger. Event-type existence is
        /// checked separately by the MCP authoring lint (catalog-aware); server-side we only
        /// enforce the shape.
        /// </summary>
        public static bool IsValidTrigger(string? trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                return false;
            // netstandard2.0 target: IsNullOrWhiteSpace carries no nullability attribute there.
            var value = trigger!;
            if (string.Equals(value, EnrollmentEnd, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(value, WhitegloveSealed, StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.StartsWith(OnEventPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var eventType = value.Substring(OnEventPrefix.Length);
                // Same shape the event-type catalog uses: lowercase snake_case identifiers.
                return eventType.Length > 0 && eventType.Length <= 128
                    && eventType.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');
            }
            return false;
        }
    }
}
