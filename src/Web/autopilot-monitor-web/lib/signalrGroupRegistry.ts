/**
 * Reference counting for SignalR group membership.
 *
 * Every consumer of the shared hub connection joins the groups it needs and leaves them on
 * cleanup, but several consumers want the same group at the same time: the dashboard and the
 * session page both want `tenant-<id>`, the global notification bell and the dashboard both
 * want `global-admins`. Membership is per connection, so the hub is joined once, on the first
 * reference, and left only when the LAST reference goes — a consumer's leave can never drop
 * another consumer's membership (an unconditional leave of `global-admins` by the dashboard
 * used to kill the bell's live pushes for the rest of the session).
 *
 * The leave is deferred by a grace period: a dashboard → session → dashboard hop releases and
 * re-acquires `tenant-<id>` within a second, and without the grace that would be a hub leave
 * and re-join for nothing. An acquire during the grace period cancels the pending leave.
 *
 * Pure bookkeeping, no hub calls: the owner keeps its own membership set, joins the hub group
 * on an acquire it is not a member for, and leaves it from the `onLeave` listener it
 * subscribes. Timers are injectable so the grace period can be tested without fake timers.
 * Consumers must pair every acquire with exactly one release (an effect's join with its
 * cleanup's leave); the registry cannot tell consumers apart.
 */
export interface GroupRegistryTimers {
  setTimeout: (fn: () => void, ms: number) => unknown;
  clearTimeout: (handle: unknown) => void;
}

export interface GroupRegistryOptions {
  /** Delay between the release of the last reference and the leave listeners (ms). */
  graceMs: number;
  /** Defaults to the global timers. */
  timers?: GroupRegistryTimers;
}

export interface GroupRegistry {
  /** Adds a reference and cancels a pending leave. Returns the new reference count (1 = first holder). */
  acquire: (group: string) => number;
  /**
   * Drops a reference; the last one notifies the leave listeners after the grace period.
   * Returns the remaining count. A release without a reference is a no-op (never schedules a leave).
   */
  release: (group: string) => number;
  /**
   * Subscribes to "grace period after the last release passed without a new acquire"; returns
   * the unsubscribe. Subscribed from an effect by the React owner, never at construction, so
   * the listener may read live refs.
   */
  onLeave: (listener: (group: string) => void) => () => void;
  /** True while the group has a reference or a leave still in its grace period. */
  isHeld: (group: string) => boolean;
  refCount: (group: string) => number;
  /**
   * Restarts the grace period of every pending leave. After a reconnect the membership is
   * re-established by a rejoin; its leave then waits a full grace period again instead of
   * racing the rejoin that is still in flight.
   */
  restartPendingLeaves: () => void;
  /**
   * Forgets every reference and pending leave without notifying the leave listeners: the
   * connection is gone and every membership with it; consumers re-acquire on the next connect.
   */
  reset: () => void;
}

const globalTimers: GroupRegistryTimers = {
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export function createGroupRegistry(options: GroupRegistryOptions): GroupRegistry {
  const { graceMs } = options;
  const timers = options.timers ?? globalTimers;
  if (!(graceMs >= 0)) {
    throw new Error(`createGroupRegistry: need graceMs >= 0, got ${graceMs}`);
  }

  const refs = new Map<string, number>();
  const pendingLeaves = new Map<string, unknown>();
  const leaveListeners = new Set<(group: string) => void>();
  const onLeave = (group: string) => {
    for (const listener of Array.from(leaveListeners)) {
      listener(group);
    }
  };

  const cancelPendingLeave = (group: string) => {
    const handle = pendingLeaves.get(group);
    if (handle === undefined) return;
    timers.clearTimeout(handle);
    pendingLeaves.delete(group);
  };

  const schedulePendingLeave = (group: string) => {
    cancelPendingLeave(group);
    pendingLeaves.set(group, timers.setTimeout(() => {
      pendingLeaves.delete(group);
      onLeave(group);
    }, graceMs));
  };

  return {
    acquire: (group) => {
      cancelPendingLeave(group);
      const count = (refs.get(group) ?? 0) + 1;
      refs.set(group, count);
      return count;
    },
    release: (group) => {
      const count = refs.get(group) ?? 0;
      if (count === 0) return 0;
      if (count > 1) {
        refs.set(group, count - 1);
        return count - 1;
      }
      refs.delete(group);
      schedulePendingLeave(group);
      return 0;
    },
    onLeave: (listener) => {
      leaveListeners.add(listener);
      return () => { leaveListeners.delete(listener); };
    },
    isHeld: (group) => refs.has(group) || pendingLeaves.has(group),
    refCount: (group) => refs.get(group) ?? 0,
    restartPendingLeaves: () => {
      for (const group of Array.from(pendingLeaves.keys())) {
        schedulePendingLeave(group);
      }
    },
    reset: () => {
      for (const handle of pendingLeaves.values()) {
        timers.clearTimeout(handle);
      }
      pendingLeaves.clear();
      refs.clear();
    },
  };
}
