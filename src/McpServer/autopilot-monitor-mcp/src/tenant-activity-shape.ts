/**
 * LLM-facing shaping of get_tenant_activity (GET /api/global/metrics/tenant-activity): how every signed-up customer
 * tenant used the product in the last N full UTC days, against the N days before.
 *
 * The backend computes every figure (stages, trend and success percentages, totals) once for the GA page Tenant
 * Activity and this tool. This layer only picks the fields a view shows, orders each lifecycle list, cuts every list to
 * a fixed row cap with its full length stated, and turns absent wire values into explicit nulls (the wire omits them,
 * which reads as "not applicable").
 *
 * Rows are slim on purpose: a full tenant row is about 500 characters (950 with a 90-day daily series), so the lists in
 * full would pass the adminStream response cap, and the route has no paging to fall back on. A ranking row carries the
 * figures of the ranking columns; a lifecycle row carries the tenant's id, domain and edition plus the figures of its
 * stage.
 */

import type { GetTenantActivityResponse, TenantActivityTenantItem } from './generated/wire-types.generated.js';
import type { TenantActivityStage } from './generated/wire-vocabularies.generated.js';

export const TENANT_ACTIVITY_VIEWS = ['summary', 'ranking', 'lifecycle'] as const;
export type TenantActivityView = (typeof TENANT_ACTIVITY_VIEWS)[number];

/** The row cap of every list a view shows; each list states its full length next to the rows it keeps. */
export const TENANT_ACTIVITY_CAPS = {
  /** summary: the most active tenants, the head of view=ranking. */
  mostActive: 15,
  /** summary: the head of each lifecycle list. */
  summaryStage: 10,
  /** view=ranking: the tenants with enrollments in the window. */
  ranking: 300,
  /** view=lifecycle without stage: each lifecycle list. */
  lifecycleStage: 100,
  /** view=lifecycle with stage: that one stage. */
  oneStage: 500,
} as const;

/** The lifecycle lists of the summary and of view=lifecycle without stage; active and quiet are listed by stage only. */
export const LIFECYCLE_LISTS = ['new', 'declining', 'dormant', 'neverEnrolled'] as const satisfies readonly TenantActivityStage[];

type Tenant = TenantActivityTenantItem;

/** A list cut to its cap: the full length, whether rows were left out, and the rows kept. */
interface CutList<R> {
  total: number;
  truncated: boolean;
  rows: R[];
}

function cut<R>(tenants: readonly Tenant[], cap: number, row: (t: Tenant) => R): CutList<R> {
  return { total: tenants.length, truncated: tenants.length > cap, rows: tenants.slice(0, cap).map(row) };
}

/** The UTC day of a wire timestamp: the backend writes UTC ISO 8601, so the first ten characters are the day. */
const utcDay = (iso: string | undefined): string | null => (iso === undefined ? null : iso.slice(0, 10));

/** A wire day or timestamp in milliseconds; `missing` when absent or unreadable. */
function instant(iso: string | undefined, missing: number): number {
  const ms = iso === undefined ? Number.NaN : Date.parse(iso);
  return Number.isNaN(ms) ? missing : ms;
}

/** Descending by key; equal keys keep the backend order (Array.prototype.sort is stable). */
const descending = (key: (t: Tenant) => number) => (a: Tenant, b: Tenant): number => {
  const ka = key(a);
  const kb = key(b);
  return ka < kb ? 1 : ka > kb ? -1 : 0;
};

/** One tenant of the ranking: the figures of the ranking columns. */
function rankingRow(t: Tenant) {
  return {
    tenantId: t.tenantId,
    domain: t.domainName,
    edition: t.edition,
    stage: t.stage,
    enrollments: t.enrollments,
    previous: t.enrollmentsPrevious,
    trendPercent: t.trendPercent ?? null,
    successPercent: t.successPercent ?? null,
    activeDays: t.activeDays,
    lastActiveDay: t.lastActiveDay ?? null,
    portalUsers: t.portalUsers ?? null,
  };
}

interface StageList {
  /** The figures of the stage; the row starts with the tenant's id, domain and edition. */
  figures: (t: Tenant) => Record<string, string | number | null>;
  /** The list order; without one, and between equal keys, the backend order stays. */
  order?: (a: Tenant, b: Tenant) => number;
}

const recentActivity = (t: Tenant) => ({ enrollments: t.enrollments, lastActiveDay: t.lastActiveDay ?? null });

