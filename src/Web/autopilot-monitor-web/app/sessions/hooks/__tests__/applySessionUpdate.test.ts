import { describe, it, expect } from "vitest";
import { applySessionUpdate } from "../useSessionSignalR";
import { makeSession } from "@/test/factories";

/**
 * applySessionUpdate gates the session-detail re-render on "newevents" pushes: a delta that
 * repeats what the page already shows must return the SAME session object so React bails out,
 * while any real change must produce a new object (live views stay live).
 */
describe("applySessionUpdate", () => {
  it("returns the same object when every updated field already holds that value", () => {
    const prev = makeSession({ status: "InProgress", currentPhase: 2, eventCount: 40 });
    expect(applySessionUpdate(prev, { status: "InProgress", currentPhase: 2, eventCount: 40 })).toBe(prev);
  });

  it("returns the same object for an empty delta", () => {
    const prev = makeSession();
    expect(applySessionUpdate(prev, {})).toBe(prev);
  });

  it("returns a new object with the delta applied when one field changed", () => {
    const prev = makeSession({ status: "InProgress", currentPhase: 2, eventCount: 40 });
    const next = applySessionUpdate(prev, { status: "InProgress", currentPhase: 3, eventCount: 40 });
    expect(next).not.toBe(prev);
    expect(next.currentPhase).toBe(3);
    expect(next.status).toBe("InProgress");
    expect(prev.currentPhase).toBe(2); // never mutated
  });

  it("treats a field cleared to undefined as unchanged only when it was already unset", () => {
    const prev = makeSession({ failureReason: "" });
    expect(applySessionUpdate(prev, { failureReason: "" })).toBe(prev);
    const cleared = applySessionUpdate(prev, { failureReason: undefined });
    expect(cleared).not.toBe(prev);
    expect(cleared.failureReason).toBeUndefined();
  });

  it("keeps fields the delta does not mention", () => {
    const prev = makeSession({ deviceName: "DEVICE-A", status: "InProgress" });
    const next = applySessionUpdate(prev, { status: "Succeeded" });
    expect(next.deviceName).toBe("DEVICE-A");
    expect(next.status).toBe("Succeeded");
  });
});
