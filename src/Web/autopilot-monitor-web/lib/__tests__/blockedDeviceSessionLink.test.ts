import { describe, it, expect } from "vitest";
import { firstBlockedSessionId, blockedSessionCount } from "../../app/admin/components/blockedDeviceHelpers";
import { extractSessionId } from "../../app/admin/components/opsEventSessionHelpers";

// The Active Blocks table renders the serial number as a link to the session that
// triggered the block. `blockedSessionIds` is a comma-separated storage column that is
// absent for manual whole-device blocks — getting this wrong either drops the link on
// every maintenance block or fabricates a session for a manual one.

describe("firstBlockedSessionId", () => {
  it("returns null when the block is not scoped to a session", () => {
    // Manual whole-device block: the API omits the field entirely (WhenWritingNull).
    expect(firstBlockedSessionId(undefined)).toBeNull();
    expect(firstBlockedSessionId(null)).toBeNull();
    expect(firstBlockedSessionId("")).toBeNull();
  });

  it("returns the single session of a maintenance auto-block", () => {
    expect(firstBlockedSessionId("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc"))
      .toBe("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc");
  });

  it("returns the first of several merged sessions", () => {
    // A device can accumulate session IDs when it trips the watchdog again after a
    // new enrollment; MergeSessionId appends them comma-separated.
    expect(firstBlockedSessionId("aaaa1111-0000-0000-0000-000000000001,bbbb2222-0000-0000-0000-000000000002"))
      .toBe("aaaa1111-0000-0000-0000-000000000001");
  });

  it("tolerates padding and empty segments from legacy rows", () => {
    expect(firstBlockedSessionId(" , aaaa1111-0000-0000-0000-000000000001 ,"))
      .toBe("aaaa1111-0000-0000-0000-000000000001");
    expect(firstBlockedSessionId(" , , ")).toBeNull();
  });
});

describe("blockedSessionCount", () => {
  it("counts nothing for a whole-device block", () => {
    expect(blockedSessionCount(null)).toBe(0);
    expect(blockedSessionCount("")).toBe(0);
  });

  it("counts real segments only", () => {
    expect(blockedSessionCount("a")).toBe(1);
    expect(blockedSessionCount("a,b")).toBe(2);
    expect(blockedSessionCount("a, ,b,")).toBe(2);
  });
});

describe("ExcessiveDataBlocked payload reaches the detail modal's actions", () => {
  it("carries a sessionId the modal can deep-link on", () => {
    // Mirrors OpsEventService.RecordExcessiveDataBlockedAsync. This event used to be
    // aggregated per tenant ({devicesBlocked, windowHours}), which left the modal with
    // no device to act on — exactly the alert where an operator wants to act.
    const details = JSON.stringify({
      sessionId: "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc",
      serialNumber: "PF5YANFB",
      windowHours: 12,
      durationHours: 8,
    });
    expect(extractSessionId(details)).toBe("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc");
  });

  it("would have rendered no actions for the old aggregate payload", () => {
    expect(extractSessionId(JSON.stringify({ devicesBlocked: 1, windowHours: 12 }))).toBeNull();
  });
});
