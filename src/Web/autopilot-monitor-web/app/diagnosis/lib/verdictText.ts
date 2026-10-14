import type { DiagnosisCulprit, DiagnosisHeadline } from "@/lib/generated/wire-types.generated";
import { stepName, stepPath } from "./diagnosisFormat";
import { vocabularyEntry, type Outcome } from "./vocabulary";

/** Status-band tone; the same families as the session status badge. */
export type VerdictTone = "red" | "orange" | "sky" | "slate" | "green" | "amber" | "blue" | "gray";

export interface VerdictText {
  tone: VerdictTone;
  /** The status band, e.g. "Enrollment failed in Account setup › Apps". */
  title: string;
  /** The label above the answer, e.g. "Why it failed". */
  heading: string;
  /** The answer in one sentence. */
  summary: string;
  /** Supporting sentences. */
  details: string[];
}

const TONES: Record<Outcome, VerdictTone> = {
  failed: "red",
  stalled: "orange",
  awaiting_user: "sky",
  incomplete: "slate",
  succeeded: "green",
  in_progress: "blue",
  pending: "amber",
  unknown: "gray",
};

const HEADINGS: Record<Outcome, string> = {
  failed: "Why it failed",
  stalled: "Why it is stuck",
  awaiting_user: "What it is waiting for",
  incomplete: "Why it did not finish",
  succeeded: "How it ended",
  in_progress: "Where it stands",
  pending: "Where it stands",
  unknown: "What we know",
};

const AGENT_REASONS: Record<string, string> = {
  oobe_update_not_finished: "The Windows update at the end of OOBE did not finish.",
  esp_exit_without_completion_evidence: "The ESP closed, but no completion signal followed.",
  esp_recovered_without_completion_evidence: "The ESP recovered from an error, but setup never reported completion.",
  effect_infrastructure_failure: "The agent hit an internal error and stopped.",
};

/** The verdict as the page states it, from the headline alone. */
export function verdictText(h: DiagnosisHeadline): VerdictText {
  const step = stepPath(h.failedStep?.category, h.failedStep?.subcategory);
  return {
    tone: h.cause === "soft_failure" ? "amber" : (vocabularyEntry(TONES, h.outcome) ?? "gray"),
    title: bandTitle(h, step),
    heading: vocabularyEntry(HEADINGS, h.outcome) ?? HEADINGS.unknown,
    ...answer(h, step),
  };
}

function bandTitle(h: DiagnosisHeadline, step: string | null): string {
  switch (h.outcome) {
    case "failed":
      return `${h.cause === "whiteglove_failure" ? "Pre-provisioning failed" : "Enrollment failed"}${step ? ` in ${step}` : ""}`;
    case "stalled":
      return "Enrollment stalled";
    case "awaiting_user":
      return "Waiting for the user";
    case "incomplete":
      return "Enrollment incomplete";
    case "succeeded":
      return h.cause === "soft_failure" ? "Enrollment succeeded after an ESP error" : "Enrollment succeeded";
    case "in_progress":
      return "Enrollment in progress";
    case "pending":
      return "Pre-provisioned, waiting for the user";
    default:
      return "Outcome unknown";
  }
}

