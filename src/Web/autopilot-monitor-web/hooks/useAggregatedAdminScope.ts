"use client";

import { useMemo, useState } from "react";
import { useTenant } from "@/contexts/TenantContext";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";

export interface AggregatedAdminScope {
  /** True when Global-Admin mode is toggled on AND the user actually is a global admin. */
  isGlobalAdmin: boolean;
  /** Sorted tenant list for the selector. Empty unless {@link isGlobalAdmin}. */
  tenants: TenantInfo[];
  /** Currently selected tenant; empty string ("") means the aggregated "All tenants" view. */
  selectedTenantId: string;
  setSelectedTenantId: (id: string) => void;
  /** Friendly domain name of the selected tenant, if known. */
  selectedTenantName: string | undefined;
  /**
   * Tenant to query: for GAs this is {@link selectedTenantId} verbatim (so "" → aggregated);
   * for regular users it is their own tenant. Pass `effectiveTenantId || undefined` to the
   * `/global/` endpoints to request the cross-tenant aggregate.
   */
  effectiveTenantId: string;
  /** GA mode with no tenant selected → aggregated cross-tenant view. */
  isAggregatedGlobalView: boolean;
  /** GA viewing a specific tenant other than their own. */
  isGlobalOverride: boolean;
  /**
   * True once the initial selection has settled (own tenant, or the URL seed). Gate data fetches on
   * this so the page does not fire a wasted request in the wrong scope before the default-selection
   * effect runs. (Stays true; selection is re-defaulted on GA-mode transitions, see {@link scopeKey}.)
   */
  scopeInitialized: boolean;
  /**
   * Stable identity of the current query scope — changes whenever GA mode is toggled OR the effective
   * tenant changes. Page fetch effects MUST depend on this (not just on selectedTenantId) so that
   * toggling GA mode while the page is mounted triggers a refetch instead of showing stale data from
   * the previous scope.
   */
  scopeKey: string;
}

/**
 * Global-Admin tenant scope for the **aggregated-capable** page variant (fleet-health,
 * geographic-performance, apps, apps/[appName]). Unlike the override variant, the selection may
 * be empty ("All tenants" aggregated), GAs always hit the `/global/` endpoints (passing
 * `selectedTenantId || undefined`), and a one-shot {@link AggregatedAdminScope.scopeInitialized}
 * guard defers the initial fetch until the default selection settles.
 *
 * @param opts.urlGlobal  seed from `?global=1` — when set, honor the URL's tenant scope on init
 *                        instead of defaulting to the own tenant (used by deep-linked detail pages).
 * @param opts.urlTenantId seed from `?tenantId=` — the specific tenant to select ("" = aggregated).
 *
 * Pair with `<TenantScopeSelector scope={scope} allowAggregated />` and
 * `<GlobalAdminBanner show={scope.isGlobalAdmin} subtitle={globalAdminSubtitle(scope)} />`.
 */
export function useAggregatedAdminScope(opts?: {
  urlGlobal?: boolean;
  urlTenantId?: string;
}): AggregatedAdminScope {
  const { tenantId } = useTenant();
  const { user } = useAuth();
  const { globalAdminMode } = useAdminMode();

  const isGlobalAdmin = Boolean(globalAdminMode && user?.isGlobalAdmin);

  const tenants = useTenantList(isGlobalAdmin);
  const [selectedTenantId, setSelectedTenantId] = useState<string>("");
  const [scopeInitialized, setScopeInitialized] = useState(false);

  const urlGlobal = opts?.urlGlobal;
  const urlTenantId = opts?.urlTenantId;

  const [prevIsGlobalAdmin, setPrevIsGlobalAdmin] = useState(isGlobalAdmin);

  // Default the selection on first settle (once tenantId is known) AND on every GA-mode transition.
  // This is done DURING RENDER (React's "adjust state when an input changes" escape hatch): React
  // discards this render and re-renders before committing, so no committed render — and therefore no
  // page fetch keyed on scopeKey — ever observes the transient (isGlobalAdmin && selectedTenantId="")
  // scope that an effect-based reset would briefly expose (would otherwise race a stale aggregated
  // /global request against the corrected own-tenant request).
  // - Regular users (GA off): selection is irrelevant (effectiveTenantId falls back to own tenant);
  //   clear it so a later GA toggle re-defaults cleanly.
  // - GAs on FIRST init with ?global=1: honor the URL seed (specific tenant, or "" = aggregated).
  // - GAs otherwise (incl. a later GA toggle-on): default to their own tenant (not aggregated).
  if (tenantId && (!scopeInitialized || prevIsGlobalAdmin !== isGlobalAdmin)) {
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

  const isAggregatedGlobalView = Boolean(isGlobalAdmin && !selectedTenantId);
  const isGlobalOverride = Boolean(
    isGlobalAdmin && selectedTenantId && selectedTenantId !== tenantId
  );
  const effectiveTenantId = isGlobalAdmin ? selectedTenantId : tenantId;
  // Stable identity of the query scope. When GA is off the selection is ignored (own tenant); when on
  // it encodes the selected/aggregated target. Toggling GA flips the prefix → page effects refetch.
  const scopeKey = isGlobalAdmin ? `ga:${selectedTenantId || "*all*"}` : `tenant:${tenantId}`;

  return {
    isGlobalAdmin,
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
