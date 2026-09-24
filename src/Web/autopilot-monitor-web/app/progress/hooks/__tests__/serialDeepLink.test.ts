import { describe, it, expect } from "vitest";
import { MAX_SERIAL_PARAM_LENGTH, readSerialParam, withSerialParam } from "../serialDeepLink";

describe("readSerialParam", () => {
  it("returns the trimmed serial", () => {
    expect(readSerialParam("?serial=ABC123")).toBe("ABC123");
    expect(readSerialParam("?serial=%20ABC123%20")).toBe("ABC123");
  });

  it("decodes device names with spaces and special characters", () => {
    expect(readSerialParam("?serial=CPC-user%20A%2B1")).toBe("CPC-user A+1");
  });

  it("returns null when the parameter is absent, empty or blank", () => {
    expect(readSerialParam("")).toBeNull();
    expect(readSerialParam("?other=1")).toBeNull();
    expect(readSerialParam("?serial=")).toBeNull();
    expect(readSerialParam("?serial=%20%20")).toBeNull();
  });

  it("rejects terms above the length cap", () => {
    expect(readSerialParam(`?serial=${"A".repeat(MAX_SERIAL_PARAM_LENGTH)}`)).toHaveLength(MAX_SERIAL_PARAM_LENGTH);
    expect(readSerialParam(`?serial=${"A".repeat(MAX_SERIAL_PARAM_LENGTH + 1)}`)).toBeNull();
  });
});

describe("withSerialParam", () => {
  it("sets the serial and keeps other parameters", () => {
    expect(withSerialParam("", "ABC123")).toBe("?serial=ABC123");
    expect(withSerialParam("?authapp=legacy&serial=OLD", "NEW 1")).toBe("?authapp=legacy&serial=NEW+1");
  });

  it("round-trips through readSerialParam", () => {
    expect(readSerialParam(withSerialParam("", "CPC-user A+1"))).toBe("CPC-user A+1");
  });
});
