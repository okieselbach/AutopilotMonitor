import { describe, expect, it } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import {
  clampHelloWaitTimeoutSeconds,
  HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS,
  HELLO_WAIT_TIMEOUT_MAX_SECONDS,
  HELLO_WAIT_TIMEOUT_MIN_SECONDS,
} from "../helloWaitTimeout";

describe("Hello wait timeout bounds", () => {
  it("mirror the server's HelloWaitTimeout range", () => {
    const source = fs.readFileSync(
      path.resolve(__dirname, "../../../../../../Shared/AutopilotMonitor.Shared/Models/Config/HelloWaitTimeout.cs"),
      "utf8",
    );
    expect(source).toMatch(new RegExp(`MinSeconds = ${HELLO_WAIT_TIMEOUT_MIN_SECONDS};`));
    expect(source).toMatch(new RegExp(`MaxSeconds = ${HELLO_WAIT_TIMEOUT_MAX_SECONDS};`));
  });

  it("default matches the C# model default carried by the shared manifest", () => {
    expect(SHARED_MANIFEST.tenantConfiguration.defaults.helloWaitTimeoutSeconds).toBe(HELLO_WAIT_TIMEOUT_DEFAULT_SECONDS);
  });

  it("keeps values inside the range", () => {
    expect(clampHelloWaitTimeoutSeconds(30)).toBe(30);
    expect(clampHelloWaitTimeoutSeconds(300)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(3600)).toBe(3600);
  });

  it("clamps values outside the range", () => {
    expect(clampHelloWaitTimeoutSeconds(0)).toBe(30);
    expect(clampHelloWaitTimeoutSeconds(29)).toBe(30);
    expect(clampHelloWaitTimeoutSeconds(86400)).toBe(3600);
  });

  it("resolves a non-numeric value to the default and rounds fractions", () => {
    expect(clampHelloWaitTimeoutSeconds(Number.NaN)).toBe(300);
    expect(clampHelloWaitTimeoutSeconds(1800.6)).toBe(1801);
  });
});
