"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { isHomeTenantTarget } from "@/utils/homeTenantScope";
import type { FleetSummary } from "../lib/fleetRollup";

export interface FleetSummariesState {
  /** tenantId (verbatim) → summary. Absent while loading or if that tenant's fetch failed. */
  summaries: Record<string, FleetSummary>;
  loading: boolean;
}

// Bounded fan-out: an MSP may manage many tenants; cap concurrent stats requests so a large fleet does not
// open dozens of simultaneous connections. Each request hits the Phase-2a single-tenant `?tenantId=` path,
// which the backend bounds to the caller's scope.
const CONCURRENCY = 6;

/**
 * Subset fan-out: fetch the per-tenant session-stats summary for each managed tenant and collect them into a
 * map. Client-orchestrated over the already-bounded single-tenant endpoints — no all-tenants code path.
 *
 * @param tenantIds    the tenant IDs to summarize (managed set, optionally including the home tenant).
 * @param days         lookback window passed to each per-tenant stats call.
 * @param homeTenantId the caller's OWN home tenant, if it is part of tenantIds: its summary is fetched via
 *   the JWT-bound member stats endpoint — home access is member-based, and the /global/ single-tenant path
 *   is bounded to the managed set (would return empty for it). See utils/homeTenantScope.ts.
 */
export function useFleetSummaries(tenantIds: string[], days: number, homeTenantId?: string): FleetSummariesState {
  const { getAccessToken } = useAuth();
  const [summaries, setSummaries] = useState<Record<string, FleetSummary>>({});
  const [loading, setLoading] = useState(false);

  // Stable dependency: the sorted tenant set. Re-fan-out only when the managed set or window changes.
  const key = [...tenantIds].sort().join(",");

  useEffect(() => {
    if (tenantIds.length === 0) {
      setSummaries({});
      setLoading(false);
      return;
    }

    let cancelled = false;
    setLoading(true);

    const run = async () => {
      const queue = [...tenantIds];
      const result: Record<string, FleetSummary> = {};

      const worker = async () => {
        while (queue.length > 0) {
          const tenantId = queue.pop();
          if (!tenantId) break;
          try {
            const url = isHomeTenantTarget(tenantId, homeTenantId)
              ? api.sessions.stats({ days })
              : api.globalSessions.stats({ tenantId, days });
            const response = await authenticatedFetch(url, getAccessToken);
            if (!response.ok) continue;
            const body = await response.json();
            if (body?.stats) result[tenantId] = body.stats as FleetSummary;
          } catch (err) {
            // One tenant's failure must not sink the whole fleet view — skip it; the card shows "—".
            console.error(`Error fetching fleet summary for tenant ${tenantId}:`, err);
          }
        }
      };

      await Promise.all(
        Array.from({ length: Math.min(CONCURRENCY, tenantIds.length) }, worker)
      );

      if (!cancelled) {
        setSummaries(result);
        setLoading(false);
      }
    };

    run();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, days, homeTenantId, getAccessToken]);

  return { summaries, loading };
}
