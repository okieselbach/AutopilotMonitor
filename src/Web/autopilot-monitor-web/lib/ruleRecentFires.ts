/**
 * Fleet-context window math for the per-rule "Fired in N enrollments" sentence.
 *
 * The rule-stats response already carries a per-day trend for the last 30 days,
 * so the 14-day count is derived client-side — no extra fetch. Analyze-rule
 * fire counts are session-deduplicated server-side (one increment per rule and
 * session), which makes the sum read as "enrollments"; RuleStats has no device
 * identity, so never present this number as devices.
 */

export interface RuleTrendPoint {
  date: string;
  fireCount: number;
}

/**
 * Sum of daily fire counts on/after `sinceIsoDate` (YYYY-MM-DD). Trend dates
 * are ISO date strings, so lexical comparison is chronological.
 */
export function sumRecentFires(
  trend: RuleTrendPoint[] | null | undefined,
  sinceIsoDate: string,
): number {
  if (!trend) return 0;
  let total = 0;
  for (const point of trend) {
    if (point.date >= sinceIsoDate && point.fireCount > 0) {
      total += point.fireCount;
    }
  }
  return total;
}

/**
 * UTC start date (inclusive) of a trailing window ending today, as YYYY-MM-DD.
 * windowDays=14 means "today plus the 13 days before it" — same inclusive
 * convention as the backend's InclusiveWindowStart.
 */
export function recentWindowStartIso(nowUtc: Date, windowDays: number): string {
  const start = new Date(Date.UTC(
    nowUtc.getUTCFullYear(),
    nowUtc.getUTCMonth(),
    nowUtc.getUTCDate() - (windowDays - 1),
  ));
  return start.toISOString().slice(0, 10);
}
