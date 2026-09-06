"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";

export interface TenantInfo {
  tenantId: string;
  domainName: string;
  /**
   * True when this entry is the caller's OWN (home) tenant surfaced into a delegated ("MSP") scope —
   * see utils/homeTenantScope.ts. Home-tenant reads route via the tenant-scoped member path, not /global/*.
   */
  isHome?: boolean;
}

/**
 * Fetches the full tenant list (for the Global-Admin tenant selector) once and sorts it by
 * domain name. This is the byte-identical fetch+sort block that was copy-pasted across every
 * Global-Admin page; both {@link "@/hooks/useGlobalAdminScope".useGlobalAdminScope} (override
 * variant) and {@link "@/hooks/useAggregatedAdminScope".useAggregatedAdminScope} (aggregated
 * variant) build on it.
 *
 * @param enabled fetch only when true (i.e. the user is actually a global admin in GA mode).
 */
export function useTenantList(enabled: boolean): TenantInfo[] {
  const { getAccessToken } = useAuth();
  const [tenants, setTenants] = useState<TenantInfo[]>([]);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    const loadTenants = async () => {
      try {
        // config/all is a bare array of tenant configurations (deliberately untyped, D-043).
        const data = await fetchJson<Array<{ tenantId: string; domainName?: string }>>(api.config.all(), getAccessToken);
        const mapped: TenantInfo[] = data.map((t) => ({
          tenantId: t.tenantId,
          domainName: t.domainName || "",
        }));
        mapped.sort((a, b) =>
          (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId)
        );
        if (!cancelled) setTenants(mapped);
      } catch (err) {
        console.error("Error fetching tenant list:", err);
      }
    };
    loadTenants();
    return () => {
      cancelled = true;
    };
  }, [enabled, getAccessToken]);

  return tenants;
}
