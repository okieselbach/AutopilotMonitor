"use client";

import { useCallback, useMemo, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";
import { readTenantScope, writeTenantScope } from "@/utils/tenantScopeStorage";
import { resolveDelegatedSeed, resolveGaSeed } from "@/hooks/aggregatedAdminScopeSeed";
import { delegatedScopedTenantList, isHomeTenantTarget, upnDomain } from "@/utils/homeTenantScope";

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
 * Global-Admin tenant scope for the **aggregated-capable** page variant (fleet-health,
 * geographic-performance, apps, apps/[appName]). A GA/Reader may pick the empty "All tenants" aggregate;
 * a delegated ("MSP") admin gets the SAME pages but bounded to its managed subset and ALWAYS on a concrete
 * tenant (no all-tenants aggregate — the backend never serves a no-tenantId aggregate to delegated here).
 *
 * @param opts.urlTenantId seed from `?tenantId=` — a specific tenant to deep-link to (GA only). When present
 *   on first init it wins over the tab-persisted selection and is itself persisted for the rest of the tab.
 * @param opts.defaultAggregated when set, a GA/Reader defaults to the aggregated "All tenants" view ("")
 *   instead of their own tenant — used only when there is no `?tenantId=` deep-link and no persisted
 *   selection. No effect for a delegated caller (never aggregated) or a regular user. Used by the audit page.
 *
 * Pair with `<TenantScopeSelector scope={scope} allowAggregated />` and
 * `<GlobalAdminBanner show={scope.isGlobalAdmin} delegated={scope.isDelegatedScope} subtitle={globalAdminSubtitle(scope)} />`.
 */
export function useAggregatedAdminScope(opts?: {
  urlTenantId?: string;
  defaultAggregated?: boolean;
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
  // config/all), PLUS the caller's own home tenant when they hold a member role there (member-path access —
  // see utils/homeTenantScope.ts). GA/Reader: the full list.
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
  const [scopeInitialized, setScopeInitialized] = useState(false);

  const urlTenantId = opts?.urlTenantId;
  const defaultAggregated = opts?.defaultAggregated ?? false;

  // Persist ONLY on an explicit user action (the selector's onChange). The auto-defaults below use
  // setSelectedRaw so a GA's aggregated ("") intent is never clobbered by a transient auto-resolve.
  const setSelectedTenantId = useCallback((id: string) => {
    setSelectedRaw(id);
    writeTenantScope(id);
  }, []);

  const [prevIsGlobalAdmin, setPrevIsGlobalAdmin] = useState(isGlobalAdmin);

  // GA/Reader seed precedence: a first-init ?tenantId= deep-link wins, else the tab-persisted selection
  // ("" = aggregated is valid for a GA), else the page default (own tenant, or "" when defaultAggregated).
  const computeGaSeed = (firstInit: boolean): string =>
    resolveGaSeed({
      firstInit,
      urlTenantId,
      storedScope: readTenantScope(),
      ownTenantId: tenantId,
      defaultAggregated,
    });

  // Default the selection (done DURING RENDER — React's "adjust state when an input changes" escape hatch;
  // it converges, so no committed render keyed on scopeKey observes a transient wrong scope).
  if (isDelegatedScope) {
    // Delegated: no aggregate. Seed from the persisted managed tenant (if still managed) or the first
    // managed tenant once the (scoped) list arrives; re-default if the selection falls outside the set.
    if (tenants.length > 0 && (!selectedTenantId || !tenants.some((t) => t.tenantId === selectedTenantId))) {
      setSelectedRaw(
        resolveDelegatedSeed({
          storedScope: readTenantScope(),
          managedTenantIds: tenants.map((t) => t.tenantId),
          firstManagedTenantId: tenants[0].tenantId,
        })
      );
      if (!scopeInitialized) setScopeInitialized(true);
      if (prevIsGlobalAdmin !== isGlobalAdmin) setPrevIsGlobalAdmin(isGlobalAdmin);
    }
  } else if (tenantId && (!scopeInitialized || prevIsGlobalAdmin !== isGlobalAdmin)) {
    // GA/Reader (or a non-global user): seed from deep-link / persisted selection / page default. This also
    // re-runs when GA mode is toggled, so re-entering GA mode restores the previously persisted tenant.
    const firstInit = !scopeInitialized;
    setPrevIsGlobalAdmin(isGlobalAdmin);
    setScopeInitialized(true);
    setSelectedRaw(isGlobalAdmin ? computeGaSeed(firstInit) : "");
    // A first-init deep-link expresses explicit intent — persist it so the rest of the tab follows.
    if (isGlobalAdmin && firstInit && urlTenantId) writeTenantScope(urlTenantId);
  } else if (
    // Stale-selection guard (GA): a persisted tenant no longer present in the list falls back to the GA's
    // own tenant. "" (aggregated) is exempt via the truthiness check. Local only — never clobbers storage.
    isGlobalAdmin &&
    selectedTenantId &&
    tenants.length > 0 &&
    !tenants.some((t) => t.tenantId === selectedTenantId)
  ) {
    setSelectedRaw(tenantId);
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
  // Delegated + home tenant → member path (see interface doc). GA/Reader always route global here.
  const routeGlobal = isGlobalAdmin && !(isDelegatedScope && isHomeTenantTarget(selectedTenantId, homeTenantId));

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
    routeGlobal,
  };
}
