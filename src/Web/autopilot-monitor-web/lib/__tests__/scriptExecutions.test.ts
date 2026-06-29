import { describe, it, expect } from "vitest";
import {
  buildScriptItemLabel,
  formatScriptDuration,
  getPhaseBadge,
  groupScriptItems,
  isDetectOnlyRow,
  isNonCompliantReport,
  mapRemediationStatus,
  reduceScriptEvents,
  scriptItemKey,
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

  it("uses Intune terminology: Remediation for all health-script phases", () => {
    // The Intune Admin Center calls these policies "Remediations"; the phase (detection /
    // remediation / post-detection) is conveyed via a separate badge, not the title.
    expect(buildScriptItemLabel(base({ scriptPart: "detection", remediationStatus: 4 }))).toBe("Remediation");
    expect(buildScriptItemLabel(base({ scriptPart: "detection", remediationStatus: 2 }))).toBe("Remediation");
    expect(buildScriptItemLabel(base({ scriptPart: "remediation", remediationStatus: 2 }))).toBe("Remediation");
    expect(buildScriptItemLabel(base({ scriptPart: "post-detection", remediationStatus: 2 }))).toBe("Remediation");
  });

  it("Remediation (running) for live placeholder", () => {
    expect(buildScriptItemLabel(base({ state: "Running", scriptPart: undefined }))).toBe("Remediation (running)");
  });

  it("Platform Script for completed platform scripts", () => {
    expect(buildScriptItemLabel(base({ scriptType: "platform", scriptPart: undefined, state: "Success" }))).toBe("Platform Script");
  });

  it("Platform Script (running) for live platform placeholder", () => {
    expect(buildScriptItemLabel(base({ scriptType: "platform", scriptPart: undefined, state: "Running" }))).toBe("Platform Script (running)");
  });
});

describe("getPhaseBadge", () => {
  it("returns the scriptPart for completed remediation rows", () => {
    expect(getPhaseBadge({ scriptType: "remediation", scriptPart: "detection", state: "Success" })).toBe("detection");
    expect(getPhaseBadge({ scriptType: "remediation", scriptPart: "remediation", state: "Success" })).toBe("remediation");
    expect(getPhaseBadge({ scriptType: "remediation", scriptPart: "post-detection", state: "Failed" })).toBe("post-detection");
  });
  it("returns null for platform scripts (no phase concept)", () => {
    expect(getPhaseBadge({ scriptType: "platform", scriptPart: undefined, state: "Success" })).toBeNull();
  });
  it("returns null for Running placeholders (no phase known yet)", () => {
    expect(getPhaseBadge({ scriptType: "remediation", scriptPart: undefined, state: "Running" })).toBeNull();
  });
});

