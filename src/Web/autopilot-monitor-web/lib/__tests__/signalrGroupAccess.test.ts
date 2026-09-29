import { describe, expect, it } from "vitest";
import { groupTenantId, refusedJoinStatus, type GroupJoinCaller } from "../signalrGroupAccess";

/**
 * The client gate must refuse exactly what the backend refuses by rule (SignalRGroupHelper +
 * SignalRAddToGroupFunction) for the caller's HOME tenant, and leave every cross-tenant join
 * to the backend's admission.
 */

const HOME = "11111111-1111-1111-1111-111111111111";
const OTHER = "22222222-2222-2222-2222-222222222222";
const SESSION = "33333333-3333-3333-3333-333333333333";

const broadcast = (tid: string) => `tenant-${tid}`;
const notifyMember = (tid: string) => `tenant-${tid}-notify-member`;
const notifyAdmin = (tid: string) => `tenant-${tid}-notify-admin`;
const session = (tid: string) => `session-${tid}-${SESSION}`;

function caller(overrides: Partial<GroupJoinCaller> = {}): GroupJoinCaller {
  return {
    tenantId: HOME,
    isTenantAdmin: false,
    isGlobalAdmin: false,
    isGlobalReader: false,
    isDelegated: false,
    role: null,
    ...overrides,
  };
}

describe("groupTenantId", () => {
  it("parses every tenant-scoped group format", () => {
    expect(groupTenantId(broadcast(HOME))).toBe(HOME);
    expect(groupTenantId(notifyMember(HOME))).toBe(HOME);
    expect(groupTenantId(notifyAdmin(HOME))).toBe(HOME);
    expect(groupTenantId(session(HOME))).toBe(HOME);
  });

  it("is null or empty for names the backend cannot attribute", () => {
    expect(groupTenantId("tenant-")).toBe("");
    expect(groupTenantId("session-abc")).toBeNull();
    expect(groupTenantId("global-admins")).toBeNull();
    expect(groupTenantId("something-else")).toBeNull();
  });
});

describe("refusedJoinStatus", () => {
  it("answers 400 for a name the backend cannot parse, whoever asks", () => {
    // The dashboard used to join "tenant-" before auth/me delivered the tenant id.
    for (const c of [null, caller(), caller({ isGlobalAdmin: true })]) {
      expect(refusedJoinStatus("tenant-", c)).toBe(400);
      expect(refusedJoinStatus("session-abc", c)).toBe(400);
      expect(refusedJoinStatus("unknown-group", c)).toBe(400);
    }
  });

  it("leaves well-formed joins to the backend while the caller is unknown", () => {
    for (const g of [broadcast(HOME), notifyMember(HOME), notifyAdmin(HOME), session(HOME), "global-admins"]) {
      expect(refusedJoinStatus(g, null)).toBeNull();
    }
  });

  it("refuses a roleless caller its home tenant's member groups and serial-less session groups", () => {
    const roleless = caller();
    expect(refusedJoinStatus(broadcast(HOME), roleless)).toBe(403);
    expect(refusedJoinStatus(notifyMember(HOME), roleless)).toBe(403);
    expect(refusedJoinStatus(notifyAdmin(HOME), roleless)).toBe(403);
    expect(refusedJoinStatus(session(HOME), roleless)).toBe(403);
  });

  it("lets a roleless caller join a session group with a serial number (Progress Portal)", () => {
    expect(refusedJoinStatus(session(HOME), caller(), "SERIAL-1")).toBeNull();
  });

  it("refuses a delegated admin without own role its HOME tenant's broadcast group", () => {
    // hasTenantReadScope would admit them; the backend does not.
    expect(refusedJoinStatus(broadcast(HOME), caller({ isDelegated: true }))).toBe(403);
  });

  it("never decides a cross-tenant join (platform or delegated admission is the backend's)", () => {
    for (const c of [caller(), caller({ isDelegated: true }), caller({ role: "Viewer" })]) {
      expect(refusedJoinStatus(broadcast(OTHER), c)).toBeNull();
      expect(refusedJoinStatus(notifyMember(OTHER), c)).toBeNull();
      expect(refusedJoinStatus(notifyAdmin(OTHER), c)).toBeNull();
      expect(refusedJoinStatus(session(OTHER), c)).toBeNull();
    }
  });

  it.each(["Operator", "Viewer"] as const)("admits a %s to every home group except the admin notify group", (role) => {
    const member = caller({ role });
    expect(refusedJoinStatus(broadcast(HOME), member)).toBeNull();
    expect(refusedJoinStatus(notifyMember(HOME), member)).toBeNull();
    expect(refusedJoinStatus(session(HOME), member)).toBeNull();
    expect(refusedJoinStatus(notifyAdmin(HOME), member)).toBe(403);
  });

  it("admits the tenant Admin and a Global Admin to every home group", () => {
    for (const c of [caller({ isTenantAdmin: true, role: "Admin" }), caller({ isGlobalAdmin: true })]) {
      for (const g of [broadcast(HOME), notifyMember(HOME), notifyAdmin(HOME), session(HOME)]) {
        expect(refusedJoinStatus(g, c)).toBeNull();
      }
    }
  });

  it("keeps the admin notify group closed to a Global Reader without the Admin role", () => {
    const reader = caller({ isGlobalReader: true });
    expect(refusedJoinStatus(broadcast(HOME), reader)).toBeNull();
    expect(refusedJoinStatus(notifyMember(HOME), reader)).toBeNull();
    expect(refusedJoinStatus(notifyAdmin(HOME), reader)).toBe(403);
  });

  it("matches the home tenant case-insensitively", () => {
    expect(refusedJoinStatus(broadcast(HOME.toUpperCase()), caller())).toBe(403);
  });

  it("opens global-admins to platform scope only", () => {
    expect(refusedJoinStatus("global-admins", caller())).toBe(403);
    expect(refusedJoinStatus("global-admins", caller({ isTenantAdmin: true, role: "Admin" }))).toBe(403);
    expect(refusedJoinStatus("global-admins", caller({ isGlobalReader: true }))).toBeNull();
    expect(refusedJoinStatus("global-admins", caller({ isGlobalAdmin: true }))).toBeNull();
  });
});
