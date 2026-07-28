"use client";

import { useMemo } from "react";
import { EnrollmentEvent, Session } from "@/types";
import { V1_PHASE_NAMES, V2_PHASE_NAMES, V1_PHASE_ORDER, V2_PHASE_ORDER } from "../utils/phaseConstants";
import { computeWhiteGloveDurations, computeWhiteGloveSplitSequence, groupEventsByPhase } from "../utils/eventHelpers";

interface PhaseGrouping {
  eventsByPhase: Record<string, EnrollmentEvent[]>;
  orderedPhases: string[];
}

export interface UseSessionDerivedDataReturn {
  filteredEvents: EnrollmentEvent[];
  appSummaryStats: {
    totalApps: number; completedApps: number; downloading: number; installing: number;
    installed: number; skipped: number; failed: number; pending: number; likelyStuck: number;
  } | null;
  ntpOffset: { offsetSeconds: number; ntpServer: string | undefined } | null;
  configMgrDetected: {
    ccmVersion: string | undefined; ccmServiceState: string | undefined;
    siteCode: string | undefined; confidenceScore: number;
  } | null;
  isGatherRulesSession: boolean;
  gatherRulesSucceeded: boolean;
  displayStatus: string;
  enrollmentDurationFromEvents: string | null;
  phaseNamesMap: Record<number, string>;
  phaseOrder: string[];
  isSkipUserStatusPage: boolean;
  isWhiteGloveSession: boolean;
  whiteGloveSplitSequence: number;
  preProvEvents: EnrollmentEvent[];
  userEnrollEvents: EnrollmentEvent[];
  whiteGloveDurations: {
    preProvDuration: string | null;
    userEnrollDuration: string | null;
    combinedDuration: string | null;
  };
  eventsByPhase: Record<string, EnrollmentEvent[]>;
  orderedPhases: string[];
  preProvGrouped: PhaseGrouping;
  userEnrollGrouped: PhaseGrouping;
}

/**
 * Pure derivations on top of events + session + severityFilters.
 * All useMemo — no side effects. Keeps page.tsx render body lean and
 * makes these derivations testable in isolation.
 */
