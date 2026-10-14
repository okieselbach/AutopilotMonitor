import type { RuleResult } from "@/types";
import type { DiagnosisCulprit } from "@/lib/generated/wire-types.generated";
import { resolvePresentation, type ResolvedPresentation } from "@/lib/rulePresentation";
import { interpolateRuleTemplate } from "@/lib/interpolateRuleTemplate";
import { humanizeKey } from "@/lib/evidenceChips";

/** One open finding as the page shows it. */
export interface FindingView {
  result: RuleResult;
  presentation: ResolvedPresentation | null;
  /** outcome | cause | context | advisory; empty for a rule without a presentation (tenant custom rules). */
  role: string;
  /** One line under the title: the presentation's summary, null when the rule has none. */
  summary: string | null;
  /** What it is about: the subject, else "This enrollment" for an outcome, else the rule category. */
  about: string;
  /** The confidence score in words. */
  confidence: string;
}

export interface FindingGroups {
  /** Findings that restate the outcome — the verdict counts them ("based on N findings"). */
  outcome: FindingView[];
  /** The findings that explain the outcome, labelled and toned for the status. */
  primary: { label: string; tone: PrimaryTone; items: FindingView[] };
  /** Circumstances (sleep, restarts, battery, updates). */
  context: FindingView[];
  /** Hygiene and security, and rules without a presentation. */
  other: FindingView[];
}

const SEVERITY_RANK: Record<string, number> = { critical: 0, high: 1, warning: 2, info: 3 };

export function confidenceWord(score: number): string {
  if (score >= 95) return "certain";
  if (score >= 85) return "very likely";
  if (score >= 70) return "likely";
  if (score >= 50) return "possible";
  return "weak";
}

/** Red for a failure, amber for a finding on a finished enrollment, orange for what holds one up. */
export type PrimaryTone = "red" | "orange" | "amber";

function primaryGroup(outcome: string): { label: string; tone: PrimaryTone } {
  switch (outcome) {
    case "failed":
      return { label: "Caused the failure", tone: "red" };
    case "succeeded":
      return { label: "Worth a look", tone: "amber" };
    default:
      return { label: "Likely holding it up", tone: "orange" };
  }
}

export function findingView(result: RuleResult): FindingView {
  const presentation = resolvePresentation(result.presentation, result.matchedConditions, result.explanation, interpolateRuleTemplate);
  const role = presentation?.role ?? "";
  return {
    result,
    presentation,
    role,
    summary: presentation?.summary ?? null,
    about: presentation?.subject?.name ?? (role === "outcome" ? "This enrollment" : humanizeKey(result.category)),
    confidence: confidenceWord(result.confidenceScore),
  };
}

/**
 * Open findings by role. Within a group a finding about one of the headline's culprits comes first
 * (the app the verdict names), then by severity, then by confidence.
 */
export function groupFindings(results: RuleResult[], outcome: string, culprits: DiagnosisCulprit[] | undefined): FindingGroups {
  const aboutCulprit = (f: FindingView) => (culprits ?? []).some((c) => isAboutCulprit(f, c));
  const order = (a: FindingView, b: FindingView) =>
    Number(aboutCulprit(b)) - Number(aboutCulprit(a)) ||
    (SEVERITY_RANK[a.result.severity] ?? 4) - (SEVERITY_RANK[b.result.severity] ?? 4) ||
    b.result.confidenceScore - a.result.confidenceScore;

  const open = results.filter((r) => !r.resolvedAt).map(findingView);
  const ofRole = (role: string) => open.filter((f) => f.role === role).sort(order);
  return {
    outcome: ofRole("outcome"),
    primary: { ...primaryGroup(outcome), items: ofRole("cause") },
    context: ofRole("context"),
    other: open.filter((f) => f.role !== "outcome" && f.role !== "cause" && f.role !== "context").sort(order),
  };
}

/** Whether a finding is about the given culprit (its subject names the culprit). */
export function isAboutCulprit(finding: FindingView, culprit: DiagnosisCulprit | undefined): boolean {
  const subject = finding.presentation?.subject;
  if (!subject || !culprit) return false;
  const keys = [culprit.key, culprit.name].filter((k): k is string => !!k).map((k) => k.toLowerCase());
  return keys.includes(subject.key.toLowerCase()) || keys.includes(subject.name.toLowerCase());
}
