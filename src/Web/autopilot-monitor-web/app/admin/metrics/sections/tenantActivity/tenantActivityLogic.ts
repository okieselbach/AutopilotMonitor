/**
 * Pure folds behind the GA Tenant Activity page (/admin/metrics/tenant-activity). The route
 * (GET global/metrics/tenant-activity) computes every figure once for the page and the MCP tool
 * get_tenant_activity — stages, trends, success rates, totals; this module only filters, sorts, groups and
 * formats them.
 */
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import type { TenantActivityDailyItem, TenantActivityTenantItem } from "@/lib/generated/wire-types.generated";
import { parseUtcMs } from "@/lib/osUpdateLive";
import { formatDayKey } from "../../lib/format";
import type { SortDir } from "../../lib/useListState";

// ── View (?view=) ─────────────────────────────────────────────────────────────

export type TenantActivityView = "ranking" | "lifecycle";

export const TENANT_ACTIVITY_VIEW_PARAM = "view";

/** The tab a link asks for; anything unknown is the ranking. */
export function parseView(raw: string | null | undefined): TenantActivityView {
  return raw === "lifecycle" ? "lifecycle" : "ranking";
}

/** The daily rows keep 180 days and the trend compares with the window before: a longer ?days= shows 90. */
export const TENANT_ACTIVITY_MAX_DAYS = 90;

export type TenantActivityStage = (typeof SHARED_MANIFEST.tenantActivityStages)[number];

// ── Ranking ───────────────────────────────────────────────────────────────────

export type RankingSortKey = "enrollments" | "trend" | "success" | "activeDays" | "portalUsers" | "lastActive";

/** The tenants with enrollments in the window, in the route's order (most first). */
export function rankingRows(tenants: readonly TenantActivityTenantItem[]): TenantActivityTenantItem[] {
  return tenants.filter((t) => t.enrollments > 0);
}

/** UTC midnight of a "yyyy-MM-dd" day key, null without one. */
function dayKeyMs(key: string | undefined): number | null {
  return key ? parseUtcMs(`${key}T00:00:00Z`) : null;
}

/** When the tenant was last active: the counters' last move, else its last day with sessions. */
export function lastActiveMs(t: TenantActivityTenantItem): number | null {
  return parseUtcMs(t.lastCounterUpdateAt) ?? dayKeyMs(t.lastActiveDay);
}

function sortValue(t: TenantActivityTenantItem, key: RankingSortKey): number | null {
  switch (key) {
    case "enrollments":
      return t.enrollments;
    case "trend":
      return t.trendPercent ?? null;
    case "success":
      return t.successPercent ?? null;
    case "activeDays":
      return t.activeDays;
    case "portalUsers":
      return t.portalUsers ?? null;
    case "lastActive":
      return lastActiveMs(t);
  }
}

/** A sorted copy: a missing value sorts last in either direction, and ties keep the route's order. */
export function sortRanking(
  rows: readonly TenantActivityTenantItem[],
  key: RankingSortKey,
  dir: SortDir,
): TenantActivityTenantItem[] {
  const sign = dir === "asc" ? 1 : -1;
  return rows
    .map((row, index) => ({ row, index, value: sortValue(row, key) }))
    .sort((a, b) => {
      if (a.value === null || b.value === null) {
        if (a.value === b.value) return a.index - b.index;
        return a.value === null ? 1 : -1;
      }
      return a.value === b.value ? a.index - b.index : (a.value - b.value) * sign;
    })
    .map(({ row }) => row);
}

/** Case-insensitive match on the domain, the company name or the tenant id; an empty query keeps every row. */
export function searchTenants(rows: readonly TenantActivityTenantItem[], query: string): readonly TenantActivityTenantItem[] {
  const q = query.trim().toLowerCase();
  if (!q) return rows;
  return rows.filter(
    (t) =>
      t.domainName.toLowerCase().includes(q) ||
      (t.companyName ?? "").toLowerCase().includes(q) ||
      t.tenantId.toLowerCase().includes(q),
  );
}

// ── Display ───────────────────────────────────────────────────────────────────

/** The trend against the window before: "new" for a new tenant, "—" without a window before, else signed. */
export function trendText(t: Pick<TenantActivityTenantItem, "stage" | "trendPercent">): string {
  if (t.stage === "new") return "new";
  if (t.trendPercent == null) return "—";
  return t.trendPercent > 0 ? `+${t.trendPercent} %` : `${t.trendPercent} %`;
}

/** A whole percentage the route computed, "—" without one. */
export function percentText(value: number | null | undefined): string {
  return value == null ? "—" : `${value} %`;
}

/** A tenant's share of the window's enrollments: whole percent, "<1 %" for a small one, "—" without a total. */
export function shareText(part: number, whole: number): string {
  if (whole <= 0) return "—";
  const percent = (100 * part) / whole;
  return part > 0 && percent < 1 ? "<1 %" : `${Math.round(percent)} %`;
}