describe("scriptItemKey", () => {
  it("is stable across re-renders for the same identity (no timestamp / index in key)", () => {
    // Critical for preserving showDetails state across upstream prop changes — the React
    // key MUST NOT include any index or timestamp that shifts on reducer re-runs.
    const a = scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: "detection", state: "Success" });
    const b = scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: "detection", state: "Success" });
    expect(a).toBe(b);
  });
  it("differs by phase so multi-phase rows get separate React identities", () => {
    expect(scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: "detection", state: "Success" }))
      .not.toBe(scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: "remediation", state: "Success" }));
  });
  it("uses a separate slot for Running placeholders so they don't collide with finals", () => {
    expect(scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: undefined, state: "Running" }))
      .not.toBe(scriptItemKey({ policyId: "p1", scriptType: "remediation", scriptPart: "detection", state: "Success" }));
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

  it("collapses repeated emissions of the same (policyId, scriptType, scriptPart) keeping the most-complete entry", () => {
    // Trade-off vs. Codex finding 3: in an Autopilot enrollment session IME often re-emits
    // the same script across ESP-phase transitions (DeviceSetup→AccountSetup), and the
    // second emission frequently has degraded data (lost exit code etc) due to agent state
    // being cleared after the first emit. The user found this confusing — same script
    // appearing twice with different data quality. We now collapse and keep the entry with
    // the most populated fields (exitCode, stdout, etc). For long-running scheduled-script
    // monitoring outside Autopilot, this would lose distinct runs — out of scope for this UI.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection", exitCode: 0, complianceResult: "True", stdout: "first run details" },
      }),
      finalEvent({
        eventType: "script_completed", ts: 86400,
        // Second emission has fewer populated fields → must lose the dedupe race.
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection" },
      }),
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].exitCode).toBe(0);
    expect(items[0].complianceResult).toBe("True");
    expect(items[0].stdout).toBe("first run details");
  });

  it("HS-COMPLIANCE early-signal sparse event is replaced by HS-NEW-RESULT full payload via dataCompleteness dedupe", () => {
    // Real-world health-script timing: HS-COMPLIANCE pattern fires immediately at the
    // [HS] pre-remdiation line (~30-90 s before HS-NEW-RESULT JSON). Both events are
    // emitted by the agent. The reducer must keep the entry with more complete data —
    // the later JSON entry wins because it carries stdout/stderr/RemediationStatus too.
    const items = reduceScriptEvents([
      // Sparse early-signal entry: just policyId + scriptPart + compliance + inferred exit.
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection",
                complianceResult: "True", exitCode: 0 },
      }),
      // Full payload arrives ~74 s later from the [HS] new result = JSON.
      finalEvent({
        eventType: "script_completed", ts: 74,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection",
                complianceResult: "True", exitCode: 0,
                stdout: "LocalAdminIsEnabled=False",
                remediationStatus: 4, targetType: 2, runContext: "System" },
      }),
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].stdout).toBe("LocalAdminIsEnabled=False");
    expect(items[0].remediationStatus).toBe(4);
    expect(items[0].runContext).toBe("System");
  });

  it("HS-COMPLIANCE alone (session ended before HS-NEW-RESULT) still surfaces a row", () => {
    // The whole point of the early-signal fallback: short Autopilot enrollments often
    // end before IME emits the consolidated JSON. Without HS-COMPLIANCE we'd see no
    // health-script rows at all in those cases. With it, we get the compliance verdict
    // even though stdout / RemediationStatus are missing.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "remediation", scriptPart: "detection",
                complianceResult: "False", exitCode: 1 },
      }),
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].complianceResult).toBe("False");
    expect(items[0].state).toBe("Success"); // detection exit != 0 is non-compliant report, not failure
    expect(items[0].stdout).toBeUndefined();
  });

  it("collapses platform-script re-emissions across ESP phases (real-world Autopilot pattern)", () => {
    // Pattern observed in session e2929c97: same platform script ran in DeviceSetup
    // (with full data) and again in AccountSetup (degraded data, no exit code). Earlier
    // versions surfaced both rows side-by-side which was confusing.
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "847da54e", scriptType: "platform", exitCode: 0, result: "Success", runContext: "System" },
      }),
      finalEvent({
        eventType: "script_completed", ts: 360,
        data: { policyId: "847da54e", scriptType: "platform", result: "Success" },
      }),
    ]);
    expect(items).toHaveLength(1);
    expect(items[0].exitCode).toBe(0);
    expect(items[0].runContext).toBe("System");
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

  // ── stderr-as-failure rule (user UX preference, debrief 2026-05-11) ─────────────
  it("PLATFORM script with stderr present renders as Failed regardless of exit code", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "platform", exitCode: 0, result: "Success", stderr: "WARNING: deprecated path" },
      }),
    ]);
    expect(items[0].state).toBe("Failed");
  });

  it("HEALTH-SCRIPT detection with stderr present is NOT Failed (compliance verdict is authoritative)", () => {
    // Detection PowerShell routinely leaks benign probe errors to stderr (path-not-found,
    // service-not-startable) while still returning a valid compliant verdict. Stamping those
    // Failed inflated errorCount and ranked them #1 in search_events on green sessions, so
    // stderr is exempt for detection/post-detection phases — the verdict (complianceResult)
    // is authoritative. Real script crashes still surface via the remediation phase / platform
    // scripts / explicit result="Failed".
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: {
          policyId: "p1",
          scriptType: "remediation",
          scriptPart: "detection",
          exitCode: 0,
          complianceResult: "True",
          stderr: "Get-ChildItem : Cannot find path '...JoinInfo' because it does not exist.",
        },
      }),
    ]);
    expect(items[0].state).toBe("Success");
  });

  it("HEALTH-SCRIPT non-compliant detection with stderr is a NonCompliant report, not Failed", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: {
          policyId: "p1",
          scriptType: "remediation",
          scriptPart: "detection",
          exitCode: 1,
          complianceResult: "False",
          stderr: "Invoke-WebRequest : The remote name could not be resolved",
        },
      }),
    ]);
    expect(items[0].state).toBe("Success");
    expect(isNonCompliantReport(items[0])).toBe(true);
  });

  it("whitespace-only stderr does not flip state to Failed", () => {
    const items = reduceScriptEvents([
      finalEvent({
        eventType: "script_completed", ts: 0,
        data: { policyId: "p1", scriptType: "platform", exitCode: 0, result: "Success", stderr: "   \n  " },
      }),
    ]);
    expect(items[0].state).toBe("Success");
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

// ── Card grouping (multi-phase remediation cycles fold into one parent card) ────────
describe("groupScriptItems", () => {
  // Helper to construct a ScriptItem inline.
  const item = (overrides: Partial<ScriptItem>): ScriptItem => ({
    policyId: "p1",
    scriptType: "remediation",
    state: "Success",
    timestamp: ts(0),
    ...overrides,
  });

  it("returns empty for empty input", () => {
    expect(groupScriptItems([])).toEqual([]);
  });

  it("platform scripts stay as single-phase cards (no nesting)", () => {
    const cards = groupScriptItems([
      item({ policyId: "plat1", scriptType: "platform", scriptPart: undefined, exitCode: 0, result: "Success" }),
    ]);
    expect(cards).toHaveLength(1);
    expect(cards[0].isCycle).toBe(false);
    expect(cards[0].phases).toHaveLength(1);
    expect(cards[0].headerState).toBe("Success");
  });

  it("detect-only remediation (single phase, RemediationStatus=4) → single-phase card with detect-only header", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4 }),
    ]);
    expect(cards).toHaveLength(1);
    expect(cards[0].isCycle).toBe(false);
    expect(cards[0].headerState).toBe("Success");
  });

  it("3-phase remediation cycle for the same policy folds into ONE parent card (the user-reported regression)", () => {
    // Real shape from session 8810cf81 / policy 99e1274b: detection non-compliant,
    // remediation script crashed (exit 1), post-detection still non-compliant. The user
    // wants this to read as ONE card with three phase rows under it, not three siblings.
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 2, timestamp: ts(0) }),
      item({ scriptPart: "remediation", state: "Failed", exitCode: 1, remediationStatus: 2, timestamp: ts(1) }),
      item({ scriptPart: "post-detection", complianceResult: "False", exitCode: 1, remediationStatus: 2, timestamp: ts(2) }),
    ]);
    expect(cards).toHaveLength(1);
    expect(cards[0].isCycle).toBe(true);
    expect(cards[0].phases).toHaveLength(3);
    // The remediation phase failed → header reads as Failed.
    expect(cards[0].headerState).toBe("Failed");
    expect(cards[0].headerLabel).toBe("Remediation script failed");
    // Phases are sorted detection → remediation → post-detection regardless of insertion order.
    expect(cards[0].phases.map(p => p.scriptPart)).toEqual(["detection", "remediation", "post-detection"]);
  });

  it("phases sort detection→remediation→post-detection even when input arrives reversed", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "post-detection", complianceResult: "True", exitCode: 0, remediationStatus: 2, timestamp: ts(2) }),
      item({ scriptPart: "remediation", exitCode: 0, remediationStatus: 2, timestamp: ts(1) }),
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 2, timestamp: ts(0) }),
    ]);
    expect(cards[0].phases.map(p => p.scriptPart)).toEqual(["detection", "remediation", "post-detection"]);
  });

  it("successful remediation cycle (post-detection now compliant) → header 'Remediated successfully' green", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 2, timestamp: ts(0) }),
      item({ scriptPart: "remediation", exitCode: 0, remediationStatus: 2, timestamp: ts(1) }),
      item({ scriptPart: "post-detection", complianceResult: "True", exitCode: 0, remediationStatus: 2, timestamp: ts(2) }),
    ]);
    expect(cards[0].headerState).toBe("Success");
    expect(cards[0].headerLabel).toBe("Remediated successfully");
  });

  it("non-compliant cycle (remediation didn't fix) → header 'Non-compliant after remediation' amber", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 3, timestamp: ts(0) }),
      item({ scriptPart: "remediation", exitCode: 0, remediationStatus: 3, timestamp: ts(1) }),
      item({ scriptPart: "post-detection", complianceResult: "False", exitCode: 1, remediationStatus: 3, timestamp: ts(2) }),
    ]);
    expect(cards[0].headerState).toBe("NonCompliant");
    expect(cards[0].headerLabel).toBe("Non-compliant after remediation");
  });

  it("detect-only non-compliant → header 'Non-compliant (detect-only)' amber, single phase", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 4 }),
    ]);
    expect(cards[0].isCycle).toBe(false);
    expect(cards[0].headerState).toBe("NonCompliant");
    expect(cards[0].headerLabel).toBe("Non-compliant (detect-only)");
  });

  it("detect-only compliant → header 'Compliant (detect-only)' green, single phase", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4 }),
    ]);
    expect(cards[0].headerState).toBe("Success");
    expect(cards[0].headerLabel).toBe("Compliant (detect-only)");
  });

  it("Running placeholder card surfaces with header state Running", () => {
    const cards = groupScriptItems([
      item({ scriptPart: undefined, state: "Running", complianceResult: undefined, exitCode: undefined }),
    ]);
    expect(cards[0].headerState).toBe("Running");
    expect(cards[0].headerLabel).toBe("Running");
  });

  it("multiple distinct policies produce multiple cards in chronological order", () => {
    const cards = groupScriptItems([
      item({ policyId: "p2", scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4, timestamp: ts(10) }),
      item({ policyId: "p1", scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 4, timestamp: ts(5) }),
    ]);
    expect(cards).toHaveLength(2);
    // Sorted by earliest-phase timestamp ascending.
    expect(cards[0].policyId).toBe("p1");
    expect(cards[1].policyId).toBe("p2");
  });

  it("does NOT collapse different policies together (sanity check)", () => {
    const cards = groupScriptItems([
      item({ policyId: "p1", scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4 }),
      item({ policyId: "p2", scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 4 }),
    ]);
    expect(cards).toHaveLength(2);
  });

  it("does NOT collapse remediation and platform of same policyId together (different scriptType)", () => {
    const cards = groupScriptItems([
      item({ policyId: "p1", scriptType: "remediation", scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4 }),
      item({ policyId: "p1", scriptType: "platform", scriptPart: undefined, exitCode: 0, result: "Success" }),
    ]);
    expect(cards).toHaveLength(2);
  });

  it("lifts the whole-cycle durationSeconds onto a multi-phase card (every phase carries the same value)", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "False", exitCode: 1, remediationStatus: 2, durationSeconds: 134, timestamp: ts(0) }),
      item({ scriptPart: "remediation", exitCode: 0, remediationStatus: 2, durationSeconds: 134, timestamp: ts(1) }),
      item({ scriptPart: "post-detection", complianceResult: "True", exitCode: 0, remediationStatus: 2, durationSeconds: 134, timestamp: ts(2) }),
    ]);
    expect(cards[0].isCycle).toBe(true);
    expect(cards[0].durationSeconds).toBe(134);
  });

  it("single-phase card inherits its lone phase's durationSeconds", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4, durationSeconds: 12 }),
    ]);
    expect(cards[0].isCycle).toBe(false);
    expect(cards[0].durationSeconds).toBe(12);
  });

  it("card durationSeconds is undefined when no phase reported one", () => {
    const cards = groupScriptItems([
      item({ scriptPart: "detection", complianceResult: "True", exitCode: 0, remediationStatus: 4 }),
    ]);
    expect(cards[0].durationSeconds).toBeUndefined();
  });
});

