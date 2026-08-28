import { describe, it, expect } from "vitest";
import { EnrollmentEvent } from "@/types";
import {
  buildProgressSteps,
  computeOverallProgress,
  lastDeclaredPhase,
  resolveActiveStepIndex,
  resolvePresentation,
  scenarioLabel,
} from "../progressLayout";

/**
 * The progress portal's scenario-aware view model. The regressions these pin:
 * Device Preparation never declares the app phase (must be promoted from app events),
 * self-deploying / SkipUserStatusPage devices never reach the user phases (must not be
 * shown as pending forever), a pre-provisioned device waiting for its user is not a
 * failure, and only Failed is red.
 */

function ev(sequence: number, eventType: string, phase = -1, data?: Record<string, unknown>): EnrollmentEvent {
  return {
    eventId: `evt-${sequence}`,
    sessionId: "s",
    timestamp: "2026-08-28T10:00:00Z",
    eventType,
    severity: "Info",
    source: "test",
    phase,
    message: "",
    sequence,
    data,
  };
}

const v1 = { status: "InProgress", currentPhase: 0, enrollmentType: "v1" };
const v2 = { status: "InProgress", currentPhase: 0, enrollmentType: "v2" };

describe("buildProgressSteps", () => {
  it("v1 shows the seven ESP steps without Complete", () => {
    expect(buildProgressSteps(v1, false).map((s) => s.id)).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it("v1 with SkipUserStatusPage hides Account setup and user apps", () => {
    expect(buildProgressSteps(v1, true).map((s) => s.id)).toEqual([0, 1, 2, 3, 6]);
  });

  it("v1 pre-provisioned keeps the user steps even with SkipUserStatusPage", () => {
    expect(buildProgressSteps({ ...v1, isPreProvisioned: true }, true).map((s) => s.id)).toEqual([0, 1, 2, 3, 4, 5, 6]);
  });

  it("v2 shows the Device Preparation layout with a single app step", () => {
    const steps = buildProgressSteps(v2, false);
    expect(steps.map((s) => s.id)).toEqual([0, 1, 3, 6]);
    expect(steps.map((s) => s.label)).toEqual([
      "Setup start",
      "Device preparation",
      "Installing apps",
      "Finalizing setup",
    ]);
    expect(steps.filter((s) => s.isAppsStep).map((s) => s.id)).toEqual([3]);
  });
});

describe("resolveActiveStepIndex", () => {
  it("v1 follows currentPhase directly", () => {
    const steps = buildProgressSteps(v1, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v1, currentPhase: 4 }, events: [], hasAppActivity: false })).toBe(4);
  });

  it("v1 skip-user layout maps a background phase 4 onto the last shown step reached", () => {
    const steps = buildProgressSteps(v1, true); // [0,1,2,3,6]
    expect(resolveActiveStepIndex({ steps, session: { ...v1, currentPhase: 4 }, events: [], hasAppActivity: false })).toBe(3);
    expect(resolveActiveStepIndex({ steps, session: { ...v1, currentPhase: 6 }, events: [], hasAppActivity: false })).toBe(4);
  });

  it("v2 stays on Device preparation without app evidence, even though phase 3 is never declared", () => {
    const steps = buildProgressSteps(v2, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v2, currentPhase: 1 }, events: [], hasAppActivity: false })).toBe(1);
  });

  it("v2 promotes to Installing apps from app-event evidence", () => {
    const steps = buildProgressSteps(v2, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v2, currentPhase: 0 }, events: [], hasAppActivity: true })).toBe(2);
  });

  it("v2 app evidence never demotes a device already finalizing", () => {
    const steps = buildProgressSteps(v2, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v2, currentPhase: 6 }, events: [], hasAppActivity: true })).toBe(3);
  });

  it("v1 app evidence does not promote — the ESP declares the app phases itself", () => {
    const steps = buildProgressSteps(v1, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v1, currentPhase: 2 }, events: [], hasAppActivity: true })).toBe(2);
  });

  it("Succeeded completes every step", () => {
    const steps = buildProgressSteps(v2, false);
    expect(resolveActiveStepIndex({ steps, session: { ...v2, status: "Succeeded", currentPhase: 7 }, events: [], hasAppActivity: true })).toBe(4);
  });

  it("Failed (currentPhase 99) is placed by the last declared phase", () => {
    const steps = buildProgressSteps(v1, false);
    const events = [ev(1, "esp_phase_changed", 2), ev(2, "app_install_failed", -1), ev(3, "esp_phase_changed", 4)];
    expect(resolveActiveStepIndex({ steps, session: { ...v1, status: "Failed", currentPhase: 99 }, events, hasAppActivity: true })).toBe(4);
  });

  it("Failed without any declared phase falls back to the app step when apps ran, else the start", () => {
    const steps = buildProgressSteps(v1, false);
    const failed = { ...v1, status: "Failed", currentPhase: 99 };
    expect(resolveActiveStepIndex({ steps, session: failed, events: [], hasAppActivity: true })).toBe(3);
    expect(resolveActiveStepIndex({ steps, session: failed, events: [], hasAppActivity: false })).toBe(0);
  });
});

