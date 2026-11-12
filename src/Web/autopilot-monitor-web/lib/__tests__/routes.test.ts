import { describe, expect, it } from "vitest";
import { appsHubUrl } from "../routes";

describe("appsHubUrl", () => {
  it("is the bare hub by default", () => {
    expect(appsHubUrl()).toBe("/apps");
  });

  it("carries a tab, a window and a tenant", () => {
    expect(appsHubUrl({ tab: "vulnerabilities" })).toBe("/apps?tab=vulnerabilities");
    expect(appsHubUrl({ days: 90 })).toBe("/apps?days=90");
    expect(appsHubUrl({ tab: "vulnerabilities", days: 7, tenantId: "11111111-1111-1111-1111-111111111111" })).toBe(
      "/apps?tab=vulnerabilities&days=7&tenantId=11111111-1111-1111-1111-111111111111",
    );
  });

  it("leaves out an empty tenant", () => {
    expect(appsHubUrl({ tab: "inventory", tenantId: "" })).toBe("/apps?tab=inventory");
  });
});
