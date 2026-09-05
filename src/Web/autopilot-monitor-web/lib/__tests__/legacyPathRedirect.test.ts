import { describe, it, expect } from "vitest";
import { legacyTarget } from "@/components/LegacyPathRedirect";

/**
 * Pins the legacy path → canonical query-URL mapping. These path shapes live
 * forever in sent Teams/Slack/webhook notifications and bookmarks; a mapping
 * regression silently strands them on the wrong page after the SWA wildcard
 * rewrite. Targets are built via lib/routes.ts, so this also pins the
 * canonical shapes end-to-end.
 */

const q = (s: string) => new URLSearchParams(s);

describe("legacyTarget", () => {
  it("maps /sessions/{id}", () => {
    expect(legacyTarget("/sessions/abc-123", q(""), "")).toBe("/sessions?id=abc-123");
  });

  it("preserves tenantId and hash (#event deep links)", () => {
    expect(legacyTarget("/sessions/abc", q("tenantId=t-1"), "#event-42")).toBe(
      "/sessions?id=abc&tenantId=t-1#event-42",
    );
  });

  it("maps /diagnosis/{id}", () => {
    expect(legacyTarget("/diagnosis/abc", q(""), "")).toBe("/diagnosis?id=abc");
  });

  it("maps /apps/{name} with encoding and carried params", () => {
    expect(legacyTarget("/apps/Adobe%20Reader", q("days=30"), "")).toBe(
      "/apps/detail?name=Adobe%20Reader&days=30",
    );
  });

  it("maps /admin/backups/{id} and /admin/customs-archive/{t}/{rk}", () => {
    expect(legacyTarget("/admin/backups/b-1", q(""), "")).toBe("/admin/backups/detail?id=b-1");
    expect(legacyTarget("/admin/customs-archive/t-1/rk-1", q(""), "")).toBe(
      "/admin/customs-archive/detail?tenantId=t-1&rowKey=rk-1",
    );
  });

  it("tolerates trailing slashes (SWA trailingSlash auto)", () => {
    expect(legacyTarget("/sessions/abc/", q(""), "")).toBe("/sessions?id=abc");
    expect(legacyTarget("/diagnosis/abc/", q(""), "")).toBe("/diagnosis?id=abc");
  });

  it("leaves canonical and unrelated paths alone", () => {
    expect(legacyTarget("/sessions", q("id=abc"), "")).toBeNull();
    expect(legacyTarget("/sessions/network-timeline", q("id=abc&tenantId=t"), "")).toBeNull();
    expect(legacyTarget("/apps/detail", q("name=x"), "")).toBeNull();
    expect(legacyTarget("/admin/backups/detail", q("id=x"), "")).toBeNull();
    expect(legacyTarget("/admin/customs-archive/detail", q(""), "")).toBeNull();
    expect(legacyTarget("/dashboard", q(""), "")).toBeNull();
    expect(legacyTarget("/", q(""), "")).toBeNull();
  });
});
