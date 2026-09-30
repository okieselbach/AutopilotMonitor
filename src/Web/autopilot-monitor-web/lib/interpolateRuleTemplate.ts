/**
 * Replaces `{{token}}` placeholders in a rule's explanation/remediation text
 * with values pulled from the rule's `matchedConditions` evidence map.
 *
 * Resolution order for `{{token}}`:
 *   1. A matchedConditions entry whose `field` equals `token`
 *      (e.g. {{uefiCA2023Status}} -> entry where field === "uefiCA2023Status");
 *      when several entries name the same field, the last one wins.
 *   2. The first matchedConditions entry that carries a same-event auto-field
 *      named `token` (added by the backend's
 *      `RuleEngine.ConditionEvaluators.AddDataFieldsToEvidence` whitelist —
 *      `RULE_TEMPLATE_AUTO_FIELDS`). Using "first" pins these to the same source event as the
 *      rule's required condition, avoiding cross-event interpolation drift
 *      when a session has multiple `app_install_failed` events.
 *   3. A matchedConditions entry whose key (signal name) equals `token`
 *      (e.g. {{ca2023_not_updated}} -> that signal's value).
 *   4. Unresolved tokens are left untouched so authors notice the typo.
 *
 * Token chars: [a-zA-Z0-9_]. Whitespace inside the braces is tolerated.
 *
 * The MCP server and the backend (notifications) carry the same resolution; all three run
 * the cases in `tests/fixtures/rule-template-interpolation/cases.json` at the repo root.
 */

/**
 * Whitelist of auto-injected event fields the backend rule engine adds to every
 * matched-condition evidence dict (`RuleEngine.EvidenceAutoFields`). Kept explicit so a
 * rogue evidence key (e.g. accidental `description`) can't shadow a template token from
 * another resolution path.
 */
export const RULE_TEMPLATE_AUTO_FIELDS = [
  "appId",
  "appName",
  "errorPatternId",
  "errorCode",
  "exitCode",
  "status",
  "timeoutHours",
] as const;

export function interpolateRuleTemplate(
  text: string | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined
): string {
  if (!text) return text ?? "";
  if (!matchedConditions || typeof matchedConditions !== "object") return text;

  const byField = new Map<string, string>();
  const byAutoField = new Map<string, string>();
  const bySignal = new Map<string, string>();

  for (const [signal, evidence] of Object.entries(matchedConditions)) {
    if (!evidence || typeof evidence !== "object") {
      if (evidence != null) bySignal.set(signal, formatValue(evidence));
      continue;
    }
    const e = evidence as Record<string, unknown>;
    if (typeof e.field === "string" && "value" in e) {
      byField.set(e.field, formatValue(e.value));
    }
    if ("value" in e) {
      bySignal.set(signal, formatValue(e.value));
    }
    // Auto-field fallback: first non-empty wins (insertion order of
    // matchedConditions mirrors rule.conditions order, so the required
    // condition's event is checked first).
    for (const af of RULE_TEMPLATE_AUTO_FIELDS) {
      if (byAutoField.has(af)) continue;
      const v = e[af];
      if (v != null && v !== "") byAutoField.set(af, formatValue(v));
    }
  }

  return text.replace(/\{\{\s*([a-zA-Z0-9_]+)\s*\}\}/g, (raw, token: string) => {
    if (byField.has(token)) return byField.get(token)!;
    if (byAutoField.has(token)) return byAutoField.get(token)!;
    if (bySignal.has(token)) return bySignal.get(token)!;
    return raw;
  });
}

function formatValue(v: unknown): string {
  if (v == null) return "";
  if (typeof v === "string") return v;
  if (typeof v === "number" || typeof v === "boolean") return String(v);
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}
