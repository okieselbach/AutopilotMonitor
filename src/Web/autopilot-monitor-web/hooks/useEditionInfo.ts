"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { dedupedAuthFetch } from "@/lib/dedupedAuthFetch";
import { parseEditionInfo, type EditionInfo } from "@/lib/edition";

/**
 * Lightweight edition surface for globally mounted chrome (navbar).
 *
 * Returns null until a feature-flags response actually confirms the edition —
 * deliberately NOT the fail-closed Community default, because the only consumer
 * renders a "Community Edition" label and a Pro tenant must never see it flash
 * while flags load. Errors also resolve to null (no label beats a wrong label).
 *
 * Uses dedupedAuthFetch so a concurrent dashboard/session fetch of the same
 * feature-flags URL collapses into one request.
 */
export function useEditionInfo(): EditionInfo | null {
  const { isAuthenticated, user, getAccessToken } = useAuth();
  const [info, setInfo] = useState<EditionInfo | null>(null);

  const tenantId = user?.tenantId;
  // Same guard as useTenantSecurityConfig: role-less members would just 401/403.
  const hasTenantRole = !!user && (user.isTenantAdmin || user.isGlobalAdmin || user.role != null);

  useEffect(() => {
    if (!isAuthenticated || !tenantId || !hasTenantRole) return;
    let cancelled = false;
    const run = async () => {
      try {
        const res = await dedupedAuthFetch(api.config.featureFlags(tenantId), getAccessToken);
        if (!res.ok) return;
        const parsed = parseEditionInfo(await res.json());
        if (!cancelled) setInfo(parsed);
      } catch {
        // Leave null — chrome renders nothing rather than a guessed edition.
      }
    };
    void run();
    return () => { cancelled = true; };
  }, [isAuthenticated, tenantId, hasTenantRole, getAccessToken]);

  return info;
}
