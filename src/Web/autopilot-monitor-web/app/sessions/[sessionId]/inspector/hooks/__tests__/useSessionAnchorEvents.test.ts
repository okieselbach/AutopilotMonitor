import { describe, expect, it } from "vitest";
import type { EnrollmentEvent } from "@/types";
import {
  filterAnchorEvents,
  isAnchorEvent,
} from "../useSessionAnchorEvents";

/**
 * Tests for the schema-driven anchor filter. The Inspector's "Lifecycle Anchors"
 * tab classifies an event as an anchor iff `data.decisionState` is a non-null
 * object — mirrors what the agent emitted exactly, even if the C# allowlist
 * evolves between agent versions. Pin the predicate so a future regress like
 * "anchor list also includes events with .data?.priorState only" or
 * "filter accidentally drops Death-Rattle (which has both keys)" gets caught.
 */

const mkEvent = (overrides: Partial<EnrollmentEvent> = {}): EnrollmentEvent => ({
  eventId: "e-default",
  sessionId: "s-1",
  timestamp: "2026-05-01T13:45:32Z",
  eventType: "agent_started",
  severity: "Info",
  source: "Agent",
  phase: -1,
  message: "msg",
  sequence: 0,
  ...overrides,
});

describe("isAnchorEvent", () => {
  it("returns true when data.decisionState is an object", () => {
    expect(
      isAnchorEvent(
        mkEvent({
          data: {
            decisionState: { schemaVersion: "decision-state-snapshot-v1", stepIndex: 1 },
          },
        }),
      ),
    ).toBe(true);
  });

  it("returns true even when decisionState is an empty object", () => {
    // Empty object is still a valid (degenerate) snapshot — the agent's builder
    // always returns at least the top-level allowlist keys, but a forwards-compat
    // mismatch shouldn't drop the row from the Inspector.
    expect(isAnchorEvent(mkEvent({ data: { decisionState: {} } }))).toBe(true);
  });

  it("returns false when data is undefined", () => {
    expect(isAnchorEvent(mkEvent({ data: undefined }))).toBe(false);
  });

  it("returns false when data.decisionState is missing", () => {
    expect(isAnchorEvent(mkEvent({ data: { other: "value" } }))).toBe(false);
  });

  it("returns false when data.decisionState is explicit null", () => {
    expect(isAnchorEvent(mkEvent({ data: { decisionState: null } }))).toBe(false);
  });

  it("returns false when data.decisionState is a primitive", () => {
    // Pre-PR2 agents that wrote decisionState as a JSON string accidentally
    // would not show in the anchors tab — render path expects a dict.
    expect(isAnchorEvent(mkEvent({ data: { decisionState: "snapshot-as-string" } }))).toBe(false);
    expect(isAnchorEvent(mkEvent({ data: { decisionState: 42 } }))).toBe(false);
  });

  it("returns true for Death-Rattle events that carry both decisionState and priorState", () => {
    // The Death-Rattle (Plan §B) special-case lives in the rendering layer; the
    // FILTER must still pick it up because the event carries decisionState (the
    // PR2 anchor enrichment fires for prior_run_died_with_state too).
    expect(
      isAnchorEvent(
        mkEvent({
          eventType: "prior_run_died_with_state",
          data: {
            previousExitType: "reboot_kill",
            priorState: { stage: "AwaitingHello" },
            decisionState: { stage: "AwaitingDesktop" },
          },
        }),
      ),
    ).toBe(true);
  });

  it("returns false for Death-Rattle-shaped events that only carry priorState", () => {
    // Defensive: if a future agent variant emits priorState without the
    // accompanying decisionState (e.g. an enrichment-bypass bug in PR2), the
    // anchors tab should drop the row rather than render half a card. The
    // operator can still find the event in the Signal Stream tab.
    expect(
      isAnchorEvent(
        mkEvent({
          eventType: "prior_run_died_with_state",
          data: {
            previousExitType: "reboot_kill",
            priorState: { stage: "AwaitingHello" },
          },
        }),
      ),
    ).toBe(false);
  });
});

describe("filterAnchorEvents", () => {
  it("returns only events that pass isAnchorEvent", () => {
    const input: EnrollmentEvent[] = [
      mkEvent({ eventId: "1", sequence: 1, eventType: "agent_started", data: { decisionState: { stepIndex: 1 } } }),
      mkEvent({ eventId: "2", sequence: 2, eventType: "app_install_completed" /* no decisionState */ }),
      mkEvent({ eventId: "3", sequence: 3, eventType: "desktop_arrived", data: { decisionState: { stepIndex: 3 } } }),
    ];
    const out = filterAnchorEvents(input);
    expect(out.map((e) => e.eventId)).toEqual(["1", "3"]);
  });

  it("sorts by sequence ascending", () => {
    // Backend may return events in any order — the Inspector renders chronologically.
    const input: EnrollmentEvent[] = [
      mkEvent({ eventId: "c", sequence: 30, data: { decisionState: { stepIndex: 30 } } }),
      mkEvent({ eventId: "a", sequence: 10, data: { decisionState: { stepIndex: 10 } } }),
      mkEvent({ eventId: "b", sequence: 20, data: { decisionState: { stepIndex: 20 } } }),
    ];
    const out = filterAnchorEvents(input);
    expect(out.map((e) => e.eventId)).toEqual(["a", "b", "c"]);
  });

  it("treats missing sequence as 0 for sort stability", () => {
    // Old events without a sequence field shouldn't crash sort; they just sink
    // to the front. (In practice every wire event has a sequence — this is
    // fail-soft behaviour for partially-deserialized backend responses.)
    const input: EnrollmentEvent[] = [
      mkEvent({ eventId: "with-seq", sequence: 5, data: { decisionState: {} } }),
      // @ts-expect-error — sequence intentionally omitted to test fail-soft
      { ...mkEvent({ eventId: "no-seq", data: { decisionState: {} } }), sequence: undefined },
    ];
    const out = filterAnchorEvents(input);
    expect(out[0].eventId).toBe("no-seq");
    expect(out[1].eventId).toBe("with-seq");
  });

  it("returns an empty array when no events have decisionState", () => {
    const input: EnrollmentEvent[] = [
      mkEvent({ eventId: "1", eventType: "app_install_completed" }),
      mkEvent({ eventId: "2", eventType: "performance_snapshot" }),
    ];
    expect(filterAnchorEvents(input)).toEqual([]);
  });

  it("returns an empty array for empty input", () => {
    expect(filterAnchorEvents([])).toEqual([]);
  });
});
