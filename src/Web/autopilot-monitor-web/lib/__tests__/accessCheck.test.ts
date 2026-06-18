import { describe, it, expect } from "vitest";
import { classifyAccessCheck } from "@/lib/accessCheck";

describe("classifyAccessCheck", () => {
  it("reconciles only when access is authoritatively present", () => {
    expect(classifyAccessCheck(true, { accessPresent: true, isTransient: false })).toBe("reconciled");
  });

  it("treats a non-ok HTTP response as transient (retryable), never absent", () => {
    expect(classifyAccessCheck(false, undefined)).toBe("transient");
  });

  it("treats a transient probe as transient even if accessPresent is (stale) true", () => {
    // An inconclusive probe must never flip the gate — guards against opening the agent gate
    // on a timeout / Graph 5xx while the role may actually be absent.
    expect(classifyAccessCheck(true, { accessPresent: true, isTransient: true })).toBe("transient");
  });

  it("treats a missing/undefined payload as transient", () => {
    expect(classifyAccessCheck(true, undefined)).toBe("transient");
  });

  it("reports absent when the permission is authoritatively not granted", () => {
    expect(classifyAccessCheck(true, { accessPresent: false, isTransient: false })).toBe("absent");
  });

  it("reports absent when accessPresent is omitted on an authoritative response", () => {
    expect(classifyAccessCheck(true, { isTransient: false })).toBe("absent");
  });
});
