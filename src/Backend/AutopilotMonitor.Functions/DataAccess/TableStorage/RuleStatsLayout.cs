using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Key layout of the <c>RuleStats</c> table (D-199): PartitionKey <c>{scope}_{yyyy-MM-dd}</c>
    /// where scope is a tenant id or <see cref="GlobalScope"/>, RowKey = ruleId. One partition
    /// per scope and day: the writer folds a whole analysis into one read plus one transaction,
    /// every reader is a PartitionKey range, and tenant offboarding wipes <c>{tenantId}_*</c>
    /// like every other composite-key table.
    /// <para>
    /// Rows written before the cutover use the legacy layout (PartitionKey = date, RowKey =
    /// <c>{scope}_{ruleId}</c>). They age out through the 90-day retention; until
    /// <see cref="LegacyLayoutUntil"/> the readers and the cleanup consult both layouts, after
    /// that date the legacy branches are dead code and can be removed.
    /// </para>
    /// </summary>
    internal static class RuleStatsKeys
    {
        public const string GlobalScope = "global";

        /// <summary>First day on which no legacy row can exist any more (cutover 2026-09-06 + 90 days retention).</summary>
        public static readonly DateTime LegacyLayoutUntil = new(2026, 12, 5, 0, 0, 0, DateTimeKind.Utc);

        public static string PartitionKey(string scope, string date) => $"{scope}_{date}";

        public static string RowKey(string ruleId) => ruleId;

        public static bool LegacyLayoutActive(DateTime utcNow) => utcNow < LegacyLayoutUntil;

        /// <summary>A bare <c>yyyy-MM-dd</c> PartitionKey is a legacy row.</summary>
        public static bool IsLegacyPartitionKey(string? partitionKey)
        {
            if (partitionKey == null || partitionKey.Length != 10) return false;
            for (var i = 0; i < 10; i++)
            {
                var c = partitionKey[i];
                if (i == 4 || i == 7) { if (c != '-') return false; }
                else if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>Scope, date and rule id of a row in either layout.</summary>
        public static (string Scope, string Date, string RuleId) Parse(string partitionKey, string rowKey)
        {
            if (IsLegacyPartitionKey(partitionKey))
            {
                var idx = rowKey.IndexOf('_');
                return idx > 0
                    ? (rowKey.Substring(0, idx), partitionKey, rowKey.Substring(idx + 1))
                    : (rowKey, partitionKey, string.Empty);
            }

            var split = partitionKey.LastIndexOf('_');
            return split > 0
                ? (partitionKey.Substring(0, split), partitionKey.Substring(split + 1), rowKey)
                : (partitionKey, string.Empty, rowKey);
        }
    }

    /// <summary>
    /// The per-rule sum of a batch of <see cref="RuleStatIncrement"/>s: what one transaction
    /// adds to a row. Metadata comes from the last increment of the rule (title/category/
    /// severity are refreshed on every write, as before).
    /// </summary>
    internal sealed record RuleStatDelta(
        string RuleId,
        string RuleType,
        string RuleTitle,
        string Category,
        string Severity,
        int Evaluations,
        int Fires,
        long ConfidenceScoreSum)
    {
        public static List<RuleStatDelta> Merge(IEnumerable<RuleStatIncrement> increments)
        {
            var byRule = new Dictionary<string, RuleStatDelta>(StringComparer.Ordinal);
            foreach (var inc in increments)
            {
                var fires = inc.Fired ? 1 : 0;
                var confidence = inc.Fired && inc.ConfidenceScore.HasValue ? inc.ConfidenceScore.Value : 0L;
                byRule[inc.RuleId] = byRule.TryGetValue(inc.RuleId, out var existing)
                    ? existing with
                    {
                        RuleTitle = inc.RuleTitle,
                        Category = inc.Category,
                        Severity = inc.Severity,
                        Evaluations = existing.Evaluations + 1,
                        Fires = existing.Fires + fires,
                        ConfidenceScoreSum = existing.ConfidenceScoreSum + confidence,
                    }
                    : new RuleStatDelta(inc.RuleId, inc.RuleType, inc.RuleTitle, inc.Category, inc.Severity, 1, fires, confidence);
            }
            return byRule.Values.ToList();
        }
    }
}
