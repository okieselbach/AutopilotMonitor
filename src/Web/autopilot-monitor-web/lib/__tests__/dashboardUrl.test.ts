import { describe, expect, it } from "vitest";
import { dashboardUrl } from "../routes";

describe("dashboardUrl", () => {
  it("returns the bare route without options", () => {
    expect(dashboardUrl()).toBe("/dashboard");
    expect(dashboardUrl({})).toBe("/dashboard");
  });

  it("omits undefined and empty params (no trailing ?)", () => {
    expect(dashboardUrl({ status: undefined, search: "" })).toBe("/dashboard");
  });

  it("builds the fleet-context deep link", () => {
    expect(dashboardUrl({ ruleId: "ESP-TIMEOUT-01" })).toBe("/dashboard?ruleId=ESP-TIMEOUT-01");
  });

  it("encodes values and carries the tenant scope", () => {
    expect(
      dashboardUrl({
        status: "Failed",
        search: "Microsoft Surface Pro",
        tenant: "11111111-2222-3333-4444-555555555555",
      }),
    ).toBe(
      "/dashboard?status=Failed&search=Microsoft%20Surface%20Pro&tenant=11111111-2222-3333-4444-555555555555",
    );
  });
});
