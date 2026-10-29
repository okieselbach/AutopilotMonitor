import { describe, expect, it, vi } from "vitest";
import { PushApiError, type PushDeviceResponse } from "../push/pushApi";
import {
  createOpenTracker,
  deviceCheckProperties,
  deviceFacts,
  NOTIFICATION_GRACE_MS,
  pairErrorCode,
  receiverScope,
  RESUME_AFTER_MS,
  screenClass,
  type AppOpen,
  type DeviceFactsInput,
} from "../push/pushTelemetry";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn(), setPushReceiverContext: vi.fn() }));

const TENANT = "0a1b2c3d-0000-4000-8000-00000000abcd";
const IPHONE_UA = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/27.0 Mobile/15E148 Safari/604.1";
const ANDROID_UA = "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Mobile Safari/537.36";

describe("receiverScope", () => {
  it("reads the tenant or the platform from the token's first segment only", () => {
    expect(receiverScope(`${TENANT.toUpperCase()}.device.secret`)).toEqual({ kind: "tenant", tenantId: TENANT });
    expect(receiverScope("platform.device.secret")).toEqual({ kind: "platform" });
  });

  it("refuses anything that is not a device token", () => {
    expect(receiverScope(null)).toBeNull();
    expect(receiverScope("")).toBeNull();
    expect(receiverScope(`${TENANT}.device`)).toBeNull();
    expect(receiverScope("contoso.device.secret")).toBeNull();
    expect(receiverScope(TENANT)).toBeNull();
  });
});

describe("deviceFacts", () => {
  const base: DeviceFactsInput = {
    userAgent: IPHONE_UA,
    maxTouchPoints: 5,
    standalone: true,
    permission: "granted",
    screenWidth: 393,
    screenHeight: 852,
    pixelRatio: 3,
    hints: null,
  };

  it("describes an iPhone without a model: Safari gives no Client Hints", () => {
    expect(deviceFacts(base)).toEqual({ platform: "ios-homescreen", display: "standalone", permission: "granted", screen: "393x852@3" });
  });

  it("adds model and version from the Client Hints and drops empty ones", () => {
    const android = { ...base, userAgent: ANDROID_UA, standalone: false, permission: "default" as const, pixelRatio: 2.625 };
    expect(deviceFacts({ ...android, hints: { model: "SM-S918B", platformVersion: "14.0.0" } })).toMatchObject({
      platform: "android-chrome",
      display: "browser",
      deviceModel: "SM-S918B",
      osVersion: "14.0.0",
      screen: "393x852@2.63",
    });
    const desktop = deviceFacts({ ...android, hints: { model: "", platformVersion: " " } });
    expect(desktop).not.toHaveProperty("deviceModel");
    expect(desktop).not.toHaveProperty("osVersion");
    expect(deviceFacts({ ...android, hints: { model: "x".repeat(100) } }).deviceModel).toHaveLength(64);
  });

  it("puts the short side first whatever the rotation", () => {
    expect(screenClass(852, 393, 3)).toBe("393x852@3");
    expect(screenClass(411.4, 914.3, 2.75)).toBe("411x914@2.75");
  });
});

function trackerHarness() {
  let now = 1_000_000;
  let nextId = 1;
  const timers = new Map<number, { at: number; callback: () => void }>();
  const events: AppOpen[] = [];
  let detected = 0;
  const tracker = createOpenTracker({
    now: () => now,
    setTimer: (callback, ms) => {
      const id = nextId++;
      timers.set(id, { at: now + ms, callback });
      return id;
    },
    clearTimer: (id) => {
      timers.delete(id);
    },
    onDetected: () => {
      detected++;
    },
    emit: (open) => events.push(open),
  });
  const advance = (ms: number) => {
    now += ms;
    for (const [id, timer] of [...timers]) {
      if (timer.at <= now) {
        timers.delete(id);
        timer.callback();
      }
    }
  };
  return { tracker, events, advance, detected: () => detected, pendingTimers: () => timers.size };
}

