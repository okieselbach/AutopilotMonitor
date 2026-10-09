import { COMMUNITY_WINDOW_CAP_DAYS } from "@/lib/timeWindow";

/** One server timeline point (UTC calendar day). */
export interface TimelinePoint {
  date: string;
  success: number;
  failed: number;
}

/** One bar of the enrollments timeline: a day, or a Monday-anchored UTC week beyond 90 days. */
export interface TimelineBar {
  /** First day the bar covers (yyyy-MM-dd) — the React key. */
  date: string;
  label: string;
  success: number;
  failed: number;
}

function weekStartUtc(isoDate: string): string {
  const d = new Date(isoDate + "T00:00:00Z");
  d.setUTCDate(d.getUTCDate() - ((d.getUTCDay() + 6) % 7));
  return d.toISOString().slice(0, 10);
}

/**
 * Bars for the enrollments timeline. Up to 90 days one bar per day, labelled as before; beyond,
 * one bar per week so a year still fits the card, labelled with the week's first day — with the
 * year once the window spans two calendar years.
 */
export function timelineBars(points: readonly TimelinePoint[], days: number): TimelineBar[] {
  if (days <= COMMUNITY_WINDOW_CAP_DAYS) {
    return points.map((p) => {
      const d = new Date(p.date + "T00:00:00");
      return {
        date: p.date,
        label:
          days <= 7
            ? d.toLocaleDateString(undefined, { weekday: "short" })
            : d.toLocaleDateString(undefined, { month: "short", day: "numeric" }),
        success: p.success,
        failed: p.failed,
      };
    });
  }

  const weeks = new Map<string, { success: number; failed: number }>();
  for (const p of points) {
    const week = weekStartUtc(p.date);
    const bucket = weeks.get(week) ?? { success: 0, failed: 0 };
    bucket.success += p.success;
    bucket.failed += p.failed;
    weeks.set(week, bucket);
  }

  const starts = [...weeks.keys()].sort();
  const spansYears = starts.length > 0 && starts[0].slice(0, 4) !== points[points.length - 1].date.slice(0, 4);
  return starts.map((start) => ({
    date: start,
    label: new Date(start + "T00:00:00").toLocaleDateString(
      undefined,
      spansYears ? { month: "short", day: "numeric", year: "numeric" } : { month: "short", day: "numeric" },
    ),
    success: weeks.get(start)!.success,
    failed: weeks.get(start)!.failed,
  }));
}
