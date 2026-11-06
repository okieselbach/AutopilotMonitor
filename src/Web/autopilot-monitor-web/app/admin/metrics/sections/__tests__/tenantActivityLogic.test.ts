import { describe, expect, it } from "vitest";
import type { TenantActivityTenantItem } from "@/lib/generated/wire-types.generated";
import { planEditionOf } from "../../../components/tenantFilters";
import { formatDayKey } from "../../lib/format";
import {
  agoText,
  changeText,
  dailyChartRows,
  LIFECYCLE_STAGES,
  lifecycleFigures,
  lifecycleGroups,
  parseView,
  percentText,
  rankingRows,
  searchTenants,
  shareText,
  sortRanking,
  trendText,
  windowDayKeys,
} from "../tenantActivity/tenantActivityLogic";

// 2026-11-06 15:00 UTC.
const NOW = Date.UTC(2026, 10, 6, 15);

function tenant(id: string, over: Partial<TenantActivityTenantItem> = {}): TenantActivityTenantItem {
  return {
    tenantId: id,
    domainName: `${id}.example`,
    edition: "community",
    stage: "active",
    enrollments: 0,
    enrollmentsPrevious: 0,
    succeeded: 0,
    failed: 0,
    activeDays: 0,
    lifetimeEnrollments: 0,
    lifetimeSucceeded: 0,
    lifetimeEvents: 0,
    ...over,
  };
}

const ids = (rows: readonly TenantActivityTenantItem[]) => rows.map((t) => t.tenantId);

describe("view", () => {
  it("opens the ranking unless the link asks for the lifecycle", () => {
    expect(parseView("lifecycle")).toBe("lifecycle");
    expect(parseView("ranking")).toBe("ranking");
    expect(parseView("tools")).toBe("ranking");
    expect(parseView(null)).toBe("ranking");
  });
});

describe("ranking", () => {
  const rows = [
    tenant("contoso", { enrollments: 40, trendPercent: 10, successPercent: 95, activeDays: 20, portalUsers: 3, lastActiveDay: "2026-11-05" }),
    tenant("fabrikam", { enrollments: 12, successPercent: 70, activeDays: 4, lastCounterUpdateAt: "2026-11-06T08:00:00.1234567Z" }),
    tenant("northwind", { enrollments: 12, trendPercent: -60, activeDays: 9, portalUsers: 0, lastActiveDay: "2026-10-01" }),
    tenant("quiet", { stage: "quiet", lifetimeEnrollments: 5 }),
  ];

  it("lists the tenants with enrollments in the route's order", () => {
    expect(ids(rankingRows(rows))).toEqual(["contoso", "fabrikam", "northwind"]);
  });

  it("sorts every column and keeps the route's order on a tie", () => {
    const active = rankingRows(rows);
    expect(ids(sortRanking(active, "enrollments", "desc"))).toEqual(["contoso", "fabrikam", "northwind"]);
    expect(ids(sortRanking(active, "enrollments", "asc"))).toEqual(["fabrikam", "northwind", "contoso"]);
    expect(ids(sortRanking(active, "activeDays", "desc"))).toEqual(["contoso", "northwind", "fabrikam"]);
    expect(ids(sortRanking(active, "success", "asc"))).toEqual(["fabrikam", "contoso", "northwind"]);
    // The counters' last move counts before a day with sessions.
    expect(ids(sortRanking(active, "lastActive", "desc"))).toEqual(["fabrikam", "contoso", "northwind"]);
  });

  it("puts a missing value last in either direction", () => {
    const active = rankingRows(rows);
    expect(ids(sortRanking(active, "trend", "desc"))).toEqual(["contoso", "northwind", "fabrikam"]);
    expect(ids(sortRanking(active, "trend", "asc"))).toEqual(["northwind", "contoso", "fabrikam"]);
    expect(ids(sortRanking(active, "portalUsers", "asc"))).toEqual(["northwind", "contoso", "fabrikam"]);
  });

  it("searches the domain, the company and the tenant id, case-insensitively", () => {
    const named = [tenant("aaaaaaaa-1111", { domainName: "contoso.example", companyName: "Contoso Ltd" }), tenant("bbbbbbbb-2222")];
    expect(ids(searchTenants(named, "CONTOSO"))).toEqual(["aaaaaaaa-1111"]);
    expect(ids(searchTenants(named, "ltd"))).toEqual(["aaaaaaaa-1111"]);
    expect(ids(searchTenants(named, "bbbbbbbb"))).toEqual(["bbbbbbbb-2222"]);
    expect(searchTenants(named, "  ")).toBe(named);
  });
});

