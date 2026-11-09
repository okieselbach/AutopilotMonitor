/** A rectangle in viewport pixels. */
export interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}

export type BubbleSide = "right" | "left" | "bottom" | "top" | "dock";

export interface BubblePlacement {
  side: BubbleSide;
  left: number;
  top: number;
  /** Offset of the arrow along the bubble edge that faces the target. */
  arrow: number;
}

export interface TourViewport {
  width: number;
  height: number;
  /** Height of the sticky bars at the top (navbar, view-as bar); nothing is placed under them. */
  topInset: number;
}

/** Below this width the bubble docks at the bottom edge instead of pointing at the target. */
export const DOCK_BELOW_WIDTH = 640;
const GAP = 14;
const MARGIN = 12;

const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(value, Math.max(min, max)));

/**
 * Where the speech bubble goes next to the highlighted element: beside it when there is room,
 * else below or above; on a narrow viewport, or when nothing fits, docked at the bottom edge.
 */
export function placeBubble(target: Box, bubble: { width: number; height: number }, viewport: TourViewport): BubblePlacement {
  const minTop = viewport.topInset + MARGIN;
  const maxTop = viewport.height - bubble.height - MARGIN;
  const maxLeft = viewport.width - bubble.width - MARGIN;
  const right = target.left + target.width;
  const bottom = target.top + target.height;

  let side: BubbleSide;
  let left: number;
  let top: number;
  if (viewport.width < DOCK_BELOW_WIDTH) {
    side = "dock";
    left = MARGIN;
    top = maxTop;
  } else if (viewport.width - right >= bubble.width + GAP + MARGIN) {
    side = "right";
    left = right + GAP;
    top = target.top;
  } else if (target.left >= bubble.width + GAP + MARGIN) {
    side = "left";
    left = target.left - GAP - bubble.width;
    top = target.top;
  } else if (viewport.height - bottom >= bubble.height + GAP + MARGIN) {
    side = "bottom";
    left = target.left + MARGIN;
    top = bottom + GAP;
  } else if (target.top - minTop >= bubble.height + GAP) {
    side = "top";
    left = target.left + MARGIN;
    top = target.top - GAP - bubble.height;
  } else {
    side = "dock";
    left = MARGIN;
    top = maxTop;
  }

  left = clamp(left, MARGIN, maxLeft);
  top = clamp(top, minTop, maxTop);
  const arrow = side === "bottom" || side === "top"
    ? clamp(target.left + 28 - left, 16, bubble.width - 32)
    : clamp(target.top + 18 - top, 16, bubble.height - 32);
  return { side, left, top, arrow };
}

/**
 * The window scroll that frames the target for the bubble: centred when the bubble fits beside
 * it, else near the top with the bubble below, leaving up to 160 px of page context above.
 */
export function scrollTopFor(
  target: Box,
  bubble: { width: number; height: number },
  viewport: TourViewport,
  scrollY: number,
): number {
  const besideFits = viewport.width >= DOCK_BELOW_WIDTH
    && (viewport.width - (target.left + target.width) >= bubble.width + GAP + MARGIN || target.left >= bubble.width + GAP + MARGIN);
  const visible = viewport.height - viewport.topInset;
  const absoluteTop = target.top + scrollY;
  if (besideFits) {
    return Math.max(0, absoluteTop - viewport.topInset - (visible - target.height) / 2);
  }
  const room = visible - target.height - bubble.height - GAP - 2 * MARGIN;
  const context = room > 0 ? Math.min(160, room) : MARGIN;
  return Math.max(0, absoluteTop - viewport.topInset - context);
}
