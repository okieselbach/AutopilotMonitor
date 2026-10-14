import { describe, it, expect } from "vitest";
import type { GetAppFleetContextResponse } from "@/lib/generated/wire-types.generated";
import { fleetSessionsHref, fleetView } from "../fleetContext";

const context = (over: Partial<GetAppFleetContextResponse>): GetAppFleetContextResponse =>
  Object.assign(
    {
      success: true,
      appName: "Contoso Notes",
      source: "ime",
      windowDays: 7,
      installed: 0,
      skipped: 0,
      failed: 0,
      incomplete: 0,
      inProgress: 0,
      collisionExcluded: 0,
      topFailureCodes: [],
    },
    over
  );

describe("fleetView", () => {
  it("counts the other enrollments, failures first with their most frequent codes", () => {
    const view = fleetView(
      context({
        failed: 4,
        installed: 11,
        inProgress: 1,
        incomplete: 1,
        skipped: 2,
        topFailureCodes: [
          { code: "0x80d03805", count: 2, sampleMessage: "" },
          { code: "esp_apps_timeout", count: 1, sampleMessage: "" },
          { code: "0x87d1041c", count: 1, sampleMessage: "" },
        ],
      }),
      "/apps/detail?name=Contoso%20Notes&days=7"
    );
    expect(view).toEqual({
      title: "Same app in other enrollments",
      detail: "Last 7 days: 4 failed (0x80d03805 ×2, ESP timeout) · 11 installed · 1 still installing · 1 not finished · 2 skipped",
      href: "/apps/detail?name=Contoso%20Notes&days=7",
    });
  });

  it("names an unknown failure type in words and leaves codes as they are", () => {
    const failed = (code: string) => fleetView(context({ failed: 1, topFailureCodes: [{ code, count: 1, sampleMessage: "" }] }), null)!.detail;
    expect(failed("esp_apps_policy_gap")).toBe("Last 7 days: 1 failed (esp apps policy gap)");
    expect(failed("1603")).toBe("Last 7 days: 1 failed (1603)");
  });

  it("shows nothing without an answer or without another enrollment (the read fails soft)", () => {
    expect(fleetView(null, null)).toBeNull();
    expect(fleetView(context({ collisionExcluded: 3 }), null)).toBeNull();
  });
});

describe("fleetSessionsHref", () => {
  const base = {
    appName: "Contoso Notes",
    windowDays: 7,
    viewedTenantId: "00000000-0000-4000-8000-0000000000aa",
    ownTenantId: "00000000-0000-4000-8000-0000000000AA",
    globalAdminMode: false,
    delegatedScope: false,
  };

  it("links a member to the app page of the own tenant", () => {
    expect(fleetSessionsHref(base)).toBe("/apps/detail?name=Contoso%20Notes&days=7");
    expect(fleetSessionsHref({ ...base, source: "realmjoin" })).toBe("/apps/detail?name=Contoso%20Notes&days=7&source=realmjoin");
  });

  it("names the tenant in Global Admin mode, which the app page follows", () => {
    expect(fleetSessionsHref({ ...base, globalAdminMode: true, ownTenantId: "00000000-0000-4000-8000-0000000000bb" })).toBe(
      "/apps/detail?name=Contoso%20Notes&days=7&tenantId=00000000-0000-4000-8000-0000000000aa"
    );
  });

  it("has no link where the app page would show another tenant", () => {
    expect(fleetSessionsHref({ ...base, ownTenantId: "00000000-0000-4000-8000-0000000000bb" })).toBeNull();
    expect(fleetSessionsHref({ ...base, delegatedScope: true })).toBeNull();
    expect(fleetSessionsHref({ ...base, viewedTenantId: "" })).toBeNull();
  });
});
