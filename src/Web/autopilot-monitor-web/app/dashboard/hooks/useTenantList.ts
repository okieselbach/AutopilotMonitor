"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";

export interface TenantListItem {
  tenantId: string;
  domainName: string;
}

/**
 * Fetches the tenant list used for fuzzy-search autocomplete when Global Admin mode is active.
 * Returns an empty array when not in global admin mode.
 * Swallows errors (non-critical — autocomplete just won't work).
 */
export function useTenantList(
  globalAdminMode: boolean,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
): TenantListItem[] {
  const [tenantList, setTenantList] = useState<TenantListItem[]>([]);

  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      if (!globalAdminMode) {
        setTenantList([]);
        return;
      }
      try {
        // config/all is a bare array of tenant configurations (deliberately untyped, D-043).
        const configs = await fetchJson<{ tenantId?: string; domainName?: string }[]>(api.config.all(), getAccessToken);
        if (cancelled) return;
        setTenantList(
          configs
            .filter((c): c is { tenantId: string; domainName: string } => !!c.tenantId && !!c.domainName)
            .map((c) => ({ tenantId: c.tenantId, domainName: c.domainName }))
        );
      } catch {
        // Non-critical — tenant autocomplete just won't work
      }
    };
    void run();
    return () => { cancelled = true; };
  }, [globalAdminMode, getAccessToken]);

  return tenantList;
}
