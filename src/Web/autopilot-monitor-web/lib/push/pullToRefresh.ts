/**
 * Pull-to-refresh for the receiver's history page, as a pure controller so the gesture rule is
 * unit-tested: a home-screen web app on iOS has no native pull-to-refresh, so the page tracks
 * the touch itself. The pull counts only while the page sits at the top; the distance is damped
 * so the indicator follows the finger at half speed and stops at maxPull; releasing at or beyond
 * the threshold runs onRefresh once and keeps the "refreshing" state until it settles. A pull
 * that starts during a refresh is ignored.
 */

export interface PullState {
  /** A downward pull is in progress (finger down, page at the top). */
  pulling: boolean;
  /** Damped distance in px, 0 when idle. */
  distance: number;
  /** Releasing now would refresh. */
  armed: boolean;
  refreshing: boolean;
}

/** The subset of a touch event the controller reads; React's TouchEvent satisfies it. */
export interface PullTouchEvent {
  touches: ArrayLike<{ clientY: number }>;
}

export interface PullToRefreshOptions {
  /** Damped distance at which a release refreshes. */
  threshold?: number;
  /** The indicator never grows beyond this. */
  maxPull?: number;
  /** Finger distance is multiplied by this (0.5 = the indicator follows at half speed). */
  damping?: number;
  /** Current scroll offset of the page; a pull starts only at 0. */
  getScrollTop: () => number;
  onRefresh: () => Promise<void> | void;
  onChange?: (state: PullState) => void;
}

export interface PullToRefreshController {
  onTouchStart: (event: PullTouchEvent) => void;
  onTouchMove: (event: PullTouchEvent) => void;
  onTouchEnd: () => void;
  onTouchCancel: () => void;
  getState: () => PullState;
  dispose: () => void;
}

export const PULL_THRESHOLD_PX = 64;
export const PULL_MAX_PX = 120;
export const PULL_DAMPING = 0.5;

const IDLE: PullState = { pulling: false, distance: 0, armed: false, refreshing: false };

export function createPullToRefresh(options: PullToRefreshOptions): PullToRefreshController {
  const threshold = options.threshold ?? PULL_THRESHOLD_PX;
  const maxPull = options.maxPull ?? PULL_MAX_PX;
  const damping = options.damping ?? PULL_DAMPING;
  let state: PullState = IDLE;
  let startY: number | null = null;
  let disposed = false;

  const set = (next: PullState) => {
    state = next;
    if (!disposed) options.onChange?.(next);
  };

  const reset = () => {
    startY = null;
    if (state.pulling || state.distance !== 0 || state.armed) set({ ...IDLE, refreshing: state.refreshing });
  };

  const refresh = async () => {
    set({ pulling: false, distance: threshold, armed: false, refreshing: true });
    try {
      await options.onRefresh();
    } catch {
      // The page shows whatever it could load; the gesture itself must not throw.
    }
    startY = null;
    set(IDLE);
  };

  return {
    onTouchStart(event) {
      if (disposed || state.refreshing || event.touches.length !== 1) return;
      startY = options.getScrollTop() <= 0 ? event.touches[0].clientY : null;
    },
    onTouchMove(event) {
      if (disposed || state.refreshing || startY === null || event.touches.length !== 1) return;
      const dy = event.touches[0].clientY - startY;
      if (dy <= 0 || options.getScrollTop() > 0) {
        reset();
        return;
      }
      const distance = Math.min(Math.round(dy * damping), maxPull);
      set({ pulling: true, distance, armed: distance >= threshold, refreshing: false });
    },
    onTouchEnd() {
      if (disposed || state.refreshing) return;
      if (state.armed) {
        void refresh();
        return;
      }
      reset();
    },
    onTouchCancel() {
      if (disposed || state.refreshing) return;
      reset();
    },
    getState: () => state,
    dispose() {
      disposed = true;
      startY = null;
    },
  };
}