function answer(h: DiagnosisHeadline, step: string | null): { summary: string; details: string[] } {
  const apps = (h.culprits ?? []).filter((c) => c.kind === "app");
  const failedApps = apps.filter((c) => c.evidence === "install_failed" || c.evidence === "presumed_failed");
  const first = failedApps[0] ?? apps[0];
  const code = h.failedStep?.errorCode;
  const origin = h.verdictPath.split(":")[0];

  switch (h.cause) {
    case "esp_app_failure":
      return {
        summary:
          failedApps.length > 1
            ? `${failedApps.length} required apps failed, so the ESP ended the enrollment.`
            : `${first?.name ?? "A required app"} could not be installed, so the ESP ended the enrollment.`,
        details: [...appSentences(first, code), ...continueSentence(h)],
      };
    case "esp_app_timeout":
      return {
        summary:
          apps.length > 1
            ? `The ESP stopped while ${apps.length} required apps had not finished.`
            : first?.evidence === "never_started"
              ? `The ESP stopped while ${first.name} had not started installing.`
              : `The ESP stopped while ${first?.name ?? "a required app"} was still installing.`,
        details: [
          ...(h.espConfig?.timeoutMinutes ? [`The ESP time limit is ${h.espConfig.timeoutMinutes} minutes.`] : []),
          ...continueSentence(h),
        ],
      };
    case "esp_step_failure": {
      const name = stepName(h.failedStep?.subcategory) ?? h.failedStep?.failureType ?? "An ESP step";
      return {
        summary: `${name === "An ESP step" ? name : `The ESP step ${name}`} failed${code ? ` with ${code}` : ""}.`,
        details: (h.culprits ?? []).map((c) =>
          c.kind === "appx_package" ? `The AppX log points at ${c.name}.` : `The policy provider ${c.name} did not finish.`
        ),
      };
    }
    case "whiteglove_failure":
      return {
        summary: `Pre-provisioning failed${step ? ` at ${step}` : ""}${code ? ` with ${code}` : ""}.`,
        details: first ? [`${first.name} did not install in the technician phase.`] : [],
      };
    case "agent_failure": {
      const reason = h.failedStep?.reason;
      return { summary: (reason && AGENT_REASONS[reason]) ?? `The agent reported a failure: ${reason ?? h.reasonText ?? "no reason given"}.`, details: [] };
    }
    case "rule_ko":
      return {
        summary: h.ruleId ? `The analyze rule ${h.ruleId} marked this session as failed.` : "An analyze rule marked this session as failed.",
        details: h.reasonText ? [h.reasonText] : [],
      };
    case "admin_marked":
      return { summary: `An administrator marked this session as ${h.outcome === "succeeded" ? "succeeded" : "failed"}.`, details: [] };
    case "agent_gave_up":
      return {
        summary:
          origin === "maxlife"
            ? "The agent reached its maximum lifetime before the enrollment finished."
            : origin === "stopwait"
              ? "The agent stopped waiting for the user to sign in."
              : h.verdictPath.endsWith(":r3")
                ? "The agent's emergency break ended the session."
                : "The agent stopped before the enrollment finished.",
        details: h.reasonText ? [h.reasonText] : [],
      };
    case "sweep_timeout":
      return { summary: "No completion arrived before the timeout, so the platform closed the session.", details: h.reasonText ? [h.reasonText] : [] };
    case "superseded":
      return { summary: "A newer enrollment of this device replaced this session.", details: [] };
    case "agent_silent":
      return { summary: "The agent stopped sending data.", details: h.reasonText ? [h.reasonText] : [] };
    case "no_progress":
      return { summary: "The agent reported no progress for an hour.", details: h.reasonText ? [h.reasonText] : [] };
    case "awaiting_user":
      return {
        summary:
          h.verdictPath.includes("wg_awaiting") || h.verdictPath.includes("r1b")
            ? "Pre-provisioning finished; the device is waiting for its user."
            : "Device setup finished; the enrollment is waiting for the user.",
        details: [],
      };
    case "whiteglove_sealed":
      return { summary: "Pre-provisioning finished and the device was sealed for its user.", details: [] };
    case "reconciled":
      return { summary: "The platform declared this session complete without a completion report.", details: h.reasonText ? [h.reasonText] : [] };
    case "soft_failure":
      return {
        summary: `The ESP failed${step ? ` in ${step}` : ""}, the user continued anyway, and setup completed.`,
        details: first ? [appOutcome(first, code, first.name) ?? `${first.name} had not finished when the ESP failed.`] : [],
      };
    case "completed":
      return { summary: "The enrollment completed.", details: [] };
    case "none":
      return { summary: "The enrollment is still running.", details: [] };
    default:
      return { summary: h.reasonText ?? "The outcome could not be attributed.", details: [] };
  }
}

function appSentences(app: DiagnosisCulprit | undefined, stepCode: string | null | undefined): string[] {
  if (!app) return [];
  const target = app.targeted?.toLowerCase();
  const outcome = appOutcome(app, stepCode, "It");
  return [...(target === "user" || target === "device" ? [`The app is required for the ${target}.`] : []), ...(outcome ? [outcome] : [])];
}

/** What happened to the app, with `subject` naming it; null when its evidence tells no more than its name. */
function appOutcome(app: DiagnosisCulprit, stepCode: string | null | undefined, subject: string): string | null {
  const starts = app.installStarts && app.installStarts > 1 ? ` after ${app.installStarts} install starts` : "";
  if (app.evidence === "presumed_failed") return `${subject} was still installing when the ESP failed${stepCode ? ` with ${stepCode}` : ""}.`;
  if (app.errorCode) return `${subject} failed with ${app.errorCode}${starts}.`;
  if (app.detectionResult === "NotDetected") return `${subject} finished installing${starts}, but its detection rule did not find the app.`;
  return null;
}

function continueSentence(h: DiagnosisHeadline): string[] {
  if (h.espConfig?.continueAnyway === false) return ["This ESP does not let users continue after an install error."];
  if (h.espConfig?.continueAnyway === true) return ["This ESP lets users continue anyway."];
  return [];
}
