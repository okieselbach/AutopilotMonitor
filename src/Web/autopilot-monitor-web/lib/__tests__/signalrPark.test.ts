import { describe, it, expect } from "vitest";
import { createParkController, PARK_AFTER_HIDDEN_MS, type ParkTimers } from "../signalrPark";

/**
 * The park controller decides when a hidden tab's SignalR connection is stopped and when it comes
 * back. Timers and the clock are driven by hand (the injectable seam), so every threshold is exact.
 */

const AFTER = 240_000;

function harness(options: { refuse?: number } = {}) {
  let now = 0;
  let nextId = 1;
  const pending = new Map<number, { at: number; fn: () => void }>();
  const timers: ParkTimers = {
    setTimeout: (fn, ms) => {
      const id = nextId++;
      pending.set(id, { at: now + ms, fn });
      return id;
    },
    clearTimeout: (handle) => { pending.delete(handle as number); },
    now: () => now,
  };
  const advance = (ms: number) => {
    const until = now + ms;
    for (;;) {
      const due = Array.from(pending.entries())
        .filter(([, t]) => t.at <= until)
        .sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      pending.delete(due[0]);
      now = due[1].at;
      due[1].fn();
    }
    now = until;
  };
  const parks: number[] = [];
  const resumes: number[] = [];
  let refusals = options.refuse ?? 0;
  const controller = createParkController({
    parkAfterHiddenMs: AFTER,
    park: (hiddenMs) => {
      parks.push(hiddenMs);
      if (refusals > 0) {
        refusals--;
        return false;
      }
      return true;
    },
    resume: (parkedMs) => { resumes.push(parkedMs); },
    timers,
  });
  return { controller, parks, resumes, advance, pendingCount: () => pending.size };
}

describe("createParkController", () => {
  it("never parks a visible tab", () => {
    const { controller, parks, advance, pendingCount } = harness();
    controller.setVisible(true);
    advance(10 * AFTER);
    expect(parks).toEqual([]);
    expect(pendingCount()).toBe(0);
    expect(controller.isParked()).toBe(false);
  });

  it("leaves a short tab switch alone", () => {
    const { controller, parks, resumes, advance, pendingCount } = harness();
    controller.setVisible(false);
    advance(AFTER - 1);
    controller.setVisible(true);
    advance(10 * AFTER);
    expect(parks).toEqual([]);
    expect(resumes).toEqual([]);
    expect(pendingCount()).toBe(0);
  });

  it("parks once the tab was hidden for the whole period", () => {
    const { controller, parks, advance } = harness();
    controller.setVisible(false);
    advance(AFTER);
    expect(parks).toEqual([AFTER]);
    expect(controller.isParked()).toBe(true);
    advance(10 * AFTER);
    expect(parks).toHaveLength(1);
  });

  it("resumes on the first visible signal after a park and reports how long it was parked", () => {
    const { controller, resumes, advance } = harness();
    controller.setVisible(false);
    advance(AFTER);
    advance(90_000);
    controller.setVisible(true);
    controller.setVisible(true);
    expect(resumes).toEqual([90_000]);
    expect(controller.isParked()).toBe(false);
  });

  it("parks again after the next full hidden period", () => {
    const { controller, parks, resumes, advance } = harness();
    controller.setVisible(false);
    advance(AFTER);
    controller.setVisible(true);
    controller.setVisible(false);
    advance(AFTER - 1);
    expect(parks).toHaveLength(1);
    advance(1);
    expect(parks).toEqual([AFTER, AFTER]);
    expect(resumes).toEqual([0]);
  });

  it("does not restart the period on a repeated hidden signal", () => {
    const { controller, parks, advance } = harness();
    controller.setVisible(false);
    advance(100_000);
    controller.setVisible(false);
    advance(AFTER - 100_000);
    expect(parks).toEqual([AFTER]);
  });

  it("reports the real hidden time when a throttled timer fires late", () => {
    // In a hidden tab the timer callback can run late; the report measures from the hide.
    const timer: { due?: () => void } = {};
    let now = 0;
    const parks: number[] = [];
    const controller = createParkController({
      parkAfterHiddenMs: AFTER,
      park: (hiddenMs) => { parks.push(hiddenMs); return true; },
      resume: () => {},
      timers: { setTimeout: (fn) => { timer.due = fn; return 1; }, clearTimeout: () => {}, now: () => now },
    });
    controller.setVisible(false);
    now = AFTER + 55_000;
    timer.due?.();
    expect(parks).toEqual([AFTER + 55_000]);
    expect(controller.isParked()).toBe(true);
  });

  it("tries again after another period when the park is refused, measuring from the hide", () => {
    const { controller, parks, advance } = harness({ refuse: 1 });
    controller.setVisible(false);
    advance(AFTER);
    expect(controller.isParked()).toBe(false);
    advance(AFTER);
    expect(parks).toEqual([AFTER, 2 * AFTER]);
    expect(controller.isParked()).toBe(true);
  });

  it("drops a refused park's retry when the tab comes back", () => {
    const { controller, parks, resumes, advance, pendingCount } = harness({ refuse: 1 });
    controller.setVisible(false);
    advance(AFTER);
    controller.setVisible(true);
    advance(10 * AFTER);
    expect(parks).toHaveLength(1);
    expect(resumes).toEqual([]);
    expect(pendingCount()).toBe(0);
  });

  it("dispose cancels a pending park and never resumes", () => {
    const { controller, parks, resumes, advance, pendingCount } = harness();
    controller.setVisible(false);
    controller.dispose();
    advance(10 * AFTER);
    expect(parks).toEqual([]);
    expect(pendingCount()).toBe(0);
    controller.setVisible(true);
    expect(resumes).toEqual([]);
  });

  it("rejects a non-positive period", () => {
    expect(() => createParkController({ parkAfterHiddenMs: 0, park: () => true, resume: () => {} })).toThrow();
    expect(() => createParkController({ parkAfterHiddenMs: Number.NaN, park: () => true, resume: () => {} })).toThrow();
  });

  it("parks below the five-minute timer throttling and above Collect Logs' three-minute wait", () => {
    expect(PARK_AFTER_HIDDEN_MS).toBeLessThan(5 * 60_000);
    expect(PARK_AFTER_HIDDEN_MS).toBeGreaterThan(3 * 60_000);
  });
});
