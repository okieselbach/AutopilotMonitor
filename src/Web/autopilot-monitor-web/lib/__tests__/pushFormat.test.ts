import { describe, expect, it } from "vitest";
import { formatDateTime, formatRelativeTime, formatTraceRecord } from "../push/pushFormat";

const NOW = Date.parse("2026-10-07T12:00:00Z");

describe("push history formatting", () => {
  it("renders relative times by the portal's steps", () => {
    expect(formatRelativeTime("2026-10-07T11:59:40Z", NOW)).toBe("just now");
    expect(formatRelativeTime("2026-10-07T11:55:00Z", NOW)).toBe("5m ago");
    expect(formatRelativeTime("2026-10-07T09:00:00Z", NOW)).toBe("3h ago");
    expect(formatRelativeTime("2026-10-05T12:00:00Z", NOW)).toBe("2d ago");
    expect(formatRelativeTime("2026-09-01T12:00:00Z", NOW)).toBe(new Date("2026-09-01T12:00:00Z").toLocaleDateString());
    expect(formatRelativeTime("nope", NOW)).toBe("");
  });

  it("renders a dash for missing or unreadable timestamps", () => {
    expect(formatDateTime(null)).toBe("—");
    expect(formatDateTime(undefined)).toBe("—");
    expect(formatDateTime("nope")).toBe("—");
    expect(formatDateTime("2026-10-07T12:00:00Z")).toBe(new Date("2026-10-07T12:00:00Z").toLocaleString());
  });
});

describe("formatTraceRecord", () => {
  it("lists time, event and the explaining fields, with the id shortened and the display only when it deviated", () => {
    const line = formatTraceRecord({ at: "2026-10-08T17:18:42Z", event: "push", source: "notification", type: "session_watch", id: "0123456789abcdef", result: "added", shown: "shown" });
    expect(line.endsWith(" · push · notification · session_watch · added · #01234567")).toBe(true);
    expect(line).not.toContain("display");
    expect(formatTraceRecord({ at: "2026-10-08T17:18:42Z", event: "push", source: "data", result: "error: QuotaExceededError", shown: "fallback" })).toContain(
      "push · data · error: QuotaExceededError · display fallback",
    );
    expect(formatTraceRecord({ at: "not a date", event: "activate" })).toBe("— · activate");
  });
});
