import { describe, it, expect } from "vitest";
import { isTemplateRule, partitionAnalyzeRules } from "../analyzeRuleTabs";

const mk = (over: Record<string, unknown>) => over as never;

describe("isTemplateRule", () => {
  it("is true when templateVariables has entries", () => {
    expect(isTemplateRule(mk({ templateVariables: [{ name: "x" }] }))).toBe(true);
  });

  it("is false for empty or missing templateVariables", () => {
    expect(isTemplateRule(mk({ templateVariables: [] }))).toBe(false);
    expect(isTemplateRule(mk({}))).toBe(false);
    expect(isTemplateRule(mk({ templateVariables: undefined }))).toBe(false);
  });
});

describe("partitionAnalyzeRules", () => {
  it("routes templates to templateTab and the rest to ruleTab", () => {
    const builtIn = mk({ ruleId: "A", templateVariables: [] });
    const template = mk({ ruleId: "B", templateVariables: [{ name: "cert" }] });
    // A derived custom copy has templateVariables cleared by the backend
    const derivedCopy = mk({ ruleId: "B-CUSTOM", derivedFromTemplateRuleId: "B", templateVariables: [] });

    const { ruleTab, templateTab } = partitionAnalyzeRules([builtIn, template, derivedCopy]);

    expect(ruleTab.map((r) => (r as { ruleId: string }).ruleId)).toEqual(["A", "B-CUSTOM"]);
    expect(templateTab.map((r) => (r as { ruleId: string }).ruleId)).toEqual(["B"]);
  });

  it("preserves input order within each partition", () => {
    const t1 = mk({ ruleId: "T1", templateVariables: [{ name: "a" }] });
    const r1 = mk({ ruleId: "R1", templateVariables: [] });
    const t2 = mk({ ruleId: "T2", templateVariables: [{ name: "b" }] });

    const { ruleTab, templateTab } = partitionAnalyzeRules([t1, r1, t2]);

    expect(templateTab.map((r) => (r as { ruleId: string }).ruleId)).toEqual(["T1", "T2"]);
    expect(ruleTab.map((r) => (r as { ruleId: string }).ruleId)).toEqual(["R1"]);
  });

  it("handles an empty input", () => {
    expect(partitionAnalyzeRules([])).toEqual({ ruleTab: [], templateTab: [] });
  });
});
