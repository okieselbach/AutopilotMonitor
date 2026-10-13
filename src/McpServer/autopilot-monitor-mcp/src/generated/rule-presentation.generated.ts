// GENERATED — do not edit by hand. Copy of a web module for the MCP server.
// Source: src/Web/autopilot-monitor-web/lib/rulePresentation.ts
// Regenerate: npm run generate:mcp-logic in src/Web/autopilot-monitor-web.

/**
 * Resolves an analyze rule's presentation block (role, summary, subject, key facts) against one
 * finding's evidence. Like the explanation, a stored finding keeps the raw `{{token}}` templates
 * and every renderer resolves them; this module is the one resolution of the presentation, pinned
 * by `tests/fixtures/rule-presentation/cases.json` at the repo root:
 *   - a fact whose value does not resolve (token not recorded, or empty) is left out;
 *   - the summary falls back to the first sentence of the explanation;
 *   - a subject whose key does not resolve is no subject; a missing name shows the key;
 *   - an absent or unknown fact format reads as "text".
 *
 * Self-contained on purpose: the MCP server runs a generated copy (npm run generate:mcp-logic),
 * so the interpolation is passed in instead of imported.
 */

export interface PresentationTemplate {
  role: string;
  summary?: string | null;
  subject?: { kind: string; key: string; name?: string | null } | null;
  facts?: { label: string; value: string; format?: string | null }[] | null;
}

export interface ResolvedFact {
  label: string;
  value: string;
  format: string;
}

export interface ResolvedPresentation {
  role: string;
  summary: string | null;
  subject: { kind: string; key: string; name: string } | null;
  facts: ResolvedFact[];
}

type Interpolate = (text: string, matchedConditions: Record<string, unknown> | null | undefined) => string;

const FACT_FORMATS = ["text", "code", "errorCode", "duration", "number", "timestamp"];
const LEFTOVER_TOKEN = /\{\{\s*[a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)?\s*\}\}/;

export function resolvePresentation(
  presentation: PresentationTemplate | null | undefined,
  matchedConditions: Record<string, unknown> | null | undefined,
  explanation: string | null | undefined,
  interpolate: Interpolate
): ResolvedPresentation | null {
  if (!presentation || typeof presentation.role !== "string" || presentation.role === "") return null;

  const resolve = (template: string | null | undefined): string | null => {
    if (!template) return null;
    const out = interpolate(template, matchedConditions).trim();
    return out === "" || LEFTOVER_TOKEN.test(out) ? null : out;
  };

  const facts: ResolvedFact[] = [];
  for (const fact of presentation.facts ?? []) {
    if (!fact || !fact.label) continue;
    const value = resolve(fact.value);
    if (value === null) continue;
    const format = fact.format && FACT_FORMATS.includes(fact.format) ? fact.format : "text";
    facts.push({ label: fact.label, value, format });
  }

  let subject: ResolvedPresentation["subject"] = null;
  const key = resolve(presentation.subject?.key);
  if (presentation.subject && key !== null) {
    subject = { kind: presentation.subject.kind, key, name: resolve(presentation.subject.name) ?? key };
  }

  const summary = resolve(presentation.summary) ?? resolve(firstSentence(explanation));
  return { role: presentation.role, summary, subject, facts };
}

/** First sentence of the explanation's first paragraph, without markdown markers. */
function firstSentence(explanation: string | null | undefined): string | null {
  if (!explanation) return null;
  const paragraph = explanation.replace(/\r\n/g, "\n").split(/\n\s*\n/)[0] ?? "";
  const plain = paragraph
    .replace(/^\s*(?:[-*+]|\d+[.)])\s+/gm, "")
    .replace(/\*\*|__|`/g, "")
    .replace(/\s+/g, " ")
    .trim();
  const end = plain.search(/[.!?](?:\s|$)/);
  return end === -1 ? plain : plain.slice(0, end + 1);
}
