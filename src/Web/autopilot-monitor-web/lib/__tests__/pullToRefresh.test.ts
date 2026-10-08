import { describe, expect, it, vi } from "vitest";
import { createPullToRefresh, PULL_MAX_PX, PULL_THRESHOLD_PX, type PullState } from "../push/pullToRefresh";

/**
 * The gesture rule of the history page's pull-to-refresh (lib/push/pullToRefresh.ts): only from
 * the top, damped, armed at the threshold, one refresh per release, no second pull while one runs.
 */

const touch = (clientY: number) => ({ touches: [{ clientY }] });

function controller(overrides: { scrollTop?: number; onRefresh?: () => Promise<void> | void } = {}) {
  const states: PullState[] = [];
  const onRefresh = vi.fn(overrides.onRefresh ?? (() => undefined));
  const pull = createPullToRefresh({
    getScrollTop: () => overrides.scrollTop ?? 0,
    onRefresh,
    onChange: (state) => states.push(state),
  });
  return { pull, states, onRefresh };
}

describe("createPullToRefresh", () => {
  it("follows the finger at half speed, arms at the threshold and stops at the cap", () => {
    const { pull } = controller();
    pull.onTouchStart(touch(100));
    pull.onTouchMove(touch(160));
    expect(pull.getState()).toEqual({ pulling: true, distance: 30, armed: false, refreshing: false });
    pull.onTouchMove(touch(100 + PULL_THRESHOLD_PX * 2));
    expect(pull.getState().armed).toBe(true);
    expect(pull.getState().distance).toBe(PULL_THRESHOLD_PX);
    pull.onTouchMove(touch(100 + PULL_MAX_PX * 4));
    expect(pull.getState().distance).toBe(PULL_MAX_PX);
  });

  it("refreshes once on release when armed and stays refreshing until onRefresh settles", async () => {
    let settle: () => void = () => undefined;
    const { pull, onRefresh, states } = controller({ onRefresh: () => new Promise<void>((resolve) => (settle = resolve)) });
    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(PULL_THRESHOLD_PX * 2));
    pull.onTouchEnd();
    expect(onRefresh).toHaveBeenCalledTimes(1);
    expect(pull.getState().refreshing).toBe(true);
    expect(pull.getState().pulling).toBe(false);

    // A pull during the refresh is ignored, and the release does not refresh again.
    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(300));
    pull.onTouchEnd();
    expect(onRefresh).toHaveBeenCalledTimes(1);

    settle();
    await Promise.resolve();
    await Promise.resolve();
    expect(pull.getState()).toEqual({ pulling: false, distance: 0, armed: false, refreshing: false });
    expect(states.at(-1)?.refreshing).toBe(false);
  });

  it("does nothing when released before the threshold, when scrolled down, or when the finger moves up", () => {
    const { pull, onRefresh } = controller();
    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(40));
    pull.onTouchEnd();
    expect(onRefresh).not.toHaveBeenCalled();
    expect(pull.getState().distance).toBe(0);

    pull.onTouchStart(touch(100));
    pull.onTouchMove(touch(50));
    expect(pull.getState().pulling).toBe(false);

    const scrolled = controller({ scrollTop: 120 });
    scrolled.pull.onTouchStart(touch(0));
    scrolled.pull.onTouchMove(touch(400));
    scrolled.pull.onTouchEnd();
    expect(scrolled.onRefresh).not.toHaveBeenCalled();
    expect(scrolled.pull.getState().pulling).toBe(false);
  });

  it("ignores multi-touch, resets on cancel and goes quiet after dispose", () => {
    const { pull, onRefresh, states } = controller();
    pull.onTouchStart({ touches: [{ clientY: 0 }, { clientY: 10 }] });
    pull.onTouchMove(touch(300));
    expect(pull.getState().pulling).toBe(false);

    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(300));
    pull.onTouchCancel();
    expect(pull.getState().distance).toBe(0);
    expect(onRefresh).not.toHaveBeenCalled();

    const before = states.length;
    pull.dispose();
    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(300));
    pull.onTouchEnd();
    expect(onRefresh).not.toHaveBeenCalled();
    expect(states.length).toBe(before);
  });

  it("swallows a failing onRefresh and returns to idle", async () => {
    const { pull } = controller({ onRefresh: () => Promise.reject(new Error("no db")) });
    pull.onTouchStart(touch(0));
    pull.onTouchMove(touch(PULL_THRESHOLD_PX * 2));
    pull.onTouchEnd();
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    expect(pull.getState().refreshing).toBe(false);
  });
});
