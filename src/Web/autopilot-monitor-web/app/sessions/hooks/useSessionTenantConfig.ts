"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { cachedAuthFetchJson, FEATURE_FLAGS_TTL_MS } from "@/lib/cachedAuthFetch";
import type { TenantFeatureFlagsResponse } from "@/lib/generated/wire-types.generated";

interface UseSessionTenantConfigReturn {
  showScriptOutput: boolean;
  enableSoftwareInventoryAnalyzer: boolean;
  enableIntegrityBypassAnalyzer: boolean;
  /** Whether an on-demand diagnostics upload can succeed right now (drives the Collect Logs button). */
  diagnosticsUploadConfigured: boolean;
  /** True once the flags were actually read — before that (and after a failed read) the values are defaults. */
  featureFlagsLoaded: boolean;
  /** Optimistic flip after a successful quick-config from the session page — avoids a refetch round-trip. */
  markDiagnosticsConfigured: () => void;
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
  const [diagnosticsUploadConfigured, setDiagnosticsUploadConfigured] = useState(false);
  const [featureFlagsLoaded, setFeatureFlagsLoaded] = useState(false);

  useEffect(() => {
    if (!sessionTenantId) return;
    let cancelled = false;
    (async () => {
      try {
        // These flags are exposed via the member-readable feature-flags endpoint so that
        // Operators and Viewers can load session details without 403'ing on the admin-only
        // full /api/config/{tenantId} response.
        const cfg = await cachedAuthFetchJson<TenantFeatureFlagsResponse>(api.config.featureFlags(sessionTenantId), getAccessToken, { ttlMs: FEATURE_FLAGS_TTL_MS });
        if (cancelled) return;
        setShowScriptOutput(cfg.showScriptOutput ?? true);
        setEnableSoftwareInventoryAnalyzer(cfg.enableSoftwareInventoryAnalyzer ?? false);
        setEnableIntegrityBypassAnalyzer(cfg.enableIntegrityBypassAnalyzer ?? true);
        setDiagnosticsUploadConfigured(cfg.diagnosticsUploadConfigured ?? false);
        setFeatureFlagsLoaded(true);
      } catch { /* non-fatal */ }
    })();
    return () => { cancelled = true; };
  }, [sessionTenantId, getAccessToken]);

  const markDiagnosticsConfigured = useCallback(() => setDiagnosticsUploadConfigured(true), []);

  return {
    showScriptOutput,
    enableSoftwareInventoryAnalyzer,
    enableIntegrityBypassAnalyzer,
    diagnosticsUploadConfigured,
    featureFlagsLoaded,
    markDiagnosticsConfigured,
  };
}
