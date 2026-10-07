import { describe, expect, it } from "vitest";
import { formatDateTime, formatRelativeTime } from "../push/pushFormat";

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
