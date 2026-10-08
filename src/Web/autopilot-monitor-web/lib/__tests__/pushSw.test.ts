import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { GENERIC_BODY, GENERIC_TITLE } from "../push/pushCore";

/**
 * Pins the K25 "always show something" contract of the service worker glue (public/push/sw.js):
 * the push handler reaches showNotification before IndexedDB is even opened, a payload that
 * cannot be read still shows the generic title, and the event's waitUntil promise settles
 * although the history write hangs or fails. The worker is loaded for real with a stubbed
 * worker global, so the test runs the shipped glue and not a copy of it.
 */

type Handler = (event: unknown) => void;

interface FakeClients {
  claim: ReturnType<typeof vi.fn>;
  matchAll: ReturnType<typeof vi.fn>;
  openWindow: ReturnType<typeof vi.fn>;
}

interface FakeSelf {
  handlers: Map<string, Handler>;
  showNotification: ReturnType<typeof vi.fn>;
  clients: FakeClients;
  /** Order of the side effects the contract cares about. */
  sequence: string[];
}

function fakeSelf(): FakeSelf {
  const handlers = new Map<string, Handler>();
  const sequence: string[] = [];
  const showNotification = vi.fn(async () => {
    sequence.push("showNotification");
  });
  const clients: FakeClients = {
    claim: vi.fn(async () => undefined),
    matchAll: vi.fn(async () => []),
    openWindow: vi.fn(async () => null),
  };
  const self = {
    addEventListener: (type: string, handler: Handler) => {
      handlers.set(type, handler);
    },
    skipWaiting: vi.fn(),
    registration: {
      showNotification,
      pushManager: { getSubscription: vi.fn(async () => null) },
    },
    clients,
    location: { origin: "https://portal.example.test" },
  };
  vi.stubGlobal("self", self);
  return { handlers, showNotification, clients, sequence };
}

/** An IDBFactory whose open() request never fires any handler. */
function hangingIndexedDb(sequence: string[]) {
  return {
    open: vi.fn(() => {
      sequence.push("indexedDB.open");
      return {};
    }),
  };
}

/** An IDBFactory whose open() request errors on the next microtask. */
function erroringIndexedDb(sequence: string[]) {
  return {
    open: vi.fn(() => {
      sequence.push("indexedDB.open");
      const request: { onerror?: () => void; error: Error } = { error: new Error("quota exceeded") };
      queueMicrotask(() => request.onerror?.());
      return request;
    }),
  };
}

interface FakeEvent {
  data?: unknown;
  notification?: unknown;
  proposedNotification?: unknown;
  waited: Promise<unknown> | null;
  waitUntil(p: Promise<unknown>): void;
}

function fakeEvent(fields: Partial<Omit<FakeEvent, "waited" | "waitUntil">>): FakeEvent {
  return {
    ...fields,
    waited: null,
    waitUntil(p) {
      this.waited = p;
    },
  };
}

const UNREADABLE_DATA = {
  json() {
    throw new Error("bad");
  },
};

type IndexedDbFactory = (sequence: string[]) => { open: ReturnType<typeof vi.fn> };

async function loadWorker(makeIndexedDb: IndexedDbFactory): Promise<FakeSelf & { indexedDbOpen: ReturnType<typeof vi.fn> }> {
  vi.resetModules();
  const env = fakeSelf();
  const indexedDB = makeIndexedDb(env.sequence);
  vi.stubGlobal("indexedDB", indexedDB);
  await import("../../public/push/sw.js");
  return { ...env, indexedDbOpen: indexedDB.open };
}

/** Resolves to "settled" or "pending" after the microtasks have run, without waiting on `p`. */
function settledState(p: Promise<unknown>): Promise<"settled" | "pending"> {
  return Promise.race([p.then(() => "settled" as const, () => "settled" as const), Promise.resolve().then(() => "pending" as const)]);
}

