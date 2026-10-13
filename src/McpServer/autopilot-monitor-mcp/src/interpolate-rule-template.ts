/**
 * Rule text rendering for MCP tool output, so tools ship the same human-readable text the web UI
 * renders — never the raw `{{reason}}/{{appName}}/{{errorCode}}` placeholders.
 *
 * `interpolateRuleTemplate` and `RULE_TEMPLATE_AUTO_FIELDS` are the web module
 * (`lib/interpolateRuleTemplate.ts`), copied into src/generated/ by the web's
 * `npm run generate:mcp-logic`; resolution order and the shared cases live there. The same holds
 * for `resolvePresentation` (rule-presentation.generated.ts). This file adds the MCP-only walk over
 * a session-analysis payload.
 */
import { interpolateRuleTemplate } from './generated/interpolate-rule-template.generated.js';
import {
  resolvePresentation,
  type PresentationTemplate,
  type ResolvedPresentation,
} from './generated/rule-presentation.generated.js';

export { interpolateRuleTemplate, RULE_TEMPLATE_AUTO_FIELDS } from './generated/interpolate-rule-template.generated.js';

/** A result's presentation after {@link interpolateAnalysisResults}: its resolved form, or null. */
export function resolvedPresentationOf(result: object): ResolvedPresentation | null {
  const p = (result as { presentation?: unknown }).presentation;
  return p && typeof p === 'object' && Array.isArray((p as { facts?: unknown }).facts) ? (p as ResolvedPresentation) : null;
}

/**
 * Interpolates a session-analysis payload in place: walks `analysis.results[]`
 * and substitutes `{{token}}` placeholders in each result's `explanation` and
 * `remediation[].title` / `remediation[].steps[]` using that result's own
 * `matchedConditions`, and replaces a `presentation` block by its resolved form
 * (facts that were not recorded left out). Tolerant of partial/malformed shapes — anything
 * that isn't a string/array is left untouched. Mutates and returns the same object
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

    // Before the explanation is rewritten: the summary falls back to its first sentence.
    if (result.presentation && typeof result.presentation === 'object') {
      result.presentation = resolvePresentation(
        result.presentation as PresentationTemplate,
        mc,
        typeof result.explanation === 'string' ? result.explanation : null,
        interpolateRuleTemplate,
      );
    }

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
