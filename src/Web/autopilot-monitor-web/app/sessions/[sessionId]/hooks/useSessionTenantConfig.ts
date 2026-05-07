"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { dedupedAuthFetch } from "@/lib/dedupedAuthFetch";

interface UseSessionTenantConfigReturn {
  showScriptOutput: boolean;
  enableSoftwareInventoryAnalyzer: boolean;
  enableIntegrityBypassAnalyzer: boolean;
}

/**
 * Fetches the session's tenant-level UI config (best-effort).
 * Triggers once sessionTenantId is known. Swallows errors — these flags
 * are non-critical (fall back to sensible defaults).
 */
export function useSessionTenantConfig(
  sessionTenantId: string | null,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
): UseSessionTenantConfigReturn {
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer] = useState(false);
  const [enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer] = useState(true);

  useEffect(() => {
    if (!sessionTenantId) return;
    let cancelled = false;
    (async () => {
      try {
        // These three flags are exposed via the member-readable feature-flags endpoint so that
        // Operators and Viewers can load session details without 403'ing on the admin-only
        // full /api/config/{tenantId} response.
        const res = await dedupedAuthFetch(api.config.featureFlags(sessionTenantId), getAccessToken);
        if (!res.ok || cancelled) return;
        const cfg = await res.json();
        if (cancelled) return;
        setShowScriptOutput(cfg.showScriptOutput ?? true);
        setEnableSoftwareInventoryAnalyzer(cfg.enableSoftwareInventoryAnalyzer ?? false);
        setEnableIntegrityBypassAnalyzer(cfg.enableIntegrityBypassAnalyzer ?? true);
      } catch { /* non-fatal */ }
    })();
    return () => { cancelled = true; };
  }, [sessionTenantId, getAccessToken]);

  return { showScriptOutput, enableSoftwareInventoryAnalyzer, enableIntegrityBypassAnalyzer };
}
