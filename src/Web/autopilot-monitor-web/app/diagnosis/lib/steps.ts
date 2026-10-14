import { inlinePlain, ruleStepInlines, ruleTextBlocks, ruleTextPlain, type RuleBlock, type RuleInline } from "@/lib/ruleText";
import type { DiagnosisHeadline } from "@/lib/generated/wire-types.generated";
import type { RuleResult } from "@/types";
import type { FindingGroups, FindingView } from "./findingGroups";
import type { VerdictText } from "./verdictText";
import { stepPath } from "./diagnosisFormat";

export interface StepGroup {
  title: RuleBlock[];
  steps: RuleInline[][];
}

export interface StepsView {
  /** The finding whose remediation these are. */
  source: FindingView;
  groups: StepGroup[];
}

/**
 * A finding's remediation filled in with its session's values. A step naming a value the session did
 * not record is dropped; a group left without steps goes with it.
 */
export function remediationGroups(result: RuleResult): StepGroup[] {
  return (result.remediation ?? [])
    .map((group) => ({
      title: ruleTextBlocks(group.title, result.matchedConditions),
      steps: group.steps.map((step) => ruleStepInlines(step, result.matchedConditions)).filter((s): s is RuleInline[] => s !== null),
    }))
    .filter((g) => g.steps.length > 0);
}

/**
 * What to do: the remediation of the strongest finding that explains the outcome (or, without one,
 * of the outcome finding) that keeps a step once filled in.
 */
export function stepsFrom(groups: FindingGroups): StepsView | null {
  for (const source of [...groups.primary.items, ...groups.outcome]) {
    const stepGroups = remediationGroups(source.result);
    if (stepGroups.length > 0) return { source, groups: stepGroups };
  }
  return null;
}

/** The verdict as plain text for a ticket or a chat. */
export function copySummary(args: { verdict: VerdictText; headline: DiagnosisHeadline; device?: string | null; steps: StepsView | null; url?: string }): string {
  const { verdict, headline, steps } = args;
  const lines: string[] = [verdict.title + (args.device ? ` — ${args.device}` : ""), verdict.summary, ...verdict.details];

  const step = stepPath(headline.failedStep?.category, headline.failedStep?.subcategory);
  if (step) lines.push(`ESP step: ${step}${headline.failedStep?.errorCode ? ` (${headline.failedStep.errorCode})` : ""}`);
  for (const culprit of headline.culprits ?? []) {
    const facts = [
      culprit.errorCode && `error ${culprit.errorCode}`,
      culprit.installStarts && culprit.installStarts > 1 && `${culprit.installStarts} install starts`,
      culprit.detectionResult === "NotDetected" && "not detected",
    ].filter(Boolean);
    lines.push(`${culprit.kind === "app" ? "App" : culprit.kind === "appx_package" ? "Package" : "Policy provider"}: ${culprit.name}${facts.length ? ` — ${facts.join(", ")}` : ""}`);
  }

  if (steps) lines.push("", "What to do:", stepsPlain(steps));
  if (args.url) lines.push("", args.url);
  return lines.join("\n");
}

/** The steps as plain text, numbered across the groups. */
export function stepsPlain(steps: StepsView): string {
  const lines: string[] = [];
  let n = 0;
  for (const group of steps.groups) {
    const title = ruleTextPlain(group.title);
    if (title) lines.push(title);
    for (const s of group.steps) lines.push(`${++n}. ${inlinePlain(s)}`);
  }
  return lines.join("\n");
}
