import { describe, it, expect } from "vitest";
import type { RuleDryRun, RuleDryRunCondition } from "@/lib/generated/wire-types.generated";
import { confidenceSummary, editorDraft, evidenceText, eventCountText, verdictLabel } from "../ruleDryRunLogic";
import { EMPTY_FORM, EMPTY_CONDITION, type AnalyzeRule, type RuleForm } from "../types";

const base: RuleDryRun = {
  verdict: "fired",
  eventCount: 233,
  preconditions: [],
  conditions: [],
  confidenceFactors: [{ signal: "esp_hresult", condition: "exists", weight: 15, matched: true }],
  baseConfidence: 70,
  finalConfidence: 85,
  confidenceThreshold: 70,
  wouldMarkSessionAsFailed: false,
};

const condition = (patch: Partial<RuleDryRunCondition>): RuleDryRunCondition => ({
  signal: "unmapped_exit_code",
  source: "event_data",
  eventType: "app_install_failed",
  required: true,
  matched: true,
  ...patch,
});

describe("verdictLabel", () => {
  it.each([
    ["fired", "Would fire"],
    ["required_condition_not_met", "A required condition did not match"],
    ["below_confidence_threshold", "Below the confidence threshold"],
    ["some_future_verdict", "some_future_verdict"],
  ])("%s → %s", (verdict, label) => {
    expect(verdictLabel(verdict)).toBe(label);
  });
});

describe("confidenceSummary", () => {
  it("adds the matched factor weights to the base", () => {
    expect(confidenceSummary(base)).toBe("85 (base 70 + 15, threshold 70)");
  });

  it("leaves unmatched factors out", () => {
    expect(confidenceSummary({ ...base, finalConfidence: 70, confidenceFactors: [{ ...base.confidenceFactors[0], matched: false }] }))
      .toBe("70 (base 70, threshold 70)");
  });

  it("says when the evaluation stopped before scoring", () => {
    expect(confidenceSummary({ ...base, finalConfidence: undefined, confidenceFactors: [] })).toBe("not scored (threshold 70)");
  });
});

describe("evidenceText", () => {
  it("shows the matched value of an evidence dictionary", () => {
    expect(evidenceText(condition({ evidence: { field: "message", value: "Admin did NOT set mapping for lpExitCode: 60001" } })))
      .toBe("Admin did NOT set mapping for lpExitCode: 60001");
  });

  it("shows the evaluator's reason for a miss", () => {
    expect(evidenceText(condition({ matched: false, evidence: "no matching data" }))).toBe("no matching data");
  });

  it("falls back to compact JSON and caps long values", () => {
    expect(evidenceText(condition({ evidence: { durationSeconds: 1200 } }))).toBe('{"durationSeconds":1200}');
    const long = evidenceText(condition({ evidence: "x".repeat(500) }));
    expect(long).toHaveLength(240);
    expect(long?.endsWith("…")).toBe(true);
  });

  it("is empty without evidence", () => {
    expect(evidenceText(condition({ evidence: undefined }))).toBeNull();
  });
});

describe("eventCountText", () => {
  it.each([
    [0, "0 events"],
    [1, "1 event"],
    [3, "3 events"],
  ])("%i → %s", (count, text) => {
    expect(eventCountText(condition({ matchingEventCount: count }))).toBe(text);
  });

  it("is empty for a condition without an event type", () => {
    expect(eventCountText(condition({ matchingEventCount: undefined }))).toBeNull();
  });
});

describe("editorDraft", () => {
  const form: RuleForm = {
    ...EMPTY_FORM,
    ruleId: "  ANALYZE-CUSTOM-110 ",
    title: " Unmapped installer exit code stopped the ESP ",
    conditions: [
      { ...EMPTY_CONDITION, signal: "unmapped_exit_code", eventType: "app_install_failed" },
      { ...EMPTY_CONDITION, signal: "" },
    ],
  };

  it("form mode sends the body a save would store, trimmed and without half-filled rows", () => {
    const draft = editorDraft(false, "", form);
    if ("error" in draft) throw new Error(draft.error);
    expect(draft.rule.ruleId).toBe("ANALYZE-CUSTOM-110");
    expect(draft.rule.title).toBe("Unmapped installer exit code stopped the ESP");
    expect((draft.rule.conditions as unknown[]).length).toBe(1);
  });

  it("an empty rule id still yields a testable draft", () => {
    const draft = editorDraft(false, "", { ...form, ruleId: " " });
    expect("rule" in draft && draft.rule.ruleId).toBe("DRAFT");
  });

  it("JSON mode reads the text, not the stale form", () => {
    const draft = editorDraft(true, JSON.stringify({ ruleId: "X-1", title: "From JSON", conditions: [] }), form);
    if ("error" in draft) throw new Error(draft.error);
    expect(draft.rule.title).toBe("From JSON");
  });

  it("JSON that does not parse is reported, never sent", () => {
    const draft = editorDraft(true, "{ not json", form);
    expect("error" in draft && draft.error.startsWith("Fix the JSON first:")).toBe(true);
  });

  it("an edit keeps the stored rule's other fields underneath the form's", () => {
    const stored = { ruleId: "ANALYZE-CUSTOM-110", title: "Old title", version: "1.0.3", author: "Contoso IT" } as unknown as AnalyzeRule;
    const draft = editorDraft(false, "", form, stored);
    if ("error" in draft) throw new Error(draft.error);
    expect(draft.rule.version).toBe("1.0.3");
    expect(draft.rule.author).toBe("Contoso IT");
    expect(draft.rule.title).toBe("Unmapped installer exit code stopped the ESP");
  });
});
