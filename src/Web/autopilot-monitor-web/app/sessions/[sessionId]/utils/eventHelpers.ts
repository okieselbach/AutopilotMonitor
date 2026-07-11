import { EnrollmentEvent } from "@/types";

// Pure helper — finds the WhiteGlove "Part 1 ends, Part 2 begins" split sequence.
//
// Returns the sequence number AT WHICH (inclusive) Part 1 ends; events with sequence
// strictly greater belong to Part 2. Returns -1 when no split point can be derived
// (Part 1 still in progress, or session is not WhiteGlove at all).
//
// Resolution order — mirrors the agent-side V1-symmetric resume mechanic (PR-A):
//   1. `whiteglove_resumed` indicates a Part-2 resume happened. The boundary the user
//      perceives is the Part-2 boot itself, not the resumed marker that follows it
//      ~0–2s later. Look for the `agent_started` that sits between whiteglove_complete
//      and whiteglove_resumed and split THERE; this keeps the post-reseal boot
//      (agent_started + its agent_version_check) inside the User Enrollment block.
//      Falls back to `resumed.sequence - 1` if no agent_started is found in between.
//   2. Older agents that never emit `whiteglove_resumed`: the first `agent_started`
//      AFTER `whiteglove_complete` is the Part 2 boot. Returns its sequence - 1.
//   3. Pre-provisioning only (no Part 2 boot yet — neither `whiteglove_resumed` nor a
//      post-close `agent_started`): there is NO User Enrollment part, so EVERY event
//      belongs to Pre-Provisioning. Split AT the highest sequence in the session. This
//      deliberately swallows any straggler the periodic collectors flush AFTER
//      `whiteglove_part1_complete` (e.g. a trailing `power_state_check` or
//      `performance_snapshot` emitted by the still-draining Part-1 agent) — such an event
//      is not a resume and must not spawn a phantom "User Enrollment / Resumed" block.
//      Splitting at the last cleanup marker instead would strand exactly those stragglers
//      in Part 2. The whole cleanup tail (software_inventory_analysis, the duplicate
//      whiteglove_complete from DecisionEngine, local_admin_analysis, agent_shutting_down,
//      whiteglove_part1_complete) therefore also stays in Part 1 as before.
//   4. Nothing: -1.
export function computeWhiteGloveSplitSequence(events: EnrollmentEvent[]): number {
  const wgEvent = events.find(e => e.eventType === "whiteglove_complete");
  const resumedEvent = events.find(e => e.eventType === "whiteglove_resumed");

  if (resumedEvent) {
    // Lower bound = whiteglove_complete iff it precedes resumed; in the race case
    // (Windows writes whiteglove_complete AFTER the Part-2 reboot) we drop the bound to 0.
    const lowerBound = (wgEvent && wgEvent.sequence < resumedEvent.sequence)
      ? wgEvent.sequence
      : 0;
    // The most recent agent_started before whiteglove_resumed is the post-reseal Part-2 boot.
    // Sort candidates desc so we don't depend on the input being pre-sorted by sequence.
    const part2Boot = events
      .filter(e =>
        e.eventType === "agent_started" &&
        e.sequence > lowerBound &&
        e.sequence < resumedEvent.sequence
      )
      .sort((a, b) => b.sequence - a.sequence)[0];
    if (part2Boot) return part2Boot.sequence - 1;
    return resumedEvent.sequence - 1;
  }

  if (wgEvent) {
    const nextStart = events.find(e =>
      e.eventType === "agent_started" && e.sequence > wgEvent.sequence
    );
    if (nextStart) return nextStart.sequence - 1;

    // No Part-2 boot exists (no post-close agent_started, and we already know there is no
    // whiteglove_resumed). There is therefore no User Enrollment part yet: keep the whole
    // event stream — cleanup tail AND any trailing collector straggler — in Pre-Provisioning
    // by splitting at the highest sequence in the session. A lone straggler after
    // whiteglove_part1_complete is not a resume and must not open a phantom Part 2.
    return events.reduce((max, e) => (e.sequence > max ? e.sequence : max), wgEvent.sequence);
  }
  return -1;
}

