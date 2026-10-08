import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createLongPress, type LongPressPointerEvent } from "../push/longPress";

/**
 * Pins the long-press contract of the history rows: fires after the delay while the pointer is
 * still down, cancels on movement or release, and suppresses exactly the one click that the
 * release after a fired press may produce.
 */

const DELAY = 500;
const TOLERANCE = 10;

function pointer(overrides: Partial<LongPressPointerEvent> = {}): LongPressPointerEvent {
  return { pointerId: 1, pointerType: "touch", isPrimary: true, button: 0, clientX: 100, clientY: 100, ...overrides };
}

function harness(options: { delayMs?: number; moveTolerancePx?: number } = {}) {
  const onLongPress = vi.fn();
  const press = createLongPress({ delayMs: DELAY, moveTolerancePx: TOLERANCE, onLongPress, ...options });
  return { onLongPress, press };
}

describe("createLongPress", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("fires once after the delay, not before, while the pointer stays down", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer());
    vi.advanceTimersByTime(DELAY - 1);
    expect(onLongPress).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(onLongPress).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(5_000);
    expect(onLongPress).toHaveBeenCalledTimes(1);
  });

  it("uses the defaults (500 ms, 10 px) when no options are given", () => {
    const onLongPress = vi.fn();
    const press = createLongPress({ onLongPress });
    press.onPointerDown(pointer());
    press.onPointerMove(pointer({ clientX: 110 })); // exactly at the tolerance: still a press
    vi.advanceTimersByTime(499);
    expect(onLongPress).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(onLongPress).toHaveBeenCalledTimes(1);
  });

  it("is cancelled by a release before the delay", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer());
    vi.advanceTimersByTime(DELAY - 1);
    press.onPointerUp(pointer());
    vi.advanceTimersByTime(5_000);
    expect(onLongPress).not.toHaveBeenCalled();
    expect(press.shouldSuppressClick()).toBe(false);
  });

  it("is cancelled by pointercancel and by the pointer leaving", () => {
    for (const end of ["onPointerCancel", "onPointerLeave"] as const) {
      const { onLongPress, press } = harness();
      press.onPointerDown(pointer());
      vi.advanceTimersByTime(100);
      press[end](pointer());
      vi.advanceTimersByTime(5_000);
      expect(onLongPress, end).not.toHaveBeenCalled();
    }
  });

  it("is cancelled by movement beyond the tolerance, not by movement within it", () => {
    const within = harness();
    within.press.onPointerDown(pointer());
    within.press.onPointerMove(pointer({ clientX: 106, clientY: 108 })); // 10 px exactly
    vi.advanceTimersByTime(DELAY);
    expect(within.onLongPress).toHaveBeenCalledTimes(1);

    const beyond = harness();
    beyond.press.onPointerDown(pointer());
    beyond.press.onPointerMove(pointer({ clientX: 108, clientY: 108 })); // ~11.3 px
    vi.advanceTimersByTime(DELAY);
    expect(beyond.onLongPress).not.toHaveBeenCalled();
  });

  it("ignores a non-primary button and a non-primary pointer", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer({ pointerType: "mouse", button: 2 }));
    press.onPointerDown(pointer({ pointerId: 2, isPrimary: false }));
    vi.advanceTimersByTime(DELAY);
    expect(onLongPress).not.toHaveBeenCalled();
  });

  it("counts the primary mouse button and the pen tip", () => {
    for (const pointerType of ["mouse", "pen"]) {
      const { onLongPress, press } = harness();
      press.onPointerDown(pointer({ pointerType }));
      vi.advanceTimersByTime(DELAY);
      expect(onLongPress, pointerType).toHaveBeenCalledTimes(1);
    }
  });

  it("ends a pending press when a second finger lands", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer());
    vi.advanceTimersByTime(100);
    press.onPointerDown(pointer({ pointerId: 2, isPrimary: false }));
    vi.advanceTimersByTime(5_000);
    expect(onLongPress).not.toHaveBeenCalled();
  });

  it("ignores move and release events of another pointer", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer());
    press.onPointerMove(pointer({ pointerId: 2, clientX: 300 }));
    press.onPointerUp(pointer({ pointerId: 2 }));
    vi.advanceTimersByTime(DELAY);
    expect(onLongPress).toHaveBeenCalledTimes(1);
  });

  it("suppresses exactly one click after a press fired", () => {
    const { press } = harness();
    expect(press.shouldSuppressClick()).toBe(false);
    press.onPointerDown(pointer());
    vi.advanceTimersByTime(DELAY);
    press.onPointerUp(pointer());
    expect(press.shouldSuppressClick()).toBe(true);
    expect(press.shouldSuppressClick()).toBe(false);
  });

  it("drops a stale suppression when a new press starts", () => {
    const { press } = harness();
    press.onPointerDown(pointer());
    vi.advanceTimersByTime(DELAY);
    press.onPointerUp(pointer());
    // The browser swallowed the click of that release; the next tap must count.
    press.onPointerDown(pointer());
    press.onPointerUp(pointer());
    expect(press.shouldSuppressClick()).toBe(false);
  });

  it("dispose clears a pending timer and any suppression", () => {
    const { onLongPress, press } = harness();
    press.onPointerDown(pointer());
    press.dispose();
    vi.advanceTimersByTime(5_000);
    expect(onLongPress).not.toHaveBeenCalled();

    press.onPointerDown(pointer());
    vi.advanceTimersByTime(DELAY);
    expect(onLongPress).toHaveBeenCalledTimes(1);
    press.dispose();
    expect(press.shouldSuppressClick()).toBe(false);
  });
});
