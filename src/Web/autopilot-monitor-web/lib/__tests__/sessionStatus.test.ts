import { describe, it, expect } from "vitest";
import { isTerminalStatus } from "../../lib/sessionStatus";

describe("isTerminalStatus", () => {
  it("returns true for terminal statuses", () => {
    expect(isTerminalStatus("Succeeded")).toBe(true);
    expect(isTerminalStatus("Failed")).toBe(true);
  });

  it("returns false for non-terminal statuses (session can still receive events)", () => {
    expect(isTerminalStatus("InProgress")).toBe(false);
    expect(isTerminalStatus("Pending")).toBe(false);
    expect(isTerminalStatus("Stalled")).toBe(false);
    expect(isTerminalStatus("Unknown")).toBe(false);
  });

  it("returns false for null / undefined / empty (status not yet hydrated)", () => {
    expect(isTerminalStatus(null)).toBe(false);
    expect(isTerminalStatus(undefined)).toBe(false);
    expect(isTerminalStatus("")).toBe(false);
  });

  it("is case-sensitive (matches backend enum casing)", () => {
    expect(isTerminalStatus("succeeded")).toBe(false);
    expect(isTerminalStatus("FAILED")).toBe(false);
  });
});
