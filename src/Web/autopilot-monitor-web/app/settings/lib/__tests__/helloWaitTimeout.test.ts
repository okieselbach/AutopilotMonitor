import { describe, expect, it } from "vitest";
import fs from "node:fs";
import path from "node:path";
import {
  clampHelloWaitTimeoutSeconds,
  HELLO_WAIT_TIMEOUT_MAX_SECONDS,
  HELLO_WAIT_TIMEOUT_MIN_SECONDS,
} from "../helloWaitTimeout";

describe("Hello wait timeout bounds", () => {
  it("mirror the server's HelloWaitTimeout constants", () => {
    const source = fs.readFileSync(
      path.resolve(__dirname, "../../../../../../Shared/AutopilotMonitor.Shared/Models/Config/HelloWaitTimeout.cs"),
      "utf8",
    );
    expect(source).toMatch(new RegExp(`MinSeconds = ${HELLO_WAIT_TIMEOUT_MIN_SECONDS};`));
    expect(source).toMatch(new RegExp(`MaxSeconds = ${HELLO_WAIT_TIMEOUT_MAX_SECONDS};`));
  });

  it("keeps values inside the range", () => {
    expect(clampHelloWaitTimeoutSeconds(300)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(1800)).toBe(1800);
    expect(clampHelloWaitTimeoutSeconds(3600)).toBe(3600);
  });

  it("clamps values outside the range", () => {
    expect(clampHelloWaitTimeoutSeconds(30)).toBe(300); // the old default
    expect(clampHelloWaitTimeoutSeconds(0)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(86400)).toBe(3600);
  });

  it("resolves missing or non-numeric values to the minimum", () => {
    expect(clampHelloWaitTimeoutSeconds(undefined)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(null)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(Number.NaN)).toBe(300);
  });

  it("rounds fractional input", () => {
    expect(clampHelloWaitTimeoutSeconds(1800.6)).toBe(1801);
  });
});