export function useSessionDerivedData(
  events: EnrollmentEvent[],
  session: Session | null,
  severityFilters: Set<string>,
): UseSessionDerivedDataReturn {
  // Filter events by severity for the timeline.
  // Trace events are always excluded — they are for backend-side auditing only.
  const filteredEvents = useMemo(
    () => events.filter(e => e.severity !== "Trace" && severityFilters.has(e.severity)),
    [events, severityFilters]
  );

  // Extract latest app_tracking_summary state-breakdown for progress headers.
  // Schema is the flat V1 shape — see AppTrackingSummaryBuilder.cs for the contract.
  // Known legacy limitation: on sessions recorded by agents without the tracker-level
  // historic-replay guard, the snapshot may count apps replayed from a previous
  // enrollment's IME log — accepted; fixed agents keep replayed apps out of the summary
  // at the source (ImeLogTracker AppMutatingActions guard).
  const appSummaryStats = useMemo(() => {
    const summaryEvents = events.filter(e => e.eventType === "app_tracking_summary");
    if (summaryEvents.length === 0) return null;
    const latest = summaryEvents[summaryEvents.length - 1];
    const d = latest.data;
    if (!d) return null;
    return {
      totalApps: parseInt(d.totalApps ?? "0", 10),
      completedApps: parseInt(d.completedApps ?? "0", 10),
      downloading: parseInt(d.downloading ?? "0", 10),
      installing: parseInt(d.installing ?? "0", 10),
      installed: parseInt(d.installed ?? "0", 10),
      skipped: parseInt(d.skipped ?? "0", 10),
      failed: parseInt(d.failed ?? "0", 10),
      pending: parseInt(d.pending ?? "0", 10),
      // c117946b debrief (2026-05-12) — subset of `failed` whose failureType is
      // `esp_apps_timeout`; rendered separately by InstallProgress so the user
      // sees confirmed failures and ESP-timeout-induced presumptions side by side.
      likelyStuck: parseInt(d.likelyStuck ?? "0", 10),
    };
  }, [events]);

  // Extract NTP offset from the first ntp_time_check event (if present)
  const ntpOffset = useMemo(() => {
    const ntpEvent = events.find(e => e.eventType === "ntp_time_check");
    if (!ntpEvent?.data?.offsetSeconds) return null;
    return {
      offsetSeconds: ntpEvent.data.offsetSeconds as number,
      ntpServer: ntpEvent.data.ntpServer as string | undefined,
    };
  }, [events]);

  // Extract ConfigMgr co-management detection (if present)
  // Only show badge when confidence >= 50 (directory-only is too weak).
  // Default to 100 for backward compat with old agent events that lack confidenceScore.
  const configMgrDetected = useMemo(() => {
    const evt = events.find(e => e.eventType === "configmgr_client_detected");
    if (!evt?.data) return null;
    const confidence = (evt.data.confidenceScore as number) ?? 100;
    if (confidence < 50) return null;
    return {
      ccmVersion: evt.data.ccmVersion as string | undefined,
      ccmServiceState: evt.data.ccmServiceState as string | undefined,
      siteCode: evt.data.siteCode as string | undefined,
      confidenceScore: confidence,
    };
  }, [events]);

  const isGatherRulesSession = session?.enrollmentType === "gather_rules";
  // For gather_rules sessions: if the completed event is present, derive status as Succeeded
  // (the backend never sets this status automatically for one-shot gather runs).
  const gatherRulesSucceeded = isGatherRulesSession &&
    events.some(e => e.eventType === "gather_rules_collection_completed");
  const displayStatus = gatherRulesSucceeded ? "Succeeded" : (session?.status ?? "");

  // Calculate enrollment duration from events (first event → enrollment_complete or last event)
  // More accurate than session.durationSeconds which is based on registration StartedAt
  const enrollmentDurationFromEvents = useMemo(() => {
    if (events.length === 0) return null;
    const timestamps = events.map(e => new Date(e.timestamp).getTime());
    const firstEventTime = Math.min(...timestamps);
    const completeEvent = events.find(e => e.eventType === "enrollment_complete");
    const endTime = completeEvent
      ? new Date(completeEvent.timestamp).getTime()
      : Math.max(...timestamps);
    const durationSec = Math.round((endTime - firstEventTime) / 1000);
    if (durationSec < 60) return `${durationSec}s`;
    if (durationSec < 3600) return `${Math.floor(durationSec / 60)}m ${durationSec % 60}s`;
    return `${Math.floor(durationSec / 3600)}h ${Math.floor((durationSec % 3600) / 60)}m`;
  }, [events]);

  const phaseNamesMap = session?.enrollmentType === "v2" ? V2_PHASE_NAMES : V1_PHASE_NAMES;
  const phaseOrder = session?.enrollmentType === "v2" ? V2_PHASE_ORDER : V1_PHASE_ORDER;

  // Detect SkipUserStatusPage from esp_config_detected event
  const isSkipUserStatusPage = useMemo(() => {
    if (session?.enrollmentType === "v2") return false;
    const espConfigEvent = events.find(e => e.eventType === "esp_config_detected");
    if (!espConfigEvent?.data) return false;
    const val = espConfigEvent.data.skipUserStatusPage;
    return val === true || val === "True" || val === "true";
  }, [events, session?.enrollmentType]);

  // Detect WhiteGlove session and find the split point
  const isWhiteGloveSession = session?.isPreProvisioned === true ||
    events.some(e => e.eventType === "whiteglove_complete");

  const whiteGloveSplitSequence = useMemo(() => {
    if (!isWhiteGloveSession) return -1;
    return computeWhiteGloveSplitSequence(events);
  }, [events, isWhiteGloveSession]);

  // For WhiteGlove sessions: split filtered events into pre-provisioning and user-enrollment parts.
  // Events are assigned purely by sequence number — no special-casing for whiteglove_complete.
  // In the race-condition case (Windows writes the WhiteGlove success event after the reboot),
  // whiteglove_complete naturally lands in the user-enrollment part, preserving chronological order.
  // When no split point exists yet (pre-provisioning still in progress), all events belong to Part 1.
  const preProvEvents = useMemo(() => {
    if (!isWhiteGloveSession) return [] as EnrollmentEvent[];
    if (whiteGloveSplitSequence < 0) return filteredEvents;
    return filteredEvents.filter(e => e.sequence <= whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  const userEnrollEvents = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) return [] as EnrollmentEvent[];
    return filteredEvents.filter(e => e.sequence > whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  // Compute per-block durations for WhiteGlove sessions (using unfiltered events for accuracy).
  // Duration 1 = pre-provisioning, Duration 2 = user enrollment, combined = D1 + D2 (pause excluded).
  const whiteGloveDurations = useMemo(() => {
    if (!isWhiteGloveSession) {
      return { preProvDuration: null as string | null, userEnrollDuration: null as string | null, combinedDuration: null as string | null };
    }
    return computeWhiteGloveDurations(events, whiteGloveSplitSequence, session?.startedAt);
  }, [events, isWhiteGloveSession, whiteGloveSplitSequence, session?.startedAt]);

  // Group events by phase — single timeline for normal sessions, two groups for WhiteGlove
  const { eventsByPhase, orderedPhases } = useMemo(() => {
    if (isWhiteGloveSession) return { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] };
    return groupEventsByPhase(filteredEvents, phaseNamesMap, phaseOrder, { preventPhaseRegression: true });
  }, [filteredEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]);

  const preProvGrouped = useMemo(() =>
    isWhiteGloveSession && preProvEvents.length > 0
      ? groupEventsByPhase(preProvEvents, phaseNamesMap, phaseOrder, { preventPhaseRegression: true })
      : { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] },
    [preProvEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]
  );

  const userEnrollGrouped = useMemo(() =>
    isWhiteGloveSession && userEnrollEvents.length > 0
      ? groupEventsByPhase(userEnrollEvents, phaseNamesMap, phaseOrder, { preventPhaseRegression: true })
      : { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] },
    [userEnrollEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]
  );

  return {
    filteredEvents,
    appSummaryStats,
    ntpOffset,
    configMgrDetected,
    isGatherRulesSession: !!isGatherRulesSession,
    gatherRulesSucceeded: !!gatherRulesSucceeded,
    displayStatus,
    enrollmentDurationFromEvents,
    phaseNamesMap,
    phaseOrder,
    isSkipUserStatusPage,
    isWhiteGloveSession,
    whiteGloveSplitSequence,
    preProvEvents,
    userEnrollEvents,
    whiteGloveDurations,
    eventsByPhase,
    orderedPhases,
    preProvGrouped,
    userEnrollGrouped,
  };
}