describe("push service worker glue (K25)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("registers the handlers the contract names", async () => {
    const env = await loadWorker(hangingIndexedDb);
    for (const type of ["push", "pushnotification", "notificationclick", "pushsubscriptionchange", "install", "activate"]) {
      expect(env.handlers.has(type), type).toBe(true);
    }
  });

  it("shows the generic notification before touching IndexedDB when the payload cannot be read", async () => {
    const env = await loadWorker(hangingIndexedDb);
    const event = fakeEvent({ data: UNREADABLE_DATA });

    env.handlers.get("push")!(event);

    // Synchronously after the handler returned: the notification call has happened, the open has
    // been issued after it, and nothing waited for the database.
    expect(env.showNotification).toHaveBeenCalledTimes(1);
    expect(env.showNotification.mock.calls[0][0]).toBe(GENERIC_TITLE);
    expect(env.showNotification.mock.calls[0][1]).toMatchObject({ body: GENERIC_BODY });
    expect(env.indexedDbOpen).toHaveBeenCalledTimes(1);
    expect(env.sequence).toEqual(["showNotification", "indexedDB.open"]);
    expect(event.waited).not.toBeNull();
  });

  it("settles waitUntil although the history write never answers", async () => {
    const env = await loadWorker(hangingIndexedDb);
    const event = fakeEvent({ data: UNREADABLE_DATA });

    env.handlers.get("push")!(event);
    const waited = event.waited!;

    // The open hangs: the promise is still pending after the microtasks…
    await expect(settledState(waited)).resolves.toBe("pending");
    expect(env.sequence).toEqual(["showNotification", "indexedDB.open"]);
    // …and the bounded persist lets it resolve (never reject) once the budget is used up.
    await vi.advanceTimersByTimeAsync(5000);
    await expect(waited).resolves.toBeDefined();
  });

  it("settles waitUntil although the history write fails, and shows the notification regardless", async () => {
    const env = await loadWorker(erroringIndexedDb);
    const event = fakeEvent({ data: UNREADABLE_DATA });

    env.handlers.get("push")!(event);

    await expect(event.waited).resolves.toBeDefined();
    expect(env.showNotification).toHaveBeenCalledTimes(1);
    expect(env.showNotification.mock.calls[0][0]).toBe(GENERIC_TITLE);
    // The failed write is followed by one more open: the trace record of the failure.
    expect(env.sequence).toEqual(["showNotification", "indexedDB.open", "indexedDB.open"]);
  });

  it("settles waitUntil and still writes the history when showNotification throws (iOS inside a declarative push event)", async () => {
    const env = await loadWorker(erroringIndexedDb);
    env.showNotification.mockImplementation(async () => {
      env.sequence.push("showNotification");
      throw new Error("NotAllowedError");
    });
    const event = fakeEvent({ data: null, notification: { title: "Enrollment finished", body: "b", data: { id: "srv-9", type: "session_watch" } } });

    env.handlers.get("push")!(event);

    // Resolves, never rejects: a rejected waitUntil would end the event with the write in flight.
    await expect(event.waited).resolves.toBeDefined();
    // Both attempts (the entry, then the generic fallback) threw; the history write started regardless.
    expect(env.showNotification).toHaveBeenCalledTimes(2);
    expect(env.sequence.slice(0, 2)).toEqual(["showNotification", "indexedDB.open"]);
  });

  it("shows a readable payload with its own title and still never waits for IndexedDB", async () => {
    const env = await loadWorker(hangingIndexedDb);
    const payload = { notification: { title: "Enrollment failed", body: "DESKTOP-1234", data: { id: "abc", type: "enrollment_failed" } } };
    const event = fakeEvent({ data: { json: () => payload } });

    env.handlers.get("push")!(event);

    expect(env.showNotification).toHaveBeenCalledWith("Enrollment failed", expect.objectContaining({ body: "DESKTOP-1234" }));
    expect(env.sequence).toEqual(["showNotification", "indexedDB.open"]);
  });

  describe("pushnotification (Safari declarative push with mutable: true)", () => {
    const proposed = { title: "From Safari", body: "proposed", data: { id: "safari-1", type: "enrollment_failed" } };

    it("takes event.notification first", async () => {
      const env = await loadWorker(hangingIndexedDb);
      const event = fakeEvent({ notification: proposed, proposedNotification: { title: "ignored" } });

      env.handlers.get("pushnotification")!(event);

      expect(env.showNotification).toHaveBeenCalledWith("From Safari", expect.objectContaining({ body: "proposed" }));
      expect(env.sequence).toEqual(["showNotification", "indexedDB.open"]);
      expect(event.waited).not.toBeNull();
    });

    it("falls back to event.proposedNotification", async () => {
      const env = await loadWorker(hangingIndexedDb);
      const event = fakeEvent({ proposedNotification: proposed });

      env.handlers.get("pushnotification")!(event);

      expect(env.showNotification).toHaveBeenCalledWith("From Safari", expect.objectContaining({ body: "proposed" }));
    });

    it("shows the generic notification when neither shape nor data is readable", async () => {
      const env = await loadWorker(erroringIndexedDb);
      const event = fakeEvent({ data: UNREADABLE_DATA });

      env.handlers.get("pushnotification")!(event);

      expect(env.showNotification.mock.calls[0][0]).toBe(GENERIC_TITLE);
      await expect(event.waited).resolves.toBeDefined();
    });
  });

  describe("push event carrying a declarative notification (Push API mutable, Safari ≥ 18.4)", () => {
    const declared = {
      title: "Enrollment finished",
      body: "DESKTOP-4711 (…CDE9) finished enrollment.",
      tag: "session-x",
      data: { id: "srv-1", type: "session_watch", ts: "2026-10-08T12:40:15Z", severity: "success", facts: [], portalUrl: "https://portal.example.test/sessions?id=x", scope: "platform" },
    };

    it("reads event.notification when event.data is null, shows the same content and persists it", async () => {
      const env = await loadWorker(hangingIndexedDb);
      const event = fakeEvent({ data: null, notification: declared });

      env.handlers.get("push")!(event);

      expect(env.showNotification).toHaveBeenCalledTimes(1);
      expect(env.showNotification.mock.calls[0][0]).toBe("Enrollment finished");
      expect(env.showNotification.mock.calls[0][1]).toMatchObject({
        body: "DESKTOP-4711 (…CDE9) finished enrollment.",
        tag: "session-x",
        data: { id: "srv-1", entry: expect.objectContaining({ id: "srv-1", type: "session_watch", severity: "success", portalUrl: "https://portal.example.test/sessions?id=x", scope: "platform" }) },
      });
      expect(env.sequence).toEqual(["showNotification", "indexedDB.open"]);
    });

    it("prefers event.notification over an unreadable event.data", async () => {
      const env = await loadWorker(hangingIndexedDb);
      const event = fakeEvent({ data: UNREADABLE_DATA, notification: declared });

      env.handlers.get("push")!(event);

      expect(env.showNotification.mock.calls[0][0]).toBe("Enrollment finished");
    });
  });

  describe("notificationclick on a notification the platform displayed itself", () => {
    function clickEvent(notification: Record<string, unknown>) {
      const event = fakeEvent({}) as FakeEvent & { notification: Record<string, unknown> };
      event.notification = { close: vi.fn(), ...notification };
      return event;
    }

    const platformShown = {
      title: "Enrollment failed",
      body: "DESKTOP-4711 (…CDE9) failed enrollment.",
      tag: "session-y",
      data: { id: "srv-2", type: "session_watch", ts: "2026-10-08T12:45:00Z", severity: "error", facts: [{ name: "Device", value: "DESKTOP-4711" }], portalUrl: null, scope: "tenant" },
    };

    it("rebuilds the history entry from the notification's data, writes it and opens the entry", async () => {
      const env = await loadWorker(erroringIndexedDb);
      const event = clickEvent(platformShown);

      env.handlers.get("notificationclick")!(event);
      await expect(event.waited).resolves.toBeUndefined();

      // The history write and, after it failed, the trace record of the click.
      expect(env.indexedDbOpen).toHaveBeenCalledTimes(2);
      expect(env.clients.openWindow).toHaveBeenCalledTimes(1);
      expect(String(env.clients.openWindow.mock.calls[0][0])).toContain("srv-2");
    });

    it("opens the page even when the history write never answers (bounded like the push path)", async () => {
      const env = await loadWorker(hangingIndexedDb);
      const event = clickEvent(platformShown);

      env.handlers.get("notificationclick")!(event);
      await vi.advanceTimersByTimeAsync(5000);
      await expect(event.waited).resolves.toBeUndefined();

      expect(env.clients.openWindow).toHaveBeenCalledTimes(1);
    });

    it("keeps using the entry the push handler attached when there is one", async () => {
      const env = await loadWorker(erroringIndexedDb);
      const attached = { id: "srv-3", ts: "2026-10-08T12:50:00Z", title: "t", body: "b", type: "push_test", severity: "info", facts: [], portalUrl: null, navigate: null, tag: null, scope: "platform", generic: false };
      const event = clickEvent({ title: "t", body: "b", data: { id: "srv-3", entry: attached } });

      env.handlers.get("notificationclick")!(event);
      await expect(event.waited).resolves.toBeUndefined();

      expect(String(env.clients.openWindow.mock.calls[0][0])).toContain("srv-3");
    });
  });
});
