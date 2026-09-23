import { describe, it, expect } from "vitest";
import { createGroupRegistry, type GroupRegistryTimers } from "../signalrGroupRegistry";

/**
 * The registry decides WHEN a shared hub group is left: only after its last consumer released
 * it and a grace period passed without a re-acquire. Timers are driven by hand here (the
 * injectable-timer seam), so the grace period is exact and independent of fake timers.
 */

const GRACE = 2_000;

function harness(graceMs = GRACE) {
  let now = 0;
  let nextId = 1;
  const pending = new Map<number, { at: number; fn: () => void }>();
  const timers: GroupRegistryTimers = {
    setTimeout: (fn, ms) => {
      const id = nextId++;
      pending.set(id, { at: now + ms, fn });
      return id;
    },
    clearTimeout: (handle) => { pending.delete(handle as number); },
  };
  const advance = (ms: number) => {
    const until = now + ms;
    for (;;) {
      const due = Array.from(pending.entries())
        .filter(([, t]) => t.at <= until)
        .sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      pending.delete(due[0]);
      now = due[1].at;
      due[1].fn();
    }
    now = until;
  };
  const left: string[] = [];
  const registry = createGroupRegistry({ graceMs, timers });
  const unsubscribe = registry.onLeave((g) => left.push(g));
  return { registry, left, advance, unsubscribe, pendingCount: () => pending.size };
}

describe("createGroupRegistry", () => {
  it("counts references per group and reports the first holder", () => {
    const { registry } = harness();
    expect(registry.acquire("tenant-a")).toBe(1);
    expect(registry.acquire("tenant-a")).toBe(2);
    expect(registry.acquire("tenant-b")).toBe(1);
    expect(registry.refCount("tenant-a")).toBe(2);
    expect(registry.refCount("tenant-b")).toBe(1);
    expect(registry.refCount("never")).toBe(0);
  });

  it("leaves only after the last release and a full grace period", () => {
    const { registry, left, advance } = harness();
    registry.acquire("tenant-a");
    registry.acquire("tenant-a");
    expect(registry.release("tenant-a")).toBe(1);
    advance(10 * GRACE);
    expect(left).toEqual([]); // one holder remains
    expect(registry.release("tenant-a")).toBe(0);
    advance(GRACE - 1);
    expect(left).toEqual([]);
    expect(registry.isHeld("tenant-a")).toBe(true); // still in its grace period
    advance(1);
    expect(left).toEqual(["tenant-a"]);
    expect(registry.isHeld("tenant-a")).toBe(false);
  });

  it("cancels the pending leave when the group is re-acquired within the grace period (page hop)", () => {
    const { registry, left, advance, pendingCount } = harness();
    registry.acquire("tenant-a"); // dashboard
    registry.release("tenant-a"); // dashboard unmounts
    advance(GRACE / 2);
    registry.acquire("tenant-a"); // session page mounts
    expect(pendingCount()).toBe(0);
    advance(10 * GRACE);
    expect(left).toEqual([]);
    expect(registry.refCount("tenant-a")).toBe(1);
  });

  it("keeps another consumer's reference when one consumer's paired join/leave cycles", () => {
    // global-admins: the notification bell holds it for the whole session; the dashboard joins
    // in GA mode and leaves when GA mode is switched off — that must not touch the bell's reference.
    const { registry, left, advance } = harness();
    registry.acquire("global-admins"); // bell
    registry.acquire("global-admins"); // dashboard, GA mode on
    registry.release("global-admins"); // dashboard, GA mode off (effect cleanup)
    advance(10 * GRACE);
    expect(left).toEqual([]);
    expect(registry.refCount("global-admins")).toBe(1);
    expect(registry.isHeld("global-admins")).toBe(true);
  });

  it("ignores a release without a reference (never schedules a leave for a group nobody holds)", () => {
    const { registry, left, advance, pendingCount } = harness();
    expect(registry.release("tenant-a")).toBe(0);
    expect(pendingCount()).toBe(0);
    advance(10 * GRACE);
    expect(left).toEqual([]);
    expect(registry.isHeld("tenant-a")).toBe(false);
  });

  it("restarts the grace period of every pending leave", () => {
    const { registry, left, advance } = harness();
    registry.acquire("tenant-a");
    registry.acquire("session-a-1");
    registry.acquire("tenant-b");
    registry.release("tenant-a");
    registry.release("session-a-1");
    advance(GRACE - 100);
    registry.restartPendingLeaves(); // reconnect: the rejoin re-established both memberships
    advance(GRACE - 1);
    expect(left).toEqual([]);
    advance(1);
    expect(left.sort()).toEqual(["session-a-1", "tenant-a"]);
    expect(registry.isHeld("tenant-b")).toBe(true); // untouched: still referenced
  });

  it("reset forgets references and pending leaves without firing onLeave", () => {
    const { registry, left, advance, pendingCount } = harness();
    registry.acquire("tenant-a");
    registry.acquire("tenant-b");
    registry.release("tenant-b");
    registry.reset(); // connection closed
    expect(pendingCount()).toBe(0);
    expect(registry.refCount("tenant-a")).toBe(0);
    expect(registry.isHeld("tenant-b")).toBe(false);
    advance(10 * GRACE);
    expect(left).toEqual([]);
    // A consumer's cleanup that runs after the reset is a plain no-op ...
    expect(registry.release("tenant-a")).toBe(0);
    advance(10 * GRACE);
    expect(left).toEqual([]);
    // ... and its re-join on the next connect starts a fresh count.
    expect(registry.acquire("tenant-a")).toBe(1);
  });

  it("leaves immediately-after-grace with a zero grace period", () => {
    const { registry, left, advance } = harness(0);
    registry.acquire("tenant-a");
    registry.release("tenant-a");
    expect(left).toEqual([]); // still asynchronous: never inside the release call
    advance(0);
    expect(left).toEqual(["tenant-a"]);
  });

  it("stops notifying an unsubscribed leave listener", () => {
    const { registry, left, advance, unsubscribe } = harness();
    registry.acquire("tenant-a");
    registry.release("tenant-a");
    unsubscribe();
    advance(10 * GRACE);
    expect(left).toEqual([]);
    expect(registry.isHeld("tenant-a")).toBe(false); // the pending leave itself still ran its course
  });

  it("rejects a negative grace period", () => {
    expect(() => createGroupRegistry({ graceMs: -1 })).toThrow();
  });
});
