import type { EnrollmentEvent } from "@/types";
import type { DiagnosisCulprit } from "@/lib/generated/wire-types.generated";
import { humanizeKey } from "@/lib/evidenceChips";

export type KeyEventTone = "failed" | "warning" | "done" | "neutral";

export interface KeyEvent {
  eventId: string;
  timestamp: string;
  title: string;
  detail: string | null;
  tone: KeyEventTone;
}

/** Milestones and verdict events; app events only for the headline's culprits. */
const KEY_TYPES = new Set([
  "phase_transition",
  "esp_phase_changed",
  "esp_failure_settle_started",
  "esp_failure_advisory",
  "esp_failure_recovered",
  "enrollment_failed",
  "enrollment_complete",
  "whiteglove_complete",
  "desktop_arrived",
  "system_reboot_detected",
  "hello_provisioning_completed",
]);

const APP_TYPES = new Set(["app_install_started", "app_install_failed", "app_install_completed", "app_post_install_detection"]);
const DONE_TYPES = new Set(["enrollment_complete", "whiteglove_complete", "hello_provisioning_completed", "desktop_arrived"]);

/**
 * The story of the session in a dozen events: milestones, the culprit apps' attempts and every error,
 * in order. A longer story keeps its first two events and its end — where the verdict was decided.
 */
export function keyEvents(events: EnrollmentEvent[], culprits: DiagnosisCulprit[] | null | undefined, max = 12): { items: KeyEvent[]; total: number } {
  const culpritKeys = new Set(
    (culprits ?? []).filter((c) => c.kind === "app").flatMap((c) => [c.key, c.name]).filter((k): k is string => !!k).map((k) => k.toLowerCase())
  );
  const isCulpritEvent = (e: EnrollmentEvent) => {
    const appId = String(e.data?.appId ?? "").toLowerCase();
    const appName = String(e.data?.appName ?? "").toLowerCase();
    return (appId !== "" && culpritKeys.has(appId)) || (appName !== "" && culpritKeys.has(appName));
  };

  const { unique: picked } = dedupe(
    events.filter(
      (e) =>
        KEY_TYPES.has(e.eventType) ||
        (APP_TYPES.has(e.eventType) && isCulpritEvent(e)) ||
        e.severity === "Error" ||
        e.severity === "Critical"
    )
  );
  const items = picked.length > max ? [...picked.slice(0, 2), ...picked.slice(picked.length - (max - 2))] : picked;
  return {
    total: picked.length,
    items: items.map((e) => ({
      eventId: e.eventId,
      timestamp: e.timestamp,
      title: e.message && e.message.length <= 140 ? e.message : humanizeKey(e.eventType),
      detail: typeof e.data?.errorCode === "string" && e.data.errorCode !== "" ? e.data.errorCode : null,
      tone:
        e.severity === "Error" || e.severity === "Critical"
          ? "failed"
          : e.severity === "Warning"
            ? "warning"
            : DONE_TYPES.has(e.eventType)
              ? "done"
              : "neutral",
    })),
  };
}

export interface ProblemEvent {
  eventId: string;
  timestamp: string;
  severity: string;
  source: string;
  message: string;
  /** How many identical events this row stands for. */
  count: number;
}

/** Errors and warnings in order; the same event arriving twice at the same second is listed once. */
export function problemEvents(events: EnrollmentEvent[]): ProblemEvent[] {
  const { unique, counts } = dedupe(events.filter((e) => e.severity === "Error" || e.severity === "Critical" || e.severity === "Warning"));
  return unique.map((e) => ({
    eventId: e.eventId,
    timestamp: e.timestamp,
    severity: e.severity,
    source: e.source,
    message: e.message || humanizeKey(e.eventType),
    count: counts.get(identity(e)) ?? 1,
  }));
}

function identity(e: EnrollmentEvent): string {
  return `${e.eventType}|${e.message}|${(e.timestamp ?? "").slice(0, 19)}`;
}

function dedupe(events: EnrollmentEvent[]): { unique: EnrollmentEvent[]; counts: Map<string, number> } {
  const counts = new Map<string, number>();
  const unique: EnrollmentEvent[] = [];
  for (const e of events) {
    const id = identity(e);
    const seen = counts.get(id);
    counts.set(id, (seen ?? 0) + 1);
    if (seen === undefined) unique.push(e);
  }
  return { unique, counts };
}
