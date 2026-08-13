"use client";

import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAggregatedAdminScope } from "@/hooks/useAggregatedAdminScope";
import { resolveConcreteScopeView } from "@/hooks/concreteAdminScopeView";
import type { TenantInfo } from "@/hooks/useTenantList";

export type { TenantInfo };

export interface GlobalAdminScope {
  /**
   * True when cross-tenant mode is on AND the caller has cross-tenant scope — a GlobalAdmin/GlobalReader
   * in GA mode, OR a delegated ("MSP") admin (always-on). This is a VISIBILITY/routing flag — it drives the
   * tenant selector, the banner and the `/global/` endpoint choice, all read-only-safe. Mutating actions
   * gate separately on the real Global-Admin / own-tenant-admin status. Name kept for page compatibility.
   */
  isGlobalAdmin: boolean;
  /** True when the cross-tenant scope is a delegated ("MSP") subset (not full platform scope). */
  isDelegatedScope: boolean;
  /** Sorted tenant list for the selector. Empty unless {@link isGlobalAdmin}; bounded to the managed subset for delegated. */
  tenants: TenantInfo[];
  /** Currently selected tenant in the scope selector. */
  selectedTenantId: string;
  setSelectedTenantId: (id: string) => void;
  /** Tenant to actually query: the override target if one is picked, else the user's own tenant (delegated: always the managed selection). */
  effectiveTenantId: string;
  /** Cross-tenant caller picked a tenant other than their own → call the cross-tenant `/global/` endpoints. */
  isGlobalOverride: boolean;
  /** GA mode with no tenant selected → aggregated cross-tenant view. Never true here / for delegated. */
  isAggregatedGlobalView: boolean;
  /**
   * Endpoint routing: true → the page should call the cross-tenant `/global/*` variant, false → the
   * JWT-bound tenant-scoped member path. Equals {@link isGlobalAdmin} EXCEPT for a delegated ("MSP")
   * caller viewing their OWN home tenant: their authorization there is member/operator (JWT-bound),
   * not a delegated grant — and the `/global/*` fan-out is bounded to the managed set, so it would
   * return an empty result for the home tenant. Mirrors the MCP server's pickGlobalOrTenantPath.
   */
  routeGlobal: boolean;
}

/**
 * Global-Admin tenant scope for the **override-only** page variant (gather-rules, analyze-rules,
 * sla, usage-metrics): the selection always resolves to a concrete tenant — defaulting to the caller's
 * own tenant for a GA, or the first managed tenant for a delegated ("MSP") admin — and endpoint choice is
 * keyed on {@link GlobalAdminScope.isGlobalOverride}. There is no aggregated "All tenants" mode here.
 *
 * Implemented as a thin projection over {@link useAggregatedAdminScope} (one hook, aggregated as a mode):
 * all selection/seeding/persistence state lives there; resolveConcreteScopeView maps a GA's persisted
 * aggregated ("") intent to their own tenant locally — WITHOUT clearing storage, so aggregated pages
 * still honor it.
 *
 * Pair with {@link "@/components/TenantScopeSelector".TenantScopeSelector} for the header dropdown
 * and {@link "@/components/GlobalAdminBanner".GlobalAdminBanner} for the view bar.
 */
export function useGlobalAdminScope(): GlobalAdminScope {
  const { tenantId } = useTenant();
  const { user } = useAuth();
  const agg = useAggregatedAdminScope();

  const view = resolveConcreteScopeView({
    isGlobalAdmin: agg.isGlobalAdmin,
    isDelegatedScope: agg.isDelegatedScope,
    selectedTenantId: agg.selectedTenantId,
    ownTenantId: tenantId,
    homeTenantId: user?.tenantId,
  });

  return {
    isGlobalAdmin: agg.isGlobalAdmin,
    isDelegatedScope: agg.isDelegatedScope,
    tenants: agg.tenants,
    selectedTenantId: view.selectedTenantId,
    setSelectedTenantId: agg.setSelectedTenantId,
    effectiveTenantId: view.effectiveTenantId,
    isGlobalOverride: view.isGlobalOverride,
    isAggregatedGlobalView: view.isAggregatedGlobalView,
    routeGlobal: view.routeGlobal,
  };
}
