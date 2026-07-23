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

// Pure helper — per-block durations for WhiteGlove sessions.
// Duration 1 = pre-provisioning, Duration 2 = user enrollment, combined = D1 + D2
// (the sealed pause between reseal and resume is excluded).
//
// Block spans are anchored on the SEQUENCE-canonical edge events (first/last event by
// sequence), NOT on min/max over all timestamps in the block. Two real-world cases break
// the min/max approach:
//   - Events re-emitted after the Part-2 resume can legitimately carry historical event
//     times (e.g. an app_install_started the ImeLogTracker re-parses from the IME log,
//     stamped with the Part-1 install start). min() would stretch the User Enrollment
//     block back across the sealed pause.
//   - A single clock-skewed source timestamp (e.g. an IME log line with a wrong timezone
//     bias that slips under the agent's 1h future-skew clamp) would stretch the block
//     the other way.
// Sequence is the canonical event order, so the block edges by sequence are the robust
// span anchors. Part 1 additionally starts at session.startedAt (registration) when
// provided — the same anchor the backend uses for DurationSeconds.
export interface WhiteGloveDurations {
  preProvDuration: string | null;
  userEnrollDuration: string | null;
  combinedDuration: string | null;
}

export function computeWhiteGloveDurations(
  events: EnrollmentEvent[],
  splitSequence: number,
  startedAt?: string,
): WhiteGloveDurations {
  const preProvEvts = splitSequence < 0 ? events : events.filter(e => e.sequence <= splitSequence);
  const userEnrollEvts = splitSequence < 0 ? [] : events.filter(e => e.sequence > splitSequence);

  const spanMs = (evts: EnrollmentEvent[], startOverrideMs?: number): number => {
    if (evts.length === 0) return 0;
    let first = evts[0];
    let last = evts[0];
    for (const e of evts) {
      if (e.sequence < first.sequence) first = e;
      if (e.sequence > last.sequence) last = e;
    }
    const start = startOverrideMs ?? new Date(first.timestamp).getTime();
    const end = new Date(last.timestamp).getTime();
    return Math.max(0, end - start);
  };

  const fmt = (ms: number): string | null => {
    const sec = Math.round(ms / 1000);
    if (sec < 1) return null;
    if (sec < 60) return `${sec}s`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m ${sec % 60}s`;
    return `${Math.floor(sec / 3600)}h ${Math.floor((sec % 3600) / 60)}m`;
  };

  const startedAtMs = startedAt ? new Date(startedAt).getTime() : NaN;
  const preProvMs = spanMs(preProvEvts, Number.isFinite(startedAtMs) ? startedAtMs : undefined);
  const userEnrollMs = spanMs(userEnrollEvts);

  return {
    preProvDuration: fmt(preProvMs),
    userEnrollDuration: fmt(userEnrollMs),
    combinedDuration: fmt(preProvMs + userEnrollMs),
  };
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
