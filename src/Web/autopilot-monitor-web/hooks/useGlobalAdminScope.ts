"use client";

import { useEffect, useMemo, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";

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
  const delegatedAllow = useMemo(
    () => new Set((user?.delegatedTenantIds ?? []).map((t) => t.toLowerCase())),
    [user?.delegatedTenantIds]
  );
  const tenants = useMemo(
    () => (isDelegatedScope ? allTenants.filter((t) => delegatedAllow.has(t.tenantId.toLowerCase())) : allTenants),
    [allTenants, isDelegatedScope, delegatedAllow]
  );

  const [selectedTenantId, setSelectedTenantId] = useState<string>("");

  // GA/Reader: default the selection to the user's own tenant (never empty in this variant).
  useEffect(() => {
    if (!isDelegatedScope && tenantId && !selectedTenantId) {
      setSelectedTenantId(tenantId);
    }
  }, [isDelegatedScope, tenantId, selectedTenantId]);

  // Delegated: default to the first managed tenant once the scoped list arrives; re-default if the current
  // selection falls outside the managed set. Done during render (converging escape hatch).
  if (isDelegatedScope && tenants.length > 0 && (!selectedTenantId || !tenants.some((t) => t.tenantId === selectedTenantId))) {
    setSelectedTenantId(tenants[0].tenantId);
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

  return {
    isGlobalAdmin,
    isDelegatedScope,
    tenants,
    selectedTenantId,
    setSelectedTenantId,
    effectiveTenantId,
    isGlobalOverride,
    isAggregatedGlobalView,
  };
}
