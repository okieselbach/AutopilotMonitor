import { EnrollmentEvent } from "@/types";

/**
 * SkipUserStatusPage as the agent read it from the ESP policy (`esp_config_detected`).
 * Shared by the session detail page and the progress portal so both hide the user
 * phases for the same sessions. Never true on Device Preparation (v2) — there is no ESP
 * and therefore no user status page to skip.
 */
export function detectSkipUserStatusPage(
  events: EnrollmentEvent[],
  enrollmentType: string | undefined,
): boolean {
  if (enrollmentType === "v2") return false;
  const espConfigEvent = events.find((e) => e.eventType === "esp_config_detected");
  if (!espConfigEvent?.data) return false;
  const val = espConfigEvent.data.skipUserStatusPage;
  return val === true || val === "True" || val === "true";
}
