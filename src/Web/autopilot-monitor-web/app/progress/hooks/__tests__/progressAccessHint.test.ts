import { describe, expect, it } from "vitest";
import { formatSignupDate, progressAccessHint } from "../progressAccessHint";

describe("progressAccessHint", () => {
  it("keeps the member hint while the status is pending or failed", () => {
    expect(progressAccessHint(null)).toEqual({ kind: "member" });
  });

  it("keeps the member hint for an organization in use", () => {
    expect(progressAccessHint({ success: true, unused: false, signedUpAt: "2026-03-12T08:30:00Z" })).toEqual({
      kind: "member",
    });
  });

  it("names the signup date of an unused organization", () => {
    expect(progressAccessHint({ success: true, unused: true, signedUpAt: "2026-03-12T08:30:00Z" })).toEqual({
      kind: "unused",
      signedUpOn: "12 March 2026",
    });
  });

  it("leaves the date out when the backend does not know it", () => {
    expect(progressAccessHint({ success: true, unused: true })).toEqual({ kind: "unused", signedUpOn: null });
  });
});

describe("formatSignupDate", () => {
  it("reads the date in UTC, so a late-evening signup keeps its day", () => {
    expect(formatSignupDate("2026-03-12T23:30:00Z")).toBe("12 March 2026");
  });

  it("returns null for an unparsable value", () => {
    expect(formatSignupDate("not a date")).toBeNull();
  });
});
