import type { FindingView } from "./findingGroups";
import { formatFact } from "./diagnosisFormat";
import { vocabularyEntry, type Caveat } from "./vocabulary";

export type ContextIcon = "power" | "update" | "clock" | "memory" | "restart" | "eye" | "info";

/** One circumstance around the outcome: a context finding, or a limit of the observation. */
export interface ContextItem {
  key: string;
  icon: ContextIcon;
  title: string;
  detail: string | null;
  /** The finding behind it, when there is one. */
  ruleId?: string;
}

const CAVEATS: Record<Caveat, { icon: ContextIcon; title: string; detail: string }> = {
  agent_started_late: {
    icon: "eye",
    title: "Start not observed",
    detail: "The agent started late, so the first part of the enrollment was not watched.",
  },
  agent_crashed: {
    icon: "eye",
    title: "An agent run crashed",
    detail: "Events of that run may be missing.",
  },
  verdict_derived: {
    icon: "info",
    title: "Older session",
    detail: "This session predates the recorded verdict path; the verdict was read from its stored reason.",
  },
  blocking_list_truncated: {
    icon: "info",
    title: "ESP app list capped",
    detail: "The ESP lists at most 50 apps per type, so an app missing from it may still have blocked.",
  },
};

/**
 * "Around the outcome": the context findings (sleep, forced restarts, battery, updates — the rules
 * that detect them carry their summary and facts) followed by the observation caveats of the verdict.
 */
export function contextItems(context: FindingView[], caveats: string[] | null | undefined): ContextItem[] {
  const items: ContextItem[] = context.map((f) => ({
    key: f.result.ruleId,
    icon: iconFor(f),
    title: f.summary ?? f.result.ruleTitle,
    detail: factLine(f),
    ruleId: f.result.ruleId,
  }));
  for (const caveat of caveats ?? []) {
    const known = vocabularyEntry(CAVEATS, caveat);
    if (known) items.push({ key: `caveat:${caveat}`, ...known });
  }
  return items;
}

/** "Label value · Label value" of a finding's facts, values in their formats. */
export function factLine(f: FindingView): string | null {
  const facts = f.presentation?.facts ?? [];
  if (facts.length === 0) return null;
  return facts.map((fact) => `${fact.label} ${formatFact(fact.value, fact.format).text}`).join(" · ");
}

function iconFor(f: FindingView): ContextIcon {
  const subject = f.presentation?.subject;
  switch (subject?.key) {
    case "power":
      return "power";
    case "windows-update":
    case "oobe-update":
    case "os":
      return "update";
    case "clock":
      return "clock";
    case "memory":
      return "memory";
  }
  return subject?.kind === "policy" || subject?.kind === "app" ? "restart" : "info";
}
