/**
 * When to park the shared SignalR connection of a hidden tab, and when to bring it back.
 *
 * Chrome and Edge run the timers of a tab hidden for five minutes only once a minute. The SignalR
 * client then misses its keep-alive, the service closes the connection and the automatic reconnect
 * restores it — a reconnect loop every minute that nobody reads. Parking stops the connection
 * before that point; the tab reconnects the moment it is visible again and every consumer re-reads
 * (useSignalRResync), so a parked tab loses no data, only pushes nobody would have seen.
 *
 * Pure state machine: the owner feeds visibility changes and carries out park and resume. The timers
 * and the clock are injectable, so it is tested without fake timers.
 */

/**
 * Below the five-minute timer throttling (the loop never starts), above Collect Logs' three-minute
 * wait for its upload toast, and far above an ordinary tab switch.
 */
export const PARK_AFTER_HIDDEN_MS = 4 * 60_000;

export interface ParkTimers {
  setTimeout: (fn: () => void, ms: number) => unknown;
  clearTimeout: (handle: unknown) => void;
  now: () => number;
}

export interface ParkControllerOptions {
  /** Hidden this long without a break → park. */
  parkAfterHiddenMs: number;
  /**
   * Stops the connection. Returns false when it cannot be parked right now (the owner is restarting
   * it); the controller tries again after another full period while the tab stays hidden.
   */
  park: (hiddenMs: number) => boolean;
  /** Reconnects a parked connection; called on the first visible signal after a park. */
  resume: (parkedMs: number) => void;
  /** Defaults to the global timers and Date.now. */
  timers?: ParkTimers;
}

export interface ParkController {
  /** Feed every visibility change; the first call may already be "hidden" (a tab opened in the background). */
  setVisible: (visible: boolean) => void;
  isParked: () => boolean;
  /** Cancels a pending park. Never resumes: the owner tears the connection down itself. */
  dispose: () => void;
}

type ParkState =
  | { kind: "active" }
  | { kind: "hidden"; since: number; timer: unknown }
  | { kind: "parked"; since: number };

const globalTimers: ParkTimers = {
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  now: () => Date.now(),
};

export function createParkController(options: ParkControllerOptions): ParkController {
  const { parkAfterHiddenMs, park, resume } = options;
  const timers = options.timers ?? globalTimers;
  if (!(parkAfterHiddenMs > 0)) {
    throw new Error(`createParkController: need parkAfterHiddenMs > 0, got ${parkAfterHiddenMs}`);
  }

  let state: ParkState = { kind: "active" };

  // `since` is when the tab went hidden: a refused park keeps it, so the next report measures the
  // whole hidden stretch.
  const armPark = (since: number) => {
    const timer = timers.setTimeout(onParkDue, parkAfterHiddenMs);
    state = { kind: "hidden", since, timer };
  };

  function onParkDue() {
    if (state.kind !== "hidden") return;
    const since = state.since;
    const now = timers.now();
    if (park(now - since)) {
      state = { kind: "parked", since: now };
    } else {
      armPark(since);
    }
  }

  return {
    setVisible(visible) {
      if (!visible) {
        if (state.kind === "active") armPark(timers.now());
        return;
      }
      if (state.kind === "hidden") {
        timers.clearTimeout(state.timer);
        state = { kind: "active" };
      } else if (state.kind === "parked") {
        const parkedMs = timers.now() - state.since;
        state = { kind: "active" };
        resume(parkedMs);
      }
    },
    isParked: () => state.kind === "parked",
    dispose() {
      if (state.kind === "hidden") timers.clearTimeout(state.timer);
      state = { kind: "active" };
    },
  };
}
