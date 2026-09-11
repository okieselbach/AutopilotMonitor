import { describe, it, expect } from "vitest";
import { findUserPhaseStartMs, userPhaseSplitIndex } from "@/lib/userPhaseBoundary";
import { buildInstallItems, type InstallEvent } from "@/lib/installProgress";

function phaseEvt(phase: number, timestamp: string) {
  return { phase, timestamp };
}

function appEvt(eventType: string, timestamp: string, appName: string): InstallEvent {
  return { eventType, timestamp, data: { appName, appId: appName } };
}

describe("findUserPhaseStartMs", () => {
  it("returns the first Account Setup / Apps (User) declaration", () => {
    const ms = findUserPhaseStartMs([
      phaseEvt(2, "2026-09-11T06:57:00Z"),
      phaseEvt(-1, "2026-09-11T06:58:00Z"),
      phaseEvt(5, "2026-09-11T07:05:00Z"),
      phaseEvt(4, "2026-09-11T07:01:50Z"),
    ]);
    expect(ms).toBe(Date.parse("2026-09-11T07:01:50Z"));
  });

  it("is null for sessions without a user phase (device-only, pre-provisioning part 1, device preparation)", () => {
    expect(findUserPhaseStartMs([phaseEvt(2, "2026-09-11T06:57:00Z"), phaseEvt(3, "2026-09-11T06:58:00Z"), phaseEvt(6, "2026-09-11T07:00:00Z")])).toBeNull();
    expect(findUserPhaseStartMs([])).toBeNull();
  });

  it("ignores declarations with an unparseable timestamp", () => {
    expect(findUserPhaseStartMs([phaseEvt(4, "not-a-date")])).toBeNull();
  });
});

describe("userPhaseSplitIndex", () => {
  const boundary = Date.parse("2026-09-11T07:00:00Z");
  const rows = ["06:58", "06:59", "07:00", "07:03"].map(t => Date.parse(`2026-09-11T${t}:00Z`));

  it("points at the first row that started at or after the boundary", () => {
    expect(userPhaseSplitIndex(rows, r => r, boundary)).toBe(2);
  });

  it("is -1 without a boundary or when every row started before it", () => {
    expect(userPhaseSplitIndex(rows, r => r, null)).toBe(-1);
    expect(userPhaseSplitIndex(rows.slice(0, 2), r => r, boundary)).toBe(-1);
  });

  it("is 0 when the whole list belongs to the user phase", () => {
    expect(userPhaseSplitIndex(rows.slice(2), r => r, boundary)).toBe(0);
  });

  it("never lets a row without a start open the user section", () => {
    expect(userPhaseSplitIndex([NaN, rows[3]], r => r, boundary)).toBe(1);
  });
});

describe("InstallItem.firstSeenAt", () => {
  it("keeps the row's first event time, so an install that finishes after the boundary stays in the device phase", () => {
    const items = buildInstallItems([
      appEvt("app_install_started", "2026-09-11T06:59:00Z", "Device App"),
      appEvt("app_install_completed", "2026-09-11T07:02:00Z", "Device App"),
      appEvt("app_install_started", "2026-09-11T07:04:00Z", "User App"),
    ]);
    expect(items.map(i => i.firstSeenAt)).toEqual(["2026-09-11T06:59:00Z", "2026-09-11T07:04:00Z"]);

    const boundary = findUserPhaseStartMs([phaseEvt(4, "2026-09-11T07:01:50Z")]);
    expect(userPhaseSplitIndex(items, i => Date.parse(i.firstSeenAt), boundary)).toBe(1);
  });
});
