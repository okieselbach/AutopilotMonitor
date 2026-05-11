import { describe, it, expect } from "vitest";
import {
  buildScriptItemLabel,
  isDetectOnlyRow,
  isNonCompliantReport,
  mapRemediationStatus,
  reduceScriptEvents,
  toNumber,
  type ScriptInputEvent,
  type ScriptItem,
} from "../scriptExecutions";

const ts = (offsetSeconds: number) => new Date(1700000000000 + offsetSeconds * 1000).toISOString();

const finalEvent = (overrides: Partial<{ eventType: string; ts: number; data: Record<string, any> }> = {}): ScriptInputEvent => ({
  timestamp: ts(overrides.ts ?? 0),
  eventType: overrides.eventType ?? "script_completed",
  data: overrides.data ?? {},
});

describe("toNumber", () => {
  it("returns numbers as-is", () => {
    expect(toNumber(0)).toBe(0);
    expect(toNumber(42)).toBe(42);
    expect(toNumber(-1)).toBe(-1);
  });
  it("coerces numeric strings (V2 wire format)", () => {
    expect(toNumber("0")).toBe(0);
    expect(toNumber("4")).toBe(4);
    expect(toNumber("-1")).toBe(-1);
  });
  it("returns undefined for null / undefined / empty / non-numeric", () => {
    expect(toNumber(null)).toBeUndefined();
    expect(toNumber(undefined)).toBeUndefined();
    expect(toNumber("")).toBeUndefined();
    expect(toNumber("abc")).toBeUndefined();
    expect(toNumber(NaN)).toBeUndefined();
  });
});

describe("mapRemediationStatus", () => {
  it("maps known IME RemediationStatus codes to labels", () => {
    expect(mapRemediationStatus(0)).toBe("Unknown");
    expect(mapRemediationStatus(1)).toBe("Compliant");
    expect(mapRemediationStatus(2)).toBe("Remediated");
    expect(mapRemediationStatus(3)).toBe("RemediationFailed");
    expect(mapRemediationStatus(4)).toBe("NoRemediation");
  });

  it("returns null for undefined or unknown codes", () => {
    expect(mapRemediationStatus(undefined)).toBeNull();
    expect(mapRemediationStatus(99)).toBeNull();
  });
});

describe("buildScriptItemLabel", () => {
  const base = (overrides: Partial<ScriptItem> = {}): Pick<ScriptItem, "scriptType" | "scriptPart" | "state" | "remediationStatus"> => ({
    scriptType: "remediation",
    scriptPart: "detection",
    state: "Success",
    remediationStatus: 4,
    ...overrides,
  });

  it("Health Script (running) for live remediation placeholder", () => {
    expect(buildScriptItemLabel(base({ state: "Running", scriptPart: undefined }))).toBe("Health Script (running)");
  });

  it("Health Detection for detect-only remediation phase", () => {
    expect(buildScriptItemLabel(base({ scriptPart: "detection", remediationStatus: 4 }))).toBe("Health Detection");
  });

  it("Health Detection for pre-detection phase of full cycle (no special label)", () => {
    expect(buildScriptItemLabel(base({ scriptPart: "detection", remediationStatus: 2 }))).toBe("Health Detection");
  });

  it("Remediation Run for the actual remediation phase", () => {
    expect(buildScriptItemLabel(base({ scriptPart: "remediation", remediationStatus: 2 }))).toBe("Remediation Run");
  });

  it("Detection (post) for post-detection verification phase", () => {
    expect(buildScriptItemLabel(base({ scriptPart: "post-detection", remediationStatus: 2 }))).toBe("Detection (post)");
  });

  it("Platform Script for completed platform scripts", () => {
    expect(buildScriptItemLabel(base({ scriptType: "platform", scriptPart: undefined, state: "Success" }))).toBe("Platform Script");
  });

  it("Platform Script (running) for live platform placeholder", () => {
    expect(buildScriptItemLabel(base({ scriptType: "platform", scriptPart: undefined, state: "Running" }))).toBe("Platform Script (running)");
  });
});

