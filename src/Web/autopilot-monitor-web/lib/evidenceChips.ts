/**
 * The events a finding's evidence points at, one chip per distinct event: a condition's own
 * `eventId`, and both events of a correlation (`eventA_eventId`, `eventB_eventId`). Confidence
 * factors (`factor_*`) and scalar entries carry no event.
 */
export interface EvidenceChip {
  /** The signal that recorded the event (first one wins when several point at the same event). */
  signal: string;
  eventId: string;
  /** Human label: the event type, else the signal name. */
  label: string;
  timestamp?: string;
}

export function evidenceChips(matchedConditions: Record<string, unknown> | null | undefined): EvidenceChip[] {
  const chips: EvidenceChip[] = [];
  const seen = new Set<string>();
  const add = (signal: string, eventId: unknown, eventType: unknown, timestamp: unknown) => {
    if (typeof eventId !== "string" || eventId === "" || seen.has(eventId)) return;
    seen.add(eventId);
    chips.push({
      signal,
      eventId,
      label: humanizeKey(typeof eventType === "string" && eventType !== "" ? eventType : signal),
      timestamp: typeof timestamp === "string" && timestamp !== "" ? timestamp : undefined,
    });
  };

  for (const [signal, raw] of Object.entries(matchedConditions ?? {})) {
    if (signal.startsWith("factor_") || !raw || typeof raw !== "object" || Array.isArray(raw)) continue;
    const e = raw as Record<string, unknown>;
    add(signal, e.eventId, e.eventType, e.timestamp);
    add(signal, e.eventA_eventId, e.eventA_eventType, e.eventA_timestamp);
    add(signal, e.eventB_eventId, e.eventB_eventType, e.eventB_timestamp);
  }
  return chips;
}

/** "app_install_failed" → "App install failed", "failedSubcategory" → "Failed subcategory". */
export function humanizeKey(key: string): string {
  const words = key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_\-.]+/g, " ")
    .trim()
    .toLowerCase();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : key;
}
