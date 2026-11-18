import { describe, it, expect } from "vitest";
import { deriveDownloadProgressThrottle } from "@/lib/downloadProgressThrottle";

const ev = (timestamp: string, data: Record<string, unknown> | null, eventType = "download_progress_throttled") => ({
  eventType,
  timestamp,
  data,
});

describe("deriveDownloadProgressThrottle", () => {
  it("is null for a session that was never throttled", () => {
    expect(deriveDownloadProgressThrottle([])).toBeNull();
    expect(deriveDownloadProgressThrottle([ev("2026-11-18T13:12:00Z", { stage: "1" }, "download_progress")])).toBeNull();
  });

  it("reads the stage the agent reported (numbers come as strings)", () => {
    const notice = deriveDownloadProgressThrottle([
      ev("2026-11-18T13:12:00Z", { stage: "1", intervalSeconds: "10", liveStartSeconds: "60" }),
    ]);
    expect(notice).toEqual({ sinceMs: Date.parse("2026-11-18T13:12:00Z"), intervalSeconds: 10, liveStartSeconds: 60 });
  });

  it("keeps the first start and the interval of the highest stage, whatever the order", () => {
    const notice = deriveDownloadProgressThrottle([
      ev("2026-11-18T13:52:00Z", { stage: 2, intervalSeconds: 30, liveStartSeconds: 60 }),
      ev("2026-11-18T13:12:00Z", { stage: 1, intervalSeconds: 10, liveStartSeconds: 60 }),
    ]);
    expect(notice?.sinceMs).toBe(Date.parse("2026-11-18T13:12:00Z"));
    expect(notice?.intervalSeconds).toBe(30);
  });

  it("ignores an event without a usable stage", () => {
    expect(deriveDownloadProgressThrottle([ev("2026-11-18T13:12:00Z", null)])).toBeNull();
    expect(deriveDownloadProgressThrottle([ev("2026-11-18T13:12:00Z", { stage: "x", intervalSeconds: "10" })])).toBeNull();
  });
});
