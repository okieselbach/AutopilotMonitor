import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createBurstScheduler } from "../burstScheduler";

/**
 * The scheduler decides WHEN the session timeline refetches on SignalR signals.
 * Shape under test (trailing 300 ms, max wait 1000 ms — the session page's values):
 *  - an isolated signal refetches at once (real-time feel),
 *  - a short burst refetches at its start and once after it ends,
 *  - a stream that never pauses refetches exactly once per second.
 */

const TRAILING = 300;
const MAX_WAIT = 1000;

function harness() {
  const runs: number[] = [];
  const t0 = Date.now();
  const scheduler = createBurstScheduler(() => runs.push(Date.now() - t0), {
    trailingMs: TRAILING,
    maxWaitMs: MAX_WAIT,
  });
  return { runs, scheduler };
}

describe("createBurstScheduler", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("runs an isolated trigger immediately (leading edge)", () => {
    const { runs, scheduler } = harness();
    scheduler.trigger();
    expect(runs).toEqual([0]);
    vi.advanceTimersByTime(5_000);
    expect(runs).toEqual([0]);
  });

  it("runs a short burst at its start and once after it ends", () => {
    const { runs, scheduler } = harness();
    scheduler.trigger(); // 0 ms
    vi.advanceTimersByTime(200);
    scheduler.trigger(); // 200 ms
    vi.advanceTimersByTime(200);
    scheduler.trigger(); // 400 ms
    vi.advanceTimersByTime(5_000);
    // 0 = leading, 700 = trailing (400 + 300); the 200-ms trigger was absorbed.
    expect(runs).toEqual([0, 700]);
  });

  it("caps a stream that never pauses at one run per max wait", () => {
    const { runs, scheduler } = harness();
    for (let t = 0; t <= 3_000; t += 100) {
      scheduler.trigger();
      vi.advanceTimersByTime(100);
    }
    vi.advanceTimersByTime(5_000);
    // Leading at 0, then max-wait bound at 1000/2000/3000, then the trailing run
    // 300 ms after the last trigger (3000 + 300).
    expect(runs).toEqual([0, 1000, 2000, 3000, 3300]);
  });

  it("does not run twice inside one trailing window right after a run", () => {
    const { runs, scheduler } = harness();
    scheduler.trigger(); // leading at 0
    vi.advanceTimersByTime(100);
    scheduler.trigger(); // 100 ms: cool-down, becomes a trailing run at 400
    vi.advanceTimersByTime(5_000);
    expect(runs).toEqual([0, 400]);
  });

  it("is leading again once the trailing window has been quiet", () => {
    const { runs, scheduler } = harness();
    scheduler.trigger(); // 0
    vi.advanceTimersByTime(TRAILING);
    scheduler.trigger(); // 300: quiet for a full trailing window since the last run
    expect(runs).toEqual([0, 300]);
  });

  it("cancel drops the pending trailing run only", () => {
    const { runs, scheduler } = harness();
    scheduler.trigger(); // 0
    vi.advanceTimersByTime(100);
    scheduler.trigger(); // pending trailing at 400
    scheduler.cancel();
    vi.advanceTimersByTime(5_000);
    expect(runs).toEqual([0]);
    // Cancel is not a reset: a later trigger still follows the normal rules.
    scheduler.trigger();
    expect(runs).toEqual([0, 5_100]);
  });

  it("rejects a max wait shorter than the trailing window", () => {
    expect(() => createBurstScheduler(() => {}, { trailingMs: 500, maxWaitMs: 100 })).toThrow();
  });
});
