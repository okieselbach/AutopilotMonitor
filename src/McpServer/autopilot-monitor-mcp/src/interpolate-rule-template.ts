/**
 * MCP-side mirror of the web's `lib/interpolateRuleTemplate.ts`. Replaces
 * `{{token}}` placeholders in a rule's explanation/remediation text with values
 * pulled from the rule result's `matchedConditions` evidence map, so MCP tool
 * output ships the same human-readable text the web UI renders — never the raw
 * `{{reason}}/{{appName}}/{{errorCode}}` placeholders.
 *
 * Keep the resolution order in lock-step with the web copy:
 *   1. matchedConditions entry whose `field` equals `token`.
 *   2. matchedConditions entry carrying a whitelisted same-event auto-field
 *      named `token` (`RULE_TEMPLATE_AUTO_FIELDS`). First non-empty wins so the
 *      value pins to the rule's required-condition event.
 *   3. matchedConditions entry whose key (signal name) equals `token`.
 *   4. Unresolved tokens are left untouched so authors notice the typo (and the
 *      reworded ANALYZE-ENRL-001 footnote explains a genuinely-absent field).
 *
 * Token chars: [a-zA-Z0-9_]. Whitespace inside the braces is tolerated.
 *
 * Web, MCP and the backend (notifications) all run the cases in
 * `tests/fixtures/rule-template-interpolation/cases.json` at the repo root.
 */

/**
 * Mirror of the backend evidence auto-capture whitelist (`RuleEngine.EvidenceAutoFields`).
 * Explicit list keeps a rogue evidence key (e.g. accidental `description`) from shadowing
 * a token. Also the list validate_rule lints tokens against.
 */
export const RULE_TEMPLATE_AUTO_FIELDS = [
  'appId',
  'appName',
  'errorPatternId',
  'errorCode',
  'exitCode',
  'status',
  'timeoutHours',
] as const;

export function interpolateRuleTemplate(
  text: string | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined
): string {
  if (!text) return text ?? '';
  if (!matchedConditions || typeof matchedConditions !== 'object') return text;

  const byField = new Map<string, string>();
  const byAutoField = new Map<string, string>();
  const bySignal = new Map<string, string>();

  for (const [signal, evidence] of Object.entries(matchedConditions)) {
    if (!evidence || typeof evidence !== 'object') {
      if (evidence != null) bySignal.set(signal, formatValue(evidence));
      continue;
    }
    const e = evidence as Record<string, unknown>;
    if (typeof e.field === 'string' && 'value' in e) {
      byField.set(e.field, formatValue(e.value));
    }
    if ('value' in e) {
      bySignal.set(signal, formatValue(e.value));
    }
    for (const af of RULE_TEMPLATE_AUTO_FIELDS) {
      if (byAutoField.has(af)) continue;
      const v = e[af];
      if (v != null && v !== '') byAutoField.set(af, formatValue(v));
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
  if (v == null) return '';
  if (typeof v === 'string') return v;
  if (typeof v === 'number' || typeof v === 'boolean') return String(v);
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}

/**
 * Interpolates a session-analysis payload in place: walks `analysis.results[]`
 * and substitutes `{{token}}` placeholders in each result's `explanation` and
 * `remediation[].title` / `remediation[].steps[]` using that result's own
 * `matchedConditions`. Tolerant of partial/malformed shapes — anything that
 * isn't a string/array is left untouched. Mutates and returns the same object
 * (the caller's `analysisData` is a fresh, throwaway fetch result).
 */
export function interpolateAnalysisResults<T>(analysis: T): T {
  if (!analysis || typeof analysis !== 'object') return analysis;
  const results = (analysis as { results?: unknown }).results;
  if (!Array.isArray(results)) return analysis;

  for (const r of results) {
    if (!r || typeof r !== 'object') continue;
    const result = r as Record<string, unknown>;
    const mc = result.matchedConditions as Record<string, unknown> | null | undefined;

    if (typeof result.explanation === 'string') {
      result.explanation = interpolateRuleTemplate(result.explanation, mc);
    }

    if (Array.isArray(result.remediation)) {
      for (const rem of result.remediation) {
        if (!rem || typeof rem !== 'object') continue;
        const r2 = rem as Record<string, unknown>;
        if (typeof r2.title === 'string') {
          r2.title = interpolateRuleTemplate(r2.title, mc);
        }
        if (Array.isArray(r2.steps)) {
          r2.steps = r2.steps.map((s) => (typeof s === 'string' ? interpolateRuleTemplate(s, mc) : s));
        }
      }
    }
  }

  return analysis;
}
