"use client";

import { useState, useMemo } from "react";
import { EnrollmentEvent, Session } from "@/types";
import { normalizeEventDataForDisplay, shortenBuildHashInMessage } from "../utils/eventHelpers";
import { getEnrichedOrLookup, formatErrorCode, type ErrorCodeEntry } from "@/utils/errorCodeMap";
import { readTimeProvenance, classifyTimeJump, readClockChangeDeltaMs } from "@/lib/timeProvenance";
import { formatDuration, formatUtcOffset } from "@/lib/formatting";

interface EventTimelineProps {
  filteredEvents: EnrollmentEvent[];
  events: EnrollmentEvent[];
  session: Session | null;
  severityFilters: Set<string>;
  toggleSeverityFilter: (severity: string) => void;
  expandedPhases: Set<string>;
  togglePhase: (phaseName: string) => void;
  timelineExpanded: boolean;
  setTimelineExpanded: (expanded: boolean) => void;
  expandAll: () => void;
  collapseAll: () => void;
  isWhiteGloveSession: boolean;
  whiteGloveSplitSequence: number;
  orderedPhases: string[];
  eventsByPhase: Record<string, EnrollmentEvent[]>;
  preProvGrouped: { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] };
  userEnrollGrouped: { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] };
  userEnrollEvents: EnrollmentEvent[];
  preProvDuration?: string | null;
  userEnrollDuration?: string | null;
  showScriptOutput?: boolean;
  autoScroll?: boolean;
  onAutoScrollToggle?: () => void;
}