/** A platform total against the window before: "+12 % vs the 30 days before", or why there is no comparison. */
export function changeText(percent: number | null | undefined, days: number): string {
  const before = days === 1 ? "the day before" : `the ${days} days before`;
  if (percent == null) return `none in ${before}`;
  return `${percent > 0 ? "+" : ""}${percent} % vs ${before}`;
}

const DAY_MS = 86_400_000;

/** "today", "yesterday" or "N days ago", counted in UTC days; "—" without a time. */
export function agoText(ms: number | null, nowMs: number): string {
  if (ms == null) return "—";
  const days = Math.floor(nowMs / DAY_MS) - Math.floor(ms / DAY_MS);
  if (days <= 0) return "today";
  return days === 1 ? "yesterday" : `${days} days ago`;
}

/** The window's UTC day keys, oldest first — the days behind a tenant's dailyEnrollments. */
export function windowDayKeys(windowStart: string, days: number): string[] {
  const startMs = dayKeyMs(windowStart);
  if (startMs == null) return [];
  return Array.from({ length: days }, (_, i) => new Date(startMs + i * DAY_MS).toISOString().slice(0, 10));
}

// ── Chart ─────────────────────────────────────────────────────────────────────

/** One bar per window day: succeeded, failed and the rest (still running at aggregation, or incomplete). */
export function dailyChartRows(daily: readonly TenantActivityDailyItem[]): Array<Record<string, unknown>> {
  return daily.map((d) => ({
    day: d.day,
    succeeded: d.succeeded,
    failed: d.failed,
    other: Math.max(0, d.enrollments - d.succeeded - d.failed),
    enrollments: d.enrollments,
    activeTenants: d.activeTenants,
  }));
}

// ── Lifecycle ─────────────────────────────────────────────────────────────────

/** The stages the lifecycle tab lists, in its order. */
export const LIFECYCLE_STAGES = ["new", "declining", "dormant", "neverEnrolled"] as const satisfies readonly TenantActivityStage[];

export type LifecycleStage = (typeof LIFECYCLE_STAGES)[number];

/** Larger first; equal values (two infinities included) are a tie. */
function descending(a: number, b: number): number {
  if (a === b) return 0;
  return a < b ? 1 : -1;
}

const LIFECYCLE_ORDER: Record<LifecycleStage, (a: TenantActivityTenantItem, b: TenantActivityTenantItem) => number> = {
  // Newest first; first sessions only the counters have seen yet carry no day and are the newest of all.
  new: (a, b) => descending(dayKeyMs(a.firstEnrollmentDay) ?? Infinity, dayKeyMs(b.firstEnrollmentDay) ?? Infinity),
  // The largest drop first.
  declining: (a, b) => descending(a.enrollmentsPrevious - a.enrollments, b.enrollmentsPrevious - b.enrollments),
  // The biggest customers gone quiet first.
  dormant: (a, b) => descending(a.lifetimeEnrollments, b.lifetimeEnrollments),
  // The latest signups first.
  neverEnrolled: (a, b) => descending(parseUtcMs(a.onboardedAt) ?? -Infinity, parseUtcMs(b.onboardedAt) ?? -Infinity),
};

/** The one line a lifecycle row shows after the tenant: the two or three figures its stage is about. */
export function lifecycleFigures(
  stage: LifecycleStage,
  t: TenantActivityTenantItem,
  nowMs: number,
  portalAvailable: boolean,
): string {
  switch (stage) {
    case "new":
      return `first sessions ${t.firstEnrollmentDay ? formatDayKey(t.firstEnrollmentDay) : "today"} · ${t.enrollments.toLocaleString()} in this window`;
    case "declining":
      return `${t.enrollmentsPrevious.toLocaleString()} → ${t.enrollments.toLocaleString()} · last activity ${agoText(lastActiveMs(t), nowMs)}`;
    case "dormant":
      return `${t.lifetimeEnrollments.toLocaleString()} since signup · last sessions ${t.lastActiveDay ? formatDayKey(t.lastActiveDay) : "unknown"}`;
    case "neverEnrolled": {
      const signup = t.onboardedAt ? `signed up ${formatDayKey(t.onboardedAt.slice(0, 10))}` : "signup unknown";
      if (!portalAvailable) return signup;
      const visit = parseUtcMs(t.lastPortalVisitAt);
      return `${signup} · ${visit == null ? "no portal visit" : `portal ${agoText(visit, nowMs)}`}`;
    }
  }
}

/** Every lifecycle stage with its tenants in the stage's order; ties keep the route's order. */
export function lifecycleGroups(
  tenants: readonly TenantActivityTenantItem[],
): Array<{ stage: LifecycleStage; rows: TenantActivityTenantItem[] }> {
  return LIFECYCLE_STAGES.map((stage) => ({
    stage,
    rows: tenants
      .map((row, index) => ({ row, index }))
      .filter(({ row }) => row.stage === stage)
      .sort((a, b) => LIFECYCLE_ORDER[stage](a.row, b.row) || a.index - b.index)
      .map(({ row }) => row),
  }));
}
