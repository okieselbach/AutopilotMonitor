"use client";

import { useEffect, useRef } from "react";
import { Session, RuleResult } from "@/types";
import { isTerminalStatus } from "@/utils/sessionStatus";

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: string, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: string, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
  joinGroup: (group: string) => Promise<void>;
  leaveGroup: (group: string) => Promise<void>;
}

interface UseSessionSignalRParams {
  sessionId: string;
  sessionTenantId: string | null;
  tenantId: string;
  sessionTenantIdFromSession: string | undefined;
  globalAdminMode: boolean;
  sessionIdRef: React.MutableRefObject<string>;
  sessionRef: React.MutableRefObject<Session | null>;
  resolveEffectiveTenantId: () => string | null;
  signalR: SignalRApi;
  scheduleFetchEvents: (delayMs?: number) => void;
  setSession: React.Dispatch<React.SetStateAction<Session | null>>;
  setSessionTenantId: React.Dispatch<React.SetStateAction<string | null>>;
  fetchAnalysisResults: (reanalyze?: boolean) => Promise<void>;
  fetchVulnerabilityReport: (rescan?: boolean) => Promise<void>;
}

/**
 * Owns the session detail page's SignalR integration:
 *  - joins tenant + session groups using subscribe-then-fetch pattern
 *  - listens for eventStream, newevents, ruleResultsReady, vulnerabilityReportReady
 *  - cleans up groups + handlers on unmount / sessionId change
 */
export function useSessionSignalR({
  sessionId,
  sessionTenantId,
  tenantId,
  sessionTenantIdFromSession,
  globalAdminMode,
  sessionIdRef,
  sessionRef,
  resolveEffectiveTenantId,
  signalR,
  scheduleFetchEvents,
  setSession,
  setSessionTenantId,
  fetchAnalysisResults,
  fetchVulnerabilityReport,
}: UseSessionSignalRParams): void {
  const { on, off, isConnected, joinGroup, leaveGroup } = signalR;
  const hasJoinedGroups = useRef(false);

  // Join SignalR groups when connected (for multi-tenancy and cost optimization)
  // Uses "subscribe-then-fetch" pattern: join groups first, then re-fetch events
  // to catch anything that arrived before the group join completed.
  useEffect(() => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!sessionId || !isConnected || !effectiveTenantId) return;

    if (!hasJoinedGroups.current) {
      const tenantGroupName = `tenant-${effectiveTenantId}`;
      const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;

      const joinAndCatchUp = async () => {
        await joinGroup(tenantGroupName);
        await joinGroup(sessionGroupName);
        hasJoinedGroups.current = true;

        // Re-fetch events after group join to catch any SignalR messages
        // that were sent before the client joined the session group.
        // The frontend deduplicates by eventId, so no duplicates.
        scheduleFetchEvents(0);
      };
      joinAndCatchUp();
    }

    return () => {
      if (hasJoinedGroups.current && effectiveTenantId) {
        const tenantGroupName = `tenant-${effectiveTenantId}`;
        const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;
        leaveGroup(tenantGroupName);
        leaveGroup(sessionGroupName);
        hasJoinedGroups.current = false;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, isConnected, sessionTenantId, tenantId, sessionTenantIdFromSession, globalAdminMode]);

  // Setup SignalR listener - re-register when connection changes
  useEffect(() => {
    // Listen for event stream signal — backend sends a lightweight signal (no event payload).
    // Frontend fetches fresh events from Table Storage on receipt: canonical truth, no gaps.
    const handleEventStream = (data: { sessionId: string; tenantId: string; newEventCount: number; newRuleResults?: RuleResult[] }) => {
      console.log('Event stream signal received via SignalR:', data);
      if (data.sessionId !== sessionIdRef.current) return;

      // Skip the full event refetch once the session has reached a terminal status —
      // agents in the post-completion grace period keep emitting performance/metrics
      // snapshots every 15-30s, which would otherwise cause the timeline to thrash and
      // scroll-jump while the user is reading. The initial page load already grabbed
      // everything; the user can refresh manually if they want to see late trailing events.
      // Rule-result + tenant-id side effects still run.
      const status = sessionRef.current?.status;
      if (!isTerminalStatus(status)) {
        // Fetch full events from storage (single source of truth), but debounce bursts.
        // Session updates arrive via the "newevents" message (tenant group) — no session
        // object in this signal to keep payloads minimal.
        scheduleFetchEvents();
      }

      if (data.tenantId) {
        setSessionTenantId(prev => prev || data.tenantId);
      }

      // Rule results from SignalR (only on enrollment completion)
      if (data.newRuleResults && data.newRuleResults.length > 0) {
        fetchAnalysisResults();
      }
    };

    // Listen for session delta updates via the tenant group ("newevents").
    // This replaces the full session object that was previously sent inside "eventStream".
    const handleNewEvents = (data: { sessionId: string; tenantId: string; sessionUpdate?: Partial<Session> }) => {
      if (data.sessionId !== sessionIdRef.current) return;

      if (data.sessionUpdate) {
        setSession(prev => prev ? { ...prev, ...data.sessionUpdate } : prev);
      }
      if (data.tenantId) {
        setSessionTenantId(prev => prev || data.tenantId);
      }
    };

    // Listen for async rule engine results (pushed after background analysis completes)
    const handleRuleResultsReady = (data: { sessionId: string; tenantId: string; ruleResultCount: number }) => {
      if (data.sessionId !== sessionIdRef.current) return;
      console.info(`[SessionDetail] ruleResultsReady signal: ${data.ruleResultCount} results`);
      fetchAnalysisResults();
    };

    // Listen for async vulnerability correlation results
    const handleVulnerabilityReportReady = (data: { sessionId: string; tenantId: string; overallRisk: string }) => {
      if (data.sessionId !== sessionIdRef.current) return;
      console.info(`[SessionDetail] vulnerabilityReportReady signal: risk=${data.overallRisk}`);
      fetchVulnerabilityReport();
    };

    on('eventStream', handleEventStream);
    on('newevents', handleNewEvents);
    on('ruleResultsReady', handleRuleResultsReady);
    on('vulnerabilityReportReady', handleVulnerabilityReportReady);

    return () => {
      off('eventStream', handleEventStream);
      off('newevents', handleNewEvents);
      off('ruleResultsReady', handleRuleResultsReady);
      off('vulnerabilityReportReady', handleVulnerabilityReportReady);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [on, off]);
}
