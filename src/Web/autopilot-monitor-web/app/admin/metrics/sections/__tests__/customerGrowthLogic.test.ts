import { describe, expect, it } from "vitest";
import { customerGrowth } from "../customerGrowthLogic";

interface Row {
  tenantId: string;
  domainName: string;
  onboardedAt: string | null;
}

let seq = 0;
function row(onboardedAt: string | null, domainName = "contoso.example"): Row {
  seq++;
  return { tenantId: `tenant-${seq}`, domainName, onboardedAt };
}

/** Tuesday 2026-10-20, 10:00 UTC. */
const NOW = Date.parse("2026-10-20T10:00:00Z");

describe("customer growth window", () => {
  it("covers today and the days before it, never the day before the window starts", () => {
    const growth = customerGrowth(
      [
        row("2026-10-13T23:59:59.9999999Z"),
        row("2026-10-14T00:00:00Z"),
        row("2026-10-20T09:00:00Z"),
        row("2026-10-20T10:00:05Z"), // seconds after the client's clock, same UTC day
      ],
      7,
      NOW,
    );
    expect(growth.startKey).toBe("2026-10-14");
    expect(growth.endKey).toBe("2026-10-20");
    expect(growth.windowCount).toBe(3);
    expect(growth.buckets.map((b) => b.key)).toEqual([
      "2026-10-14", "2026-10-15", "2026-10-16", "2026-10-17", "2026-10-18", "2026-10-19", "2026-10-20",
    ]);
    expect(growth.buckets[0].count).toBe(1);
    expect(growth.buckets[6].count).toBe(2);
  });

  it("puts a seven-digit fraction just before midnight on its own UTC day", () => {
    const growth = customerGrowth([row("2026-10-15T23:59:59.9999999Z")], 7, NOW);
    expect(growth.buckets.find((b) => b.key === "2026-10-15")?.count).toBe(1);
    expect(growth.buckets.find((b) => b.key === "2026-10-16")?.count).toBe(0);
  });

  it("draws one bar per day up to 90 days, empty days included", () => {
    for (const days of [1, 7, 30, 90]) {
      const growth = customerGrowth([], days, NOW);
      expect(growth.weekly, `days=${days}`).toBe(false);
      expect(growth.buckets, `days=${days}`).toHaveLength(days);
      expect(growth.buckets.every((b) => b.count === 0 && b.startKey === b.key && b.endKey === b.key)).toBe(true);
    }
    expect(customerGrowth([], 1, NOW).buckets[0].key).toBe("2026-10-20");
  });

  it("switches to Monday-anchored weeks beyond 90 days, cut at both window edges", () => {
    const growth = customerGrowth([row("2026-07-22T08:00:00Z"), row("2026-10-20T08:00:00Z")], 91, NOW);
    expect(growth.weekly).toBe(true);
    expect(growth.startKey).toBe("2026-07-22"); // a Wednesday
    for (const b of growth.buckets) expect(new Date(b.key + "T00:00:00Z").getUTCDay()).toBe(1);

    const first = growth.buckets[0];
    expect(first).toMatchObject({ key: "2026-07-20", startKey: "2026-07-22", endKey: "2026-07-26", count: 1 });
    const last = growth.buckets[growth.buckets.length - 1];
    expect(last).toMatchObject({ key: "2026-10-19", startKey: "2026-10-19", endKey: "2026-10-20", count: 1 });
    expect(growth.buckets).toHaveLength(14);
  });

  it("keeps a week across the year boundary in one bucket", () => {
    const sunday = Date.parse("2027-01-03T12:00:00Z");
    const growth = customerGrowth([row("2026-12-31T10:00:00Z"), row("2027-01-01T10:00:00Z")], 365, sunday);
    const week = growth.buckets.find((b) => b.key === "2026-12-28");
    expect(week).toMatchObject({ startKey: "2026-12-28", endKey: "2027-01-03", count: 2 });
  });

  it("accumulates from zero to the window count", () => {
    const growth = customerGrowth(
      [row("2026-09-25T08:00:00Z"), row("2026-10-01T08:00:00Z"), row("2026-10-01T09:00:00Z"), row("2026-10-19T08:00:00Z")],
      30,
      NOW,
    );
    const cumulative = growth.buckets.map((b) => b.cumulative);
    for (let i = 1; i < cumulative.length; i++) expect(cumulative[i]).toBeGreaterThanOrEqual(cumulative[i - 1]);
    expect(cumulative[0]).toBe(0);
    expect(cumulative[cumulative.length - 1]).toBe(growth.windowCount);
    expect(growth.windowCount).toBe(4);
  });
});