/** Figures and order per stage, keyed by the generated vocabulary: a stage the backend adds fails tsc here. */
const STAGE_LISTS: Record<TenantActivityStage, StageList> = {
  new: {
    figures: (t) => ({ onboarded: utcDay(t.onboardedAt), firstEnrollmentDay: t.firstEnrollmentDay ?? null, enrollments: t.enrollments }),
    // Newest first. Without a first day the first sessions came after the last daily row: the newest of all.
    order: descending((t) => instant(t.firstEnrollmentDay, Number.POSITIVE_INFINITY)),
  },
  declining: {
    figures: (t) => ({
      previous: t.enrollmentsPrevious,
      enrollments: t.enrollments,
      trendPercent: t.trendPercent ?? null,
      lastActiveDay: t.lastActiveDay ?? null,
    }),
    // The largest drop against the previous window first.
    order: descending((t) => t.enrollmentsPrevious - t.enrollments),
  },
  dormant: {
    figures: (t) => ({ lifetimeEnrollments: t.lifetimeEnrollments, lastActiveDay: t.lastActiveDay ?? null }),
    // The most enrollments since signup first.
    order: descending((t) => t.lifetimeEnrollments),
  },
  neverEnrolled: {
    figures: (t) => ({ onboarded: utcDay(t.onboardedAt), lastPortalVisit: utcDay(t.lastPortalVisitAt) }),
    // The newest signup first; a row without a signup time last.
    order: descending((t) => instant(t.onboardedAt, Number.NEGATIVE_INFINITY)),
  },
  active: { figures: recentActivity },
  quiet: { figures: recentActivity },
};

function stageList(tenants: readonly Tenant[], stage: TenantActivityStage, cap: number) {
  const { figures, order } = STAGE_LISTS[stage];
  const members = tenants.filter((t) => t.stage === stage);
  if (order) members.sort(order);
  return cut(members, cap, (t) => ({ tenantId: t.tenantId, domain: t.domainName, edition: t.edition, ...figures(t) }));
}

function lifecycleLists(tenants: readonly Tenant[], cap: number) {
  return Object.fromEntries(LIFECYCLE_LISTS.map((stage) => [stage, stageList(tenants, stage, cap)]));
}

const SUMMARY_NOTE =
  `mostActive is the head of view=ranking; each stage list shows its first ${TENANT_ACTIVITY_CAPS.summaryStage} rows. ` +
  `view=lifecycle lists them up to ${TENANT_ACTIVITY_CAPS.lifecycleStage} rows each, and with stage= one stage up to ` +
  `${TENANT_ACTIVITY_CAPS.oneStage} rows, active and quiet included.`;

const LIFECYCLE_NOTE = `stage= lists one stage up to ${TENANT_ACTIVITY_CAPS.oneStage} rows, active and quiet included.`;

/**
 * One view of the tenant activity. `stage` applies to view=lifecycle only (the tool refuses it with another view):
 * that one stage's list instead of the four lifecycle lists.
 */
export function shapeTenantActivity(
  data: GetTenantActivityResponse,
  view: TenantActivityView,
  stage?: TenantActivityStage,
): Record<string, unknown> {
  const head = {
    view,
    window: {
      days: data.windowDays,
      start: data.windowStart,
      through: data.usageDaysThrough,
      previousStart: data.previousWindowStart,
      rowsFrom: data.rowsFrom,
    },
    portalActivityAvailable: data.portalActivityAvailable,
  };
  // In the backend order: the most enrollments in the window first, then the most since signup.
  const enrolled = data.tenants.filter((t) => t.enrollments > 0);
  const totals = data.totals;

  switch (view) {
    case 'summary':
      return {
        ...head,
        totals: {
          ...totals,
          activeTenantsTrendPercent: totals.activeTenantsTrendPercent ?? null,
          enrollmentsTrendPercent: totals.enrollmentsTrendPercent ?? null,
          successPercent: totals.successPercent ?? null,
          top10SharePercent: totals.top10SharePercent ?? null,
          portalUsers: totals.portalUsers ?? null,
          portalTenants: totals.portalTenants ?? null,
        },
        daily: data.daily,
        mostActive: cut(enrolled, TENANT_ACTIVITY_CAPS.mostActive, rankingRow),
        stages: lifecycleLists(data.tenants, TENANT_ACTIVITY_CAPS.summaryStage),
        note: SUMMARY_NOTE,
      };
    case 'ranking':
      return { ...head, tenants: cut(enrolled, TENANT_ACTIVITY_CAPS.ranking, rankingRow) };
    case 'lifecycle':
      return stage
        ? { ...head, stages: { [stage]: stageList(data.tenants, stage, TENANT_ACTIVITY_CAPS.oneStage) } }
        : { ...head, stages: lifecycleLists(data.tenants, TENANT_ACTIVITY_CAPS.lifecycleStage), note: LIFECYCLE_NOTE };
  }
}
