// Phase definitions per enrollment type
export const V1_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 2, name: "Device Setup",        shortName: "Device Setup" },
  { id: 3, name: "Apps (Device)",       shortName: "Apps (Device)" },
  { id: 4, name: "Account Setup",       shortName: "Account Setup" },
  { id: 5, name: "Apps (User)",         shortName: "Apps (User)" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

// V1 with SkipUserStatusPage=true: AccountSetup (4) and AppsUser (5) are visualized as
// skipped — the ESP policy explicitly disables the user status page. Background events
// for those phases (e.g. esp_phase_changed → AccountSetup) still appear in the timeline,
// but the progress bar reflects the policy intent.
export const V1_SKIP_USER_PHASE_IDS: ReadonlySet<number> = new Set([4, 5]);

export const V2_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 3, name: "App Installation",    shortName: "Apps" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

export const V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup", "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
export const V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation", "Finalizing Setup", "Complete", "Failed"];

export type PhaseDescriptor = { id: number; name: string; shortName: string };

// WhiteGlove (IsPreProvisioned=true) runs the full user enrollment in Part 2 even when the
// admin set SkipUserStatusPage=true — skip-marking only applies to pure device-only
// deployments. V2 always uses its simplified phase set.
export function resolvePhaseLayout(params: {
  enrollmentType?: string;
  isSkipUserStatusPage?: boolean;
  isPreProvisioned?: boolean;
}): { phases: PhaseDescriptor[]; useSkipLayout: boolean; skippedPhaseIds: ReadonlySet<number> } {
  if (params.enrollmentType === "v2") {
    return { phases: V2_PHASES, useSkipLayout: false, skippedPhaseIds: new Set<number>() };
  }
  const useSkipLayout = !!params.isSkipUserStatusPage && !params.isPreProvisioned;
  return {
    phases: V1_PHASES,
    useSkipLayout,
    skippedPhaseIds: useSkipLayout ? V1_SKIP_USER_PHASE_IDS : new Set<number>(),
  };
}

// Lookup by phase number — Phase 3 has different names per enrollment type
export const V1_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "Apps (Device)",    4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };
export const V2_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "App Installation", 4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };
