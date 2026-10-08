/**
 * Pointer-based long press, as chat apps use it to open a message's actions. Pure timers and
 * plain state, no React: the row component creates one controller per mount, wires the
 * handlers onto its element and disposes it on unmount.
 *
 * Rules: only the primary pointer with the primary button counts (touch contact, pen tip,
 * left mouse button); moving beyond the tolerance or releasing before the delay cancels; the
 * press fires once, while the pointer is still down. The release after a fired press may
 * produce a click, which the element must not treat as a tap — `shouldSuppressClick()` is
 * true exactly once after a press fired. A new press drops a stale suppression, because the
 * browser may swallow that click (Android's own long-press gesture does) and the next real tap
 * must not pay for it.
 */

/** The fields the controller reads; a React or DOM PointerEvent satisfies it. */
export interface LongPressPointerEvent {
  pointerId: number;
  pointerType: string;
  isPrimary: boolean;
  button: number;
  clientX: number;
  clientY: number;
}

export interface LongPressOptions {
  /** Hold time before the press fires (ms). */
  delayMs?: number;
  /** Movement from the press origin that turns the press into a scroll or drag (px). */
  moveTolerancePx?: number;
  onLongPress: () => void;
}

export interface LongPressHandlers {
  onPointerDown: (event: LongPressPointerEvent) => void;
  onPointerMove: (event: LongPressPointerEvent) => void;
  onPointerUp: (event: LongPressPointerEvent) => void;
  onPointerCancel: (event: LongPressPointerEvent) => void;
  onPointerLeave: (event: LongPressPointerEvent) => void;
}

export interface LongPressController extends LongPressHandlers {
  /** True exactly once after a press fired: the click of that release is not a tap. */
  shouldSuppressClick: () => boolean;
  /** Drops a pending press and any stale click suppression (unmount). */
  dispose: () => void;
}

export const LONG_PRESS_DELAY_MS = 500;
export const LONG_PRESS_MOVE_TOLERANCE_PX = 10;

interface PendingPress {
  pointerId: number;
  x: number;
  y: number;
  timer: ReturnType<typeof setTimeout>;
}

export function createLongPress(options: LongPressOptions): LongPressController {
  const delayMs = options.delayMs ?? LONG_PRESS_DELAY_MS;
  const moveTolerancePx = options.moveTolerancePx ?? LONG_PRESS_MOVE_TOLERANCE_PX;
  const { onLongPress } = options;

  let pending: PendingPress | null = null;
  let suppressClick = false;

  const cancel = () => {
    if (!pending) return;
    clearTimeout(pending.timer);
    pending = null;
  };

  const endIfPending = (event: LongPressPointerEvent) => {
    if (pending && pending.pointerId === event.pointerId) cancel();
  };

  return {
    onPointerDown: (event) => {
      // A second finger or a non-primary button is not a press; it also ends a pending one.
      cancel();
      if (!event.isPrimary || event.button !== 0) return;
      suppressClick = false;
      const timer = setTimeout(() => {
        pending = null;
        suppressClick = true;
        onLongPress();
      }, delayMs);
      pending = { pointerId: event.pointerId, x: event.clientX, y: event.clientY, timer };
    },
    onPointerMove: (event) => {
      if (!pending || pending.pointerId !== event.pointerId) return;
      const dx = event.clientX - pending.x;
      const dy = event.clientY - pending.y;
      if (Math.hypot(dx, dy) > moveTolerancePx) cancel();
    },
    onPointerUp: endIfPending,
    onPointerCancel: endIfPending,
    onPointerLeave: endIfPending,
    shouldSuppressClick: () => {
      const suppress = suppressClick;
      suppressClick = false;
      return suppress;
    },
    dispose: () => {
      cancel();
      suppressClick = false;
    },
  };
}