describe("customer growth figures", () => {
  it("names the busiest day of the window, the most recent one on a tie", () => {
    const growth = customerGrowth(
      [
        row("2026-10-15T08:00:00Z"), row("2026-10-15T09:00:00Z"),
        row("2026-10-18T08:00:00Z"), row("2026-10-18T09:00:00Z"),
        row("2026-10-19T08:00:00Z"),
        row("2026-09-01T08:00:00Z"), row("2026-09-01T09:00:00Z"), row("2026-09-01T10:00:00Z"), // outside 7 days
      ],
      7,
      NOW,
    );
    expect(growth.bestDay).toEqual({ key: "2026-10-18", count: 2 });
    expect(customerGrowth([], 7, NOW).bestDay).toBeNull();
  });

  it("takes the best day from days, not weeks, in the weekly view", () => {
    const growth = customerGrowth(
      [
        row("2026-10-05T08:00:00Z"), row("2026-10-06T08:00:00Z"), row("2026-10-07T08:00:00Z"), // one week, 3 days
        row("2026-08-12T08:00:00Z"), row("2026-08-12T09:00:00Z"),
      ],
      365,
      NOW,
    );
    expect(growth.weekly).toBe(true);
    expect(growth.bestDay).toEqual({ key: "2026-08-12", count: 2 });
  });

  it("averages per week over the window length", () => {
    const daysBack = (n: number) => new Date(NOW - n * 86_400_000).toISOString();
    const signups = (count: number) => Array.from({ length: count }, (_, i) => row(daysBack(i % 7)));
    expect(customerGrowth(signups(14), 7, NOW).weeklyAverage).toBe(14);
    expect(customerGrowth(signups(30), 30, NOW).weeklyAverage).toBeCloseTo(7, 10);
    expect(customerGrowth(signups(0), 365, NOW).weeklyAverage).toBe(0);
  });

  it("counts the last 7 days whatever the window", () => {
    const rows = [row("2026-10-14T08:00:00Z"), row("2026-10-20T08:00:00Z"), row("2026-10-13T08:00:00Z"), row("2026-08-01T08:00:00Z")];
    const week = customerGrowth(rows, 7, NOW);
    expect(week.last7Days).toBe(2);
    expect(week.last7Days).toBe(week.windowCount);
    expect(customerGrowth(rows, 90, NOW).last7Days).toBe(2);
    expect(customerGrowth(rows, 1, NOW).last7Days).toBe(2);
  });

  it("counts only rows with a domain, and an unreadable timestamp in the total only", () => {
    const growth = customerGrowth(
      [
        row("2026-10-19T08:00:00Z", ""), // a default row from a read of an unknown tenant id
        row(null),
        row(""),
        row("not-a-date"),
        row("2026-10-19T09:00:00Z"),
      ],
      7,
      NOW,
    );
    expect(growth.total).toBe(4);
    expect(growth.windowCount).toBe(1);
    expect(growth.last7Days).toBe(1);
  });

  it("lists every signup of a bucket, oldest first", () => {
    const late = row("2026-10-19T17:00:00Z");
    const early = row("2026-10-19T06:00:00Z");
    const growth = customerGrowth([late, early], 7, NOW);
    const monday = growth.buckets.find((b) => b.key === "2026-10-19");
    expect(monday?.signups.map((s) => s.row.tenantId)).toEqual([early.tenantId, late.tenantId]);
    expect(monday?.signups[0].atMs).toBe(Date.parse("2026-10-19T06:00:00Z"));
  });

  it("returns zeros for no tenants", () => {
    const growth = customerGrowth([], 30, NOW);
    expect(growth).toMatchObject({ windowCount: 0, bestDay: null, weeklyAverage: 0, last7Days: 0, total: 0 });
    expect(growth.buckets).toHaveLength(30);
  });
});
