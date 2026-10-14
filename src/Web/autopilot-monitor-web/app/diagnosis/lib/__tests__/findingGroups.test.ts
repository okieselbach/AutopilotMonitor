import { describe, it, expect } from "vitest";
import { makeRuleResult } from "@/test/factories";
import { confidenceWord, groupFindings } from "../findingGroups";
import { contextItems } from "../contextItems";

const app = { kind: "app", key: "00000000-0000-4000-8000-0000000000a1", name: "{{failed.appName}}" };

const results = [
  makeRuleResult({ ruleId: "ANALYZE-ENRL-001", severity: "critical", confidenceScore: 100, presentation: { role: "outcome", summary: "Enrollment failed" } }),
  makeRuleResult({ ruleId: "ANALYZE-APP-008", severity: "warning", confidenceScore: 70, presentation: { role: "cause", summary: "Slow install" } }),
  makeRuleResult({
    ruleId: "ANALYZE-APP-006",
    severity: "high",
    confidenceScore: 75,
    presentation: { role: "cause", summary: "{{failed.appName}} was started {{retry.count}} times", subject: app, facts: [{ label: "Install starts", value: "{{retry.count}}", format: "number" }] },
    matchedConditions: { failed: { appName: "Contoso Notes" }, retry: { count: 4 } },
  }),
  makeRuleResult({
    ruleId: "ANALYZE-DEV-012",
    severity: "info",
    presentation: { role: "context", summary: "The device went to sleep during setup", subject: { kind: "device", key: "power", name: "Power" }, facts: [{ label: "Asleep for", value: "{{s.value}}", format: "duration" }] },
    matchedConditions: { s: { field: "durationSeconds", value: "274" } },
  }),
  makeRuleResult({ ruleId: "ANALYZE-ID-002", severity: "high", presentation: { role: "advisory" } }),
  makeRuleResult({ ruleId: "ANALYZE-CUSTOM-001", severity: "critical" }),
  makeRuleResult({ ruleId: "ANALYZE-OLD-001", resolvedAt: "2026-10-13T15:00:00Z", presentation: { role: "cause" } }),
];

describe("groupFindings", () => {
  const culprits = [{ kind: "app", evidence: "install_failed", key: "00000000-0000-4000-8000-0000000000a1", name: "Contoso Notes" }];
  const groups = groupFindings(results, "failed", culprits);

  it("sorts open findings by role; resolved ones are gone", () => {
    expect(groups.outcome.map((f) => f.result.ruleId)).toEqual(["ANALYZE-ENRL-001"]);
    expect(groups.context.map((f) => f.result.ruleId)).toEqual(["ANALYZE-DEV-012"]);
    // Advisory and rules without a presentation (tenant custom rules) land under "other".
    expect(groups.other.map((f) => f.result.ruleId)).toEqual(["ANALYZE-CUSTOM-001", "ANALYZE-ID-002"]);
  });

  it("labels the primary group for the status and puts the culprit's findings first", () => {
    expect(groups.primary.label).toBe("Caused the failure");
    expect(groups.primary.items.map((f) => f.result.ruleId)).toEqual(["ANALYZE-APP-006", "ANALYZE-APP-008"]);
    expect(groups.primary.items[0].summary).toBe("Contoso Notes was started 4 times");
    expect(groups.primary.items[0].about).toBe("Contoso Notes");
    expect(groups.primary.tone).toBe("red");
    expect(groupFindings(results, "stalled", []).primary).toMatchObject({ label: "Likely holding it up", tone: "orange" });
    expect(groupFindings(results, "succeeded", []).primary).toMatchObject({ label: "Worth a look", tone: "amber" });
  });

  it("names what a finding is about", () => {
    expect(groups.outcome[0].about).toBe("This enrollment");
    expect(groups.other[0].about).toBe("Apps");
  });
});

describe("contextItems", () => {
  it("turns context findings and caveats into circumstances", () => {
    const groups = groupFindings(results, "failed", []);
    expect(contextItems(groups.context, ["agent_started_late", "unknown_caveat"])).toEqual([
      { key: "ANALYZE-DEV-012", icon: "power", title: "The device went to sleep during setup", detail: "Asleep for 4m 34s", ruleId: "ANALYZE-DEV-012" },
      { key: "caveat:agent_started_late", icon: "eye", title: "Start not observed", detail: "The agent started late, so the first part of the enrollment was not watched." },
    ]);
  });
});

describe("confidenceWord", () => {
  it.each([
    [100, "certain"],
    [90, "very likely"],
    [70, "likely"],
    [55, "possible"],
    [30, "weak"],
  ])("%d → %s", (score, word) => expect(confidenceWord(score)).toBe(word));
});
