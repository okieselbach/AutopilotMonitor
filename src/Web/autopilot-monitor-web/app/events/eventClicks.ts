/** The one custom event the event follow-up pages send: properties `event` (slug) and `button`. */
export const EVENT_FOLLOWUP_CLICKED = "event_followup_clicked";

interface TrackableTarget {
  closest(selector: string): { getAttribute(name: string): string | null } | null;
}

/** The data-track id of the clicked element or its nearest marked ancestor, else null. */
export function trackIdOf(target: TrackableTarget | null): string | null {
  return target?.closest("[data-track]")?.getAttribute("data-track") || null;
}
