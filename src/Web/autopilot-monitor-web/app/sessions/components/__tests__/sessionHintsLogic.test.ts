import { describe, it, expect } from "vitest";
import {
  showDiagnosisButton,
  showDiagnosticsSetupHint,
  showInvestigateHint,
  type DiagnosticsSetupHintInput,
  type InvestigateHintInput,
} from "../sessionHintsLogic";

const diag: DiagnosticsSetupHintInput = {
  status: "Failed",
  hasDiagnosticsPackage: false,
  featureFlagsLoaded: true,
  diagnosticsUploadConfigured: false,
  isCrossTenantView: false,
  isGatherRulesSession: false,
};

describe("showDiagnosticsSetupHint", () => {
  it("shows on a failed enrollment without a package when the tenant has no usable upload", () => {
    expect(showDiagnosticsSetupHint(diag)).toBe(true);
  });

  it.each<[string, Partial<DiagnosticsSetupHintInput>]>([
    ["a succeeded session", { status: "Succeeded" }],
    ["a session still running", { status: "InProgress" }],
    ["a session that has a package", { hasDiagnosticsPackage: true }],
    ["flags not loaded yet (or failed to load)", { featureFlagsLoaded: false }],
    ["a tenant with a working upload", { diagnosticsUploadConfigured: true }],
    ["a cross-tenant view", { isCrossTenantView: true }],
    ["a gather-rules session", { isGatherRulesSession: true }],
  ])("stays hidden for %s", (_label, patch) => {
    expect(showDiagnosticsSetupHint({ ...diag, ...patch })).toBe(false);
  });
});

const investigate: InvestigateHintInput = { status: "Failed", analysisLoaded: true, loadingAnalysis: false, findingCount: 0 };

describe("showInvestigateHint", () => {
  it("shows on a failed session whose loaded analysis has no finding", () => {
    expect(showInvestigateHint(investigate)).toBe(true);
  });

  it.each<[string, Partial<InvestigateHintInput>]>([
    ["a succeeded session", { status: "Succeeded" }],
    ["an analysis that never loaded", { analysisLoaded: false }],
    ["a running re-analysis", { loadingAnalysis: true }],
    ["an open or resolved finding", { findingCount: 1 }],
  ])("stays hidden for %s", (_label, patch) => {
    expect(showInvestigateHint({ ...investigate, ...patch })).toBe(false);
  });
});

describe("showDiagnosisButton", () => {
  it.each([
    ["Failed", 0, true],
    ["Stalled", 0, true],
    ["AwaitingUser", 0, true],
    ["Incomplete", 0, true],
    ["Succeeded", 0, false],
    ["Succeeded", 2, true],
    ["InProgress", 3, false],
    ["Pending", 1, false],
    [undefined, 0, false],
  ] as const)("%s with %d open findings → %s", (status, open, expected) => {
    expect(showDiagnosisButton(status, open)).toBe(expected);
  });
});
