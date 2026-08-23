/**
 * Pure helpers for the Verdict Calibration matrix (docs/backend/verdict-calibration.md).
 * Wire shape mirrors GetVerdictCalibrationFunction / VerdictCalibrationResponse (camelCase).
 */

export interface TrendWindow {
  count: number;
  sessions: number;
  sharePct: number;
}

export interface VerdictCalibrationPath {
  verdictPath: string;
  status: string;
  count: number;
  sharePct: number;
  derivedCount: number;
  eligible7d: number;
  reEnrolled7d: number;
  /** null below 20 eligible sessions — never a rate on a handful */
  reEnrollRatePct: number | null;
  overriddenByAdmin: number;
  overriddenByLateCompletion: number;
  overriddenOther: number;
  window7: TrendWindow;
  baseline28: TrendWindow;
  /** window share ÷ baseline share; null without a baseline */
  lift: number | null;
}

export interface VerdictCalibrationDimension {
  dimension: string;
  value: string;
  hitCount: number;
  hitSharePct: number;
  allSharePct: number;
  lift: number;
}

/** One ACTIVE drift episode (mirrors VerdictCalibrationAlert). */
export interface VerdictCalibrationAlert {
  tenantId: string;
  kind: "share_regression" | "silence_share_regression" | "evidence_gap" | string;
  verdictPath: string;
  status: string;
  windowHitCount: number;
  windowSessionCount: number;
  baselineHitCount: number;
  baselineSessionCount: number;
  windowRatePct: number;
  baselineRatePct: number;
  lift: number | null;
  windowStartDate: string;
  windowEndDate: string;
  dimension: VerdictCalibrationDimension | null;
  firstNotifiedAt: string;
  lastEvaluatedAt: string;
}

export const ALERT_KIND_LABELS: Record<string, string> = {
  share_regression: "Share regression",
  silence_share_regression: "Silence share regression (agent liveness)",
  evidence_gap: "Evidence gap (r6 fallthrough)",
};

/** One-line alert summary; the dimension clause keeps the rule-radar wording contract. */
export function describeAlert(a: VerdictCalibrationAlert): string {
  const base =
    `${a.windowHitCount} of ${a.windowSessionCount} (${a.windowRatePct.toFixed(1)}%) over 7d vs ` +
    `${a.baselineRatePct.toFixed(1)}% baseline (${a.baselineHitCount} of ${a.baselineSessionCount} over 28d)` +
    (a.lift != null ? ` — lift ${a.lift.toFixed(1)}×` : "");
  if (!a.dimension) return base;
  return `${base}. ${a.dimension.value} carries ${a.dimension.hitSharePct.toFixed(0)}% of hits vs ${a.dimension.allSharePct.toFixed(0)}% of all sessions — correlated, not necessarily causal.`;
}

/** Index active per-path alerts by "path|status" for the matrix pill. */
export function alertsByPath(alerts: VerdictCalibrationAlert[]): Map<string, VerdictCalibrationAlert> {
  const map = new Map<string, VerdictCalibrationAlert>();
  for (const a of alerts) {
    if (a.kind === "share_regression") map.set(`${a.verdictPath}|${a.status}`, a);
  }
  return map;
}

export interface VerdictCalibrationResponse {
  success: boolean;
  tenantId: string;
  windowDays: number;
  windowStart: string;
  windowEnd: string;
  computedAt: string | null;
  versions: number[];
  totals: { sessions: number; terminal: number; derived: number; days: number };
  trend: { windowDays: number; baselineDays: number; windowSessions: number; baselineSessions: number };
  paths: VerdictCalibrationPath[];
  alerts: VerdictCalibrationAlert[];
}

/** The `origin` half of a verdict path (`sweep` for `sweep:r6`); the whole string without a colon. */
export function pathOrigin(path: string): string {
  const i = path.indexOf(":");
  return i < 0 ? path : path.slice(0, i);
}

/** Display order + labels for the origin groups; unknown origins sort last, alphabetically. */
export const VERDICT_PATH_ORIGIN_LABELS: Record<string, string> = {
  agent: "Agent-declared",
  ingest: "Ingest mapping",
  sweep: "Maintenance sweep (silence classifier)",
  maxlife: "Agent max-lifetime shutdown (silence classifier)",
  late: "Late telemetry reconcile (silence classifier)",
  retro: "Retro reclassification",
  register: "Session registration",
  rule: "Analyze rule (MarkSessionAsFailed)",
  manual: "Administrator",
  legacy: "Pre-instrumentation (derived, origin unknown)",
};

const ORIGIN_ORDER = Object.keys(VERDICT_PATH_ORIGIN_LABELS);

export interface OriginGroup {
  origin: string;
  rows: VerdictCalibrationPath[];
}

/** Groups rows by origin in display order; rows inside a group keep the server order (count desc). */
export function groupPathsByOrigin(paths: VerdictCalibrationPath[]): OriginGroup[] {
  const byOrigin = new Map<string, VerdictCalibrationPath[]>();
  for (const p of paths) {
    const origin = pathOrigin(p.verdictPath);
    const list = byOrigin.get(origin);
    if (list) list.push(p);
    else byOrigin.set(origin, [p]);
  }
  return Array.from(byOrigin.entries())
    .sort(([a], [b]) => {
      const ia = ORIGIN_ORDER.indexOf(a);
      const ib = ORIGIN_ORDER.indexOf(b);
      if (ia !== -1 || ib !== -1) return (ia === -1 ? ORIGIN_ORDER.length : ia) - (ib === -1 ? ORIGIN_ORDER.length : ib);
      return a.localeCompare(b);
    })
    .map(([origin, rows]) => ({ origin, rows }));
}

/**
 * 7d-vs-28d trend cell. Lift ≥ 2 (share at least doubled) and ≤ 0.5 (halved) get the arrow +
 * color; anything in between is a neutral "≈". No baseline → "new" (a new signal has no finite
 * lift — never invented). Below 5 window hits the arrow is withheld: one session can double a
 * tiny share.
 */
export function trendGlyph(row: VerdictCalibrationPath): { text: string; className: string } {
  if (row.lift == null) {
    return row.window7.count > 0 && row.baseline28.sessions > 0
      ? { text: "new", className: "text-gray-500" }
      : { text: "—", className: "text-gray-400" };
  }
  const lift = row.lift;
  const label = `${lift.toFixed(1)}×`;
  if (row.window7.count < 5) return { text: `≈ ${label}`, className: "text-gray-400" };
  if (lift >= 2) return { text: `↑ ${label}`, className: "text-red-600 font-medium" };
  if (lift <= 0.5) return { text: `↓ ${label}`, className: "text-green-700" };
  return { text: `≈ ${label}`, className: "text-gray-500" };
}
