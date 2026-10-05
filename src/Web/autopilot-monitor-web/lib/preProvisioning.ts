import type { EnrollmentEvent } from "@/types";

/**
 * A pre-provisioned device whose technician part (whiteglove_complete) finished and that no
 * user has resumed yet. The backend caps such a session at the last device phase, so a
 * phase-driven reading would keep "Installing apps (device)" running forever; the progress
 * headline, its step list and the live update hint must read the seal instead.
 */
export function isParkedAfterTechnicianPart(
  session: { isPreProvisioned?: boolean | null; resumedAt?: string | null },
  events: readonly Pick<EnrollmentEvent, "eventType">[],
): boolean {
  if (!session.isPreProvisioned || session.resumedAt) return false;
  let sealed = false;
  for (const e of events) {
    if (e.eventType === "whiteglove_resumed") return false;
    if (e.eventType === "whiteglove_complete") sealed = true;
  }
  return sealed;
}
