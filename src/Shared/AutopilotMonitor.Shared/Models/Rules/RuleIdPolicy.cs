using System.Text.RegularExpressions;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Naming policy for rule identifiers. The numeric built-in namespace
    /// (<c>ANALYZE-&lt;CATEGORY&gt;-&lt;NUMBER&gt;</c> / <c>GATHER-&lt;CATEGORY&gt;-&lt;NUMBER&gt;</c>)
    /// is reserved for rules shipped with the platform. Tenant custom rules must
    /// never occupy it — including IDs that are currently unused: gaps in the
    /// sequence are usually retired rules, and a future built-in re-using a
    /// squatted ID would silently shadow the tenant's custom rule at merge time
    /// (the global rule wins and the tenant copy is skipped).
    /// </summary>
    public static class RuleIdPolicy
    {
        // Case-insensitive on purpose: "analyze-sec-002" would not collide on the
        // ordinal RowKey, but it is indistinguishable to humans and would defeat
        // the reservation in every UI. Template copies ("...-001-CUSTOM") do not
        // match because the ID must END in the number. The CUSTOM category is the
        // sanctioned tenant namespace (e.g. "ANALYZE-CUSTOM-001", the pattern the
        // portal suggests) — the platform commits to never shipping a built-in
        // rule in a category named CUSTOM.
        private static readonly Regex ReservedBuiltInPattern = new Regex(
            @"^(ANALYZE|GATHER)-(?!CUSTOM-)[A-Z]+-\d+$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when <paramref name="ruleId"/> lies in the reserved built-in
        /// namespace and therefore must not be used for a tenant custom rule.
        /// </summary>
        public static bool IsReservedBuiltInId(string ruleId)
            => !string.IsNullOrEmpty(ruleId) && ReservedBuiltInPattern.IsMatch(ruleId);

        /// <summary>Human-readable rejection message for creates that violate the policy.</summary>
        public static string ReservedMessage(string ruleId)
            => $"Rule ID '{ruleId}' matches the reserved built-in naming scheme " +
               "(ANALYZE|GATHER)-<CATEGORY>-<NUMBER>, which is used by rules shipped with the platform " +
               "(including retired IDs that may return). Use the CUSTOM category (e.g. 'ANALYZE-CUSTOM-001'), " +
               "a '-CUSTOM' suffix, or an organization prefix like 'CONTOSO-WIFI-001' instead.";
    }
}
