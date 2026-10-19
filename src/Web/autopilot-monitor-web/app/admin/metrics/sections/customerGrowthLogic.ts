/**
 * Tenant signups for the Customers section: one bucket per UTC calendar day of the window, or per
 * Monday-anchored UTC week beyond 90 days (the Fleet Health timeline's rule), plus the window's
 * figures. Pure — the section passes the tenant rows and the clock of its last fetch.
 *
 * A signup is a configuration row WITH a domain: only a member's first login writes DomainName,
 * while reading an unknown tenant id creates a default row without one. The row's OnboardedAt is
 * its insert time, so the series counts tenants that still exist (offboarding deletes the row).
 */
import { parseUtcMs } from "@/lib/osUpdateLive";
import { COMMUNITY_WINDOW_CAP_DAYS, utcDateDaysAgo, utcWeekStart } from "@/lib/timeWindow";

/** The fields of a tenant row the signup series reads. */
export interface SignupRow {
  domainName?: string | null;
  onboardedAt?: string | null;
}

export interface Signup<T> {
  row: T;
  atMs: number;
}

export interface GrowthBucket<T> {
  /** First day of the bucket (yyyy-MM-dd): the day itself, or the Monday of its week. */
  key: string;
  /** First and last day the bucket covers inside the window — a week is cut at both window edges. */
  startKey: string;
  endKey: string;
  count: number;
  /** Signups from the window start through the end of this bucket. */
  cumulative: number;
  /** Oldest first. */
  signups: Signup<T>[];
}

export interface CustomerGrowth<T> {
  /** First and last day of the window (yyyy-MM-dd, UTC); the last day is today. */
  startKey: string;
  endKey: string;
  weekly: boolean;
  buckets: GrowthBucket<T>[];
  windowCount: number;
  /** The window's busiest day, the most recent one on a tie; null without a signup in the window. */
  bestDay: { key: string; count: number } | null;
  weeklyAverage: number;
  /** Signups in the last 7 days, whatever the window. */
  last7Days: number;
  /** Every signup, including those outside the window and those without a readable timestamp. */
  total: number;
}

const LAST_DAYS = 7;

function utcDayKey(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

/**
 * Buckets the signups of the last `days` UTC calendar days, today included. Membership is decided
 * on the UTC day, never on `atMs <= nowMs`, so a client clock a little behind the server still
 * counts a signup made moments ago.
 */
export function customerGrowth<T extends SignupRow>(rows: readonly T[], days: number, nowMs: number): CustomerGrowth<T> {
  const now = new Date(nowMs);
  const endKey = utcDayKey(nowMs);
  const startKey = utcDateDaysAgo(days - 1, now);
  const last7StartKey = utcDateDaysAgo(LAST_DAYS - 1, now);

  let total = 0;
  let last7Days = 0;
  const byDay = new Map<string, Signup<T>[]>();
  for (const row of rows) {
    if (!row.domainName) continue;
    total++;
    const atMs = parseUtcMs(row.onboardedAt);
    if (atMs == null) continue;
    const key = utcDayKey(atMs);
    if (key > endKey) continue;
    if (key >= last7StartKey) last7Days++;
    if (key < startKey) continue;
    const signups = byDay.get(key);
    if (signups) signups.push({ row, atMs });
    else byDay.set(key, [{ row, atMs }]);
  }

  const weekly = days > COMMUNITY_WINDOW_CAP_DAYS;
  const buckets: GrowthBucket<T>[] = [];
  let windowCount = 0;
  let bestDay: { key: string; count: number } | null = null;
  for (let ago = days - 1; ago >= 0; ago--) {
    const day = utcDateDaysAgo(ago, now);
    const signups = (byDay.get(day) ?? []).sort((a, b) => a.atMs - b.atMs);
    windowCount += signups.length;
    // Oldest to newest, so ">=" hands a tie to the most recent day.
    if (signups.length > 0 && signups.length >= (bestDay?.count ?? 0)) bestDay = { key: day, count: signups.length };

    const key = weekly ? utcWeekStart(day) : day;
    const last = buckets[buckets.length - 1];
    if (last && last.key === key) {
      last.endKey = day;
      last.count += signups.length;
      last.cumulative = windowCount;
      last.signups.push(...signups);
    } else {
      buckets.push({ key, startKey: day, endKey: day, count: signups.length, cumulative: windowCount, signups: [...signups] });
    }
  }

  return {
    startKey,
    endKey,
    weekly,
    buckets,
    windowCount,
    bestDay,
    weeklyAverage: (windowCount * 7) / days,
    last7Days,
    total,
  };
}
