/**
 * Per-tenant health summary for one managed tenant — the headline subset of the server-aggregated
 * DashboardStats returned by `/global/stats/sessions?tenantId=`. Pure data; no React.
 */
export interface FleetSummary {
  activeCount: number;
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  successRatePct: number;
}

/** Fleet-wide roll-up across the managed tenants that have reported a summary. */
export interface FleetRollup {
  /** Number of tenants contributing to the roll-up (those with a loaded summary). */
  tenantCount: number;
  activeCount: number;
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  /**
   * Weighted fleet success rate, 1 decimal. TERMINAL-ONLY: sum(succeeded) / (sum(succeeded) + sum(failed)) —
   * matches the per-tenant card / backend DashboardStats semantic (succeeded over terminal sessions), so the
   * roll-up and the cards never disagree. Active/pending sessions are NOT in the denominator. 0 when the
   * fleet has no terminal sessions.
   */
  successRatePct: number;
}

/**
 * Aggregates per-tenant summaries into a fleet roll-up. The success rate is WEIGHTED by terminal-session
 * volume (sum succeeded / sum terminal) — not an average of per-tenant rates — so a tenant with 1000
 * terminal sessions counts more than one with 3. Terminal-only matches the backend's per-tenant rate, so a
 * tenant card and the roll-up tile use the same definition. Pure + testable.
 */
export function computeFleetRollup(summaries: FleetSummary[]): FleetRollup {
  const acc = summaries.reduce(
    (a, s) => {
      a.activeCount += s.activeCount;
      a.totalLastNDays += s.totalLastNDays;
      a.succeededLastNDays += s.succeededLastNDays;
      a.failedLastNDays += s.failedLastNDays;
      return a;
    },
    { activeCount: 0, totalLastNDays: 0, succeededLastNDays: 0, failedLastNDays: 0 }
  );

  const terminal = acc.succeededLastNDays + acc.failedLastNDays;
  const successRatePct =
    terminal > 0 ? Math.round((acc.succeededLastNDays / terminal) * 1000) / 10 : 0;

  return { tenantCount: summaries.length, ...acc, successRatePct };
}
