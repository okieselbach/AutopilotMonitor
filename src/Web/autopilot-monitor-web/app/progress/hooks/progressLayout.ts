import { EnrollmentEvent, Session } from "@/types";
import { resolvePhaseLayout } from "@/app/sessions/utils/phaseConstants";

/**
 * Pure view-model for the end-user progress page. The page used to hardcode the seven
 * Autopilot v1 ESP phases and gate its live app panel on `currentPhase === 3 || 5`; both
 * broke every other enrollment rail (Device Preparation never declares phase 3, self-
 * deploying / SkipUserStatusPage devices never reach the user phases, a pre-provisioned
 * device waiting for its user is not "an issue"). Everything scenario-specific lives here
 * so the page only renders steps + a presentation.
 */

export interface ProgressStep {
  id: number;
  label: string;
  /** Step during which the live download/install panel is meaningful. */
  isAppsStep: boolean;
}

type ProgressSession = Pick<
  Session,
  | "status"
  | "currentPhase"
  | "enrollmentType"
  | "isPreProvisioned"
  | "isSelfDeployingProfile"
  | "isHybridJoin"
  | "isCloudPc"
  | "resumedAt"
>;

// End-user wording (the dashboard's phaseConstants carry the operator wording).
const V1_STEP_LABELS: Record<number, string> = {
  0: "Setup start",
  1: "Device preparation",
  2: "Device setup",
  3: "Installing apps (device)",
  4: "Account setup",
  5: "Installing apps (user)",
  6: "Finalizing setup",
};

const V2_STEP_LABELS: Record<number, string> = {
  0: "Setup start",
  1: "Device preparation",
  3: "Installing apps",
  6: "Finalizing setup",
};

const APPS_STEP_IDS: ReadonlySet<number> = new Set([3, 5]);
const COMPLETE_PHASE_ID = 7;
const FAILED_PHASE_ID = 99;

/**
 * Steps shown to the end user, derived from the same layout the session detail page uses
 * (v2 = Start / Device preparation / Installing apps / Finalizing; v1 = the ESP phases,
 * minus Account setup + user apps when the ESP policy skips the user status page). The
 * terminal "Complete" phase is not a step — success is the whole card.
 */
export function buildProgressSteps(
  session: Pick<ProgressSession, "enrollmentType" | "isPreProvisioned">,
  isSkipUserStatusPage: boolean,
): ProgressStep[] {
  const { phases, skippedPhaseIds } = resolvePhaseLayout({
    enrollmentType: session.enrollmentType,
    isSkipUserStatusPage,
    isPreProvisioned: session.isPreProvisioned,
  });
  const labels = session.enrollmentType === "v2" ? V2_STEP_LABELS : V1_STEP_LABELS;
  return phases
    .filter((p) => p.id !== COMPLETE_PHASE_ID && !skippedPhaseIds.has(p.id))
    .map((p) => ({ id: p.id, label: labels[p.id] ?? p.name, isAppsStep: APPS_STEP_IDS.has(p.id) }));
}

const PHASE_DECLARATION_TYPES: ReadonlySet<string> = new Set([
  "agent_started",
  "phase_changed",
  "esp_phase_changed",
  "phase_transition",
]);

/**
 * Highest phase the device ever declared (phase-declaration events only — every other
 * event carries Phase=Unknown by contract). -1 when nothing was declared. Used to place a
 * Failed session, whose stored currentPhase is the terminal 99 sentinel.
 */
export function lastDeclaredPhase(events: EnrollmentEvent[]): number {
  let max = -1;
  for (const e of events) {
    if (!PHASE_DECLARATION_TYPES.has(e.eventType)) continue;
    if (typeof e.phase !== "number" || e.phase < 0 || e.phase >= FAILED_PHASE_ID) continue;
    if (e.phase > max) max = e.phase;
  }
  return max;
}

export interface ResolveActiveStepParams {
  steps: ProgressStep[];
  session: Pick<ProgressSession, "status" | "currentPhase" | "enrollmentType">;
  events: EnrollmentEvent[];
  /** Any app download/install/summary evidence in the event stream. */
  hasAppActivity: boolean;
}

