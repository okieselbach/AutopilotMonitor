import { isHomeTenantTarget } from "@/utils/homeTenantScope";

/**
 * Pure projection of the aggregated admin scope onto the CONCRETE (override-only) page
 * variant — the single place the two former hook twins actually differed. Extracted so
 * useGlobalAdminScope can be a thin wrapper over useAggregatedAdminScope and the
 * per-variant semantics stay pinned by unit tests (concreteAdminScopeView.test.ts).
 */
export interface ConcreteScopeInputs {
  /** Cross-tenant mode flag (GA/Reader in GA mode, or delegated always-on). */
  isGlobalAdmin: boolean;
  isDelegatedScope: boolean;
  /** Raw selection from the aggregated hook — "" is a valid aggregated intent there. */
  selectedTenantId: string;
  /** The caller's own tenant (useTenant). */
  ownTenantId: string;
  /** The caller's home tenant (user.tenantId) — the delegated member-path carve-out target. */
  homeTenantId: string | undefined;
}

export interface ConcreteScopeView {
  /** Selection resolved to a concrete tenant: a GA's persisted aggregated ("") intent maps
   * to their own tenant WITHOUT clearing storage, so aggregated pages still honor it. */
  selectedTenantId: string;
  /** Tenant to query; "" for a delegated caller before a managed tenant is seeded (pages
   * gate their fetch on truthiness to avoid a transient own-tenant request). */
  effectiveTenantId: string;
  isGlobalOverride: boolean;
  /** Never true in this variant once the own tenant is known. */
  isAggregatedGlobalView: boolean;
  /** Same carve-out as the aggregated hook: delegated + home tenant → member path. */
  routeGlobal: boolean;
}

export function resolveConcreteScopeView(i: ConcreteScopeInputs): ConcreteScopeView {
  const selected = i.isDelegatedScope ? i.selectedTenantId : (i.selectedTenantId || i.ownTenantId);
  return {
    selectedTenantId: selected,
    effectiveTenantId: i.isGlobalAdmin && selected
      ? selected
      : (i.isDelegatedScope ? "" : i.ownTenantId),
    isGlobalOverride: Boolean(i.isGlobalAdmin && selected && selected !== i.ownTenantId),
    isAggregatedGlobalView: Boolean(i.isGlobalAdmin && !selected && !i.isDelegatedScope),
    routeGlobal: i.isGlobalAdmin && !(i.isDelegatedScope && isHomeTenantTarget(selected, i.homeTenantId)),
  };
}
