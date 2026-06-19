import { AnalyzeRule } from "@/app/analyze-rules/types";

/**
 * A rule is a "template" when it ships environment-specific placeholders
 * (`templateVariables`) that must be filled in before it can be activated.
 * Enabling a template creates an editable custom copy rather than toggling
 * the template itself on. Templates live in their own UI tab so users are
 * aware they are copy blueprints, not directly-usable rules.
 */
export function isTemplateRule(rule: Pick<AnalyzeRule, "templateVariables">): boolean {
  return (rule.templateVariables?.length ?? 0) > 0;
}

export interface PartitionedAnalyzeRules<T> {
  /** Rules shown in the "Rules" tab: everything except un-instantiated templates. */
  ruleTab: T[];
  /** Rules shown in the "Templates" tab: only template blueprints. */
  templateTab: T[];
}

/**
 * Split a rule set into the "Rules" tab list and the "Templates" tab list.
 * Custom copies derived from a template are NOT templates themselves
 * (the backend clears `templateVariables` on the copy), so they correctly
 * land in the "Rules" tab.
 */
export function partitionAnalyzeRules<T extends Pick<AnalyzeRule, "templateVariables">>(
  rules: T[]
): PartitionedAnalyzeRules<T> {
  const ruleTab: T[] = [];
  const templateTab: T[] = [];
  for (const rule of rules) {
    if (isTemplateRule(rule)) {
      templateTab.push(rule);
    } else {
      ruleTab.push(rule);
    }
  }
  return { ruleTab, templateTab };
}