/**
 * Index of the step the device is on. Equals `steps.length` once the session Succeeded
 * (every step completed). Otherwise the last step whose phase id the device has reached —
 * tolerant of gaps in the layout (a v1 device in phase 4 on a skip-user layout lands on
 * "Installing apps (device)", a v2 device that declared 5 lands on "Installing apps").
 *
 * Device Preparation never declares the app phase (the agent's sub-phase declaration is
 * ESP-driven), so on v2 the app step is promoted from app-event evidence instead.
 */
export function resolveActiveStepIndex({ steps, session, events, hasAppActivity }: ResolveActiveStepParams): number {
  if (steps.length === 0) return 0;
  if (session.status === "Succeeded") return steps.length;

  let phase = session.currentPhase;
  if (phase === FAILED_PHASE_ID || session.status === "Failed") {
    phase = lastDeclaredPhase(events);
  }

  let index = 0;
  if (phase >= 0) {
    for (let i = 0; i < steps.length; i++) {
      if (steps[i].id <= phase) index = i;
    }
  }

  if (hasAppActivity) {
    const appsIndex = steps.findIndex((s) => s.isAppsStep);
    // v2: the only way to ever reach the app step. Failed-without-evidence: app activity
    // is the best available placement on any rail.
    if (appsIndex >= 0 && (session.enrollmentType === "v2" || phase < 0)) {
      index = Math.max(index, appsIndex);
    }
  }

  return Math.min(index, steps.length - 1);
}

export function computeOverallProgress(status: string, activeStepIndex: number, stepCount: number): number {
  if (status === "Succeeded") return 100;
  if (stepCount === 0) return 0;
  return Math.max(0, Math.min(100, Math.round((activeStepIndex / stepCount) * 100)));
}

export type PresentationKind = "working" | "waiting" | "success" | "failed" | "incomplete" | "unsupported";

export interface ProgressPresentation {
  kind: PresentationKind;
  title: string;
  detail?: string;
}

const CONTACT_IT = "Please contact your IT department.";

/**
 * Headline the end user sees. Mirrors the backend SessionStatus vocabulary instead of the
 * old InProgress / Succeeded / everything-else-is-red trichotomy: a pre-provisioned device
 * parked for its user, a stalled device and an Incomplete verdict are not failures.
 */
export function resolvePresentation(session: ProgressSession, events: EnrollmentEvent[]): ProgressPresentation {
  if (session.isCloudPc) {
    return {
      kind: "unsupported",
      title: "Windows 365 Cloud PC",
      detail: "Cloud PC setup progress is not shown here. " + CONTACT_IT,
    };
  }

  switch (session.status) {
    case "Succeeded":
      return { kind: "success", title: "Setup complete!" };
    case "Failed":
      return { kind: "failed", title: "Setup encountered an issue" };
    case "Incomplete":
      return { kind: "incomplete", title: "Setup did not finish", detail: CONTACT_IT };
    case "AwaitingUser":
      return awaitingUser(session);
  }

  // Non-terminal. A pre-provisioned device whose technician part finished and that has not
  // been resumed by its user is waiting, not working.
  if (
    session.isPreProvisioned &&
    !session.resumedAt &&
    events.some((e) => e.eventType === "whiteglove_complete")
  ) {
    return awaitingUser(session);
  }

  if (session.status === "Stalled") {
    return {
      kind: "working",
      title: "Setting up your device...",
      detail: "This is taking longer than usual. Keep the device powered on and connected.",
    };
  }

  return { kind: "working", title: "Setting up your device..." };
}

function awaitingUser(session: Pick<ProgressSession, "isPreProvisioned">): ProgressPresentation {
  return {
    kind: "waiting",
    title: "Waiting for you to sign in",
    detail: session.isPreProvisioned
      ? "The technician part of the setup is complete. Sign in on the device to continue."
      : "Device setup is complete. Sign in on the device to continue.",
  };
}

/**
 * Short scenario tag under the device name. Plain user-driven Autopilot v1 is the default
 * and gets no tag; anything the user might not expect is named.
 */
export function scenarioLabel(
  session: Pick<ProgressSession, "enrollmentType" | "isPreProvisioned" | "isSelfDeployingProfile" | "isCloudPc">,
): string | null {
  const parts: string[] = [];
  if (session.enrollmentType === "v2") parts.push("Device Preparation");
  else if (session.isSelfDeployingProfile) parts.push("Self-Deploying");
  else if (session.isCloudPc) parts.push("Cloud PC");
  if (session.isPreProvisioned) parts.push("Pre-provisioned");
  return parts.length > 0 ? parts.join(" · ") : null;
}
