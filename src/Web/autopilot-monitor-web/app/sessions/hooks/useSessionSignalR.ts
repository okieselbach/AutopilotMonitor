"use client";

import { useEffect } from "react";
import { Session, RuleResult } from "@/types";
import { isTerminalStatus } from "@/utils/sessionStatus";
import type { SignalRMessageName } from "@/lib/signalrMessages";
import type { FetchEventsReason } from "./useSessionEvents";

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: SignalRMessageName, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: SignalRMessageName, handler: (...args: any[]) => void) => void;
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
  sessionIdRef: React.RefObject<string>;
  sessionRef: React.RefObject<Session | null>;
  resolveEffectiveTenantId: () => string | null;
  signalR: SignalRApi;
  scheduleFetchEvents: (reason?: FetchEventsReason) => void;
  setSession: React.Dispatch<React.SetStateAction<Session | null>>;
  setSessionTenantId: React.Dispatch<React.SetStateAction<string | null>>;
  fetchAnalysisResults: (reanalyze?: boolean) => Promise<void>;
  fetchVulnerabilityReport: (rescan?: boolean) => Promise<void>;
}

/**
 * Applies a "newevents" session delta. Returns `prev` itself when every field of the update
 * already holds the same value, so React bails out of the re-render: a live session pushes a
 * delta with every agent batch, and most of them repeat the status and phase the page already
 * shows. Shallow comparison only — a nested object with a fresh identity counts as a change
 * (a pure derivation, recomputed on every push; nothing is cached across pushes).
 */
export function applySessionUpdate(prev: Session, update: Partial<Session>): Session {
  const keys = Object.keys(update) as (keyof Session)[];
  if (keys.every((key) => Object.is(prev[key], update[key]))) return prev;
  return { ...prev, ...update };
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

  // Join SignalR groups when connected (for multi-tenancy and cost optimization)
  // Uses "subscribe-then-fetch" pattern: join groups first, then re-fetch events
  // to catch anything that arrived before the group join completed.
  useEffect(() => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!sessionId || !isConnected || !effectiveTenantId) return;

    const tenantGroupName = `tenant-${effectiveTenantId}`;
    const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;
    // Cleared by the cleanup: a run superseded while its joins were in flight (typically because
    // sessionTenantId resolved) must not schedule a catch-up of its own.
    let current = true;

    const joinAndCatchUp = async () => {
      await Promise.all([joinGroup(tenantGroupName), joinGroup(sessionGroupName)]);
      if (!current) return;

      // Re-fetch events after group join to catch any SignalR messages that were sent
      // before the client joined the session group — unless a fetch is still walking the
      // pages, which reads them anyway (see shouldScheduleFetch in useSessionEvents).
      // The frontend deduplicates by eventId, so no duplicates.
      scheduleFetchEvents("join-catch-up");
    };
    void joinAndCatchUp();

    // Every run that joined leaves in its own cleanup, whether or not its joins have resolved:
    // the SignalR layer counts references per group and settles a leave that arrives before the
    // join resolved. (A "joined" flag set after the await used to skip this leave when the effect
    // re-ran mid-join, and the re-run then took a second reference nobody released.)
    return () => {
      current = false;
      leaveGroup(tenantGroupName);
      leaveGroup(sessionGroupName);
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
        // Fetch full events from storage (single source of truth); the scheduler coalesces bursts.
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

      const update = data.sessionUpdate;
      if (update) {
        setSession(prev => prev ? applySessionUpdate(prev, update) : prev);
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
