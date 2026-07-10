"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation } from "@/lib/paginationLink";
import { asGuidOrUndefined } from "@/utils/inputValidation";
import { boundTenantToDelegatedScope } from "@/utils/delegatedScope";
import { isHomeTenantTarget } from "@/utils/homeTenantScope";
import type { NotificationType } from "@/contexts/NotificationContext";
import type { Session } from "../types";

const DEFAULT_PAGE_SIZE = 10;
const MAX_PAGE_SIZE = 1000;

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface User {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  isGlobalReader?: boolean;
  isDelegated?: boolean;
  delegatedTenantIds?: string[];
  role?: string | null;
}

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: string, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: string, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
  joinGroup: (group: string) => Promise<void>;
  leaveGroup: (group: string) => Promise<void>;
}

interface UseDashboardSessionsParams {
  user: User | null | undefined;
  tenantId: string | null | undefined;
  /**
   * Cross-tenant mode: drives the `/global/sessions` endpoint choice + cross-tenant event acceptance.
   * True for a real GA in GA mode AND for a delegated ("MSP") admin (whose aggregate the backend bounds
   * to the managed subset). Named globalAdminMode for back-compat; see {@link joinGlobalAdmins}.
   */
  globalAdminMode: boolean;
  /**
   * Whether to join the cross-tenant `global-admins` SignalR broadcast group. Real GA only — a delegated
   * caller has no platform scope and would be rejected (403); they rely on the per-tenant reconnect refetch.
   */
  joinGlobalAdmins: boolean;
  tenantIdFilter: string;
  adminMode: boolean;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
  setBlockedDevicesSet: (next: Set<string>) => void;
  signalR: SignalRApi;
}

export interface UseDashboardSessionsReturn {
  sessions: Session[];
  loading: boolean;
  hasMore: boolean;
  loadingMore: boolean;
  refetch: () => void;
  refetchWith: (tenantIdOverride: string) => void;
  loadMore: () => void;
  loadAll: () => void;
  removeSession: (sessionId: string) => void;
}

/**
 * Owns the dashboard's session list lifecycle:
 *  - initial fetch (gated on user role + tenantId/globalAdminMode readiness)
 *  - SignalR group joining (tenant + global-admins) and live update handlers
 *  - reconnect refetch
 *  - paginated load-more via cursor
 *  - blocked-devices sync after each fresh fetch
 *  - reset-on-globalAdminMode-toggle
 */
