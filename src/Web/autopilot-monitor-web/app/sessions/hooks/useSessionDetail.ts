"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { isGuid } from "@/utils/inputValidation";
import { Session } from "@/types";
import type { NotificationType } from "@/contexts/NotificationContext";

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
  role?: string | null;
}

interface UseSessionDetailParams {
  sessionId: string;
  tenantId: string;
  globalAdminMode: boolean;
  /**
   * Explicit target tenant from the URL (`?tenantId=`). Set when a delegated ("MSP") admin drills into a
   * managed tenant's session from the fleet: that caller has no global scope, so the backend cannot resolve
   * the session cross-tenant from a null tenantId — it needs the named tenant on every read. Takes priority
   * over the JWT home tenant for the initial fetch; ignored once the session's own tenantId is known.
   */
  tenantIdOverride?: string;
  user: User | null | undefined;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
}

export interface UseSessionDetailReturn {
  session: Session | null;
  setSession: React.Dispatch<React.SetStateAction<Session | null>>;
  sessionTenantId: string | null;
  setSessionTenantId: React.Dispatch<React.SetStateAction<string | null>>;
  loading: boolean;
  setLoading: React.Dispatch<React.SetStateAction<boolean>>;
  fetchSessionDetails: () => Promise<void>;
  resolveEffectiveTenantId: () => string | null;
  sessionRef: React.MutableRefObject<Session | null>;
  sessionIdRef: React.MutableRefObject<string>;
}

/**
 * Owns the session detail page's core session-object lifecycle:
 *  - initial fetch (gated on auth + tenantId readiness; resets on sessionId change)
 *  - StrictMode-safe single-fetch guards (hasInitialFetch, lastFetchedSessionId)
 *  - eager sessionTenantId hydration from TenantContext to eliminate fetch waterfall
 *  - resolveEffectiveTenantId helper used by events/SignalR hooks
 */
export function useSessionDetail({
  sessionId,
  tenantId,
  globalAdminMode,
  tenantIdOverride,
  user,
  getAccessToken,
  addNotification,
}: UseSessionDetailParams): UseSessionDetailReturn {
  const [session, setSession] = useState<Session | null>(null);
  const [sessionTenantId, setSessionTenantId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const sessionRef = useRef<Session | null>(null);
  const sessionIdRef = useRef(sessionId);
  const tenantIdRef = useRef(tenantId);
  const sessionTenantIdRef = useRef<string | null>(sessionTenantId);
  const globalAdminModeRef = useRef(globalAdminMode);
  const tenantIdOverrideRef = useRef<string | undefined>(tenantIdOverride);
  const hasInitialFetch = useRef(false);
  const lastFetchedSessionId = useRef<string | null>(null);

  useEffect(() => { sessionRef.current = session; }, [session]);
  useEffect(() => { sessionIdRef.current = sessionId; }, [sessionId]);
  useEffect(() => { tenantIdRef.current = tenantId; }, [tenantId]);
  useEffect(() => { sessionTenantIdRef.current = sessionTenantId; }, [sessionTenantId]);
  useEffect(() => { globalAdminModeRef.current = globalAdminMode; }, [globalAdminMode]);
  useEffect(() => { tenantIdOverrideRef.current = tenantIdOverride; }, [tenantIdOverride]);

  const resolveEffectiveTenantId = useCallback((): string | null => {
    const knownSessionTenant = sessionTenantIdRef.current || sessionRef.current?.tenantId || null;
    if (knownSessionTenant) return knownSessionTenant;
    // Explicit URL target (delegated cross-tenant drill-in) wins over the JWT home tenant — the caller is
    // viewing someone else's tenant and has no global scope, so a null tenantId can't resolve it server-side.
    if (tenantIdOverrideRef.current) return tenantIdOverrideRef.current;
    if (globalAdminModeRef.current) return null;
    return tenantIdRef.current || null;
  }, []);

  const fetchSessionDetails = useCallback(async () => {
    try {
      const knownTenantId = resolveEffectiveTenantId();
      // Always use the direct session endpoint — the backend resolves the tenant
      // via FindSessionTenantIdAsync for global admins when tenantId is unknown.
      const endpoint = knownTenantId
        ? api.sessions.get(sessionId, knownTenantId)
        : api.sessions.get(sessionId);

      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (response.ok) {
        const data = await response.json();
        const foundSession = data.session ?? data.sessions?.find((s: Session) => s.sessionId === sessionId);
        if (foundSession) {
          setSession(foundSession);
          setSessionTenantId(foundSession.tenantId);
        }
      } else {
        addNotification('error', 'Backend Error', `Failed to load session details: ${response.statusText}`, 'session-detail-fetch-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        return;
      }
      console.error("Failed to fetch session details:", error);
      addNotification('error', 'Backend Not Reachable', 'Unable to load session details. Please check your connection.', 'session-detail-fetch-error');
      // Allow retry on network errors
      hasInitialFetch.current = false;
    }
  }, [sessionId, getAccessToken, addNotification, resolveEffectiveTenantId]);

  // Initial data fetch — wait for auth to be ready and a real tenantId before calling the backend.
  // TenantContext initializes to '' and updates once AuthContext finishes loading.
  // `user` is included so that a retry fires once MSAL settles (token becomes available).
  useEffect(() => {
    if (!sessionId) return;
    if (!globalAdminMode && !tenantId && !tenantIdOverride) return; // wait for a real target tenant

    // Reset fetch flag only if navigating to a different session
    if (lastFetchedSessionId.current !== sessionId) {
      hasInitialFetch.current = false;
      lastFetchedSessionId.current = sessionId;
      setSessionTenantId(null);
    }

    // Prevent duplicate fetches in React StrictMode (development double-mounting)
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;

    // Performance: eager-set sessionTenantId if we already know it from TenantContext.
    // This lets dependent effects kick off fetchEvents/analysis/vulns/config in parallel
    // with fetchSessionDetails instead of waiting for its roundtrip — eliminates a waterfall.
    // Global Admins in all-tenant view fall through with null and keep the old behavior.
    const knownTenantId = resolveEffectiveTenantId();
    if (knownTenantId && isGuid(knownTenantId)) {
      setSessionTenantId(knownTenantId);
    }

    fetchSessionDetails();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, tenantId, globalAdminMode, tenantIdOverride, user]);

  return {
    session,
    setSession,
    sessionTenantId,
    setSessionTenantId,
    loading,
    setLoading,
    fetchSessionDetails,
    resolveEffectiveTenantId,
    sessionRef,
    sessionIdRef,
  };
}
