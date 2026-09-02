import { describe, it, expect } from "vitest";
import { PORTAL_HOST, portalHandoverUrl } from "../hostRouting";

describe("portalHandoverUrl", () => {
  it("passes the learned app registration along as ?authapp=", () => {
    expect(portalHandoverUrl("/dashboard", "legacy")).toBe(`https://${PORTAL_HOST}/dashboard?authapp=legacy`);
  });

  it("keeps an existing query string on the target", () => {
    expect(portalHandoverUrl("/sessions?id=abc", "primary")).toBe(`https://${PORTAL_HOST}/sessions?id=abc&authapp=primary`);
  });

  it("adds nothing outside the parallel window", () => {
    expect(portalHandoverUrl("/progress", null)).toBe(`https://${PORTAL_HOST}/progress`);
  });
});