describe("createOpenTracker", () => {
  it("reports one load per document", () => {
    const h = trackerHarness();
    h.tracker.load("notification");
    h.tracker.load("direct");
    expect(h.events).toEqual([{ launch: "load", source: "notification" }]);
    expect(h.detected()).toBe(1);
  });

  it("counts a return only after a minute away, as direct once the grace has passed", () => {
    const h = trackerHarness();
    h.tracker.visible();                       // never hidden: nothing
    h.tracker.hidden();
    h.advance(RESUME_AFTER_MS - 1);
    h.tracker.visible();                       // a quick switch: nothing
    expect(h.events).toEqual([]);

    h.tracker.hidden();
    h.advance(RESUME_AFTER_MS);
    h.tracker.visible();
    expect(h.events).toEqual([]);              // waiting out the grace
    expect(h.detected()).toBe(1);
    h.advance(NOTIFICATION_GRACE_MS);
    expect(h.events).toEqual([{ launch: "resume", source: "direct" }]);
  });

  it("names the tap as the reason when it arrives within the grace, and counts it once", () => {
    const h = trackerHarness();
    h.tracker.hidden();
    h.advance(RESUME_AFTER_MS * 5);
    h.tracker.visible();
    h.advance(100);
    h.tracker.notification();                  // the worker's message
    h.advance(50);
    h.tracker.notification();                  // the hash change it causes
    h.advance(NOTIFICATION_GRACE_MS * 2);
    expect(h.events).toEqual([{ launch: "resume", source: "notification" }]);
    expect(h.pendingTimers()).toBe(0);
  });

  it("does not count the return again when the tap is reported before the page is visible", () => {
    const h = trackerHarness();
    h.tracker.hidden();
    h.advance(RESUME_AFTER_MS * 2);
    h.tracker.notification();
    h.advance(10);
    h.tracker.visible();
    h.advance(NOTIFICATION_GRACE_MS * 2);
    expect(h.events).toEqual([{ launch: "resume", source: "notification" }]);
  });

  it("still counts a return that is left again within the grace", () => {
    const h = trackerHarness();
    h.tracker.hidden();
    h.advance(RESUME_AFTER_MS);
    h.tracker.visible();
    h.advance(500);
    h.tracker.hidden();
    expect(h.events).toEqual([{ launch: "resume", source: "direct" }]);
    expect(h.pendingTimers()).toBe(0);
  });

  it("counts a tap that reaches the app in the foreground", () => {
    const h = trackerHarness();
    h.tracker.load("direct");
    h.advance(10_000);
    h.tracker.notification();
    expect(h.events).toEqual([
      { launch: "load", source: "direct" },
      { launch: "resume", source: "notification" },
    ]);
  });
});

describe("event properties", () => {
  const device: PushDeviceResponse = {
    deviceId: "device",
    status: "Stale",
    label: "Phone",
    platform: "ios-homescreen",
    kid: "k1",
    pairedUtc: "2026-10-20T10:00:00Z",
    ownerUpn: "admin@contoso.invalid",
    scopeName: "Contoso",
    activeKid: "k1",
    activeVapidPublicKey: "key",
  };

  it("reports the server check without owner, label or scope name", () => {
    const props = deviceCheckProperties({ state: "ok", device, meta: {}, subscriptionPresent: true, repair: "put", repairError: null });
    expect(props).toEqual({ state: "ok", status: "Stale", subscription: "present", repair: "put", repairFailed: false });
    expect(deviceCheckProperties({ state: "gone" })).toEqual({ state: "gone" });
    expect(deviceCheckProperties({ state: "error", message: "offline", meta: {} })).toEqual({ state: "error" });
  });

  it("classifies a failed pairing step by code or status, never by message", () => {
    expect(pairErrorCode(new PushApiError(400, "refused", "InvalidSubscription", "c", null))).toBe("InvalidSubscription");
    expect(pairErrorCode(new PushApiError(502, "Bad gateway", null, null, null))).toBe("http-502");
    expect(pairErrorCode(Object.assign(new Error("slow"), { name: "TimeoutError" }))).toBe("timeout");
    expect(pairErrorCode(new TypeError("Failed to fetch"))).toBe("network");
    expect(pairErrorCode("boom")).toBe("other");
  });
});
