"use client";

import { useMemo, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";

export interface AggregatedAdminScope {
  /**
   * True when the page should drive the cross-tenant `/global/` endpoints + show the tenant selector:
   * a GlobalAdmin/GlobalReader in GA mode, OR a delegated ("MSP") admin (always-on). The name is kept
   * for backward compatibility with the consuming pages; use {@link isDelegatedScope} to tell them apart.
   */
  isGlobalAdmin: boolean;
  /** True when the cross-tenant scope is a delegated ("MSP") subset (not full platform scope). */
  isDelegatedScope: boolean;
  /** Sorted tenant list for the selector. Empty unless {@link isGlobalAdmin}; bounded to the managed subset for delegated. */
  tenants: TenantInfo[];
  /** Currently selected tenant; empty string ("") means the aggregated "All tenants" view (GA only — never for delegated). */
  selectedTenantId: string;
  setSelectedTenantId: (id: string) => void;
  /** Friendly domain name of the selected tenant, if known. */
  selectedTenantName: string | undefined;
  /**
   * Tenant to query: for cross-tenant callers this is {@link selectedTenantId} verbatim (so "" → aggregated
   * for a GA); for regular users it is their own tenant. A delegated caller always resolves to a concrete
   * managed tenant. Pass `effectiveTenantId || undefined` to the `/global/` endpoints.
   */
  effectiveTenantId: string;
  /** GA mode with no tenant selected → aggregated cross-tenant view. Never true for a delegated caller. */
  isAggregatedGlobalView: boolean;
  /** Cross-tenant caller viewing a specific tenant other than their own. */
  isGlobalOverride: boolean;
  /**
   * True once the initial selection has settled (own tenant / URL seed for a GA, or the first managed
   * tenant for a delegated caller). Gate data fetches on this so the page does not fire a wasted request
   * in the wrong scope before the default-selection effect runs.
   */
  scopeInitialized: boolean;
  /**
   * Stable identity of the current query scope — changes whenever GA mode is toggled OR the effective
   * tenant changes. Page fetch effects MUST depend on this (not just on selectedTenantId) so that
   * toggling GA mode while the page is mounted triggers a refetch instead of showing stale data.
   */
  scopeKey: string;
}

/**
 * Global-Admin tenant scope for the **aggregated-capable** page variant (fleet-health,
 * geographic-performance, apps, apps/[appName]). A GA/Reader may pick the empty "All tenants" aggregate;
 * a delegated ("MSP") admin gets the SAME pages but bounded to its managed subset and ALWAYS on a concrete
 * tenant (no all-tenants aggregate — the backend never serves a no-tenantId aggregate to delegated here).
 *
 * @param opts.urlGlobal  seed from `?global=1` — when set, honor the URL's tenant scope on init (GA only).
 * @param opts.urlTenantId seed from `?tenantId=` — the specific tenant to select ("" = aggregated, GA only).
 *
 * Pair with `<TenantScopeSelector scope={scope} allowAggregated />` and
 * `<GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />`.
 */
export function useAggregatedAdminScope(opts?: {
  urlGlobal?: boolean;
  urlTenantId?: string;
}): AggregatedAdminScope {
  const { tenantId } = useTenant();
  const { hasGlobalScope, user } = useAuth();
  const { globalAdminMode } = useAdminMode();

  // A delegated ("MSP") admin has cross-tenant read of a SUBSET of tenants but no full platform scope.
  // Cross-tenant mode is on for a GA/Reader in GA mode, AND always-on for a delegated admin (they have no
  // own-tenant view to toggle from). The field is exposed as isGlobalAdmin for page compatibility.
  const isDelegated = user?.isDelegated ?? false;
  const isDelegatedScope = Boolean(isDelegated && !hasGlobalScope);
  const isGlobalAdmin = Boolean((globalAdminMode && hasGlobalScope) || isDelegatedScope);

  const allTenants = useTenantList(isGlobalAdmin);
  // Delegated: bound the selector to the managed allow-list (defense in depth on top of the backend-bounded
  // config/all). GA/Reader: the full list.
  const delegatedAllow = useMemo(
    () => new Set((user?.delegatedTenantIds ?? []).map((t) => t.toLowerCase())),
    [user?.delegatedTenantIds]
  );
  const tenants = useMemo(
    () => (isDelegatedScope ? allTenants.filter((t) => delegatedAllow.has(t.tenantId.toLowerCase())) : allTenants),
    [allTenants, isDelegatedScope, delegatedAllow]
  );

  const [selectedTenantId, setSelectedTenantId] = useState<string>("");
  const [scopeInitialized, setScopeInitialized] = useState(false);

  const urlGlobal = opts?.urlGlobal;
  const urlTenantId = opts?.urlTenantId;

  const [prevIsGlobalAdmin, setPrevIsGlobalAdmin] = useState(isGlobalAdmin);

  // Default the selection (done DURING RENDER — React's "adjust state when an input changes" escape hatch;
  // it converges, so no committed render keyed on scopeKey observes a transient wrong scope).
  if (isDelegatedScope) {
    // Delegated: no aggregate. Default to the first managed tenant once the (scoped) list arrives, and
    // re-default if the current selection ever falls outside the managed set. Never empty.
    if (tenants.length > 0 && (!selectedTenantId || !tenants.some((t) => t.tenantId === selectedTenantId))) {
      setSelectedTenantId(tenants[0].tenantId);
      if (!scopeInitialized) setScopeInitialized(true);
      if (prevIsGlobalAdmin !== isGlobalAdmin) setPrevIsGlobalAdmin(isGlobalAdmin);
    }
  } else if (tenantId && (!scopeInitialized || prevIsGlobalAdmin !== isGlobalAdmin)) {
    // GA/Reader (or a non-global user): default to own tenant, honoring a ?global=1 URL seed on first init.
    const firstInit = !scopeInitialized;
    setPrevIsGlobalAdmin(isGlobalAdmin);
    setScopeInitialized(true);
    setSelectedTenantId(
      isGlobalAdmin ? (firstInit && urlGlobal ? urlTenantId ?? "" : tenantId) : ""
    );
  }

  const selectedTenantName = useMemo(
    () => tenants.find((t) => t.tenantId === selectedTenantId)?.domainName,
    [tenants, selectedTenantId]
  );

  // A delegated caller is never aggregated (always a concrete managed tenant).
  const isAggregatedGlobalView = Boolean(isGlobalAdmin && !selectedTenantId && !isDelegatedScope);
  const isGlobalOverride = Boolean(
    isGlobalAdmin && selectedTenantId && selectedTenantId !== tenantId
  );
  const effectiveTenantId = isGlobalAdmin ? selectedTenantId : tenantId;
  const scopeKey = isGlobalAdmin ? `ga:${selectedTenantId || "*all*"}` : `tenant:${tenantId}`;

  return {
    isGlobalAdmin,
    isDelegatedScope,
    tenants,
    selectedTenantId,
    setSelectedTenantId,
    selectedTenantName,
    effectiveTenantId,
    isAggregatedGlobalView,
    isGlobalOverride,
    scopeInitialized,
    scopeKey,
  };
}
