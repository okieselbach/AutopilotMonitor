import { describe, it, expect } from "vitest";
import { targetBlocked } from "../types";

// The enable gate for gather rules: a CUSTOM rule whose target fails guardrail
// validation must not be enableable — the agent would block it on every device.

const custom = { isBuiltIn: false, isCommunity: false };

describe("targetBlocked", () => {
  it("blocks a custom rule with a disallowed WMI query", () => {
    expect(targetBlocked({ ...custom, collectorType: "wmi", target: "SELECT * FROM Win32_Process" }, false)).toBe(true);
  });

  it("does not block an allowed projection query", () => {
    expect(targetBlocked({ ...custom, collectorType: "wmi", target: "SELECT BatteryStatus FROM Win32_Battery" }, false)).toBe(false);
  });

  it("never blocks built-in or community rules", () => {
    expect(targetBlocked({ isBuiltIn: true, isCommunity: false, collectorType: "wmi", target: "SELECT * FROM Win32_Process" }, false)).toBe(false);
    expect(targetBlocked({ isBuiltIn: false, isCommunity: true, collectorType: "wmi", target: "SELECT * FROM Win32_Process" }, false)).toBe(false);
  });

  it("respects unrestricted mode", () => {
    expect(targetBlocked({ ...custom, collectorType: "wmi", target: "SELECT * FROM Win32_Process" }, true)).toBe(false);
  });

  it("does not block an empty target (required-field validation owns that)", () => {
    expect(targetBlocked({ ...custom, collectorType: "wmi", target: "" }, false)).toBe(false);
  });

  it("blocks disallowed targets of other collector types too", () => {
    expect(targetBlocked({ ...custom, collectorType: "file", target: "C:\\Users\\alice\\secret.txt" }, false)).toBe(true);
    expect(targetBlocked({ ...custom, collectorType: "eventlog", target: "Security" }, false)).toBe(true);
  });
});