describe("display", () => {
  it("shows the route's trend, with 'new' for a new tenant and a dash without a window before", () => {
    expect(trendText({ stage: "new", trendPercent: undefined })).toBe("new");
    expect(trendText({ stage: "active", trendPercent: undefined })).toBe("—");
    expect(trendText({ stage: "active", trendPercent: 25 })).toBe("+25 %");
    expect(trendText({ stage: "declining", trendPercent: -60 })).toBe("-60 %");
    expect(trendText({ stage: "active", trendPercent: 0 })).toBe("0 %");
  });

  it("formats percentages, shares and changes", () => {
    expect(percentText(undefined)).toBe("—");
    expect(percentText(83)).toBe("83 %");
    expect(shareText(5, 0)).toBe("—");
    expect(shareText(1, 400)).toBe("<1 %");
    expect(shareText(0, 400)).toBe("0 %");
    expect(shareText(100, 400)).toBe("25 %");
    expect(changeText(undefined, 30)).toBe("none in the 30 days before");
    expect(changeText(12, 30)).toBe("+12 % vs the 30 days before");
    expect(changeText(-5, 1)).toBe("-5 % vs the day before");
  });

  it("counts UTC days back from now", () => {
    expect(agoText(null, NOW)).toBe("—");
    expect(agoText(Date.UTC(2026, 10, 6, 0, 5), NOW)).toBe("today");
    expect(agoText(Date.UTC(2026, 10, 5, 23, 59), NOW)).toBe("yesterday");
    expect(agoText(Date.UTC(2026, 10, 1), NOW)).toBe("5 days ago");
  });

  it("lists the window's days oldest first", () => {
    expect(windowDayKeys("2026-10-30", 7)).toEqual([
      "2026-10-30", "2026-10-31", "2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04", "2026-11-05",
    ]);
    expect(windowDayKeys("", 7)).toEqual([]);
  });

  it("stacks the chart as succeeded, failed and the rest, never below zero", () => {
    expect(dailyChartRows([
      { day: "2026-11-05", enrollments: 10, succeeded: 7, failed: 2, activeTenants: 3 },
      { day: "2026-11-04", enrollments: 1, succeeded: 1, failed: 1, activeTenants: 1 },
    ])).toEqual([
      { day: "2026-11-05", succeeded: 7, failed: 2, other: 1, enrollments: 10, activeTenants: 3 },
      { day: "2026-11-04", succeeded: 1, failed: 1, other: 0, enrollments: 1, activeTenants: 1 },
    ]);
  });

  it("reads a wire edition as a plan facet value, an unknown one as Community", () => {
    expect(planEditionOf("pro-msp")).toBe("pro-msp");
    expect(planEditionOf("pro-trial")).toBe("pro-trial");
    expect(planEditionOf("enterprise")).toBe("community");
  });
});

describe("lifecycle", () => {
  const tenants = [
    tenant("n-old", { stage: "neverEnrolled", onboardedAt: "2026-09-01T10:00:00Z" }),
    tenant("d-small", { stage: "dormant", lifetimeEnrollments: 4, lastActiveDay: "2026-09-20" }),
    tenant("w-older", { stage: "new", firstEnrollmentDay: "2026-10-20", enrollments: 3 }),
    tenant("c-small", { stage: "declining", enrollmentsPrevious: 10, enrollments: 5 }),
    tenant("n-new", { stage: "neverEnrolled", onboardedAt: "2026-11-04T09:00:00.1234567Z" }),
    tenant("w-today", { stage: "new" }),
    tenant("d-big", { stage: "dormant", lifetimeEnrollments: 400 }),
    tenant("c-big", { stage: "declining", enrollmentsPrevious: 80, enrollments: 10 }),
    tenant("w-newer", { stage: "new", firstEnrollmentDay: "2026-11-02", enrollments: 1 }),
    tenant("n-unknown", { stage: "neverEnrolled" }),
    tenant("a", { stage: "active", enrollments: 9 }),
  ];

  it("groups the four stages in their order, each sorted by what matters for it", () => {
    const groups = lifecycleGroups(tenants);
    expect(groups.map((g) => g.stage)).toEqual([...LIFECYCLE_STAGES]);
    expect(groups.map((g) => ids(g.rows))).toEqual([
      ["w-today", "w-newer", "w-older"],
      ["c-big", "c-small"],
      ["d-big", "d-small"],
      ["n-new", "n-old", "n-unknown"],
    ]);
  });

  it("keeps the route's order on a tie", () => {
    const tied = [tenant("x", { stage: "dormant", lifetimeEnrollments: 7 }), tenant("y", { stage: "dormant", lifetimeEnrollments: 7 })];
    expect(ids(lifecycleGroups(tied)[2].rows)).toEqual(["x", "y"]);
  });

  it("shows the figures each stage is about", () => {
    expect(lifecycleFigures("new", tenants[2], NOW, true)).toBe(`first sessions ${formatDayKey("2026-10-20")} · 3 in this window`);
    expect(lifecycleFigures("new", tenants[5], NOW, true)).toBe("first sessions today · 0 in this window");
    expect(lifecycleFigures("declining", tenants[3], NOW, true)).toBe("10 → 5 · last activity —");
    expect(lifecycleFigures("dormant", tenants[1], NOW, true)).toBe(`4 since signup · last sessions ${formatDayKey("2026-09-20")}`);
    expect(lifecycleFigures("neverEnrolled", tenants[0], NOW, true)).toBe(`signed up ${formatDayKey("2026-09-01")} · no portal visit`);
    expect(lifecycleFigures("neverEnrolled", { ...tenants[0], lastPortalVisitAt: "2026-11-04T12:00:00Z" }, NOW, true))
      .toBe(`signed up ${formatDayKey("2026-09-01")} · portal 2 days ago`);
    expect(lifecycleFigures("neverEnrolled", tenants[0], NOW, false)).toBe(`signed up ${formatDayKey("2026-09-01")}`);
    expect(lifecycleFigures("neverEnrolled", tenants[9], NOW, false)).toBe("signup unknown");
  });
});
