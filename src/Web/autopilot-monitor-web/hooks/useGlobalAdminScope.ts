"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";
import { readTenantScope, writeTenantScope } from "@/utils/tenantScopeStorage";
import { delegatedScopedTenantList, isHomeTenantTarget, upnDomain } from "@/utils/homeTenantScope";

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
 * Pair with {@link "@/components/TenantScopeSelector".TenantScopeSelector} for the header dropdown
 * and {@link "@/components/GlobalAdminBanner".GlobalAdminBanner} for the view bar.
 */
export function useGlobalAdminScope(): GlobalAdminScope {
  const { tenantId } = useTenant();
  const { hasGlobalScope, user } = useAuth();
  const { globalAdminMode } = useAdminMode();

  // Cross-tenant mode: GA/Reader in GA mode, OR a delegated ("MSP") admin (always-on). See AggregatedAdminScope.
  const isDelegated = user?.isDelegated ?? false;
  const isDelegatedScope = Boolean(isDelegated && !hasGlobalScope);
  const isGlobalAdmin = Boolean((globalAdminMode && hasGlobalScope) || isDelegatedScope);

  const allTenants = useTenantList(isGlobalAdmin);
  // Delegated: managed subset PLUS the caller's own home tenant when they hold a member role there
  // (member-path access — see utils/homeTenantScope.ts). config/all is backend-bounded to the managed
  // set, so the home entry is synthesized with the UPN-derived domain when absent.
  const homeTenantId = user?.tenantId;
  const hasHomeRole = !!user?.role;
  const tenants = useMemo(
    () =>
      isDelegatedScope
        ? delegatedScopedTenantList(allTenants, user?.delegatedTenantIds, homeTenantId, upnDomain(user?.upn), hasHomeRole)
        : allTenants,
    [allTenants, isDelegatedScope, user?.delegatedTenantIds, homeTenantId, user?.upn, hasHomeRole]
  );

  const [selectedTenantId, setSelectedRaw] = useState<string>("");

  // Persist ONLY on an explicit user action (the selector's onChange). The auto-defaults below use
  // setSelectedRaw so they never write back — in particular a GA's persisted aggregated ("") intent
  // is left untouched here (this override-only variant has no aggregate) so aggregated pages still honor it.
  const setSelectedTenantId = useCallback((id: string) => {
    setSelectedRaw(id);
    writeTenantScope(id);
  }, []);

  // GA/Reader: seed the selection from the tab-persisted choice, else the user's own tenant (never empty
  // in this variant). A persisted aggregated "" resolves locally to the own tenant without clearing storage.
  useEffect(() => {
    if (isDelegatedScope || !tenantId || selectedTenantId) return;
    const stored = readTenantScope();
    setSelectedRaw(stored ? stored : tenantId);
  }, [isDelegatedScope, tenantId, selectedTenantId]);

  // Delegated: seed from the persisted managed tenant (if still managed) or the first managed tenant once
  // the scoped list arrives; re-default if the selection falls outside the managed set. Render-time (converges).
  if (isDelegatedScope && tenants.length > 0 && (!selectedTenantId || !tenants.some((t) => t.tenantId === selectedTenantId))) {
    const stored = readTenantScope();
    const storedManaged = stored && tenants.some((t) => t.tenantId === stored) ? stored : null;
    setSelectedRaw(storedManaged ?? tenants[0].tenantId);
  }

  // Stale-selection guard (GA): a persisted tenant no longer present in the list falls back to the own
  // tenant. Local only — never clobbers storage.
  if (!isDelegatedScope && isGlobalAdmin && selectedTenantId && tenants.length > 0 && !tenants.some((t) => t.tenantId === selectedTenantId)) {
    setSelectedRaw(tenantId);
  }

  const isGlobalOverride = Boolean(
    isGlobalAdmin && selectedTenantId && selectedTenantId !== tenantId
  );
  // Delegated has no valid own-tenant data, so before a managed tenant is selected resolve to "" (empty)
  // — pages gate their fetch on a truthy effectiveTenantId, so this avoids a transient own-tenant request.
  const effectiveTenantId = isGlobalAdmin && selectedTenantId
    ? selectedTenantId
    : (isDelegatedScope ? "" : tenantId);
  const isAggregatedGlobalView = Boolean(isGlobalAdmin && !selectedTenantId && !isDelegatedScope);
  // Delegated + home tenant → member path (see interface doc). GA/Reader always route global here.
  const routeGlobal = isGlobalAdmin && !(isDelegatedScope && isHomeTenantTarget(selectedTenantId, homeTenantId));

  return {
    isGlobalAdmin,
    isDelegatedScope,
    tenants,
    selectedTenantId,
    setSelectedTenantId,
    effectiveTenantId,
    isGlobalOverride,
    isAggregatedGlobalView,
    routeGlobal,
  };
}
