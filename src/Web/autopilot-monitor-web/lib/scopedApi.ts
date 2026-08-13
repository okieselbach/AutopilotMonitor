import { api } from "./api";
import { asGuidOrUndefined } from "@/utils/inputValidation";

/**
 * Scope-aware URL builders for the tenant/global endpoint pairs in lib/api.ts.
 *
 * Every cross-tenant page used to hand-roll the same idiom at each call site:
 *   routeGlobal ? api.x.globalY(..., selectedTenantId || undefined) : api.x.y(tenantId, ...)
 * — with the tenantId parameter sitting at a DIFFERENT position in nearly every global
 * variant (the exact trap the 2026-08 audit flagged). These builders take the scope
 * selection ONCE and own the routing decision + tenant-parameter placement, so a page
 * can no longer pick the wrong variant, misplace the tenant argument, or bypass the
 * delegated-home carve-out that the scope hooks encode in `routeGlobal`.
 */
export interface TenantScopeSelection {
  /** From the scope hook: global route incl. the delegated-home carve-out. */
  routeGlobal: boolean;
  /** From the scope hook: "" = aggregated (GA only), else a tenant id. */
  selectedTenantId: string;
  /**
   * From the scope hook. Whenever routeGlobal is false this IS the caller's own tenant
   * (non-GA user, or delegated on their home tenant) — the member-path variants use it.
   * Both scope hooks return these three fields, so pages pass their scope object verbatim.
   */
  effectiveTenantId: string;
}

/**
 * Tenant query param for the /global/ variants: "" (aggregated) → undefined, and anything
 * that is not a GUID is dropped too (defense in depth — selections come from the tenant
 * list, so a non-GUID value is corrupted state, not intent; mirrors useFleetHealth).
 */
const globalTenantParam = (sel: TenantScopeSelection): string | undefined =>
  asGuidOrUndefined(sel.selectedTenantId.trim());

export const scopedApi = {
  fleetHealth: (sel: TenantScopeSelection, days: number) =>
    sel.routeGlobal ? api.metrics.globalFleetHealth(days, globalTenantParam(sel)) : api.metrics.fleetHealth(days),

  appMetrics: (sel: TenantScopeSelection, days: number) =>
    sel.routeGlobal ? api.metrics.globalApp(days, globalTenantParam(sel)) : api.metrics.app(sel.effectiveTenantId, days),

  timeAttribution: (sel: TenantScopeSelection) =>
    sel.routeGlobal ? api.metrics.globalTimeAttribution(globalTenantParam(sel)) : api.metrics.timeAttribution(),

  deviceJourneys: (sel: TenantScopeSelection, days: number) =>
    sel.routeGlobal ? api.metrics.globalDeviceJourneys(days, globalTenantParam(sel)) : api.metrics.deviceJourneys(days),

  geographic: (sel: TenantScopeSelection, days: number, groupBy: string) =>
    sel.routeGlobal
      ? api.metrics.globalGeographic(days, groupBy, globalTenantParam(sel))
      : api.metrics.geographic(sel.effectiveTenantId, days, groupBy),

  vulnerability: (sel: TenantScopeSelection, days: number, topN: number) =>
    sel.routeGlobal
      ? api.metrics.globalVulnerability(days, topN, globalTenantParam(sel))
      : api.metrics.vulnerability(days, topN),

  appsList: (sel: TenantScopeSelection, days: number) =>
    sel.routeGlobal ? api.apps.globalList(days, globalTenantParam(sel)) : api.apps.list(sel.effectiveTenantId, days),

  appAnalytics: (sel: TenantScopeSelection, appName: string, days: number) =>
    sel.routeGlobal
      ? api.apps.globalAnalytics(appName, days, globalTenantParam(sel))
      : api.apps.analytics(sel.effectiveTenantId, appName, days),

  appSessions: (
    sel: TenantScopeSelection,
    appName: string,
    days: number,
    status: "all" | "failed" | "succeeded",
    offset: number,
    limit: number,
    opts?: { model?: string; version?: string },
  ) =>
    sel.routeGlobal
      ? api.apps.globalSessions(appName, days, status, offset, limit, globalTenantParam(sel), opts)
      : api.apps.sessions(sel.effectiveTenantId, appName, days, status, offset, limit, opts),

  annotationsList: (
    sel: TenantScopeSelection,
    opts: { verdict?: string; lane?: string; pageSize?: number; continuation?: string },
  ) =>
    sel.routeGlobal
      ? api.annotations.globalList({ ...opts, tenantId: globalTenantParam(sel) })
      : api.annotations.list(opts),

  auditLogs: (
    sel: TenantScopeSelection,
    opts: { dateFrom?: string; dateTo?: string; pageSize?: number; continuation?: string; excludeDeletions?: boolean },
  ) =>
    sel.routeGlobal
      ? api.audit.globalLogs({ ...opts, tenantId: globalTenantParam(sel) })
      : api.audit.logs(opts),
};
