"use client";

import { useCallback, useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { cachedAuthFetchJson, FEATURE_FLAGS_TTL_MS } from "@/lib/cachedAuthFetch";
import { parseEditionInfo } from "@/lib/edition";
import {
  COMMUNITY_WINDOW_CAP_DAYS,
  PLATFORM_MAX_WINDOW_DAYS,
  WINDOW_DAYS_PARAM,
  effectiveWindowDays,
  parseWindowDays,
} from "@/lib/timeWindow";

export interface WindowDays {
  /** The window to query and show as selected: the URL's `?days=` within the viewer's cap, else the page default. */
  days: number;
  /** False only while a URL value above the Community cap waits for the viewed tenant's edition. */
  ready: boolean;
  /** Writes `?days=` (the page default removes it) without navigating; other parameters stay. */
  setDays: (days: number) => void;
}

/**
 * The analytics window of a page, carried in the URL. The URL is the only state: the view reads it,
 * a preset click writes it, so a shared link opens the same window. The cap never rewrites the URL —
 * the link keeps the intent, the view shows intent within the viewer's entitlement:
 * - platform roles (Global Admin / Reader): {@link PLATFORM_MAX_WINDOW_DAYS} in every scope;
 * - everyone else: the viewed tenant's edition cap (Community 90, Pro 365), read from its
 *   feature flags only when the URL asks for more than {@link COMMUNITY_WINDOW_CAP_DAYS} days; a failed
 *   read falls back to the Community cap.
 *
 * Needs a Suspense boundary above the page content (useSearchParams under the static export).
 *
 * @param tenantId the tenant whose data the page shows (`scope.effectiveTenantId` on scope-aware pages).
 */
export function useWindowDays({ defaultDays, tenantId }: { defaultDays: number; tenantId: string | undefined }): WindowDays {
  const searchParams = useSearchParams();
  const { hasGlobalScope, getAccessToken } = useAuth();
  const requested = parseWindowDays(searchParams?.get(WINDOW_DAYS_PARAM));

  const needsEdition = !hasGlobalScope && requested != null && requested > COMMUNITY_WINDOW_CAP_DAYS;
  const [edition, setEdition] = useState<{ tenantId: string; capDays: number } | null>(null);

  useEffect(() => {
    if (!needsEdition || !tenantId) return;
    let cancelled = false;
    const run = async () => {
      let capDays = COMMUNITY_WINDOW_CAP_DAYS;
      try {
        const flags = await cachedAuthFetchJson<unknown>(api.config.featureFlags(tenantId), getAccessToken, { ttlMs: FEATURE_FLAGS_TTL_MS });
        capDays = parseEditionInfo(flags).entitlements.retentionCapDays;
      } catch {
        // Fail-closed: the Community cap.
      }
      if (!cancelled) setEdition({ tenantId, capDays });
    };
    void run();
    return () => { cancelled = true; };
  }, [needsEdition, tenantId, getAccessToken]);

  const capDays = hasGlobalScope
    ? PLATFORM_MAX_WINDOW_DAYS
    : !needsEdition
      ? COMMUNITY_WINDOW_CAP_DAYS
      : edition && edition.tenantId === tenantId
        ? edition.capDays
        : null;

  const setDays = useCallback((next: number) => {
    const url = new URL(window.location.href);
    if (next === defaultDays) url.searchParams.delete(WINDOW_DAYS_PARAM);
    else url.searchParams.set(WINDOW_DAYS_PARAM, String(next));
    window.history.replaceState(null, "", url.toString());
  }, [defaultDays]);

  return {
    days: effectiveWindowDays(requested, defaultDays, capDays ?? COMMUNITY_WINDOW_CAP_DAYS),
    ready: capDays != null,
    setDays,
  };
}
