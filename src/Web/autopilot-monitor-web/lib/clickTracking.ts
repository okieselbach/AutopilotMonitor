/**
 * Anonymous usage counts on the public marketing pages: clicks on elements marked
 * data-track="<id>" and sections marked data-track-section="<id>" (components/MarketingTracker.tsx).
 * Counts per button and section, never per person (D-304, D-305).
 */
export const MARKETING_CTA_CLICKED = "marketing_cta_clicked";
export const MARKETING_SECTION_VIEWED = "marketing_section_viewed";

/** Shape of data-track ids: lower-case words, `_` inside a word group, `:` before a value. */
export const TRACK_ID_PATTERN = /^[a-z0-9]+(?:[_-][a-z0-9]+)*(?::[a-z0-9]+(?:[_-][a-z0-9]+)*)?$/;

interface TrackableTarget {
  closest(selector: string): { getAttribute(name: string): string | null } | null;
}

/** The data-track id of the clicked element or its nearest marked ancestor, else null. */
export function trackIdOf(target: TrackableTarget | null): string | null {
  return target?.closest("[data-track]")?.getAttribute("data-track") || null;
}

/**
 * A section counts as seen once a third of it is on screen, or, for a section taller than the
 * viewport, once it fills half of the viewport.
 */
export function isSectionSeen(ratio: number, visibleHeight: number, viewportHeight: number): boolean {
  return ratio >= 1 / 3 || (viewportHeight > 0 && visibleHeight >= viewportHeight / 2);
}
