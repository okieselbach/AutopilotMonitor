"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { extractContinuation } from "@/lib/paginationLink";
import type { Session } from "@/types/session";

export interface TenantSessionsState {
  sessions: Session[];
  /** First-page load. */
  loading: boolean;
  /** A subsequent page (loadMore) is in flight. */
  loadingMore: boolean;
  /** The server has more rows beyond what's loaded (a continuation token is held). */
  hasMore: boolean;
  /** Set if a fetch failed (network or non-2xx); the page renders an inline error. */
  error: boolean;
  /** Fetch and append the next page. No-op while one is in flight or when there's nothing more. */
  loadMore: () => void;
}

// Page size for the managed-tenant session list — server-bounded by `days` on top of this.
const PAGE_SIZE = 100;

/**
 * Fetches the recent session list for ONE managed tenant via the Phase-2a single-tenant
 * `/api/global/sessions?tenantId=` endpoint, with continuation-token pagination (Load more). The backend
 * bounds this read to the caller's delegated scope (GlobalReadOrDelegatedSubset); the page also guards the
 * tenantId against delegatedTenantIds as defense in depth before mounting this hook.
 *
 * @param tenantId managed tenant to load (verbatim, as it appears in the route/delegated list).
 * @param days     lookback window passed to the list endpoint.
 */
export function useTenantSessions(tenantId: string, days: number): TenantSessionsState {
  const { getAccessToken } = useAuth();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [hasMore, setHasMore] = useState(false);
  const [error, setError] = useState(false);

  const continuationRef = useRef<string | null>(null);
  // Generation guard: bumped on every (tenant, days) change so an in-flight loadMore from a previous
  // tenant can't append into the new tenant's list after a fast switch.
  const genRef = useRef(0);
  const loadingMoreRef = useRef(false);

  useEffect(() => {
    const myGen = ++genRef.current;
    continuationRef.current = null;

    if (!tenantId) {
      setSessions([]);
      setLoading(false);
      setHasMore(false);
      setError(false);
      return;
    }

    // Clear rows on every new tenant fetch — otherwise navigating A→B briefly renders A's sessions under
    // B's heading until B's request lands (both in scope, so not a leak, but misleading in the drill-in).
    setSessions([]);
    setLoading(true);
    setHasMore(false);
    setError(false);

    const run = async () => {
      try {
        const response = await authenticatedFetch(
          api.globalSessions.list(tenantId, days, { pageSize: PAGE_SIZE }),
          getAccessToken
        );
        if (myGen !== genRef.current) return; // a newer tenant/days took over
        if (!response.ok) {
          setError(true);
          setLoading(false);
          return;
        }
        const data = await response.json();
        if (myGen !== genRef.current) return;
        const cont = extractContinuation(data.nextLink);
        continuationRef.current = cont;
        setSessions((data.sessions as Session[]) || []);
        setHasMore(!!cont);
        setLoading(false);
      } catch (err) {
        console.error(`Error fetching sessions for tenant ${tenantId}:`, err);
        if (myGen === genRef.current) {
          setError(true);
          setLoading(false);
        }
      }
    };

    run();
  }, [tenantId, days, getAccessToken]);

  const loadMore = useCallback(async () => {
    const cont = continuationRef.current;
    if (!cont || loadingMoreRef.current || !tenantId) return;

    const myGen = genRef.current;
    loadingMoreRef.current = true;
    setLoadingMore(true);
    try {
      const response = await authenticatedFetch(
        api.globalSessions.list(tenantId, days, { pageSize: PAGE_SIZE, continuation: cont }),
        getAccessToken
      );
      if (myGen !== genRef.current) return; // tenant/days switched mid-flight — drop this page
      if (!response.ok) {
        // Stop paginating on error rather than spamming a broken token; the loaded rows stay.
        setHasMore(false);
        return;
      }
      const data = await response.json();
      if (myGen !== genRef.current) return;
      const next = extractContinuation(data.nextLink);
      continuationRef.current = next;
      setSessions((prev) => [...prev, ...((data.sessions as Session[]) || [])]);
      setHasMore(!!next);
    } catch (err) {
      console.error(`Error loading more sessions for tenant ${tenantId}:`, err);
      if (myGen === genRef.current) setHasMore(false);
    } finally {
      loadingMoreRef.current = false;
      if (myGen === genRef.current) setLoadingMore(false);
    }
  }, [tenantId, days, getAccessToken]);

  return { sessions, loading, loadingMore, hasMore, error, loadMore };
}
