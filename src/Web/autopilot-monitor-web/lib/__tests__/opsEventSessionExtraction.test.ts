import { describe, it, expect } from "vitest";
import { extractSessionId, extractPortalPath, buildAutoReason } from "../../app/admin/components/opsEventSessionHelpers";
import { tenantManagementUrl } from "../routes";

// Locks in the contract that connects the Ops Events detail modal's "Block this
// device" / "Kill this device" deep-link buttons to the Device Block form.
// extractSessionId decides whether the buttons render at all; buildAutoReason
// decides what arrives pre-filled in the reason field. A typo here would either
// hide the shortcut buttons or surface the wrong reason in the audit log.

describe("extractSessionId", () => {
  it("returns null when details is null or empty", () => {
    expect(extractSessionId(null)).toBeNull();
    expect(extractSessionId("")).toBeNull();
  });

  it("returns null when details is not valid JSON", () => {
    expect(extractSessionId("not json")).toBeNull();
  });

  it("returns null when the parsed object has no session id key", () => {
    expect(extractSessionId(JSON.stringify({ foo: "bar", eventCount: 3 }))).toBeNull();
  });

  it("returns null when the parsed value is a primitive (not an object)", () => {
    expect(extractSessionId(JSON.stringify("a-string"))).toBeNull();
    expect(extractSessionId(JSON.stringify(42))).toBeNull();
  });

  it("extracts sessionId in canonical camelCase shape", () => {
    const json = JSON.stringify({
      sessionId: "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc",
      eventCount: 2243,
      threshold: 2000,
    });
    expect(extractSessionId(json)).toBe("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc");
  });

  it("accepts the PascalCase variant emitted by some backend serializers", () => {
    const json = JSON.stringify({ SessionId: "abc-123" });
    expect(extractSessionId(json)).toBe("abc-123");
  });

  it("accepts snake_case and uppercase ID variants", () => {
    expect(extractSessionId(JSON.stringify({ session_id: "v1" }))).toBe("v1");
    expect(extractSessionId(JSON.stringify({ sessionID: "v2" }))).toBe("v2");
  });

  it("trims surrounding whitespace from the session id", () => {
    expect(extractSessionId(JSON.stringify({ sessionId: "  abc  " }))).toBe("abc");
  });

  it("ignores empty or whitespace-only session id values", () => {
    expect(extractSessionId(JSON.stringify({ sessionId: "" }))).toBeNull();
    expect(extractSessionId(JSON.stringify({ sessionId: "   " }))).toBeNull();
  });

  it("ignores non-string session id values", () => {
    expect(extractSessionId(JSON.stringify({ sessionId: 12345 }))).toBeNull();
    expect(extractSessionId(JSON.stringify({ sessionId: null }))).toBeNull();
  });
});

describe("buildAutoReason", () => {
  const sessionId = "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc";

  it("renders the loop-bug-specific reason for ExcessiveSessionEvents", () => {
    expect(buildAutoReason("ExcessiveSessionEvents", sessionId))
      .toBe("Excessive session events on session 806f61c3");
  });

  it("falls back to a generic reason for other event types", () => {
    expect(buildAutoReason("AgentCrashLoop", sessionId))
      .toBe("From ops alert: AgentCrashLoop (session 806f61c3)");
  });

  it("handles session IDs without dashes by slicing the first 8 chars", () => {
    expect(buildAutoReason("ExcessiveSessionEvents", "abcdef1234567890"))
      .toBe("Excessive session events on session abcdef12");
  });

  it("handles short session IDs without throwing", () => {
    expect(buildAutoReason("ExcessiveSessionEvents", "abc")).toBe("Excessive session events on session abc");
  });
});

// The modal's "Open related page" follows an ops event's details.portalPath. Details are free
// JSON, so only a same-origin path may become a link.
describe("extractPortalPath", () => {
  it("returns an in-app path with its query", () => {
    expect(extractPortalPath(JSON.stringify({ portalPath: "/admin/reports/session-reports?reportId=r1" })))
      .toBe("/admin/reports/session-reports?reportId=r1");
  });

  it("returns null without a usable portalPath", () => {
    expect(extractPortalPath(null)).toBeNull();
    expect(extractPortalPath("not json")).toBeNull();
    expect(extractPortalPath(JSON.stringify({ sessionId: "s" }))).toBeNull();
    expect(extractPortalPath(JSON.stringify({ portalPath: 42 }))).toBeNull();
  });

  it("refuses anything a browser could resolve to another origin", () => {
    for (const portalPath of ["//evil.example", "/\\evil.example", "https://evil.example/x", "javascript:alert(1)", "admin/ops", "/admin\nops"]) {
      expect(extractPortalPath(JSON.stringify({ portalPath }))).toBeNull();
    }
  });

  // TenantSignup's portalPath is built by the backend in exactly this shape; the modal shows
  // one "View tenant" button instead of two only while both sides agree.
  it("matches the tenant link the backend writes for a signup", () => {
    const tenantId = "11111111-1111-1111-1111-111111111111";
    expect(tenantManagementUrl(tenantId)).toBe(`/admin/tenants/management?tenantId=${tenantId}`);
  });
});
