"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import type { Session } from "@/types/session";

export interface TenantSessionsState {
  sessions: Session[];
  loading: boolean;
  /** A first page was returned but the server signalled more rows (nextLink present). */
  hasMore: boolean;
  /** Set if the fetch failed (network or non-2xx); the page renders an inline error. */
  error: boolean;
}

// First page only — the Fleet drill-in is a recent-activity overview, not a full pager. The server caps
// the window via `days`; we cap the row count here. Deep pagination/filtering is a follow-up.
const PAGE_SIZE = 100;

/**
 * Fetches the recent session list for ONE managed tenant via the Phase-2a single-tenant
 * `/api/global/sessions?tenantId=` endpoint. The backend bounds this read to the caller's delegated
 * scope (GlobalReadOrDelegatedSubset); the page also guards the tenantId against delegatedTenantIds as
 * defense in depth before mounting this hook.
 *
 * @param tenantId managed tenant to load (verbatim, as it appears in the route/delegated list).
 * @param days     lookback window passed to the stats/list endpoints.
 */
export function useTenantSessions(tenantId: string, days: number): TenantSessionsState {
  const { getAccessToken } = useAuth();
  const [state, setState] = useState<TenantSessionsState>({
    sessions: [],
    loading: true,
    hasMore: false,
    error: false,
  });

  useEffect(() => {
    if (!tenantId) {
      setState({ sessions: [], loading: false, hasMore: false, error: false });
      return;
    }

    let cancelled = false;
    setState((s) => ({ ...s, loading: true, error: false }));

    const run = async () => {
      try {
        const response = await authenticatedFetch(
          api.globalSessions.list(tenantId, days, { pageSize: PAGE_SIZE }),
          getAccessToken
        );
        if (!response.ok) {
          if (!cancelled) setState({ sessions: [], loading: false, hasMore: false, error: true });
          return;
        }
        const data = await response.json();
        if (cancelled) return;
        setState({
          sessions: (data.sessions as Session[]) || [],
          loading: false,
          hasMore: !!data.nextLink,
          error: false,
        });
      } catch (err) {
        console.error(`Error fetching sessions for tenant ${tenantId}:`, err);
        if (!cancelled) setState({ sessions: [], loading: false, hasMore: false, error: true });
      }
    };

    run();
    return () => {
      cancelled = true;
    };
  }, [tenantId, days, getAccessToken]);

  return state;
}