describe("isDetectOnlyRow", () => {
  const base = (overrides: Partial<ScriptItem> = {}): Pick<ScriptItem, "scriptType" | "scriptPart" | "remediationStatus" | "state"> => ({
    scriptType: "remediation",
    scriptPart: "detection",
    remediationStatus: 4,
    state: "Success",
    ...overrides,
  });

  it("true when remediation+detection+RemediationStatus=4 (NoRemediation) and not Running", () => {
    expect(isDetectOnlyRow(base())).toBe(true);
  });

  it("false for the remediation phase", () => {
    expect(isDetectOnlyRow(base({ scriptPart: "remediation", remediationStatus: 2 }))).toBe(false);
  });

  it("false when remediation actually ran (status != 4)", () => {
    expect(isDetectOnlyRow(base({ remediationStatus: 2 }))).toBe(false);
  });

  it("false for live Running placeholder", () => {
    expect(isDetectOnlyRow(base({ state: "Running" }))).toBe(false);
  });

  it("false for platform scripts", () => {
    expect(isDetectOnlyRow(base({ scriptType: "platform", scriptPart: undefined }))).toBe(false);
  });
});

describe("reduceScriptEvents", () => {
  it("returns empty for empty input", () => {
    expect(reduceScriptEvents([])).toEqual([]);
  });

  it("emits a Running placeholder when only script_started is seen", () => {
    const items = reduceScriptEvents([
      { ...finalEvent({ eventType: "script_started", ts: 0, data: { policyId: "p1", scriptType: "remediation" } }) },
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Running");
    expect(items[0].policyId).toBe("p1");
    expect(items[0].scriptPart).toBeUndefined();
  });

  it("suppresses the Running placeholder once any final phase has been seen", () => {
    const items = reduceScriptEvents([
      finalEvent({ eventType: "script_started", ts: 0, data: { policyId: "p1", scriptType: "remediation" } }),
      finalEvent({
        eventType: "script_completed", ts: 30,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", complianceResult: "True", exitCode: 0 },
      }),
    ]);
    // Only the final detection row remains — the Running placeholder is dropped.
    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Success");
    expect(items[0].scriptPart).toBe("detection");
  });

  it("renders all three phases of a full remediation cycle as Success (detection non-compliance is a report, not a failure)", () => {
    // Phase-aware semantics: a successfully-remediated cycle (status=2) means ALL THREE
    // phases reflect a healthy run. Detection's exit 1 is a non-compliance report,
    // remediation actually fixed the issue (exit 0), and post-detection confirms (exit 0).
    // None of these are script failures — the cycle is healthy and metrics should not
    // count it as failed.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 5,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 2 },
      }),
      finalEvent({
        eventType: "script_completed", ts: 6,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "remediation", exitCode: 0, remediationStatus: 2 },
      }),
      finalEvent({
        eventType: "script_completed", ts: 7,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "post-detection", complianceResult: "True", exitCode: 0, remediationStatus: 2 },
      }),
    ]);
    expect(items).toHaveLength(3);
    expect(items.map(i => i.scriptPart)).toEqual(["detection", "remediation", "post-detection"]);
    expect(items.map(i => i.state)).toEqual(["Success", "Success", "Success"]);
  });

  it("dedupes re-fetched events with the same timestamp (refresh / SignalR replay)", () => {
    const same = {
      eventType: "script_completed",
      ts: 0,
      data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0 },
    };
    const items = reduceScriptEvents([
      finalEvent(same),
      finalEvent(same),
      finalEvent(same),
    ]);
    expect(items).toHaveLength(1);
  });

  it("does NOT collapse repeated runs of the same policy with different timestamps (Codex finding 3)", () => {
    // A health script policy on a daily schedule will run multiple times in a long-lived
    // session. Each run has a distinct timestamp from IME and must surface as its own row,
    // otherwise the second run silently disappears from the UI.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0, complianceResult: "True" },
      }),
      finalEvent({
        eventType: "script_completed", ts: 86400,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 1, complianceResult: "False", remediationStatus: 4 },
      }),
    ]);
    expect(items).toHaveLength(2);
    // Both rows are state=Success (script ran fine each time); the second carries a
    // non-compliant compliance verdict surfaced via the amber non-compliant styling.
    expect(items.map(i => i.state)).toEqual(["Success", "Success"]);
    expect(items[0].complianceResult).toBe("True");
    expect(items[1].complianceResult).toBe("False");
  });

  it("two runs interleaved with started signals each show their own Running placeholder until matched", () => {
    // started(t=0) → completed(t=5) → started(t=100) [no completion yet]
    // First run finalizes; second run still pending → one final + one running placeholder.
    const items = reduceScriptEvents([
      finalEvent({ eventType: "script_started", ts: 0, data: { policyId: "p1", scriptType: "remediation" } }),
      finalEvent({
        eventType: "script_completed", ts: 5,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0 },
      }),
      finalEvent({ eventType: "script_started", ts: 100, data: { policyId: "p1", scriptType: "remediation" } }),
    ]);
    expect(items).toHaveLength(2);
    const final = items.find(i => i.state === "Success");
    const running = items.find(i => i.state === "Running");
    expect(final).toBeDefined();
    expect(running).toBeDefined();
    expect(running!.timestamp).toBe(new Date(1700000000000 + 100 * 1000).toISOString());
  });

  it("script_started AFTER an existing final emits a new Running placeholder (treated as second run, not out-of-order)", () => {
    // After Codex finding 3 we changed dedup semantics: timestamps are part of the key,
    // so a started event with a later timestamp than a known final is treated as the
    // start of a SECOND run rather than out-of-order delivery of the first. This is the
    // production-dominant case (health scripts on schedules) and SignalR maintains order
    // within a connection, so genuine reordering of started/completed for a single run
    // is vanishingly rare. The Stale-warning >600s catches any actually-stuck placeholder.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 5,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0 },
      }),
      finalEvent({ eventType: "script_started", ts: 10, data: { policyId: "p1", scriptType: "remediation" } }),
    ]);
    expect(items).toHaveLength(2);
    expect(items.find(i => i.state === "Success")).toBeDefined();
    expect(items.find(i => i.state === "Running")).toBeDefined();
  });

  it("script_started BEFORE its own final does not emit a stale placeholder", () => {
    // Normal flow: started lands first, completion later. The placeholder must vanish
    // when the final for the same policyId/scriptType arrives at-or-after the started ts.
    const items = reduceScriptEvents([
      finalEvent({ eventType: "script_started", ts: 0, data: { policyId: "p1", scriptType: "remediation" } }),
      finalEvent({
        eventType: "script_completed", ts: 5,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0 },
      }),
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].state).toBe("Success");
  });

  it("propagates new RemediationStatus / TargetType / ErrorCode fields onto items", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: {
          policyId: "p1", scriptType: "remediation", scriptPart: "detection",
          remediationStatus: 4, targetType: 2, errorCode: 0,
          errorDetails: null,
        },
      }),
    ]);
    expect(items[0].remediationStatus).toBe(4);
    expect(items[0].targetType).toBe(2);
    expect(items[0].errorCode).toBe(0);
  });

  it("accepts both camelCase and snake_case field names (forward+backward compat)", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: {
          policy_id: "p1", script_type: "remediation", script_part: "detection",
          compliance_result: "True", exit_code: 0,
          remediation_status: 4, target_type: 1,
        },
      }),
    ]);
    expect(items[0].policyId).toBe("p1");
    expect(items[0].scriptPart).toBe("detection");
    expect(items[0].complianceResult).toBe("True");
    expect(items[0].exitCode).toBe(0);
    expect(items[0].remediationStatus).toBe(4);
    expect(items[0].targetType).toBe(1);
  });

  it("Running placeholder is keyed per (policyId, scriptType) — separate platform vs remediation", () => {
    const items = reduceScriptEvents([
      finalEvent({ eventType: "script_started", ts: 0, data: { policyId: "p1", scriptType: "remediation" } }),
      finalEvent({ eventType: "script_started", ts: 1, data: { policyId: "p1", scriptType: "platform" } }),
    ]);
    expect(items).toHaveLength(2);
    expect(items.map(i => i.scriptType).sort()).toEqual(["platform", "remediation"]);
  });

  // ── Codex finding 2: wire-format coercion (Backend serializes ints as strings) ───────
  it("coerces V2 wire-format string fields to numbers (Codex finding 2)", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: {
          // Real V2 wire shape: every numeric field arrives as a string
          // (Dictionary<string, string> serialization in the Functions ingest).
          policyId: "p1",
          scriptType: "remediation",
          scriptPart: "detection",
          exitCode: "0",
          remediationStatus: "4",
          targetType: "2",
          errorCode: "0",
        },
      }),
    ]);
    expect(items[0].exitCode).toBe(0);
    expect(items[0].remediationStatus).toBe(4);
    expect(items[0].targetType).toBe(2);
    expect(items[0].errorCode).toBe(0);
  });

  it("detect-only badge condition lights up on V2 wire shape (string remediationStatus='4')", () => {
    // Codex finding 2 follow-on: with the prior bug, RemediationStatus arriving as "4"
    // (string) was dropped → isDetectOnlyRow returned false → badge never showed. With
    // toNumber coercion it now lights up correctly.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection",
                exitCode: "0", remediationStatus: "4" },
      }),
    ]);
    expect(isDetectOnlyRow(items[0])).toBe(true);
  });

  // ── Phase-aware failure detection ────────────────────────────────────────────────
  it("health-script DETECTION with exit 1 is Success-with-non-compliant (script ran fine, just reported state)", () => {
    // The user-visible UX win: a non-compliant detection that gets remediated must NOT
    // count as failed. The script ran perfectly; it's just reporting "non-compliant".
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 1, complianceResult: "False" },
      }),
    ]);
    expect(items[0].state).toBe("Success");
    expect(items[0].complianceResult).toBe("False");
    expect(isNonCompliantReport(items[0])).toBe(true);
  });

  it("health-script POST-DETECTION with exit 1 is Success-with-non-compliant (remediation didn't take)", () => {
    // Even when remediation didn't fix the underlying issue, the post-detection script
    // itself ran correctly. RemediationStatus=3 conveys the cycle outcome separately.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "post-detection", exitCode: 1, complianceResult: "False", remediationStatus: 3 },
      }),
    ]);
    expect(items[0].state).toBe("Success");
    expect(isNonCompliantReport(items[0])).toBe(true);
  });

  it("health-script REMEDIATION phase with exit != 0 IS Failed (genuine script crash)", () => {
    // The remediation phase is the one that does treat non-zero as a real failure: the
    // script crashed while attempting to fix the issue.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_failed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "remediation", exitCode: 1 },
      }),
    ]);
    expect(items[0].state).toBe("Failed");
    expect(isNonCompliantReport(items[0])).toBe(false);
  });

  it("PLATFORM script with non-zero exit is Failed (no compliance-report exemption)", () => {
    // Platform scripts have no detection/post-detection concept; non-zero exit = crash.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "platform", exitCode: 1 },
      }),
    ]);
    expect(items[0].state).toBe("Failed");
  });

  it("treats script_completed with result='Failed' as Failed (defense)", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "platform", exitCode: 0, result: "Failed" },
      }),
    ]);
    expect(items[0].state).toBe("Failed");
  });

  it("script_failed event type renders as Failed even with exit 0 (rare but defensive)", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_failed", ts: 0,
        data: { policyId: "p1", scriptType: "platform", exitCode: 0 },
      }),
    ]);
    expect(items[0].state).toBe("Failed");
  });
});

describe("isNonCompliantReport", () => {
  it("true for detection with complianceResult=False", () => {
    expect(isNonCompliantReport({ scriptType: "remediation", scriptPart: "detection", complianceResult: "False", state: "Success" })).toBe(true);
  });
  it("true for post-detection with complianceResult=False", () => {
    expect(isNonCompliantReport({ scriptType: "remediation", scriptPart: "post-detection", complianceResult: "False", state: "Success" })).toBe(true);
  });
  it("false when complianceResult is True", () => {
    expect(isNonCompliantReport({ scriptType: "remediation", scriptPart: "detection", complianceResult: "True", state: "Success" })).toBe(false);
  });
  it("false for the remediation phase", () => {
    expect(isNonCompliantReport({ scriptType: "remediation", scriptPart: "remediation", complianceResult: undefined, state: "Success" })).toBe(false);
  });
  it("false when state is Failed", () => {
    expect(isNonCompliantReport({ scriptType: "remediation", scriptPart: "detection", complianceResult: "False", state: "Failed" })).toBe(false);
  });
  it("false for platform scripts", () => {
    expect(isNonCompliantReport({ scriptType: "platform", scriptPart: undefined, complianceResult: undefined, state: "Success" })).toBe(false);
  });
});
