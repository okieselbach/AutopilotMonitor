import { describe, it, expect } from "vitest";
import { shouldScheduleFetch } from "../useSessionEvents";

/**
 * The policy behind the session page's "subscribe-then-fetch": the catch-up after the SignalR
 * group join must not queue a second full page walk behind one that is still running, while a
 * live signal (a burst after the walk finished, or during it) is always scheduled.
 */
describe("shouldScheduleFetch", () => {
  it("always schedules a live signal, walk in flight or not", () => {
    expect(shouldScheduleFetch("signal", false)).toBe(true);
    expect(shouldScheduleFetch("signal", true)).toBe(true);
  });

  it("runs the join catch-up only when no walk is in flight", () => {
    expect(shouldScheduleFetch("join-catch-up", false)).toBe(true);
    expect(shouldScheduleFetch("join-catch-up", true)).toBe(false);
  });
});
