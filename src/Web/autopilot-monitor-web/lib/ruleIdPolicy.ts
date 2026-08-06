// Mirror of AutopilotMonitor.Shared.Models.RuleIdPolicy — keep the pattern in sync.
// The numeric built-in namespace (ANALYZE|GATHER)-<CATEGORY>-<NUMBER> is reserved for
// rules shipped with the platform: gaps in the sequence are usually retired rules, and
// a future built-in re-using a squatted ID would silently shadow the tenant's custom
// rule at merge time. The CUSTOM category (e.g. ANALYZE-CUSTOM-001) is the sanctioned
// tenant namespace; the backend rejects reserved IDs on create/update with 409.
const RESERVED_BUILTIN_PATTERN = /^(ANALYZE|GATHER)-(?!CUSTOM-)[A-Z]+-\d+$/i;

export function isReservedBuiltInRuleId(ruleId: string): boolean {
  return RESERVED_BUILTIN_PATTERN.test(ruleId.trim());
}

export const RESERVED_RULE_ID_MESSAGE =
  "This ID matches the reserved built-in naming scheme (ANALYZE|GATHER)-<CATEGORY>-<NUMBER>. " +
  "Use the CUSTOM category (e.g. ANALYZE-CUSTOM-001), a -CUSTOM suffix, or your own prefix (e.g. CONTOSO-WIFI-001).";
