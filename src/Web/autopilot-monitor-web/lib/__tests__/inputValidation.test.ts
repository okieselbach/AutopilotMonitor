import { describe, it, expect } from "vitest";
import { isGuid, asGuidOrUndefined, isSessionId } from "../inputValidation";

describe("isGuid", () => {
  it("accepts a valid lowercase GUID", () => {
    expect(isGuid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")).toBe(true);
  });

  it("accepts a valid uppercase GUID", () => {
    expect(isGuid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")).toBe(true);
  });

  it("accepts a GUID with leading/trailing whitespace (trimmed)", () => {
    expect(isGuid("  a1b2c3d4-e5f6-7890-abcd-ef1234567890  ")).toBe(true);
  });

  it("rejects a string that is too short", () => {
    expect(isGuid("a1b2c3d4-e5f6-7890")).toBe(false);
  });

  it("rejects a GUID without dashes", () => {
    expect(isGuid("a1b2c3d4e5f67890abcdef1234567890")).toBe(false);
  });

  it("rejects an empty string", () => {
    expect(isGuid("")).toBe(false);
  });

  it("rejects null", () => {
    expect(isGuid(null)).toBe(false);
  });

  it("rejects undefined", () => {
    expect(isGuid(undefined)).toBe(false);
  });

  it("rejects SQL injection attempt", () => {
    expect(isGuid("'; DROP TABLE Sessions;--")).toBe(false);
  });

  it("rejects a GUID with invalid hex characters", () => {
    expect(isGuid("g1b2c3d4-e5f6-7890-abcd-ef1234567890")).toBe(false);
  });
});

describe("asGuidOrUndefined", () => {
  it("returns trimmed GUID for valid input", () => {
    expect(asGuidOrUndefined("  a1b2c3d4-e5f6-7890-abcd-ef1234567890  ")).toBe(
      "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    );
  });

  it("returns undefined for invalid input", () => {
    expect(asGuidOrUndefined("not-a-guid")).toBeUndefined();
  });

  it("returns undefined for null", () => {
    expect(asGuidOrUndefined(null)).toBeUndefined();
  });

  it("returns undefined for empty string", () => {
    expect(asGuidOrUndefined("")).toBeUndefined();
  });
});

describe("isSessionId", () => {
  it("accepts a valid GUID (delegates to isGuid)", () => {
    expect(isSessionId("a1b2c3d4-e5f6-7890-abcd-ef1234567890")).toBe(true);
  });

  it("rejects invalid input", () => {
    expect(isSessionId("not-valid")).toBe(false);
  });
});
