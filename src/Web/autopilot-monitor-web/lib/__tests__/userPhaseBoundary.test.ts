import { describe, it, expect } from "vitest";
import { findUserPhaseBoundary, userPhaseSplitIndex, type UserPhaseBoundary } from "@/lib/userPhaseBoundary";
import { buildInstallItems, type InstallEvent } from "@/lib/installProgress";

function phaseEvt(phase: number, timestamp: string) {
  return { phase, timestamp };
}

function appEvt(eventType: string, timestamp: string, appName: string, targeted?: string): InstallEvent {
  return { eventType, timestamp, data: { appName, appId: appName, ...(targeted ? { targeted } : {}) } };
}

describe("findUserPhaseBoundary", () => {
  it("returns the first Account Setup / Apps (User) declaration with the phase timeline's names", () => {
    const b = findUserPhaseBoundary([
      phaseEvt(2, "2026-09-11T06:57:00Z"),
      phaseEvt(-1, "2026-09-11T06:58:00Z"),
      phaseEvt(5, "2026-09-11T07:05:00Z"),
      phaseEvt(4, "2026-09-11T07:01:50Z"),
    ], "v1");
    expect(b).toEqual({ startMs: Date.parse("2026-09-11T07:01:50Z"), beforeLabel: "Device Setup", afterLabel: "Account Setup" });
  });

  it("is null for sessions without Account Setup (device-only, pre-provisioning part 1)", () => {
    expect(findUserPhaseBoundary([phaseEvt(2, "2026-09-11T06:57:00Z"), phaseEvt(3, "2026-09-11T06:58:00Z"), phaseEvt(6, "2026-09-11T07:00:00Z")], "v1")).toBeNull();
    expect(findUserPhaseBoundary([], "v1")).toBeNull();
  });

  it("is null for Device Preparation (v2) even though the management extension logs the AccountSetup line there", () => {
    // Session 80241394 (v2): esp_phase_changed AccountSetup exists, but the v2 timeline has no
    // Device Setup / Account Setup split — the whole run happens after the user signed in.
    expect(findUserPhaseBoundary([phaseEvt(4, "2026-09-08T15:56:07Z")], "v2")).toBeNull();
  });

  it("ignores declarations with an unparseable timestamp", () => {
    expect(findUserPhaseBoundary([phaseEvt(4, "not-a-date")], "v1")).toBeNull();
  });
});

describe("userPhaseSplitIndex", () => {
  const boundary: UserPhaseBoundary = { startMs: Date.parse("2026-09-11T07:00:00Z"), beforeLabel: "Device Setup", afterLabel: "Account Setup" };
  const rows = ["06:58", "06:59", "07:00", "07:03"].map(t => Date.parse(`2026-09-11T${t}:00Z`));

  it("points at the first row that started at or after the boundary", () => {
    expect(userPhaseSplitIndex(rows, r => r, boundary)).toBe(2);
  });

  it("is -1 without a boundary or when every row started before it", () => {
    expect(userPhaseSplitIndex(rows, r => r, null)).toBe(-1);
    expect(userPhaseSplitIndex(rows.slice(0, 2), r => r, boundary)).toBe(-1);
  });

  it("is 0 when every row started after Account Setup began", () => {
    expect(userPhaseSplitIndex(rows.slice(2), r => r, boundary)).toBe(0);
  });

  it("never lets a row without a start open the section", () => {
    expect(userPhaseSplitIndex([NaN, rows[3]], r => r, boundary)).toBe(1);
  });
});

describe("InstallItem.firstSeenAt and targeted", () => {
  it("keeps the row's first event time, so an install that finishes after the boundary stays above it", () => {
    const items = buildInstallItems([
      appEvt("app_install_started", "2026-09-11T06:59:00Z", "Device App", "Device"),
      appEvt("app_install_completed", "2026-09-11T07:02:00Z", "Device App", "Device"),
      appEvt("app_install_started", "2026-09-11T07:04:00Z", "User App", "User"),
    ]);
    expect(items.map(i => i.firstSeenAt)).toEqual(["2026-09-11T06:59:00Z", "2026-09-11T07:04:00Z"]);

    const boundary = findUserPhaseBoundary([phaseEvt(4, "2026-09-11T07:01:50Z")], "v1");
    expect(userPhaseSplitIndex(items, i => Date.parse(i.firstSeenAt), boundary)).toBe(1);
  });

  it("carries the IME assignment target and keeps it when a later event omits it", () => {
    // Session cbaed57b: a device-assigned row can start after Account Setup began (Cloud PC) —
    // the assignment is the row's own fact, independent of the divider.
    const items = buildInstallItems([
      appEvt("app_install_started", "2026-09-11T08:55:00Z", "Device App", "Device"),
      appEvt("app_install_completed", "2026-09-11T08:55:11Z", "Device App"),
      appEvt("app_install_started", "2026-09-11T08:56:00Z", "User App", "User"),
      appEvt("office_install_started", "2026-09-11T08:57:00Z", "Microsoft 365 Apps"),
    ]);
    expect(items.map(i => i.targeted)).toEqual(["Device", "User", undefined]);
  });
});