describe("formatScriptDuration", () => {
  it("returns null for null/negative/non-finite", () => {
    expect(formatScriptDuration(undefined)).toBeNull();
    expect(formatScriptDuration(-1)).toBeNull();
    expect(formatScriptDuration(Number.NaN)).toBeNull();
  });
  it("formats sub-minute as seconds", () => {
    expect(formatScriptDuration(0)).toBe("0s");
    expect(formatScriptDuration(42)).toBe("42s");
    expect(formatScriptDuration(59.4)).toBe("59s"); // rounds down, stays sub-minute
  });
  it("rolls 60s into the minutes format", () => {
    expect(formatScriptDuration(59.6)).toBe("1m 00s"); // rounds up to 60 → minutes branch
  });
  it("formats minutes with zero-padded seconds", () => {
    expect(formatScriptDuration(134)).toBe("2m 14s");
    expect(formatScriptDuration(1800)).toBe("30m 00s");
  });
  it("formats hours with zero-padded minutes", () => {
    expect(formatScriptDuration(3780)).toBe("1h 03m");
  });
});

describe("reduceScriptEvents — durationSeconds", () => {
  it("parses durationSeconds (string or number) onto the item", () => {
    const [fromString] = reduceScriptEvents([
      finalEvent({ data: { policyId: "p1", scriptType: "platform", result: "Success", durationSeconds: "1800.00" } }),
    ]);
    expect(fromString.durationSeconds).toBe(1800);

    const [fromNumber] = reduceScriptEvents([
      finalEvent({ data: { policyId: "p2", scriptType: "platform", result: "Success", durationSeconds: 42 } }),
    ]);
    expect(fromNumber.durationSeconds).toBe(42);
  });
  it("leaves durationSeconds undefined when absent", () => {
    const [item] = reduceScriptEvents([
      finalEvent({ data: { policyId: "p1", scriptType: "platform", result: "Success" } }),
    ]);
    expect(item.durationSeconds).toBeUndefined();
  });
});