export default function EventTimeline({
  filteredEvents,
  events,
  session,
  severityFilters,
  toggleSeverityFilter,
  expandedPhases,
  togglePhase,
  timelineExpanded,
  setTimelineExpanded,
  expandAll,
  collapseAll,
  isWhiteGloveSession,
  orderedPhases,
  eventsByPhase,
  preProvGrouped,
  userEnrollGrouped,
  userEnrollEvents,
  preProvDuration,
  userEnrollDuration,
  showScriptOutput,
  autoScroll,
  onAutoScrollToggle,
}: EventTimelineProps) {
  const [searchQuery, setSearchQuery] = useState("");
  const [rawMode, setRawMode] = useState(false);

  const matchesSearch = useMemo(() => {
    if (!searchQuery.trim()) return null;
    const q = searchQuery.toLowerCase();
    return (event: EnrollmentEvent) =>
      event.eventType?.toLowerCase().includes(q) ||
      event.message?.toLowerCase().includes(q) ||
      event.source?.toLowerCase().includes(q);
  }, [searchQuery]);

  const sortedBySequence = useMemo(() => {
    let filtered = events.filter(e => severityFilters.has(e.severity));
    if (matchesSearch) filtered = filtered.filter(matchesSearch);
    return filtered.sort((a, b) => a.sequence - b.sequence);
  }, [events, severityFilters, matchesSearch]);

  // Ground-truth clock steps recorded by the agent's system timeline watcher — lets the
  // TimeJumpBadge name a backwards display step as an actual OS clock set instead of an
  // unexplained amber anomaly. Collected from ALL events (filters must not hide the cause).
  const clockDeltas = useMemo(
    () => events
      .filter(e => e.eventType === "system_clock_changed")
      .map(e => readClockChangeDeltaMs(e.data))
      .filter((n): n is number => n !== null),
    [events],
  );

  const filterPhaseEvents = (phaseEvents: EnrollmentEvent[]) =>
    matchesSearch ? phaseEvents.filter(matchesSearch) : phaseEvents;

  return (
    <div className="space-y-6">
      {/* Search + Severity filters + Expand/Collapse — shared controls above the timeline(s) */}
      <div className="flex flex-col sm:flex-row sm:items-center gap-2">
        {/* Search bar — full width on mobile, fixed width on desktop */}
        <div className="relative w-full sm:w-48 flex-shrink-0">
          <svg className="absolute left-2 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-gray-400 pointer-events-none" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            type="text"
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            placeholder="Search events..."
            className="w-full pl-7 pr-7 py-1 text-xs border border-gray-300 rounded-full focus:outline-none focus:ring-1 focus:ring-green-500 focus:border-green-500"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery("")}
              className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600"
            >
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
        {/* Severity filters + Expand/Collapse */}
        <div className="flex flex-wrap items-center gap-2 flex-1 min-w-0">
          <span className="text-xs font-medium text-gray-500">Filter:</span>
          {(rawMode ? ["Trace", "Debug", "Info", "Warning", "Error", "Critical"] as const : ["Debug", "Info", "Warning", "Error", "Critical"] as const).map((sev) => {
            const active = severityFilters.has(sev);
            const colors: Record<string, { on: string; off: string }> = {
              Trace:    { on: "bg-purple-100 text-purple-800", off: "bg-gray-50 text-gray-400" },
              Debug:    { on: "bg-gray-200 text-gray-800",  off: "bg-gray-50 text-gray-400" },
              Info:     { on: "bg-blue-100 text-blue-800",  off: "bg-gray-50 text-gray-400" },
              Warning:  { on: "bg-yellow-100 text-yellow-800", off: "bg-gray-50 text-gray-400" },
              Error:    { on: "bg-red-100 text-red-800",    off: "bg-gray-50 text-gray-400" },
              Critical: { on: "bg-red-200 text-red-900",    off: "bg-gray-50 text-gray-400" },
            };
            return (
              <button
                key={sev}
                onClick={() => toggleSeverityFilter(sev)}
                className={`px-2.5 py-1 text-xs font-medium rounded-full transition-colors ${active ? colors[sev].on : colors[sev].off} hover:opacity-80`}
              >
                {sev}
              </button>
            );
          })}
          <span className="text-xs text-gray-400">({filteredEvents.length}/{events.length})</span>
          <div className="flex gap-1.5 ml-auto items-center">
            <button
              onClick={() => setRawMode(!rawMode)}
              className={`text-xs hover:underline mr-1 ${rawMode ? 'text-purple-700 font-semibold' : 'text-gray-400 hover:text-gray-600'}`}
            >
              {rawMode ? '← Timeline' : 'Raw'}
            </button>
            <button
              onClick={expandAll}
              title="Expand All"
              className="flex items-center gap-1 px-2 py-1 text-xs bg-blue-50 text-blue-700 hover:bg-blue-100 rounded transition-colors"
            >
              <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
              </svg>
              <span className="hidden sm:inline">Expand All</span>
            </button>
            <button
              onClick={collapseAll}
              title="Collapse All"
              className="flex items-center gap-1 px-2 py-1 text-xs bg-gray-50 text-gray-700 hover:bg-gray-100 rounded transition-colors"
            >
              <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 15l7-7 7 7" />
              </svg>
              <span className="hidden sm:inline">Collapse All</span>
            </button>
            {onAutoScrollToggle && (
              <button
                onClick={onAutoScrollToggle}
                title={autoScroll ? "Disable auto-scroll" : "Enable auto-scroll — keeps you at the bottom as new events arrive"}
                className={`flex items-center gap-1 px-2 py-1 text-xs rounded transition-colors ${
                  autoScroll
                    ? 'bg-green-100 text-green-700 hover:bg-green-200'
                    : 'bg-gray-50 text-gray-500 hover:bg-gray-100'
                }`}
              >
                {autoScroll && <span className="w-1.5 h-1.5 rounded-full bg-green-500 animate-pulse flex-shrink-0" />}
                <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 14l-7 7m0 0l-7-7m7 7V3" />
                </svg>
                <span className="hidden sm:inline">Live</span>
              </button>
            )}
          </div>
        </div>
      </div>

      {/* Raw mode — compact flat list by sequence (global only) */}
      {rawMode ? (
        <div className="bg-white shadow rounded-lg p-4">
          <h2 className="text-sm font-semibold text-gray-700 mb-3">Raw Events ({sortedBySequence.length})</h2>
          <div className="divide-y divide-gray-100">
            {sortedBySequence.map((ev, i) => (
              <RawEventRow
                key={ev.eventId || `${ev.sessionId}-${ev.sequence}`}
                event={ev}
                prevEvent={i > 0 ? sortedBySequence[i - 1] : null}
                clockDeltas={clockDeltas}
              />
            ))}
          </div>
        </div>
      ) : isWhiteGloveSession ? (
        <>
          {/* Pre-Provisioning Part */}
          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex items-center gap-3 mb-6">
              <h2 className="text-xl font-semibold text-gray-900">Pre-Provisioning Part</h2>
              <span className="px-2 py-0.5 text-xs font-semibold rounded-full bg-amber-100 text-amber-800">WhiteGlove</span>
              {preProvDuration && (
                <span className="text-sm text-gray-500">{preProvDuration}</span>
              )}
              {userEnrollEvents.length > 0 && (
                <a href="#user-enrollment-part" className="text-sm text-blue-500 hover:text-blue-700 ml-auto">
                  Jump to User Enrollment
                </a>
              )}
            </div>
            {preProvGrouped.orderedPhases.length === 0 ? (
              <div className="text-gray-500 text-center py-8">No events found.</div>
            ) : (
              <div className="space-y-8">
                {preProvGrouped.orderedPhases.map((phaseName) => (
                  <PhaseSection
                    key={`pre-${phaseName}`}
                    phaseName={phaseName}
                    events={filterPhaseEvents(preProvGrouped.eventsByPhase[phaseName])}
                    isExpanded={expandedPhases.has(`pre-${phaseName}`)}
                    onToggle={() => togglePhase(`pre-${phaseName}`)}
                    showScriptOutput={showScriptOutput}
                    borderColor="border-amber-400"
                    clockDeltas={clockDeltas}
                  />
                ))}
              </div>
            )}
          </div>

          {/* Visual separator between the two WhiteGlove parts */}
          {userEnrollEvents.length > 0 && (
            <div className="flex items-center gap-4 px-4">
              <div className="flex-1 border-t-2 border-dashed border-gray-300"></div>
              <span className="text-xs text-gray-400 font-medium whitespace-nowrap">Device sealed / powered off</span>
              <div className="flex-1 border-t-2 border-dashed border-gray-300"></div>
            </div>
          )}

          {/* User Enrollment Part */}
          {userEnrollEvents.length > 0 ? (
            <div id="user-enrollment-part" className="bg-white shadow rounded-lg p-6 scroll-mt-4">
              <div className="flex items-center gap-3 mb-6">
                <h2 className="text-xl font-semibold text-gray-900">User Enrollment Part</h2>
                <span className="px-2 py-0.5 text-xs font-semibold rounded-full bg-blue-100 text-blue-800">Resumed</span>
                {userEnrollDuration && (
                  <span className="text-sm text-gray-500">{userEnrollDuration}</span>
                )}
              </div>
              {userEnrollGrouped.orderedPhases.length === 0 ? (
                <div className="text-gray-500 text-center py-8">No events found.</div>
              ) : (
                <div className="space-y-8">
                  {userEnrollGrouped.orderedPhases.map((phaseName) => (
                    <PhaseSection
                      key={`user-${phaseName}`}
                      phaseName={phaseName}
                      events={filterPhaseEvents(userEnrollGrouped.eventsByPhase[phaseName])}
                      isExpanded={expandedPhases.has(`user-${phaseName}`)}
                      onToggle={() => togglePhase(`user-${phaseName}`)}
                      showScriptOutput={showScriptOutput}
                      clockDeltas={clockDeltas}
                    />
                  ))}
                </div>
              )}
            </div>
          ) : session?.status === 'Pending' ? (
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-6 text-center">
              <p className="text-amber-800 font-medium mb-1">Awaiting User Enrollment</p>
              <p className="text-amber-600 text-sm">
                Pre-provisioning is complete. The timeline will continue when the user powers on the device.
              </p>
            </div>
          ) : session?.status === 'Stalled' ? (
            <div className="bg-orange-50 border border-orange-200 rounded-lg p-6 text-center">
              <p className="text-orange-800 font-medium mb-1">Session Stalled</p>
              <p className="text-orange-600 text-sm">
                {session.failureReason || 'No progress detected. The session will heal automatically when new events arrive, or expire after the timeout window.'}
              </p>
            </div>
          ) : null}
        </>
      ) : (
        /* Original single-timeline card */
        <div className="bg-white shadow rounded-lg p-6">
          <button
            onClick={() => setTimelineExpanded(!timelineExpanded)}
            className="flex items-center justify-between w-full text-left mb-4"
          >
            <h2 className="text-xl font-semibold text-gray-900">Event Timeline</h2>
            <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${timelineExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
            </svg>
          </button>
          {timelineExpanded && (
            <>
              {orderedPhases.length === 0 ? (
                <div className="text-gray-500 text-center py-8">No events found for this session.</div>
              ) : (
                <div className="space-y-8">
                  {orderedPhases.map((phaseName) => (
                    <PhaseSection
                      key={phaseName}
                      phaseName={phaseName}
                      events={filterPhaseEvents(eventsByPhase[phaseName])}
                      isExpanded={expandedPhases.has(phaseName)}
                      onToggle={() => togglePhase(phaseName)}
                      showScriptOutput={showScriptOutput}
                      clockDeltas={clockDeltas}
                    />
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}

function PhaseSection({
  phaseName,
  events,
  isExpanded,
  onToggle,
  showScriptOutput,
  borderColor = 'border-blue-500',
  clockDeltas,
}: {
  phaseName: string;
  events: EnrollmentEvent[];
  isExpanded: boolean;
  onToggle: () => void;
  showScriptOutput?: boolean;
  borderColor?: string;
  clockDeltas?: number[];
}) {
  return (
    <div id={`phase-${phaseName.replace(/[^a-zA-Z0-9]/g, '-')}`} className={`border-l-4 ${borderColor} pl-4`}>
      <button
        onClick={onToggle}
        className="flex items-center justify-between w-full text-left mb-3 group"
      >
        <h3 className="text-lg font-semibold text-gray-900 group-hover:text-green-600">
          {phaseName} ({events.length} events)
        </h3>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {isExpanded && (
        <div className="space-y-3">
          {events.map((event, i) => (
            <EventRow
              key={event.eventId || `${event.sessionId}-${event.sequence}`}
              event={event}
              showScriptOutput={showScriptOutput}
              prevEvent={i > 0 ? events[i - 1] : null}
              clockDeltas={clockDeltas}
            />
          ))}
        </div>
      )}
    </div>
  );
}

// Backfilled events (ModernDeploymentTracker error backfill): pre-agent event-log records
// replayed at agent start carry backfilled=true plus the original event-log timeCreated —
// the event's own timestamp is agent emission time, so without this the whole block renders
// with one identical (wrong) time and full Error severity, reading like a live failure.
function getBackfillInfo(event: EnrollmentEvent): { isBackfilled: boolean; recordedAt: Date | null } {
  const raw = event.data?.backfilled ?? event.data?.Backfilled;
  const isBackfilled = raw === true || raw === "true" || raw === "True";
  if (!isBackfilled) return { isBackfilled: false, recordedAt: null };
  const t = event.data?.timeCreated ?? event.data?.TimeCreated;
  const recordedAt = typeof t === "string" && t !== "" && !isNaN(Date.parse(t)) ? new Date(t) : null;
  return { isBackfilled, recordedAt };
}

const BACKFILL_TOOLTIP =
  "Recorded by Windows before the agent started — replayed from the event log for visibility. " +
  "If enrollment progressed afterwards, this error was already resolved by a retry. " +
  "The timestamp shown is the original event-log time.";

// Display timestamp of a row: backfilled events show the original event-log time.
function getDisplayTime(event: EnrollmentEvent): Date {
  return getBackfillInfo(event).recordedAt ?? new Date(event.timestamp);
}

// Rows render time-of-day only, so a multi-hour/multi-day gap to the previous row
// (and the date change it implies) is invisible without expanding event details.
// Shown on the row AFTER the gap; hover reveals the full date.
function GapBadge({ prevTime, eventTime }: { prevTime: Date | null; eventTime: Date }) {
  if (!prevTime) return null;
  const gapMs = eventTime.getTime() - prevTime.getTime();
  if (gapMs < 60 * 60 * 1000) return null;
  const label = gapMs >= 48 * 60 * 60 * 1000
    ? `+${Math.round(gapMs / (24 * 60 * 60 * 1000))}d`
    : `+${Math.round(gapMs / (60 * 60 * 1000))}h`;
  return (
    <span
      className="px-1.5 py-0.5 rounded text-xs font-medium bg-amber-50 text-amber-700 whitespace-nowrap flex-shrink-0"
      title={`${label} after the previous event — ${eventTime.toLocaleString()}`}
    >
      ⏱ {label}
    </span>
  );
}

// A displayed time stepping BACKWARDS within the sequence order is information, not a
// rendering bug (P13): rows are ordered by the clock-immune sequence counter, while the
// displayed time is corrected UTC — a backdated log line or an era-mixed log makes it
// step back. Shown on the row AFTER the jump (GapBadge convention) and only at ≥5 min
// (BACKWARD_JUMP_THRESHOLD_MS), so normal interleaved-writer jitter (≤2 min by the
// agent's grid tolerance) never renders a badge. Known causes get the informational sky
// tier; an unexplained backwards step gets amber.
const TIME_JUMP_ORDER_NOTE = "Events are shown in true write order (sequence).";

function TimeJumpBadge({ prevEvent, event, clockDeltas }: { prevEvent?: EnrollmentEvent | null; event: EnrollmentEvent; clockDeltas?: number[] }) {
  if (!prevEvent) return null;
  const jump = classifyTimeJump(
    { displayTime: getDisplayTime(prevEvent), provenance: readTimeProvenance(prevEvent.data) },
    { displayTime: getDisplayTime(event), provenance: readTimeProvenance(event.data) },
    undefined,
    clockDeltas,
  );
  if (!jump) return null;

  const label = jump.deltaMs >= 48 * 60 * 60 * 1000
    ? `−${Math.round(jump.deltaMs / (24 * 60 * 60 * 1000))}d`
    : jump.deltaMs >= 60 * 60 * 1000
      ? `−${Math.round(jump.deltaMs / (60 * 60 * 1000))}h`
      : `−${Math.round(jump.deltaMs / (60 * 1000))}m`;
  const human = formatDuration(jump.deltaMs / 1000);
  const times = `(${getDisplayTime(prevEvent).toLocaleTimeString()} → ${getDisplayTime(event).toLocaleTimeString()})`;

  const title =
    jump.cause === "clock-set"
      ? `Time moved backwards by ${human} vs the previous event ${times}. The OS clock was actually set back — a system_clock_changed event in this session records a matching step (ground truth, not an ordering artifact). ${TIME_JUMP_ORDER_NOTE}`
      : jump.cause === "era-offset"
      ? `Time moved backwards by ${human} vs the previous event ${times}. The two log lines were corrected with different UTC offsets (era-mixed log) — the jump reflects the offset change, not reordering. ${TIME_JUMP_ORDER_NOTE}`
      : jump.cause === "derived-timestamp"
        ? `Backdated log line: written ${human} before the previous event's displayed time ${times}. Its own timestamp was unusable, so the agent's clock time is shown. ${TIME_JUMP_ORDER_NOTE}`
        : jump.cause === "rejected-source"
          ? `Time moved backwards by ${human} vs the previous event ${times}. The line's own timestamp was rejected by the staleness clamp; the agent's clock time is shown instead. ${TIME_JUMP_ORDER_NOTE}`
          : `Time moved backwards by ${human} vs the previous event ${times}. ${TIME_JUMP_ORDER_NOTE} Displayed times are corrected UTC and can step backwards when the source log mixes clock eras.`;

  const color = jump.cause ? "bg-sky-100 text-sky-800" : "bg-amber-100 text-amber-800";
  return (
    <span className={`px-1.5 py-0.5 rounded text-xs font-medium whitespace-nowrap flex-shrink-0 ${color}`} title={title}>
      ↩ {label}
    </span>
  );
}

const CLAMPED_TOOLTIP_PREFIX =
  "The device-reported timestamp was outside the accepted range and was clamped to server receive time on ingest.";

function EventRow({ event, showScriptOutput, prevEvent, clockDeltas }: { event: EnrollmentEvent; showScriptOutput?: boolean; prevEvent?: EnrollmentEvent | null; clockDeltas?: number[] }) {
  const prevDisplayTime = prevEvent ? getDisplayTime(prevEvent) : null;
  const [showDetails, setShowDetails] = useState(false);
  const [showRaw, setShowRaw] = useState(false);
  const [copied, setCopied] = useState(false);
  const [copiedDetail, setCopiedDetail] = useState(false);
  // Collapsed by default: the same values sit in the raw JSON dump — this labeled view is
  // an opt-in dive, it must not inflate the metadata block (user decision 2026-08-20).
  const [showProvenance, setShowProvenance] = useState(false);
  const timeProvenance = useMemo(() => readTimeProvenance(event.data), [event.data]);
  const rawDetailData = useMemo(() => normalizeEventDataForDisplay(event.data), [event.data]);

  // Filter stdout from script events when showScriptOutput is false.
  // script_started has no stdout/stderr (live indicator only) but include it so the
  // timeline still applies the same script-event styling/iconography.
  const isScriptEvent = event.eventType === "script_started"
    || event.eventType === "script_completed"
    || event.eventType === "script_failed";
  const detailData = useMemo(() => {
    if (!rawDetailData || !isScriptEvent || showScriptOutput !== false) return rawDetailData;
    const filtered = { ...rawDetailData };
    if ("stdout" in filtered) {
      delete filtered.stdout;
      filtered._stdoutHidden = "stdout hidden by admin setting";
    }
    return filtered;
  }, [rawDetailData, isScriptEvent, showScriptOutput]);

  // Detect truncated data: backend sets _rawDataJson when DataJson could not be parsed
  const rawDataJson = detailData?._rawDataJson as string | undefined;
  const isTruncated = typeof rawDataJson === "string";

  // Gather rule console output detection — use source, not eventType,
  // because users can name gather rule event types freely.
  const isGatherEvent = event.source === "GatherRuleExecutor";
  // Read output/command from raw event.data (not normalized detailData) — these fields contain
  // free-form console text that must not be parsed even if it happens to look like JSON.
  const gatherOutputRaw = isGatherEvent
    ? (event.data?.output ?? event.data?.Output) ?? null
    : null;
  const gatherOutput: string | null = gatherOutputRaw == null
    ? null
    : typeof gatherOutputRaw === 'string'
      ? gatherOutputRaw
      : JSON.stringify(gatherOutputRaw, null, 2);
  const gatherCommand = isGatherEvent
    ? ((event.data?.command ?? event.data?.Command) as string | null | undefined) ?? null
    : null;
  const gatherExitCode = isGatherEvent
    ? ((detailData?.exit_code ?? detailData?.exitCode) as number | null | undefined) ?? null
    : null;
  const hasGatherOutput = gatherOutput != null && gatherOutput !== "";
  const formattedOutput = hasGatherOutput
    ? gatherOutput.replace(/\r\n/g, "\n").replace(/\r/g, "\n")
    : null;

  const copyDetailContent = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedDetail(true);
      setTimeout(() => setCopiedDetail(false), 1400);
    } catch (err) {
      console.error('Failed to copy detail content:', err);
    }
  };

  const copyEventId = async () => {
    try {
      await navigator.clipboard.writeText(event.eventId);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (err) {
      console.error('Failed to copy EventID:', err);
    }
  };

  const hasDetails = true; // Every event has at least the metadata block

  const { isBackfilled, recordedAt } = getBackfillInfo(event);

  return (
    <div id={`event-${event.eventId}`} className="bg-gray-50 rounded-lg p-3 hover:bg-gray-100 transition-colors">
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3">
            <span className="text-xs text-gray-500 font-mono">
              {(recordedAt ?? new Date(event.timestamp)).toLocaleTimeString()}
              {recordedAt && (
                <span className="text-gray-400 font-sans"> (reported {new Date(event.timestamp).toLocaleTimeString()})</span>
              )}
            </span>
            <GapBadge prevTime={prevDisplayTime ?? null} eventTime={recordedAt ?? new Date(event.timestamp)} />
            <TimeJumpBadge prevEvent={prevEvent} event={event} clockDeltas={clockDeltas} />
            <SeverityBadge severity={event.severity} />
            {isBackfilled && (
              <span
                className="px-2 py-0.5 rounded text-xs font-medium bg-slate-200 text-slate-600"
                title={BACKFILL_TOOLTIP}
              >
                ⟲ Backfilled
              </span>
            )}
            {event.timestampClamped && (
              <span
                className="px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800"
                title={`${CLAMPED_TOOLTIP_PREFIX}${event.originalTimestamp ? ` Original device timestamp: ${event.originalTimestamp}.` : ""}`}
              >
                ⧖ Clamped
              </span>
            )}
            <span className="text-sm font-medium text-gray-900">{event.eventType}</span>
          </div>
          <p className="mt-1 text-sm text-gray-600" title={event.message || undefined}>{shortenBuildHashInMessage(event.message)}</p>
          {/* Exit code / HRESULT badge for app install events */}
          {(event.eventType === "app_install_failed" || event.eventType === "app_install_completed") && (() => {
            const ec = (event.data?.exitCode ?? event.data?.exit_code) as string | number | undefined;
            const hr = (event.data?.hresultFromWin32 ?? event.data?.hresult_from_win32) as string | number | undefined;
            const hasNonZero = (ec && String(ec) !== "0") || (hr && String(hr) !== "0");
            if (!hasNonZero) return null;
            // Prefer backend-enriched *Info sibling, fall back to local lookup for older
            // responses that pre-date the backend ErrorCodeEnricher.
            const ecEntry = ec ? getEnrichedOrLookup(event.data?.exitCodeInfo as ErrorCodeEntry | undefined, String(ec)) : null;
            const hrEntry = hr ? getEnrichedOrLookup(event.data?.hresultFromWin32Info as ErrorCodeEntry | undefined, String(hr)) : null;
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                {ec && String(ec) !== "0" && (
                  <>
                    <span className="px-1.5 py-0.5 rounded bg-red-100 text-red-800 font-mono font-medium">
                      Exit: {formatErrorCode(String(ec))}
                    </span>
                    {ecEntry && (
                      <span className="text-red-600" title={`${ecEntry.source} (${ecEntry.confidence} confidence)`}>
                        {ecEntry.description}
                      </span>
                    )}
                  </>
                )}
                {hr && String(hr) !== "0" && (
                  <>
                    <span className="px-1.5 py-0.5 rounded bg-red-100 text-red-800 font-mono font-medium">
                      HRESULT: {formatErrorCode(String(hr))}
                    </span>
                    {hrEntry && (
                      <span className="text-red-600" title={`${hrEntry.source} (${hrEntry.confidence} confidence)`}>
                        {hrEntry.description}
                      </span>
                    )}
                  </>
                )}
              </div>
            );
          })()}
          {/* HRESULT badge for ESP failures (enrollment_failed via esp_terminal_failure,
              esp_provisioning_status failed-subcategory, esp_failure_advisory via ContinueAnyway
              defang). The HRESULT is extracted from the ESP registry statusText by the agent
              (e.g. "Apps (0x87d1041c)") and surfaced as top-level event data so the UI can
              render it without parsing nested text. */}
          {(event.eventType === "enrollment_failed" || event.eventType === "esp_provisioning_status" || event.eventType === "esp_failure_advisory" || event.eventType === "esp_appx_failure_analysis") && (() => {
            const code = (event.data?.errorCode ?? event.data?.failedSubcategoryErrorCode ?? event.data?.espErrorCode) as string | number | undefined;
            const sub = (event.data?.failedSubcategory ?? event.data?.failedSubcategories) as string | undefined;
            const likelyCulpritApps = event.data?.likelyCulpritApps as string | undefined;
            const isAdvisory = event.eventType === "esp_failure_advisory";
            if (!code && !isAdvisory) return null;
            const codeStr = code ? String(code) : null;
            const entry = codeStr ? getEnrichedOrLookup(event.data?.errorCodeInfo as ErrorCodeEntry | undefined, codeStr) : null;
            // Advisory path uses warning-color palette (amber); the device continued past the
            // failure via ContinueAnyway, so this is not a hard error. PR1 Session 4fa5a2d4.
            const badgeBg = isAdvisory ? "bg-amber-100 text-amber-800" : "bg-red-100 text-red-800";
            const descColor = isAdvisory ? "text-amber-700" : "text-red-600";
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                {isAdvisory && (
                  <span
                    className="px-1.5 py-0.5 rounded bg-amber-100 text-amber-800 font-medium"
                    title="ESP reported a subcategory failure but the device had already progressed to AccountSetup — the agent continues monitoring instead of declaring the session failed."
                  >
                    Advisory (ContinueAnyway)
                  </span>
                )}
                {codeStr && (
                  <span className={`px-1.5 py-0.5 rounded ${badgeBg} font-mono font-medium`}>
                    HRESULT: {formatErrorCode(codeStr)}
                  </span>
                )}
                {entry && (
                  <span className={descColor} title={`${entry.source} (${entry.confidence} confidence)`}>
                    {entry.description}
                  </span>
                )}
                {sub && (
                  <span className="text-gray-500">
                    subcategory: <span className="font-mono">{String(sub)}</span>
                  </span>
                )}
                {likelyCulpritApps && (
                  <span
                    className="px-1.5 py-0.5 rounded bg-orange-100 text-orange-800 font-medium"
                    title="Tracked app(s) ESP most likely failed on — never-started apps ranked first (snapshot at failure time)."
                  >
                    Likely app: {String(likelyCulpritApps)}
                  </span>
                )}
              </div>
            );
          })()}
          {/* Recovery-story badges (session 4910a5a5): a terminally reported ESP failure that
              later un-happened — the user retried ("Try again"), the failed step re-ran and
              recovered. Green/teal palette so the timeline visually closes the earlier red/amber
              failure arc. */}
          {(event.eventType === "esp_failure_retry_detected" || event.eventType === "esp_failure_recovered" || event.eventType === "esp_failure_advisory_resolved") && (() => {
            const isRetry = event.eventType === "esp_failure_retry_detected";
            const label = isRetry ? "User retry" : "Recovered";
            const badgeCls = isRetry ? "bg-sky-100 text-sky-800" : "bg-emerald-100 text-emerald-800";
            const cat = event.data?.category as string | undefined;
            const sub = event.data?.subcategory ?? event.data?.failedSubcategory;
            const mins = event.data?.minutesSinceFailure ?? event.data?.minutesSinceAdvisory;
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                <span
                  className={`px-1.5 py-0.5 rounded ${badgeCls} font-medium`}
                  title={isRetry
                    ? "The failed ESP step left the failed state — consistent with the user pressing 'Try again' on the ESP failure page."
                    : "The previously failed ESP category completed successfully — the earlier failure no longer applies."}
                >
                  {label}
                </span>
                {cat && (
                  <span className="text-gray-500">
                    {String(cat)}{sub ? <> / <span className="font-mono">{String(sub)}</span></> : null}
                  </span>
                )}
                {mins !== undefined && mins !== null && (
                  <span className="text-gray-500">{String(mins)} min after the failure</span>
                )}
              </div>
            );
          })()}
          {/* Ground-truth clock step (system timeline watcher): the OS clock was set from
              oldTime to newTime — payload times are authoritative. Amber at ≥5 min (can
              invalidate token/cert windows and visibly reorders the timeline), sky below. */}
          {event.eventType === "system_clock_changed" && (() => {
            const deltaMs = readClockChangeDeltaMs(event.data);
            if (deltaMs === null) return null;
            const forward = deltaMs >= 0;
            const isLarge = Math.abs(deltaMs) >= 5 * 60 * 1000;
            const reasonText = event.data?.reasonText as string | undefined;
            const processName = event.data?.processName as string | undefined;
            const processLeaf = processName ? processName.split("\\").pop() : null;
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                <span
                  className={`px-1.5 py-0.5 rounded font-mono font-medium ${isLarge ? "bg-amber-100 text-amber-800" : "bg-sky-100 text-sky-800"}`}
                  title={`The system clock was stepped ${forward ? "forward" : "backward"} by ${formatDuration(Math.abs(deltaMs) / 1000)}. oldTime/newTime in the event details are the authoritative instants.`}
                >
                  ⏱ {forward ? "+" : "−"}{formatDuration(Math.abs(deltaMs) / 1000)}
                </span>
                {reasonText && <span className="text-gray-500">{reasonText === "application_set" ? "set by a process" : reasonText === "hardware_clock_sync" ? "hardware clock sync" : reasonText}</span>}
                {processLeaf && <span className="text-gray-500 font-mono">{processLeaf}</span>}
              </div>
            );
          })()}
          {/* Completed sleep episode (emitted retroactively at wake): enteredAt/exitedAt in the
              payload are the authoritative bounds — the gap in the surrounding timeline is this
              episode, not missing telemetry. */}
          {event.eventType === "system_sleep_episode" && (() => {
            const kind = event.data?.kind as string | undefined;
            const kindLabel = kind === "modern_standby" ? "Modern Standby" : kind === "hibernate" ? "Hibernate" : "Sleep";
            const durationSeconds = Number(event.data?.durationSeconds);
            const wakeText = event.data?.wakeSourceText as string | undefined;
            const onAc = event.data?.onAcPower;
            const onAcLabel = onAc === true || onAc === "true" ? "on AC" : onAc === false || onAc === "false" ? "on battery" : null;
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                <span
                  className="px-1.5 py-0.5 rounded bg-indigo-100 text-indigo-800 font-medium"
                  title="The device completed a sleep episode; this event is recorded at wake. enteredAt/exitedAt in the details are the episode bounds — the surrounding timeline gap is explained by this pause."
                >
                  🌙 {kindLabel}
                </span>
                {Number.isFinite(durationSeconds) && durationSeconds > 0 && (
                  <span className="text-gray-600 font-medium">{formatDuration(durationSeconds)}</span>
                )}
                {wakeText && <span className="text-gray-500">wake: {wakeText}</span>}
                {onAcLabel && <span className="text-gray-500">{onAcLabel}</span>}
              </div>
            );
          })()}
          {/* Live power-state change (power watcher, DEV-009/010): make the transition readable
              without expanding the details. */}
          {event.eventType === "power_state_change" && (() => {
            const transition = event.data?.transition as string | undefined;
            const batteryPercent = event.data?.batteryPercent;
            const threshold = event.data?.thresholdPercent;
            const chip = transition === "ac_to_battery"
              ? { label: "AC → battery", cls: "bg-amber-100 text-amber-800" }
              : transition === "battery_to_ac"
                ? { label: "Battery → AC", cls: "bg-emerald-100 text-emerald-800" }
                : transition === "threshold_crossed"
                  ? { label: `Battery below ${String(threshold ?? "?")}%`, cls: String(threshold) === "15" ? "bg-red-100 text-red-800" : "bg-amber-100 text-amber-800" }
                  : null;
            if (!chip) return null;
            return (
              <div className="mt-1 flex flex-wrap items-center gap-2 text-xs">
                <span className={`px-1.5 py-0.5 rounded font-medium ${chip.cls}`}>🔋 {chip.label}</span>
                {batteryPercent !== undefined && batteryPercent !== null && String(batteryPercent) !== "unknown" && (
                  <span className="text-gray-500">{String(batteryPercent)}% charge</span>
                )}
              </div>
            );
          })()}
          <div className="mt-1 flex items-center gap-3 text-xs text-gray-500">
            <span>Source: {event.source}</span>
            <span>Seq: {event.sequence}</span>
          </div>
        </div>
        {hasDetails && (
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-xs text-green-700 hover:text-green-800 ml-4 flex-shrink-0"
          >
            {showDetails ? 'Hide' : hasGatherOutput ? 'Output' : 'Details'}
          </button>
        )}
      </div>

      {/* Event metadata block — always shown when details are expanded */}
      {showDetails && (() => {
        const receivedDelta = event.receivedAt
          ? Math.round((new Date(event.receivedAt).getTime() - new Date(event.timestamp).getTime()) / 1000 * 10) / 10
          : null;
        const hasPhase = event.phaseName && event.phaseName !== 'Unknown';
        return (
          <div className="mt-2 border border-gray-200 rounded-md px-3 py-2 text-xs text-gray-600 relative group/meta">
            <button
              type="button"
              onClick={copyEventId}
              title={copied ? 'Copied!' : 'Copy EventId'}
              className="absolute top-1.5 right-1.5 inline-flex items-center justify-center w-5 h-5 rounded border border-gray-200 bg-white text-gray-400 opacity-0 group-hover/meta:opacity-100 focus:opacity-100 hover:bg-gray-50 hover:text-green-600 transition-opacity"
            >
              {copied ? (
                <svg className="w-3 h-3 text-green-500" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.2 7.2a1 1 0 01-1.415 0l-3.2-3.2a1 1 0 111.414-1.42l2.493 2.494 6.493-6.494a1 1 0 011.415 0z" clipRule="evenodd" />
                </svg>
              ) : (
                <svg className="w-3 h-3" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M6 2a2 2 0 00-2 2v8a2 2 0 002 2h1v-2H6V4h7v1h2V4a2 2 0 00-2-2H6z" />
                  <path d="M9 7a2 2 0 00-2 2v7a2 2 0 002 2h7a2 2 0 002-2V9a2 2 0 00-2-2H9z" />
                </svg>
              )}
            </button>
            <div className="flex">
              <span className="w-16 flex-shrink-0 text-gray-400">EventId</span>
              <span className="font-mono">{event.eventId}</span>
            </div>
            {recordedAt && (
              <div className="flex mt-0.5">
                <span className="w-16 flex-shrink-0 text-gray-400">Recorded</span>
                <span className="font-mono">
                  {recordedAt.toISOString().replace('T', ' ').replace('Z', '')}
                  <span className="text-gray-400 ml-1" title={BACKFILL_TOOLTIP}>(event log, pre-agent)</span>
                </span>
              </div>
            )}
            <div className="flex mt-0.5">
              <span className="w-16 flex-shrink-0 text-gray-400">Created</span>
              <span className="font-mono">
                {event.timestamp}
                {(timeProvenance || event.timestampClamped) && (
                  <button
                    type="button"
                    onClick={() => setShowProvenance(!showProvenance)}
                    className="ml-2 font-sans text-gray-400 hover:text-green-600"
                    title="How this timestamp was produced (offset, origin, raw log time)"
                  >
                    {showProvenance ? "▾ time provenance" : "▸ time provenance"}
                  </button>
                )}
              </span>
            </div>
            {showProvenance && (timeProvenance || event.timestampClamped) && (
              <TimeProvenanceRows event={event} provenance={timeProvenance} />
            )}
            <div className="flex mt-0.5">
              <span className="w-16 flex-shrink-0 text-gray-400">Received</span>
              <span className="font-mono">
                {event.receivedAt
                  ? new Date(event.receivedAt).toISOString().replace('T', ' ').replace('Z', '')
                  : '—'}
                {receivedDelta !== null && (
                  receivedDelta < -5 ? (
                    <span className="text-amber-500 ml-1" title="Device clock is ahead of server clock">(clock skew)</span>
                  ) : receivedDelta < 0 ? (
                    <span className="text-gray-400 ml-1" title="Minor clock skew between device and server">(+0s)</span>
                  ) : (
                    <span className="text-gray-400 ml-1">(+{receivedDelta}s)</span>
                  )
                )}
              </span>
            </div>
            {hasPhase && (
              <div className="flex mt-0.5">
                <span className="w-16 flex-shrink-0 text-gray-400">Phase</span>
                <span>{event.phaseName}</span>
              </div>
            )}
          </div>
        );
      })()}

      {/* Gather rule: terminal-style output block */}
      {showDetails && hasGatherOutput && (
        <div className="mt-3">
          {gatherCommand && (
            <div className="flex items-center gap-1.5 mb-1.5 text-xs font-mono text-gray-600">
              <span className="text-gray-400 select-none">$</span>
              <span>{gatherCommand}</span>
            </div>
          )}
          <div className="bg-gray-900 rounded-lg overflow-hidden relative group/detail">
            <button
              type="button"
              onClick={() => copyDetailContent(formattedOutput!)}
              title={copiedDetail ? "Copied!" : "Copy to clipboard"}
              className="absolute top-1.5 right-1.5 inline-flex items-center justify-center w-5 h-5 rounded border border-gray-600 bg-gray-800 text-gray-400 opacity-0 group-hover/detail:opacity-100 focus:opacity-100 hover:bg-gray-700 hover:text-gray-200 transition-opacity z-10"
            >
              {copiedDetail ? (
                <svg className="w-3.5 h-3.5 text-green-400" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.2 7.2a1 1 0 01-1.415 0l-3.2-3.2a1 1 0 111.414-1.42l2.493 2.494 6.493-6.494a1 1 0 011.415 0z" clipRule="evenodd" />
                </svg>
              ) : (
                <svg className="w-3.5 h-3.5" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M6 2a2 2 0 00-2 2v8a2 2 0 002 2h1v-2H6V4h7v1h2V4a2 2 0 00-2-2H6z" />
                  <path d="M9 7a2 2 0 00-2 2v7a2 2 0 002 2h7a2 2 0 002-2V9a2 2 0 00-2-2H9z" />
                </svg>
              )}
            </button>
            <div className="px-3 py-2 max-h-96 overflow-y-auto overflow-x-auto">
              <pre className="text-xs text-gray-100 font-mono whitespace-pre">{formattedOutput}</pre>
            </div>
          </div>
          <div className="mt-1.5 flex items-center justify-between">
            {gatherExitCode !== null ? (
              <span className={`text-xs font-mono ${gatherExitCode === 0 ? 'text-green-600' : 'text-red-600'}`}>
                exit {gatherExitCode}
              </span>
            ) : <span />}
            <button
              onClick={() => setShowRaw(!showRaw)}
              className="text-xs text-gray-400 hover:text-gray-600"
            >
              {showRaw ? 'hide raw' : 'raw JSON'}
            </button>
          </div>
          {showRaw && (
            <div className="mt-2 p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
              <pre>{JSON.stringify(detailData, null, 2)}</pre>
            </div>
          )}
        </div>
      )}

      {/* Truncated data: show raw string as-is when JSON parsing failed */}
      {showDetails && isTruncated && (
        <div className="mt-3">
          <div className="flex items-center gap-2 mb-1.5">
            <span className="text-xs font-medium text-amber-600">Data truncated (exceeded 64KB storage limit)</span>
          </div>
          <div className="p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto max-h-96 overflow-y-auto relative group/detail">
            <button
              type="button"
              onClick={() => copyDetailContent(rawDataJson!)}
              title={copiedDetail ? "Copied!" : "Copy to clipboard"}
              className="absolute top-1.5 right-1.5 inline-flex items-center justify-center w-5 h-5 rounded border border-gray-600 bg-gray-800 text-gray-400 opacity-0 group-hover/detail:opacity-100 focus:opacity-100 hover:bg-gray-700 hover:text-gray-200 transition-opacity z-10"
            >
              {copiedDetail ? (
                <svg className="w-3.5 h-3.5 text-green-400" viewBox="0 0 20 20" fill="currentColor">
                  <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.2 7.2a1 1 0 01-1.415 0l-3.2-3.2a1 1 0 111.414-1.42l2.493 2.494 6.493-6.494a1 1 0 011.415 0z" clipRule="evenodd" />
                </svg>
              ) : (
                <svg className="w-3.5 h-3.5" viewBox="0 0 20 20" fill="currentColor">
                  <path d="M6 2a2 2 0 00-2 2v8a2 2 0 002 2h1v-2H6V4h7v1h2V4a2 2 0 00-2-2H6z" />
                  <path d="M9 7a2 2 0 00-2 2v7a2 2 0 002 2h7a2 2 0 002-2V9a2 2 0 00-2-2H9z" />
                </svg>
              )}
            </button>
            <pre className="whitespace-pre-wrap break-words">{rawDataJson}</pre>
          </div>
        </div>
      )}

      {/* Non-gather (or gather without output): raw JSON details */}
      {showDetails && !isTruncated && !hasGatherOutput && detailData && (
        <div className="mt-3 p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto relative group/detail">
          <button
            type="button"
            onClick={() => copyDetailContent(JSON.stringify(detailData, null, 2))}
            title={copiedDetail ? "Copied!" : "Copy to clipboard"}
            className="absolute top-1.5 right-1.5 inline-flex items-center justify-center w-5 h-5 rounded border border-gray-600 bg-gray-800 text-gray-400 opacity-0 group-hover/detail:opacity-100 focus:opacity-100 hover:bg-gray-700 hover:text-gray-200 transition-opacity z-10"
          >
            {copiedDetail ? (
              <svg className="w-3.5 h-3.5 text-green-400" viewBox="0 0 20 20" fill="currentColor">
                <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.2 7.2a1 1 0 01-1.415 0l-3.2-3.2a1 1 0 111.414-1.42l2.493 2.494 6.493-6.494a1 1 0 011.415 0z" clipRule="evenodd" />
              </svg>
            ) : (
              <svg className="w-3.5 h-3.5" viewBox="0 0 20 20" fill="currentColor">
                <path d="M6 2a2 2 0 00-2 2v8a2 2 0 002 2h1v-2H6V4h7v1h2V4a2 2 0 00-2-2H6z" />
                <path d="M9 7a2 2 0 00-2 2v7a2 2 0 002 2h7a2 2 0 002-2V9a2 2 0 00-2-2H9z" />
              </svg>
            )}
          </button>
          <pre>{JSON.stringify(detailData, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}

// Origin chip styling/wording for the sourceOffsetOrigin provenance value. Unknown
// values render verbatim in gray so a future agent origin never breaks the UI.
const ORIGIN_CHIP: Record<string, { className: string; title: string }> = {
  "line-anchored": {
    className: "bg-sky-100 text-sky-800",
    title: "Offset anchored to a UTC reference line near this log line — the most precise source.",
  },
  bias: {
    className: "bg-slate-200 text-slate-600",
    title: "Offset taken from the log line's own timezone bias — declared by the writer, authoritative.",
  },
  "reader-zone-fallback": {
    className: "bg-amber-100 text-amber-800",
    title: "No anchor available — the reader's timezone was assumed for the whole file. Times are internally consistent but the absolute frame may be wrong.",
  },
  calibrated: {
    className: "bg-gray-100 text-gray-500",
    title: "Legacy origin emitted by older agents (retired).",
  },
};

// The raw CMTrace time-resolution values (P13): how this event's UTC timestamp was
// produced. Collapsed behind the "time provenance" toggle on the Created row — the same
// values sit in the raw JSON dump, so this labeled view is an opt-in dive that must not
// inflate the default metadata block. Per-row badges stay reserved for jump/clamp cases.
function TimeProvenanceRows({ event, provenance }: { event: EnrollmentEvent; provenance: ReturnType<typeof readTimeProvenance> }) {
  const origin = provenance?.sourceOffsetOrigin;
  const originChip = origin ? ORIGIN_CHIP[origin] ?? { className: "bg-gray-100 text-gray-500", title: origin } : null;

  return (
    <div className="ml-2 pl-2 border-l-2 border-gray-200">
      {provenance?.sourceLocalTs && (
        <div className="flex mt-0.5">
          <span className="w-24 flex-shrink-0 text-gray-400">Local time</span>
          <span className="font-mono">
            {provenance.sourceLocalTs}
            <span className="text-gray-400 ml-1 font-sans">(as written in the log)</span>
          </span>
        </div>
      )}
      {provenance?.sourceOffsetMinutes !== null && provenance?.sourceOffsetMinutes !== undefined && (
        <div className="flex mt-0.5 items-center">
          <span className="w-24 flex-shrink-0 text-gray-400">UTC offset</span>
          <span className="font-mono">{formatUtcOffset(provenance.sourceOffsetMinutes)}</span>
          <span className="text-gray-400 ml-1">(applied)</span>
          {originChip && origin && (
            <span className={`ml-2 px-1.5 py-0.5 rounded text-[10px] font-medium ${originChip.className}`} title={originChip.title}>
              {origin}
              {origin === "calibrated" && <span className="ml-1 font-normal">(retired)</span>}
            </span>
          )}
        </div>
      )}
      {provenance?.measuredWriterOffsetMinutes !== null && provenance?.measuredWriterOffsetMinutes !== undefined && (
        <div className="flex mt-0.5">
          <span className="w-24 flex-shrink-0 text-gray-400">Writer offset</span>
          <span
            className="font-mono"
            title="Measured from the log writer's paired local/UTC lines. Sticky after era flip-backs, so it can lag reality. The applied correction is the UTC offset above."
          >
            {formatUtcOffset(provenance.measuredWriterOffsetMinutes)}
            <span className="text-gray-400 ml-1 font-sans">(observed from the writer&apos;s own UTC lines — not the applied correction)</span>
          </span>
        </div>
      )}
      {provenance?.derivedTimestamp && (
        <div className="flex mt-0.5">
          <span className="w-24 flex-shrink-0 text-gray-400">Derived time</span>
          <span className="font-mono">
            {provenance.derivedTimestamp}
            <span className="text-gray-400 ml-1 font-sans">(line timestamp unusable — agent clock substituted)</span>
          </span>
        </div>
      )}
      {provenance?.rejectedSourceTimestamp && (
        <div className="flex mt-0.5">
          <span className="w-24 flex-shrink-0 text-gray-400">Rejected time</span>
          <span className="font-mono">
            {provenance.rejectedSourceTimestamp}
            <span className="text-gray-400 ml-1 font-sans">(rejected by the staleness clamp)</span>
          </span>
        </div>
      )}
      {event.timestampClamped && (
        <div className="flex mt-0.5">
          <span className="w-24 flex-shrink-0 text-gray-400">Original time</span>
          <span className="font-mono">
            {event.originalTimestamp ?? "—"}
            <span className="text-gray-400 ml-1 font-sans">(before ingest clamp)</span>
          </span>
        </div>
      )}
    </div>
  );
}

function RawEventRow({ event, prevEvent, clockDeltas }: { event: EnrollmentEvent; prevEvent?: EnrollmentEvent | null; clockDeltas?: number[] }) {
  const [expanded, setExpanded] = useState(false);
  const detailData = useMemo(() => normalizeEventDataForDisplay(event.data), [event.data]);
  const hasDetails = detailData && Object.keys(detailData).length > 0;
  const { isBackfilled, recordedAt } = getBackfillInfo(event);
  const prevDisplayTime = prevEvent ? getDisplayTime(prevEvent) : null;

  const sevColor: Record<string, string> = {
    Trace: "text-purple-500",
    Debug: "text-gray-400",
    Info: "text-blue-600",
    Warning: "text-yellow-600",
    Error: "text-red-600",
    Critical: "text-red-800 font-semibold",
  };

  return (
    <div id={`event-${event.eventId}`} className="py-1.5 text-xs font-mono">
      <div className="flex items-start gap-2">
        <span className="text-gray-400 w-8 text-right flex-shrink-0">{event.sequence}</span>
        <span className="text-gray-500 flex-shrink-0">{(recordedAt ?? new Date(event.timestamp)).toLocaleTimeString()}</span>
        <GapBadge prevTime={prevDisplayTime ?? null} eventTime={recordedAt ?? new Date(event.timestamp)} />
        <TimeJumpBadge prevEvent={prevEvent} event={event} clockDeltas={clockDeltas} />
        <span className={`flex-shrink-0 w-14 ${sevColor[event.severity] || "text-gray-500"}`}>{event.severity}</span>
        {isBackfilled && (
          <span className="text-slate-500 flex-shrink-0" title={BACKFILL_TOOLTIP}>⟲</span>
        )}
        {event.timestampClamped && (
          <span className="text-amber-600 flex-shrink-0" title={`${CLAMPED_TOOLTIP_PREFIX}${event.originalTimestamp ? ` Original device timestamp: ${event.originalTimestamp}.` : ""}`}>⧖</span>
        )}
        <span className="text-gray-900 font-medium flex-shrink-0">{event.eventType}</span>
        <span className="text-gray-500 truncate flex-1 min-w-0" title={event.message || undefined}>{shortenBuildHashInMessage(event.message)}</span>
        {hasDetails && (
          <button onClick={() => setExpanded(!expanded)} className="text-gray-400 hover:text-green-600 flex-shrink-0 ml-1">
            {expanded ? '−' : '+'}
          </button>
        )}
      </div>
      {expanded && hasDetails && (
        <div className="ml-10 mt-1 p-2 bg-gray-900 rounded text-[11px] text-gray-100 overflow-x-auto max-h-60 overflow-y-auto">
          <pre>{JSON.stringify(detailData, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}

function SeverityBadge({ severity }: { severity: string }) {
  const colors = {
    Info: "bg-blue-100 text-blue-800",
    Warning: "bg-yellow-100 text-yellow-800",
    Error: "bg-red-100 text-red-800",
    Critical: "bg-red-200 text-red-900"
  };

  const color = colors[severity as keyof typeof colors] || colors.Info;

  return (
    <span className={`px-2 py-0.5 rounded text-xs font-medium ${color}`}>
      {severity}
    </span>
  );
}
