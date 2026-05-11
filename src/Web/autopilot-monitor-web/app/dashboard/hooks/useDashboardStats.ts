"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { asGuidOrUndefined } from "@/utils/inputValidation";
import type { NotificationType } from "@/contexts/NotificationContext";

export interface DashboardStats {
  days: number;
  activeCount: number;
  totalLastNDays: number;
  succeededLastNDays: number;
  failedLastNDays: number;
  successRatePct: number;
  avgDurationMinutes: number;
  totalToday: number;
  failedToday: number;
  computedAt: string;
}

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: string, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: string, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
}

interface UseDashboardStatsParams {
  tenantId: string | null | undefined;
  globalAdminMode: boolean;
  submittedTenantIdFilter: string;
  days?: number;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
  signalR: SignalRApi;
  /**
   * When true (e.g. user role not yet known or regular non-admin user),
   * the hook stays idle — no fetches, no SignalR subscriptions.
   */
  disabled?: boolean;
}

export interface UseDashboardStatsReturn {
  stats: DashboardStats | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

const DEFAULT_DAYS = 7;
const SIGNALR_DEBOUNCE_MS = 3000;

/**
 * Server-side stats for the dashboard cards. Replaces the old "compute from
 * whatever happens to be paginated client-side" path that drifted with the
 * load-more cursor and printed a fictional "Last 7 days" label.
 *
 * Refresh model: initial fetch on scope change + debounced refetch on every
 * SignalR newSession/newevents in the active scope. On SignalR reconnect we
 * also force a refetch so any messages missed during the outage are reflected.
 */
export function useDashboardStats({
  tenantId,
  globalAdminMode,
  submittedTenantIdFilter,
  days = DEFAULT_DAYS,
  getAccessToken,
  addNotification,
  signalR,
  disabled = false,
}: UseDashboardStatsParams): UseDashboardStatsReturn {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Scope refs for SignalR handlers (avoid restarting subscriptions on every keystroke).
  const tenantIdRef = useRef(tenantId);
  tenantIdRef.current = tenantId;
  const globalAdminModeRef = useRef(globalAdminMode);
  globalAdminModeRef.current = globalAdminMode;
  const submittedFilterRef = useRef(submittedTenantIdFilter);
  submittedFilterRef.current = submittedTenantIdFilter;
  const daysRef = useRef(days);
  daysRef.current = days;
  const disabledRef = useRef(disabled);
  disabledRef.current = disabled;

  // Generation counter — invalidates an in-flight fetch when the scope shifts
  // mid-request (tenant switch, GA toggle, filter Submit). Without this, a slow
  // request from the previous scope could land its result over a newer one.
  const fetchGenRef = useRef(0);
  // Debounce timer for SignalR-triggered refetches. Bursts of events (e.g. 50
  // newevents in one second during a session storm) collapse into a single
  // backend call ~SIGNALR_DEBOUNCE_MS after the last event.
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Tracks whether SignalR was connected on the previous render so we can
  // distinguish "first connection" from "reconnect after outage" — the latter
  // needs an explicit refetch to recover from missed messages.
  const wasConnectedRef = useRef(false);

  const fetchStats = useCallback(async (): Promise<void> => {
    if (disabledRef.current) return;
    if (!globalAdminModeRef.current && !tenantIdRef.current) return;

    const myGen = ++fetchGenRef.current;
    setLoading(true);
    setError(null);

    try {
      const url = globalAdminModeRef.current
        ? api.globalSessions.stats({
            tenantId: asGuidOrUndefined(submittedFilterRef.current.trim()),
            days: daysRef.current,
          })
        : api.sessions.stats({ days: daysRef.current });

      const response = await authenticatedFetch(url, getAccessToken);

      // Stale fetch — a newer scope change won the race. Drop silently.
      if (myGen !== fetchGenRef.current) return;

      if (!response.ok) {
        let detail = response.statusText;
        try {
          const body = await response.json();
          if (body?.message) detail = body.message;
        } catch { /* not JSON */ }
        setError(detail);
        return;
      }

      const body = await response.json();
      if (body?.success && body?.stats) {
        setStats(body.stats as DashboardStats);
      } else {
        setError("Malformed stats response");
      }
    } catch (e) {
      if (myGen !== fetchGenRef.current) return;
      if (e instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", e.message, "session-expired-error");
      } else {
        console.error("Failed to fetch dashboard stats:", e);
        setError("Unable to load stats");
      }
    } finally {
      if (myGen === fetchGenRef.current) setLoading(false);
    }
  }, [getAccessToken, addNotification]);

  const refresh = useCallback(() => {
    if (debounceTimerRef.current) {
      clearTimeout(debounceTimerRef.current);
      debounceTimerRef.current = null;
    }
    fetchStats();
  }, [fetchStats]);

  const scheduleDebouncedRefresh = useCallback(() => {
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(() => {
      debounceTimerRef.current = null;
      fetchStats();
    }, SIGNALR_DEBOUNCE_MS);
  }, [fetchStats]);

  // Refetch on scope change (tenant switch, GA toggle, filter Submit, days change).
  // Reset stats synchronously so any previous-scope numbers (e.g. cross-tenant
  // sums when toggling GA off) don't linger on the cards if the refetch errors
  // out or returns nothing — the user sees "..." instead of stale data.
  useEffect(() => {
    if (disabled) return;
    if (!globalAdminMode && !tenantId) return;
    setStats(null);
    fetchStats();
    // fetchStats is stable; intentionally not in deps to avoid re-running on identity flips.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, globalAdminMode, submittedTenantIdFilter, days, disabled]);

  // SignalR-triggered debounced refetch + reconnect-recovery.
  useEffect(() => {
    if (disabled) return;

    const handleNewSession = (...args: unknown[]) => {
      const data = args[0] as { tenantId?: string } | undefined;
      if (!isInScope(data?.tenantId)) return;
      scheduleDebouncedRefresh();
    };

    const handleNewEvents = (...args: unknown[]) => {
      const data = args[0] as { tenantId?: string } | undefined;
      if (!isInScope(data?.tenantId)) return;
      scheduleDebouncedRefresh();
    };

    function isInScope(eventTenantId: string | undefined): boolean {
      if (!eventTenantId) return false;
      if (globalAdminModeRef.current) {
        const filter = submittedFilterRef.current.trim();
        return !filter || eventTenantId === filter;
      }
      return !!tenantIdRef.current && eventTenantId === tenantIdRef.current;
    }

    signalR.on("newSession", handleNewSession);
    signalR.on("newevents", handleNewEvents);

    return () => {
      signalR.off("newSession", handleNewSession);
      signalR.off("newevents", handleNewEvents);
    };
    // Re-bind only if connection identity changes — handlers read live refs
    // so scope changes don't need to tear down the subscription.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isConnected, disabled]);

  // Reconnect-recovery: when SignalR transitions disconnected → connected and
  // we already had an initial fetch, force a refetch to pick up any sessions
  // we missed during the outage.
  useEffect(() => {
    if (disabled) return;
    if (signalR.isConnected) {
      const isReconnect = wasConnectedRef.current;
      wasConnectedRef.current = true;
      if (isReconnect) refresh();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isConnected, disabled]);

  // Cleanup on unmount.
  useEffect(() => {
    return () => {
      if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
      fetchGenRef.current++;
    };
  }, []);

  return { stats, loading, error, refresh };
}