export function useDashboardSessions({
  user,
  tenantId,
  globalAdminMode,
  joinGlobalAdmins,
  tenantIdFilter,
  adminMode,
  getAccessToken,
  addNotification,
  setBlockedDevicesSet,
  signalR,
}: UseDashboardSessionsParams): UseDashboardSessionsReturn {
  const { on, off, isConnected, joinGroup, leaveGroup } = signalR;
  const joinGlobalAdminsRef = useRef(joinGlobalAdmins);
  joinGlobalAdminsRef.current = joinGlobalAdmins;

  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [hasMore, setHasMore] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [continuation, setContinuation] = useState<string | null>(null);

  // Refs for SignalR handlers to access current filter state without restarting subscriptions
  const tenantIdFilterRef = useRef(tenantIdFilter);
  tenantIdFilterRef.current = tenantIdFilter;
  const globalAdminModeRef = useRef(globalAdminMode);
  globalAdminModeRef.current = globalAdminMode;
  const tenantIdRef = useRef(tenantId);
  tenantIdRef.current = tenantId;
  // Delegated ("MSP") bound for the cross-tenant filter: a delegated reader (no platform scope) may only
  // drill a managed tenant. Refs so the fetch closure reads live values without re-creating it (matches the
  // other scope refs). isDelegatedScope excludes a delegated user who is ALSO GA/Reader (those are unbounded).
  const isDelegatedScopeRef = useRef(false);
  isDelegatedScopeRef.current = !!user?.isDelegated && !user?.isGlobalAdmin && !user?.isGlobalReader;
  const delegatedTenantIdsRef = useRef<string[] | undefined>(undefined);
  delegatedTenantIdsRef.current = user?.delegatedTenantIds;

  // Refs for fetch closures (refetch is called from various effects/handlers and should
  // always see current filter values without forcing dependency-driven recreation)
  const adminModeRef = useRef(adminMode);
  adminModeRef.current = adminMode;
  const continuationRef = useRef(continuation);
  continuationRef.current = continuation;
  const loadingMoreRef = useRef(loadingMore);
  loadingMoreRef.current = loadingMore;

  // Synchronous lock: set true BEFORE the first await so two triggers in the same
  // render cycle (pagination effect + debounced search effect in page.tsx) cannot
  // both pass the guard. The state-mirrored loadingMoreRef above lags by one tick
  // and is insufficient for that race.
  const fetchLockRef = useRef(false);
  // Cancellation token for the progressive loadAll() loop. Bumped whenever the
  // session list is being reset (refetch/refetchWith/unmount) so an in-flight
  // loop stops appending to a now-stale list.
  const loadAllTokenRef = useRef(0);
  // Generation counter for the session-fetch lifecycle. Bumped on every
  // refetch/refetchWith/global-mode-toggle so an in-flight loadMore that started
  // under the previous filter scope cannot land its result (or its now-stale
  // continuation token) into the new scope. Without this guard, a no-filter
  // loadMore that resolves AFTER a with-filter refetch would clobber the
  // freshly-set continuation with a token whose fingerprint binds to a different
  // (filterTenantId, days) tuple → next loadMore round-trips it back to the
  // backend → 400 filter_mismatch → auto-load-more retries forever → buttons flicker.
  const fetchGenRef = useRef(0);
  // Tenant filter as captured at the time of the last refetch/refetchWith. Continuation
  // tokens bind to (filterTenantId, days) at the issuing call — pagination is only valid
  // while that scope is unchanged. Using the live `tenantIdFilterRef` for fetches would
  // mismatch the continuation fingerprint whenever the user types between Submit clicks
  // (the live ref updates per-keystroke; the filter input drives client-side display via
  // effectiveSessions but is NOT applied to the backend until Submit / refetch).
  const submittedTenantIdFilterRef = useRef<string>("");

  const hasInitialFetch = useRef(false);
  const hasGlobalModeInitialized = useRef(false);
  const hasJoinedGroup = useRef(false);
  const wasConnectedRef = useRef(false);

  const fetchBlockedDevices = useCallback(async (currentSessions: Session[]) => {
    if (!adminModeRef.current || !globalAdminModeRef.current) {
      setBlockedDevicesSet(new Set());
      return;
    }

    try {
      const tenantIds = globalAdminModeRef.current
        ? [...new Set(currentSessions.map((s) => s.tenantId))]
        : tenantIdRef.current ? [tenantIdRef.current] : [];

      if (tenantIds.length === 0) {
        setBlockedDevicesSet(new Set());
        return;
      }

      const results = await Promise.allSettled(
        tenantIds.map((tid) =>
          authenticatedFetch(api.devices.blocked(tid), getAccessToken)
            .then((res) => (res.ok ? res.json() : { blocked: [] })),
        ),
      );

      const newSet = new Set<string>();
      results.forEach((result) => {
        if (result.status === "fulfilled" && result.value?.blocked) {
          for (const device of result.value.blocked) {
            newSet.add(`${device.tenantId}:${device.serialNumber}`);
          }
        }
      });

      setBlockedDevicesSet(newSet);
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", error.message, "session-expired-error");
      } else {
        console.error("Failed to fetch blocked devices:", error);
      }
    }
  }, [getAccessToken, addNotification, setBlockedDevicesSet]);

  const getInitialPageSize = (): number => {
    // Pattern B2 default first-paint pageSize is 10; localStorage may override
    // (legacy "sessionsPerPage" key). Cap to backend MAX_PAGE_SIZE.
    if (typeof window === "undefined") return DEFAULT_PAGE_SIZE;
    const stored = window.localStorage.getItem("sessionsPerPage");
    const parsed = stored ? parseInt(stored, 10) : NaN;
    const value = Number.isFinite(parsed) && parsed > 0 ? parsed : DEFAULT_PAGE_SIZE;
    return Math.min(value, MAX_PAGE_SIZE);
  };

  // Internal batch fetcher — returns data without touching state so callers can
  // decide how to apply it (single append vs. progressive loop). For first-page
  // calls we use getInitialPageSize() (Pattern B2: typically 10). For load-more
  // we keep the same pageSize so the user-visible cadence is uniform.
  const fetchSessionsBatch = useCallback(async (
    loadMoreContinuation?: string,
    globalTenantIdOverride?: string,
  ): Promise<{ sessions: Session[]; hasMore: boolean; nextContinuation: string | null } | null> => {
    try {
      // Use the SUBMITTED filter (last refetch's value) instead of the live ref so
      // a loadMore-with-continuation always queries with the same filter scope the
      // continuation was issued for. globalTenantIdOverride wins (refetchWith path).
      const rawFilter = globalTenantIdOverride !== undefined ? globalTenantIdOverride : submittedTenantIdFilterRef.current.trim();
      // Defense-in-depth: a delegated caller must never request a tenant outside its managed set, even via a
      // hand-crafted ?tenant= deep link or a free-typed GUID — drop it to the bounded aggregate. The backend
      // bounds this too; this just keeps the client from ever asking for an unmanaged tenant.
      const effectiveTenantFilter = boundTenantToDelegatedScope(
        asGuidOrUndefined(rawFilter), isDelegatedScopeRef.current, delegatedTenantIdsRef.current);
      const pageSize = getInitialPageSize();
      const opts = loadMoreContinuation
        ? { pageSize, continuation: loadMoreContinuation }
        : { pageSize };
      // A delegated ("MSP") caller filtering on their OWN home tenant routes to the JWT-bound member
      // list — their access there is member-based, and the /global/ path is bounded to the managed
      // set (would return empty). Mirrors the scope hooks' routeGlobal carve-out.
      const homeSelected = isDelegatedScopeRef.current &&
        isHomeTenantTarget(asGuidOrUndefined(rawFilter), tenantIdRef.current ?? undefined);
      const endpoint = globalAdminModeRef.current && !homeSelected
        ? api.globalSessions.list(effectiveTenantFilter, undefined, opts)
        : api.sessions.list(tenantIdRef.current ?? undefined, undefined, opts);

      const response = await authenticatedFetch(endpoint, getAccessToken);

      if (response.ok) {
        const data = await response.json();
        const nextContinuation = extractContinuation(data.nextLink);
        return {
          sessions: data.sessions || [],
          hasMore: !!nextContinuation,
          nextContinuation,
        };
      } else {
        // Surface the backend's error reason (e.g. "Invalid continuation token (filter_mismatch).")
        // instead of the generic statusText so a stale-token race is diagnosable from the UI.
        let detail = response.statusText;
        try {
          const body = await response.json();
          if (body?.message) detail = body.message;
        } catch { /* response body was not JSON — fall back to statusText */ }
        console.error(`Failed to fetch sessions (${response.status}): ${detail}`);
        addNotification("error", "Backend Error", `Failed to fetch sessions: ${detail}`, "backend-error");
        return null;
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", error.message, "session-expired-error");
      } else {
        console.error("Failed to fetch sessions:", error);
        addNotification(
          "error",
          "Backend Not Reachable",
          "Unable to connect to the backend API. Please ensure the backend server is running.",
          "backend-unreachable",
        );
      }
      return null;
    }
  }, [getAccessToken, addNotification]);

  // High-level fetch that applies result to state (initial load + single load-more).
  const fetchSessions = useCallback(async (loadMoreContinuation?: string, globalTenantIdOverride?: string) => {
    // Capture generation BEFORE the await — if a refetch/global-mode-toggle bumps it
    // while we're in flight, our result is stale and must not land in state.
    const myGen = fetchGenRef.current;
    const result = await fetchSessionsBatch(loadMoreContinuation, globalTenantIdOverride);

    // Stale fetch — a newer refetch has taken over. Drop the result silently.
    // Don't even toggle loading flags: the active refetch owns those.
    if (myGen !== fetchGenRef.current) return;

    if (result) {
      if (loadMoreContinuation) {
        setSessions((prev) => [...prev, ...result.sessions]);
      } else {
        setSessions(result.sessions);
        fetchBlockedDevices(result.sessions);
      }
      setHasMore(result.hasMore);
      setContinuation(result.nextContinuation);
    } else if (loadMoreContinuation) {
      // Auto-load-more failure (e.g. 400 from a stale continuation token).
      // Stop the retry loop so the page.tsx auto-load-more effect doesn't hammer
      // the backend with the same broken request. User can refresh to recover.
      setHasMore(false);
      setContinuation(null);
    }

    setLoading(false);
    setLoadingMore(false);
  }, [fetchSessionsBatch, fetchBlockedDevices]);

  const refetch = useCallback(() => {
    loadAllTokenRef.current++; // cancel any in-flight progressive loader
    fetchGenRef.current++; // invalidate any in-flight loadMore result
    // Capture the live filter as the new submitted scope — subsequent loadMore
    // calls will query under this filter regardless of further keystrokes.
    submittedTenantIdFilterRef.current = tenantIdFilterRef.current;
    // Reset continuation/hasMore synchronously so a stale loadMore that fired
    // before us cannot leave its no-filter token in state if it resolves first.
    setContinuation(null);
    setHasMore(false);
    setLoading(true);
    fetchSessions();
  }, [fetchSessions]);

  const refetchWith = useCallback((tenantIdOverride: string) => {
    loadAllTokenRef.current++; // cancel any in-flight progressive loader
    fetchGenRef.current++; // invalidate any in-flight loadMore result
    submittedTenantIdFilterRef.current = tenantIdOverride;
    setContinuation(null);
    setHasMore(false);
    setLoading(true);
    fetchSessions(undefined, tenantIdOverride);
  }, [fetchSessions]);

  const loadMore = useCallback(() => {
    if (!continuationRef.current || fetchLockRef.current) return;
    fetchLockRef.current = true;
    setLoadingMore(true);
    fetchSessions(continuationRef.current).finally(() => {
      fetchLockRef.current = false;
    });
  }, [fetchSessions]);

  // Progressive loader — fetches ALL remaining sessions batch by batch.
  // Used when search is active and local results are insufficient.
  const loadAll = useCallback(async () => {
    if (!continuationRef.current || fetchLockRef.current) return;
    fetchLockRef.current = true;
    const myToken = ++loadAllTokenRef.current;
    setLoadingMore(true);
    try {
      let currentContinuation: string | null = continuationRef.current;
      while (currentContinuation && loadAllTokenRef.current === myToken) {
        const result = await fetchSessionsBatch(currentContinuation);
        if (!result || loadAllTokenRef.current !== myToken) break;

        setSessions((prev) => [...prev, ...result.sessions]);
        setContinuation(result.nextContinuation);
        setHasMore(result.hasMore);
        currentContinuation = result.hasMore ? result.nextContinuation : null;
      }
    } finally {
      fetchLockRef.current = false;
      setLoadingMore(false);
    }
  }, [fetchSessionsBatch]);

  const removeSession = useCallback((sessionId: string) => {
    setSessions((prev) => prev.filter((s) => s.sessionId !== sessionId));
  }, []);

  // Cancel any in-flight progressive loadAll() loop on unmount so it
  // doesn't call setSessions against a torn-down component.
  useEffect(() => {
    return () => {
      loadAllTokenRef.current++;
    };
  }, []);

  // Initial fetch — gated on user role + tenant readiness
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && !user.isDelegated && user.role !== "Operator") {
      return; // regular users are redirected elsewhere; don't fetch
    }
    if (!globalAdminMode && !tenantId) return; // wait for real tenant ID
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;

    // Anchor the submitted filter to whatever the input started with (typically "")
    // so the first paginated loadMore queries under the same scope as the initial fetch.
    submittedTenantIdFilterRef.current = tenantIdFilterRef.current;
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user, tenantId, globalAdminMode]);

  // Join tenant SignalR group; refetch on reconnect
  useEffect(() => {
    if (isConnected) {
      const isReconnect = wasConnectedRef.current;
      wasConnectedRef.current = true;

      if (!hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        hasJoinedGroup.current = true;
        joinGroup(groupName);
      }

      if (isReconnect && hasInitialFetch.current) {
        fetchSessions();
      }
    }

    return () => {
      if (hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        leaveGroup(groupName);
        hasJoinedGroup.current = false;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, tenantId]);

  // Join/leave the cross-tenant global-admins broadcast group. Real GA only (joinGlobalAdmins) — a
  // delegated ("MSP") caller has no platform scope and would be rejected (403); the dashboard still reads
  // the bounded aggregate and recovers live state via the reconnect refetch above.
  useEffect(() => {
    if (!isConnected) return;

    if (joinGlobalAdmins) {
      console.log("[Dashboard] joining global-admins group");
      joinGroup("global-admins");
    } else {
      console.log("[Dashboard] leaving global-admins group");
      leaveGroup("global-admins");
    }

    return () => {
      if (joinGlobalAdmins) {
        console.log("[Dashboard] Component unmounting: leaving global-admins group");
        leaveGroup("global-admins");
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, joinGlobalAdmins]);

  // SignalR listeners — re-register when connection cycles
  useEffect(() => {
    const handleNewSession = (...args: unknown[]) => {
      const data = args[0] as { sessionId: string; tenantId: string; session: Session } | undefined;
      if (!data) return;
      console.log("New session registered", data);

      if (!globalAdminModeRef.current && tenantIdRef.current && data.tenantId !== tenantIdRef.current) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (not in global mode, own tenant: ${tenantIdRef.current})`);
        return;
      }

      const activeFilter = tenantIdFilterRef.current.trim();
      if (globalAdminModeRef.current && activeFilter && data.tenantId !== activeFilter) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (filtered to ${activeFilter})`);
        return;
      }

      if (data.session) {
        setSessions((prevSessions) => {
          const sessionIndex = prevSessions.findIndex((s) => s.sessionId === data.session.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = data.session;
            return updated;
          }
          return [data.session, ...prevSessions];
        });
      } else {
        console.warn("newSession event received without session data, falling back to fetch");
        fetchSessions();
      }
    };

    const handleNewEvents = (...args: unknown[]) => {
      const data = args[0] as { sessionId: string; tenantId: string; eventCount: number; sessionUpdate?: Partial<Session>; session?: Session } | undefined;
      if (!data) return;
      console.log("New events notification received on dashboard", data);

      const update = data.sessionUpdate || data.session;
      if (update) {
        setSessions((prevSessions) => {
          const sessionIndex = prevSessions.findIndex((s) => s.sessionId === data.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = { ...prevSessions[sessionIndex], ...update };
            return updated;
          }
          return prevSessions;
        });
      }
    };

    on("newSession", handleNewSession);
    on("newevents", handleNewEvents);

    return () => {
      off("newSession", handleNewSession);
      off("newevents", handleNewEvents);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected]);

  // Refetch + reset visible state when Global Admin mode toggles (after first init)
  useEffect(() => {
    if (!hasGlobalModeInitialized.current) {
      hasGlobalModeInitialized.current = true;
      return;
    }

    fetchGenRef.current++; // invalidate any in-flight loadMore from the previous mode
    submittedTenantIdFilterRef.current = tenantIdFilterRef.current;
    setSessions([]);
    setContinuation(null);
    setHasMore(false);
    setLoading(true);
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [globalAdminMode]);

  return {
    sessions,
    loading,
    hasMore,
    loadingMore,
    refetch,
    refetchWith,
    loadMore,
    loadAll,
    removeSession,
  };
}
