import { describe, expect, it } from "vitest";
import { placeBubble, scrollTopFor, type Box, type TourViewport } from "../tourPlacement";

const BUBBLE = { width: 380, height: 240 };
const DESKTOP: TourViewport = { width: 1440, height: 900, topInset: 56 };

const box = (left: number, top: number, width: number, height: number): Box => ({ left, top, width, height });

describe("placeBubble", () => {
  it("goes to the right of a narrow target", () => {
    const placement = placeBubble(box(300, 200, 500, 120), BUBBLE, DESKTOP);
    expect(placement.side).toBe("right");
    expect(placement.left).toBe(300 + 500 + 14);
    expect(placement.top).toBe(200);
  });

  it("goes to the left when the target reaches the right edge", () => {
    const placement = placeBubble(box(700, 200, 700, 120), BUBBLE, DESKTOP);
    expect(placement.side).toBe("left");
    expect(placement.left + BUBBLE.width).toBe(700 - 14);
  });

  it("goes below a full-width target, above when the target sits low", () => {
    expect(placeBubble(box(20, 150, 1400, 200), BUBBLE, DESKTOP).side).toBe("bottom");
    const above = placeBubble(box(20, 560, 1400, 200), BUBBLE, DESKTOP);
    expect(above.side).toBe("top");
    expect(above.top + BUBBLE.height).toBe(560 - 14);
  });

  it("docks at the bottom when nothing fits", () => {
    const placement = placeBubble(box(20, 120, 1400, 700), BUBBLE, DESKTOP);
    expect(placement.side).toBe("dock");
    expect(placement.top).toBe(900 - BUBBLE.height - 12);
  });

  it("docks on a phone-width viewport", () => {
    expect(placeBubble(box(16, 200, 340, 120), BUBBLE, { width: 390, height: 844, topInset: 56 }).side).toBe("dock");
  });

  it("never places the bubble under the sticky bars or past the window", () => {
    const placement = placeBubble(box(300, 10, 400, 60), BUBBLE, DESKTOP);
    expect(placement.top).toBeGreaterThanOrEqual(56 + 12);
    expect(placement.left + BUBBLE.width).toBeLessThanOrEqual(DESKTOP.width - 12);
  });

  it("keeps the arrow on the bubble edge", () => {
    for (const target of [box(300, 200, 500, 120), box(20, 150, 1400, 200), box(1000, 700, 380, 100)]) {
      const placement = placeBubble(target, BUBBLE, DESKTOP);
      const edge = placement.side === "top" || placement.side === "bottom" ? BUBBLE.width : BUBBLE.height;
      expect(placement.arrow).toBeGreaterThanOrEqual(16);
      expect(placement.arrow).toBeLessThanOrEqual(edge - 32);
    }
  });
});

describe("scrollTopFor", () => {
  it("centres a target that has the bubble beside it", () => {
    const target = box(300, 1200, 500, 100);
    const scroll = scrollTopFor(target, BUBBLE, DESKTOP, 0);
    const visible = DESKTOP.height - DESKTOP.topInset;
    expect(scroll).toBe(1200 - 56 - (visible - 100) / 2);
  });

  it("leaves room for the bubble below a wide target", () => {
    const target = box(20, 1200, 1400, 200);
    expect(scrollTopFor(target, BUBBLE, DESKTOP, 0)).toBe(1200 - 56 - 160);
  });

  it("never scrolls above the page top", () => {
    expect(scrollTopFor(box(300, 80, 500, 100), BUBBLE, DESKTOP, 0)).toBe(0);
  });
});
