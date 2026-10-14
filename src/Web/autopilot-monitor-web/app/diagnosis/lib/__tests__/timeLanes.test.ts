import { describe, it, expect } from "vitest";
import type { SessionTimeBreakdown } from "@/lib/generated/wire-types.generated";
import { timeLanes } from "../timeLanes";

const T = (min: number) => new Date(Date.UTC(2026, 9, 13, 13, 0) + min * 60_000).toISOString();
const span = (from: number, to: number) => ({ startUtc: T(from), endUtc: T(to), seconds: (to - from) * 60 });

const breakdown = {
  segments: [
    { segmentKey: "device_prep", ...span(0, 10) },
    { segmentKey: "esp_apps", ...span(10, 30) },
    { segmentKey: "identity_hello", ...span(30, 40) },
    { segmentKey: "user_esp", ...span(40, 100) },
  ],
  blockingApps: [
    { appId: "a", appName: "Contoso Portal", ...span(12, 15) },
    { appId: "b", appName: "Contoso Notes", ...span(50, 60) },
    { appId: "b", appName: "Contoso Notes", ...span(70, 80) },
  ],
  sleepSpans: [{ kind: "modern_standby", segmentKey: "user_esp", ...span(55, 58) }],
  rebootSpans: [{ segmentKey: "user_esp", ...span(85, 90) }],
  osUpdates: [],
} as unknown as SessionTimeBreakdown;

describe("timeLanes", () => {
  const lanes = timeLanes(breakdown, {
    failedCategory: "AccountSetup",
    failure: { at: T(100), label: "ESP failed" },
    culprits: [{ kind: "app", evidence: "install_failed", key: "B", name: "Contoso Notes", failedAt: T(80) }],
  })!;

  it("draws the ESP phases with the failed one marked, then sign-in, the apps and the device", () => {
    expect(lanes.lanes.map((l) => l.key)).toEqual(["esp", "sign-in", "app:a", "app:b", "device"]);
    const esp = lanes.lanes[0].bars;
    expect(esp.map((b) => [b.label, b.tone, Math.round(b.left), Math.round(b.width)])).toEqual([
      ["Device preparation", "done", 0, 10],
      ["Device setup", "done", 10, 20],
      ["Account setup", "failed", 40, 60],
    ]);
  });

  it("emphasizes the culprit app (matched by id, case-insensitively) with all its attempts", () => {
    const notes = lanes.lanes.find((l) => l.key === "app:b")!;
    expect(notes.emphasis).toBe(true);
    expect(notes.bars.map((b) => b.tone)).toEqual(["appFailed", "appFailed"]);
    expect(lanes.lanes.find((l) => l.key === "app:a")!.emphasis).toBe(false);
  });

  it("puts sleep and restarts on the device lane and marks the failures", () => {
    expect(lanes.lanes.at(-1)!.bars.map((b) => [b.tone, b.label])).toEqual([
      ["asleep", "Asleep"],
      ["restart", "Restart"],
    ]);
    expect(lanes.markers.map((m) => Math.round(m.left))).toEqual([80, 100]);
    expect(lanes.markers[1].label.startsWith("ESP failed ")).toBe(true);
    expect(lanes.ticks.map((t) => [t.left, t.minor])).toEqual([
      [0, false],
      [25, true],
      [50, false],
      [75, true],
      [100, false],
    ]);
  });

  it("keeps marker labels inside the chart, in time order", () => {
    expect(lanes.markers.map((m) => m.align)).toEqual(["center", "end"]);
    const early = timeLanes(breakdown, {
      failure: { at: T(3), label: "Failed" },
      culprits: [{ kind: "app", evidence: "install_failed", name: "Contoso Notes", failedAt: T(60) }],
    })!;
    expect(early.markers.map((m) => [m.label.split(" ")[0], m.align])).toEqual([
      ["Failed", "start"],
      ["App", "center"],
    ]);
  });

  it("keeps the culprit when it caps the app lanes", () => {
    const capped = timeLanes(breakdown, { culprits: [{ kind: "app", evidence: "install_failed", name: "Contoso Notes" }], maxApps: 1 })!;
    expect(capped.lanes.filter((l) => l.key.startsWith("app:")).map((l) => l.label)).toEqual(["Contoso Notes"]);
  });

  it("is null without a breakdown or a window", () => {
    expect(timeLanes(null, {})).toBeNull();
    expect(timeLanes({ ...breakdown, segments: [] } as SessionTimeBreakdown, {})).toBeNull();
  });
});