// Pure helper — groups a flat event list into phase buckets.
// Extracted from the useMemo so it can be called multiple times for WhiteGlove split timelines.
//
// PHASE STRATEGY: Only a small set of events carry a non-Unknown phase (esp_phase_changed,
// agent_started). These are "phase-declaration events" that open a new phase in the timeline.
// All other events use Phase=Unknown and get sorted into the currently active phase.
// This means the number of phase-declaration events must match the number of distinct phases
// shown in the timeline — no duplicates, no accidental phase tags on analyzer/lifecycle events.
//
// preventPhaseRegression: when true, once the phase advances past a certain point it cannot
// regress to an earlier phase. Used for WhiteGlove Part 2 (User Enrollment) to absorb
// mid-enrollment reboots that emit a new agent_started (Phase=Start) without disrupting the
// timeline flow. The reboot events stay in whatever phase was active before the reboot.
export function groupEventsByPhase(
  events: EnrollmentEvent[],
  phaseNamesMap: Record<number, string>,
  phaseOrder: string[],
  options?: { preventPhaseRegression?: boolean }
): { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] } {
  const sortedEvents = [...events]
    .sort((a, b) => {
      const seqDiff = a.sequence - b.sequence;
      if (seqDiff !== 0) return seqDiff;
      // Fallback: timestamp breaks ties when sequence counter was not persisted before reboot
      return (a.timestamp ?? "").localeCompare(b.timestamp ?? "");
    })
    .map(e => ({ ...e, phaseName: phaseNamesMap[e.phase] ?? "Unknown" }));

  const preventRegression = options?.preventPhaseRegression === true;
  const eventsByPhase: Record<string, EnrollmentEvent[]> = {};
  let currentActivePhaseName = "Start";
  let maxPhaseIndex = 0;

  for (const event of sortedEvents) {
    let targetPhase = event.phaseName || "Unknown";
    if (targetPhase !== "Unknown") {
      if (preventRegression) {
        const candidateIndex = phaseOrder.indexOf(targetPhase);
        if (candidateIndex >= 0 && candidateIndex >= maxPhaseIndex) {
          currentActivePhaseName = targetPhase;
          maxPhaseIndex = candidateIndex;
        } else {
          // Phase would regress (e.g. reboot agent_started) — keep current phase
          targetPhase = currentActivePhaseName;
        }
      } else {
        currentActivePhaseName = targetPhase;
      }
    } else {
      targetPhase = currentActivePhaseName;
    }
    if (!eventsByPhase[targetPhase]) eventsByPhase[targetPhase] = [];
    eventsByPhase[targetPhase].push(event);
  }

  // Order phase sections chronologically by first event sequence (not hardcoded).
  // This ensures the display always matches the actual event sequence — critical when
  // SkipUserStatusPage=true reorders phases (FinalizingSetup before AppsUser).
  const orderedPhases = Object.keys(eventsByPhase)
    .filter(p => eventsByPhase[p]?.length > 0)
    .sort((a, b) => {
      const aFirst = eventsByPhase[a][0]?.sequence ?? 0;
      const bFirst = eventsByPhase[b][0]?.sequence ?? 0;
      return aFirst - bFirst;
    });
  return { eventsByPhase, orderedPhases };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function normalizeJsonLikeValue(value: unknown): any {
  if (typeof value === "string") {
    const trimmed = value.trim();
    const looksLikeJson =
      (trimmed.startsWith("{") && trimmed.endsWith("}")) ||
      (trimmed.startsWith("[") && trimmed.endsWith("]"));

    if (!looksLikeJson) return value;

    try {
      return normalizeJsonLikeValue(JSON.parse(value));
    } catch {
      return value;
    }
  }

  if (Array.isArray(value)) {
    return value.map(normalizeJsonLikeValue);
  }

  if (value && typeof value === "object") {
    const normalized: Record<string, any> = {};
    for (const [k, v] of Object.entries(value)) {
      normalized[k] = normalizeJsonLikeValue(v);
    }
    return normalized;
  }

  return value;
}

export function normalizeEventDataForDisplay(data?: Record<string, any>): Record<string, any> | null {
  if (!data) return null;
  return normalizeJsonLikeValue(data);
}

// Shortens SemVer+git-hash build metadata (e.g. "1.0.987+4da1540f8c64d8ff5f7ab75d9c5f0b8dd3506bfa")
// to the first 7 hash chars for display. Full hash is preserved in event.data / JSON details.
export function shortenBuildHashInMessage(message?: string | null): string {
  if (!message) return "";
  return message.replace(/\+([0-9a-f]{7})[0-9a-f]+/gi, "+$1");
}
