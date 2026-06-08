"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { asGuidOrUndefined } from "@/utils/inputValidation";
import type { NotificationType } from "@/contexts/NotificationContext";

// Wire shape of the server-aggregated Fleet Health payload (camelCase of the
// backend FleetHealthMetrics DTO). Presentation-only derivations (bar maxima,
// axis labels) stay in the page.
export interface FleetHealthStats {
  total: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  successRate: number;
  avgDurationMinutes: number;
}
export interface FleetDailyPoint {
  date: string;
  success: number;
  failed: number;
}
export interface FleetFailureReason {
  reason: string;
  count: number;
}
export interface FleetModelHealth {
  model: string;
  total: number;
  succeeded: number;
}
export interface FleetSlowModel {
  model: string;
  avgMinutes: number;
  count: number;
}
export interface FleetFailingModel {
  model: string;
  failed: number;
  total: number;
  failureRate: number;
}
export interface FleetHealthData {
  success: boolean;
  days: number;
  stats: FleetHealthStats;
  dailyData: FleetDailyPoint[];
  failureReasons: FleetFailureReason[];
  modelHealth: FleetModelHealth[];
  slowestModels: FleetSlowModel[];
  topFailingModels: FleetFailingModel[];
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

interface UseFleetHealthParams {
  isGlobalAdmin: boolean;
  /** GA-aggregated selection: "" = all tenants, else a single tenant id. */
  selectedTenantId: string;
  /** The user's own tenant id — used for non-GA SignalR scope filtering. */
  tenantId: string | null | undefined;
  /** Gates fetching until the admin scope has resolved. */
  scopeInitialized: boolean;
  /** Opaque key that changes whenever the effective scope changes. */
  scopeKey: string;
  days: number;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
  signalR: SignalRApi;
}

export interface UseFleetHealthReturn {
  data: FleetHealthData | null;
  loading: boolean;
  error: string | null;
  refresh: () => void;
}

const SIGNALR_DEBOUNCE_MS = 3000;

/**
 * Server-aggregated Fleet Health. Replaces the old client path that drained up
 * to 200k raw sessions into the browser and aggregated on the main thread.
 *
 * Refresh model mirrors useDashboardStats: initial fetch on scope/days change +
 * debounced refetch on every in-scope SignalR newSession/newevents, with a
 * forced refetch on reconnect to recover messages missed during an outage.
 */
export function useFleetHealth({
  isGlobalAdmin,
  selectedTenantId,
  tenantId,
  scopeInitialized,
  scopeKey,
  days,
  getAccessToken,
  addNotification,
  signalR,
}: UseFleetHealthParams): UseFleetHealthReturn {
  const [data, setData] = useState<FleetHealthData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Live refs so SignalR handlers see current scope without re-subscribing.
  const isGlobalAdminRef = useRef(isGlobalAdmin);
  isGlobalAdminRef.current = isGlobalAdmin;
  const selectedTenantIdRef = useRef(selectedTenantId);
  selectedTenantIdRef.current = selectedTenantId;
  const tenantIdRef = useRef(tenantId);
  tenantIdRef.current = tenantId;
  const daysRef = useRef(days);
  daysRef.current = days;

  // Invalidates an in-flight fetch when scope/days shift mid-request.
  const fetchGenRef = useRef(0);
  const debounceTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const wasConnectedRef = useRef(false);

  const fetchData = useCallback(async (): Promise<void> => {
    const myGen = ++fetchGenRef.current;
    setLoading(true);
    setError(null);

    try {
      const url = isGlobalAdminRef.current
        ? api.metrics.globalFleetHealth(
            daysRef.current,
            asGuidOrUndefined(selectedTenantIdRef.current.trim()),
          )
        : api.metrics.fleetHealth(daysRef.current);

      const response = await authenticatedFetch(url, getAccessToken);

      // Stale fetch — a newer scope change won the race. Drop silently.
      if (myGen !== fetchGenRef.current) return;

      if (!response.ok) {
        let detail = response.statusText;
        try {
          const body = await response.json();
          if (body?.message) detail = body.message;
        } catch {
          /* not JSON */
        }
        setError(detail);
        return;
      }

      const body = (await response.json()) as FleetHealthData;
      if (body?.success) {
        setData(body);
      } else {
        setError("Malformed fleet health response");
      }
    } catch (e) {
      if (myGen !== fetchGenRef.current) return;
      if (e instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", e.message, "session-expired-error");
      } else {
        console.error("Failed to fetch fleet health:", e);
        setError("Unable to load fleet health data");
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
    fetchData();
  }, [fetchData]);

  const scheduleDebouncedRefresh = useCallback(() => {
    if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
    debounceTimerRef.current = setTimeout(() => {
      debounceTimerRef.current = null;
      fetchData();
    }, SIGNALR_DEBOUNCE_MS);
  }, [fetchData]);

  // Initial fetch + refetch on scope/days change. Reset data synchronously so
  // previous-scope numbers don't linger if the refetch errors out.
  useEffect(() => {
    if (!scopeInitialized) return;
    setData(null);
    fetchData();
    // fetchData is stable; scopeKey/days drive the refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, scopeKey, days]);

  // SignalR-triggered debounced refetch.
  useEffect(() => {
    function isInScope(eventTenantId: string | undefined): boolean {
      if (!eventTenantId) return false;
      if (isGlobalAdminRef.current) {
        const filter = selectedTenantIdRef.current.trim();
        return !filter || eventTenantId === filter;
      }
      return !!tenantIdRef.current && eventTenantId === tenantIdRef.current;
    }

    const handler = (...args: unknown[]) => {
      const evt = args[0] as { tenantId?: string } | undefined;
      if (!isInScope(evt?.tenantId)) return;
      scheduleDebouncedRefresh();
    };

    signalR.on("newSession", handler);
    signalR.on("newevents", handler);
    return () => {
      signalR.off("newSession", handler);
      signalR.off("newevents", handler);
    };
    // Handlers read live refs, so scope changes don't need a re-bind.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isConnected]);

  // Reconnect-recovery: refetch when SignalR transitions disconnected → connected.
  useEffect(() => {
    if (signalR.isConnected) {
      const isReconnect = wasConnectedRef.current;
      wasConnectedRef.current = true;
      if (isReconnect) refresh();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isConnected]);

  // Cleanup on unmount.
  useEffect(() => {
    return () => {
      if (debounceTimerRef.current) clearTimeout(debounceTimerRef.current);
      fetchGenRef.current++;
    };
  }, []);

  return { data, loading, error, refresh };
}
