import { describe, it, expect } from "vitest";
import { FRAMES, STILL_FRAME } from "@/components/landing/PushWatchDemo";

/** The landing's Notify me when done demo is a frame table; these pin its pacing and its end states. */
describe("PushWatchDemo frames", () => {
  it("one loop lasts about ten seconds", () => {
    const total = FRAMES.reduce((sum, frame) => sum + frame.ms, 0);
    expect(total).toBeGreaterThanOrEqual(9000);
    expect(total).toBeLessThanOrEqual(11000);
  });

  it("the still frame tells the whole story: watched, finished, pushed, not fading", () => {
    expect(FRAMES[STILL_FRAME]).toMatchObject({ watching: true, done: true, notified: true, fade: false });
  });

  it("every loop starts from the untouched enrollment", () => {
    expect(FRAMES[0]).toMatchObject({ watching: false, done: false, notified: false, fade: false });
  });
});
