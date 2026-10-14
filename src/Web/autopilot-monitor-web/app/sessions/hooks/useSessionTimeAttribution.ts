"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";
import { useLatest } from "@/hooks/useLatest";
import type { GetSessionTimeAttributionResponse } from "@/lib/generated/wire-types.generated";
import type { SessionTimeBreakdownDto } from "../components/TimeAttributionLane";

/**
 * F1 time attribution: the pre-computed breakdown of a terminal session. The backend writes it
 * with the terminal status, so it exists once the page sees Succeeded or Failed. Null is normal
 * (session before the feature, Incomplete — no wall clock) and simply omits the time views; the
 * read is fail-soft, nothing else may depend on it.
 */
export function useSessionTimeAttribution(
  sessionId: string,
  status: string | null | undefined,
  tenantId: string | null | undefined,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>
): SessionTimeBreakdownDto | null {
  const [breakdown, setBreakdown] = useState<SessionTimeBreakdownDto | null>(null);
  const getToken = useLatest(getAccessToken);

  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    const run = async () => {
      if (status !== "Succeeded" && status !== "Failed") {
        setBreakdown(null);
        return;
      }
      try {
        const json = await fetchJson<GetSessionTimeAttributionResponse>(
          api.sessions.timeAttribution(sessionId, tenantId || undefined),
          (force?: boolean) => getToken.current(force)
        );
        if (!cancelled) setBreakdown(json.breakdown ?? null);
      } catch {
        // fail-soft: no time views
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [sessionId, status, tenantId, getToken]);

  return breakdown;
}
