import type { RuleDryRun, RuleDryRunCondition } from "@/lib/generated/wire-types.generated";
import { formToDryRunRule, jsonToForm, type AnalyzeRule, type PastedAnalyzeJson, type RuleForm } from "./types";

/** The editor's current draft, or why it cannot be tested yet (JSON that does not parse). */
export type DryRunDraft = { rule: Record<string, unknown> } | { error: string };

/**
 * The draft a dry-run sends, read from whichever mode the editor is in — the same way Save reads it: JSON
 * mode parses the text (merged onto the form when editing), and an edit keeps the stored rule's other
 * fields underneath the form's.
 */
export function editorDraft(jsonMode: boolean, jsonText: string, form: RuleForm, stored?: AnalyzeRule): DryRunDraft {
  let source = form;
  if (jsonMode) {
    try {
      const parsed = JSON.parse(jsonText) as PastedAnalyzeJson;
      source = jsonToForm(stored ? { ...form, ...parsed } : parsed);
    } catch (e) {
      return { error: `Fix the JSON first: ${e instanceof Error ? e.message : "it does not parse"}.` };
    }
  }
  return { rule: { ...(stored ?? {}), ...formToDryRunRule(source) } };
}

/** Plain words for the dry-run verdicts (RuleDryRunVerdict on the backend); an unknown value shows as is. */
const VERDICT_LABELS: Record<string, string> = {
  fired: "Would fire",
  skipped_by_precondition: "Skipped by a precondition",
  required_condition_not_met: "A required condition did not match",
  no_conditions_matched: "No condition matched",
  below_confidence_threshold: "Below the confidence threshold",
  no_events: "The session has no events",
};

export function verdictLabel(verdict: string): string {
  return VERDICT_LABELS[verdict] ?? verdict;
}

/**
 * The confidence line: "85 (base 70 + 15, threshold 70)", or why there is no score — the engine stops
 * before the confidence stage when a precondition or a required condition fails.
 */
export function confidenceSummary(result: RuleDryRun): string {
  if (result.finalConfidence == null) return `not scored (threshold ${result.confidenceThreshold})`;
  const bonus = result.confidenceFactors.filter(f => f.matched).reduce((sum, f) => sum + f.weight, 0);
  const parts = bonus > 0 ? `base ${result.baseConfidence} + ${bonus}` : `base ${result.baseConfidence}`;
  return `${result.finalConfidence} (${parts}, threshold ${result.confidenceThreshold})`;
}

const MAX_EVIDENCE_CHARS = 240;

/**
 * One line of evidence for a condition: the matched value, or the evaluator's reason when it missed
 * (the wire carries a dictionary for a match and a string for a miss).
 */
export function evidenceText(condition: RuleDryRunCondition): string | null {
  const evidence = condition.evidence;
  if (evidence == null) return null;
  let text: string;
  if (typeof evidence === "string") {
    text = evidence;
  } else if (typeof evidence === "object" && "value" in evidence && (evidence as { value: unknown }).value != null) {
    const value = (evidence as { value: unknown }).value;
    text = typeof value === "string" ? value : JSON.stringify(value);
  } else {
    text = JSON.stringify(evidence);
  }
  return text.length > MAX_EVIDENCE_CHARS ? `${text.slice(0, MAX_EVIDENCE_CHARS - 1)}…` : text;
}

/** "1 event" / "0 events"; null when the condition names no event type. */
export function eventCountText(condition: RuleDryRunCondition): string | null {
  if (condition.matchingEventCount == null) return null;
  return `${condition.matchingEventCount} ${condition.matchingEventCount === 1 ? "event" : "events"}`;
}
