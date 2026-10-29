import { describe, it, expect } from "vitest";
import { makeRuleResult } from "@/test/factories";
import type { DiagnosisHeadline } from "@/lib/generated/wire-types.generated";
import { groupFindings } from "../findingGroups";
import { copySummary, stepsFrom } from "../steps";
import { verdictText } from "../verdictText";
import { formatFact, stepPath, waitName } from "../diagnosisFormat";

const outcome = makeRuleResult({
  ruleId: "ANALYZE-ENRL-001",
  presentation: { role: "outcome" },
  remediation: [{ title: "Find the cause", steps: ["Open the finding that names the cause"] }],
});
const cause = makeRuleResult({
  ruleId: "ANALYZE-APP-002",
  presentation: { role: "cause" },
  matchedConditions: { failed: { field: "appName", value: "Contoso Notes" } },
  remediation: [
    { title: "Check {{appName}} in Intune", steps: ["Open Apps › {{appName}}", "Look up `{{errorCode}}`"] },
    { title: "Only unrecorded", steps: ["Retry {{exitCode}}"] },
  ],
});

describe("stepsFrom", () => {
  it("takes the cause finding's remediation, filled in, without steps it cannot fill", () => {
    const steps = stepsFrom(groupFindings([outcome, cause], "failed", []))!;
    expect(steps.source.result.ruleId).toBe("ANALYZE-APP-002");
    expect(steps.groups).toHaveLength(1);
    expect(steps.groups[0].steps).toHaveLength(1);
  });

  it("falls back to the outcome finding, and is null without remediation", () => {
    expect(stepsFrom(groupFindings([outcome], "failed", []))!.source.result.ruleId).toBe("ANALYZE-ENRL-001");
    expect(stepsFrom(groupFindings([makeRuleResult({ presentation: { role: "cause" } })], "failed", []))).toBeNull();
  });

  it("passes over a finding whose steps all name unrecorded values", () => {
    const unfillable = makeRuleResult({
      ruleId: "ANALYZE-APP-006",
      presentation: { role: "cause" },
      remediation: [{ title: "Retry", steps: ["Retry {{exitCode}}"] }],
    });
    expect(stepsFrom(groupFindings([unfillable, outcome], "failed", []))!.source.result.ruleId).toBe("ANALYZE-ENRL-001");
  });
});

describe("copySummary", () => {
  it("writes the verdict, the step, the culprit and the steps as plain text", () => {
    const headline: DiagnosisHeadline = {
      outcome: "failed",
      cause: "esp_app_failure",
      verdictPath: "agent:failed",
      failedStep: { category: "AccountSetup", subcategory: "Apps", errorCode: "0x80d03805", at: "2026-10-13T14:44:24Z" },
      culprits: [{ kind: "app", evidence: "install_failed", name: "Contoso Notes", errorCode: "0x80d03805", installStarts: 4 }],
    };
    const steps = stepsFrom(groupFindings([cause], "failed", []));
    const text = copySummary({ verdict: verdictText(headline), headline, device: "DESKTOP-CONTOSO1", steps, url: "https://portal.example/diagnosis?id=1" });
    expect(text).toBe(
      [
        "Enrollment failed in Account setup › Apps — DESKTOP-CONTOSO1",
        "Contoso Notes could not be installed, so the ESP ended the enrollment.",
        "It failed with 0x80d03805 after 4 install starts.",
        "ESP step: Account setup › Apps (0x80d03805)",
        "App: Contoso Notes — error 0x80d03805, 4 install starts",
        "",
        "What to do:",
        "Check Contoso Notes in Intune",
        "1. Open Apps › Contoso Notes",
        "",
        "https://portal.example/diagnosis?id=1",
      ].join("\n")
    );
  });

  it("names a detection failure and shows IME's code as hex", () => {
    const headline: DiagnosisHeadline = {
      outcome: "failed",
      cause: "esp_app_failure",
      verdictPath: "agent:failed",
      failedStep: { category: "DeviceSetup", subcategory: "Apps", errorCode: "0x87d1041c", at: "2026-10-13T14:44:24Z" },
      culprits: [{ kind: "app", evidence: "install_failed", name: "Contoso Notes", errorCode: "-2016345060" }],
    };
    const text = copySummary({ verdict: verdictText(headline), headline, steps: null });
    expect(text).toContain("App: Contoso Notes — error 0x87D1041C, not detected after install");
    expect(text).toContain("It finished installing, but Intune did not detect it afterwards (0x87D1041C).");
  });
});

describe("diagnosisFormat", () => {
  it("names ESP phases and steps", () => {
    expect(stepPath("AccountSetup", "WaitingForAadRegistration")).toBe("Account setup › Entra registration");
    expect(stepPath(null, "SecurityPolicies")).toBe("Security policies");
    expect(stepPath("SomeFuturePhase", null)).toBe("Some future phase");
    expect(stepPath(null, null)).toBeNull();
  });

  it("formats facts by their declared format", () => {
    expect(formatFact("274", "duration")).toEqual({ text: "4m 34s", mono: false });
    expect(formatFact("n/a", "duration")).toEqual({ text: "n/a", mono: false });
    expect(formatFact("0x80d03805", "code")).toEqual({ text: "0x80d03805", mono: true });
    expect(formatFact("plain", "text")).toEqual({ text: "plain", mono: false });
    expect(formatFact("0x80070005", "errorCode").mono).toBe(true);
    // IME writes HRESULTs as signed decimals; they read as hex, with the catalog meaning.
    expect(formatFact("-2016345060", "errorCode").text).toBe("0x87D1041C");
    expect(formatFact("-2016345060", "errorCode").hint).toMatch(/not detected/i);
    expect(formatFact("1603", "errorCode").text).toBe("1603");
  });

  it("names what an enrollment still waits for", () => {
    expect(waitName({ kind: "prerequisite", name: "desktop_arrival" })).toBe("Desktop to appear");
    expect(waitName({ kind: "prerequisite", name: "account_setup_provisioning_complete" })).toBe("Account setup to finish");
    expect(waitName({ kind: "prerequisite", name: "some_future_gate" })).toBe("Some future gate");
    expect(waitName({ kind: "app", name: "Contoso Notes" })).toBe("Contoso Notes");
  });
});