describe("lastDeclaredPhase", () => {
  it("ignores non-declaration events and the terminal sentinels", () => {
    expect(lastDeclaredPhase([ev(1, "app_install_started", 5), ev(2, "phase_transition", 99)])).toBe(-1);
    expect(lastDeclaredPhase([ev(1, "phase_transition", 6), ev(2, "agent_started", 0)])).toBe(6);
  });
});

describe("computeOverallProgress", () => {
  it("is step-based and 100 on success", () => {
    expect(computeOverallProgress("InProgress", 2, 4)).toBe(50);
    expect(computeOverallProgress("Failed", 3, 7)).toBe(43);
    expect(computeOverallProgress("Succeeded", 0, 4)).toBe(100);
    expect(computeOverallProgress("InProgress", 0, 0)).toBe(0);
  });
});

describe("resolvePresentation", () => {
  it("maps the status vocabulary — only Failed is a failure", () => {
    expect(resolvePresentation({ ...v1, status: "Succeeded" }, []).kind).toBe("success");
    expect(resolvePresentation({ ...v1, status: "Failed" }, []).kind).toBe("failed");
    expect(resolvePresentation({ ...v1, status: "Incomplete" }, []).kind).toBe("incomplete");
    expect(resolvePresentation({ ...v1, status: "AwaitingUser" }, []).kind).toBe("waiting");
    expect(resolvePresentation({ ...v1, status: "Pending" }, []).kind).toBe("working");
    expect(resolvePresentation({ ...v1, status: "InProgress" }, []).kind).toBe("working");
  });

  it("Stalled keeps working but explains the delay", () => {
    const p = resolvePresentation({ ...v1, status: "Stalled" }, []);
    expect(p.kind).toBe("working");
    expect(p.detail).toMatch(/longer than usual/);
  });

  it("a pre-provisioned device parked after the technician part is waiting, not working", () => {
    const parked = { ...v1, isPreProvisioned: true };
    const p = resolvePresentation(parked, [ev(1, "whiteglove_complete")]);
    expect(p.kind).toBe("waiting");
    expect(p.detail).toMatch(/technician/i);
    // Once resumed by the user it is working again.
    expect(resolvePresentation({ ...parked, resumedAt: "2026-08-28T11:00:00Z" }, [ev(1, "whiteglove_complete")]).kind).toBe("working");
    // Still in Part 1 (no whiteglove_complete yet) — working.
    expect(resolvePresentation(parked, []).kind).toBe("working");
  });

  it("Cloud PC is unsupported regardless of status", () => {
    expect(resolvePresentation({ ...v1, status: "Succeeded", isCloudPc: true }, []).kind).toBe("unsupported");
  });
});

describe("scenarioLabel", () => {
  it("names everything except plain user-driven Autopilot", () => {
    expect(scenarioLabel(v1)).toBeNull();
    expect(scenarioLabel(v2)).toBe("Device Preparation");
    expect(scenarioLabel({ ...v1, isSelfDeployingProfile: true })).toBe("Self-Deploying");
    expect(scenarioLabel({ ...v1, isPreProvisioned: true })).toBe("Pre-provisioned");
    expect(scenarioLabel({ ...v2, isPreProvisioned: true })).toBe("Device Preparation · Pre-provisioned");
  });
});
