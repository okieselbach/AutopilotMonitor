import { describe, it, expect } from "vitest";
import { summarizeEventsByPhase } from "../phaseEventSummary";
import type { EnrollmentEvent } from "@/types";

// Only the fields the summary reads matter; the rest satisfy the wire type.
function ev(phase: number, sequence: number, eventType: string, extra: Partial<EnrollmentEvent> = {}): EnrollmentEvent {
  return {
    eventId: `e-${sequence}`,
    timestamp: "2026-09-23T10:00:00Z",
    eventType,
    severity: "Info",
    source: "Test",
    phase,
    phaseName: "",
    message: "",
    data: {},
    sequence,
    timestampClamped: false,
    rowKey: "",
    ...extra,
  } as EnrollmentEvent;
}

describe("summarizeEventsByPhase", () => {
  it("returns an empty summary for no events", () => {
    const s = summarizeEventsByPhase([]);
    expect(s.byPhase.size).toBe(0);
    expect(s.hasWhiteGloveComplete).toBe(false);
    expect(s.hasWhiteGloveResumed).toBe(false);
  });

  it("takes first/last per phase from the timestamps regardless of list order", () => {
    const s = summarizeEventsByPhase([
      ev(3, 2, "x", { timestamp: "2026-09-23T10:05:00Z" }),
      ev(3, 1, "x", { timestamp: "2026-09-23T10:01:00Z" }),
      ev(3, 3, "x", { timestamp: "2026-09-23T10:03:00Z" }),
      ev(6, 4, "x", { timestamp: "2026-09-23T10:09:00Z" }),
    ]);
    expect(s.byPhase.get(3)).toMatchObject({
      firstMs: Date.parse("2026-09-23T10:01:00Z"),
      lastMs: Date.parse("2026-09-23T10:05:00Z"),
      count: 3,
    });
    expect(s.byPhase.get(6)).toMatchObject({
      firstMs: Date.parse("2026-09-23T10:09:00Z"),
      lastMs: Date.parse("2026-09-23T10:09:00Z"),
      count: 1,
    });
    expect(s.byPhase.has(4)).toBe(false);
  });

  it("derives the activity from the highest-sequence event of a kind, not from list order", () => {
    const s = summarizeEventsByPhase([
      ev(3, 9, "app_install_started", { data: { appName: "Later" } }),
      ev(3, 5, "app_download_started", { data: { appName: "Earlier" } }),
    ]);
    expect(s.byPhase.get(3)?.currentActivity).toBe("Installing Later");
  });

  it("keeps the first event seen on a sequence tie (stable sort + find semantics)", () => {
    const s = summarizeEventsByPhase([
      ev(3, 5, "download_progress", { data: { app_name: "First" } }),
      ev(3, 5, "download_progress", { data: { app_name: "Second" } }),
    ]);
    expect(s.byPhase.get(3)?.currentActivity).toBe("Downloading First");
  });

  it("ranks the activity sources by kind: tracking summary, then ESP state, then app events", () => {
    const s = summarizeEventsByPhase([
      ev(3, 1, "app_tracking_summary", { data: { completedApps: "2", totalApps: "5" } }),
      ev(3, 2, "esp_ui_state", { data: { blocking_apps_completed: "1", blocking_apps_total: "4", current_item: "Installing App" } }),
      ev(3, 3, "app_install_started", { data: { appName: "App" } }),
    ]);
    expect(s.byPhase.get(3)?.currentActivity).toBe("Installing apps (2/5)");

    const esp = summarizeEventsByPhase([
      ev(3, 1, "esp_ui_state", { data: { blockingAppsCompleted: "1", blockingAppsTotal: "4", currentItem: "Installing App" } }),
      ev(3, 2, "app_install_started", { data: { appName: "App" } }),
    ]);
    expect(esp.byPhase.get(3)?.currentActivity).toBe("Installing App (1/4)");
  });

  it("falls back to the latest short message and ignores snapshot noise", () => {
    const s = summarizeEventsByPhase([
      ev(6, 1, "esp_phase_changed", { message: "Finalizing" }),
      ev(6, 2, "performance_snapshot", { message: "cpu 12%" }),
    ]);
    expect(s.byPhase.get(6)).toMatchObject({ count: 2, currentActivity: "Finalizing" });

    const noiseOnly = summarizeEventsByPhase([ev(6, 1, "performance_snapshot", { message: "cpu 12%" })]);
    expect(noiseOnly.byPhase.get(6)?.currentActivity).toBeNull();

    const longMessage = summarizeEventsByPhase([ev(6, 1, "x", { message: "m".repeat(80) })]);
    expect(longMessage.byPhase.get(6)?.currentActivity).toBeNull();
  });

  it("reports download progress with a percentage when both byte counters are present", () => {
    const s = summarizeEventsByPhase([
      ev(3, 1, "download_progress", { data: { app_name: "Pkg", bytes_downloaded: "50", bytes_total: "200" } }),
    ]);
    expect(s.byPhase.get(3)?.currentActivity).toBe("Downloading Pkg - 25%");
  });

  it("flags the WhiteGlove signals wherever they appear", () => {
    const s = summarizeEventsByPhase([ev(3, 1, "whiteglove_complete"), ev(7, 2, "agent_shutdown")]);
    expect(s.hasWhiteGloveComplete).toBe(true);
    expect(s.hasWhiteGloveResumed).toBe(false);
    expect(summarizeEventsByPhase([ev(4, 1, "whiteglove_resumed")]).hasWhiteGloveResumed).toBe(true);
  });
});
