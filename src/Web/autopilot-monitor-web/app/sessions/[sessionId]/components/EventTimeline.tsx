"use client";

import { useState, useMemo } from "react";
import { EnrollmentEvent, Session } from "@/types";
import { normalizeEventDataForDisplay, shortenBuildHashInMessage } from "../utils/eventHelpers";
import { getErrorCodeEntry, formatErrorCode } from "@/utils/errorCodeMap";

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
  whiteGloveSplitSequence,
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
            className="w-full pl-7 pr-7 py-1 text-xs border border-gray-300 rounded-full focus:outline-none focus:ring-1 focus:ring-blue-500 focus:border-blue-500"
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
            {sortedBySequence.map((ev) => (
              <RawEventRow key={ev.eventId || `${ev.sessionId}-${ev.sequence}`} event={ev} />
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
  borderColor = 'border-blue-500'
}: {
  phaseName: string;
  events: EnrollmentEvent[];
  isExpanded: boolean;
  onToggle: () => void;
  showScriptOutput?: boolean;
  borderColor?: string;
}) {
  return (
    <div id={`phase-${phaseName.replace(/[^a-zA-Z0-9]/g, '-')}`} className={`border-l-4 ${borderColor} pl-4`}>
      <button
        onClick={onToggle}
        className="flex items-center justify-between w-full text-left mb-3 group"
      >
        <h3 className="text-lg font-semibold text-gray-900 group-hover:text-blue-600">
          {phaseName} ({events.length} events)
        </h3>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {isExpanded && (
        <div className="space-y-3">
          {events.map((event, index) => (
            <EventRow
              key={event.eventId || `${event.sessionId}-${event.sequence}`}
              event={event}
              showScriptOutput={showScriptOutput}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function EventRow({ event, showScriptOutput }: { event: EnrollmentEvent; showScriptOutput?: boolean }) {
  const [showDetails, setShowDetails] = useState(false);
  const [showRaw, setShowRaw] = useState(false);
  const [copied, setCopied] = useState(false);
  const [copiedDetail, setCopiedDetail] = useState(false);
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

  const hasData = isTruncated || (detailData && Object.keys(detailData).length > 0);
  const hasDetails = true; // Every event has at least the metadata block

  return (
    <div id={`event-${event.eventId}`} className="bg-gray-50 rounded-lg p-3 hover:bg-gray-100 transition-colors">
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3">
            <span className="text-xs text-gray-500 font-mono">
              {new Date(event.timestamp).toLocaleTimeString()}
            </span>
            <SeverityBadge severity={event.severity} />
            <span className="text-sm font-medium text-gray-900">{event.eventType}</span>
          </div>
          <p className="mt-1 text-sm text-gray-600" title={event.message || undefined}>{shortenBuildHashInMessage(event.message)}</p>
          {/* Exit code / HRESULT badge for app install events */}
          {(event.eventType === "app_install_failed" || event.eventType === "app_install_completed") && (() => {
            const ec = event.data?.exitCode ?? event.data?.exit_code;
            const hr = event.data?.hresultFromWin32 ?? event.data?.hresult_from_win32;
            const hasNonZero = (ec && String(ec) !== "0") || (hr && String(hr) !== "0");
            if (!hasNonZero) return null;
            const ecEntry = ec ? getErrorCodeEntry(String(ec)) : null;
            const hrEntry = hr ? getErrorCodeEntry(String(hr)) : null;
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
          <div className="mt-1 flex items-center gap-3 text-xs text-gray-500">
            <span>Source: {event.source}</span>
            <span>Seq: {event.sequence}</span>
          </div>
        </div>
        {hasDetails && (
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-xs text-blue-600 hover:text-blue-800 ml-4 flex-shrink-0"
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
              className="absolute top-1.5 right-1.5 inline-flex items-center justify-center w-5 h-5 rounded border border-gray-200 bg-white text-gray-400 opacity-0 group-hover/meta:opacity-100 focus:opacity-100 hover:bg-gray-50 hover:text-blue-600 transition-opacity"
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
            <div className="flex mt-0.5">
              <span className="w-16 flex-shrink-0 text-gray-400">Created</span>
              <span className="font-mono">{event.timestamp}</span>
            </div>
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

function RawEventRow({ event }: { event: EnrollmentEvent }) {
  const [expanded, setExpanded] = useState(false);
  const detailData = useMemo(() => normalizeEventDataForDisplay(event.data), [event.data]);
  const hasDetails = detailData && Object.keys(detailData).length > 0;

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
        <span className="text-gray-500 flex-shrink-0">{new Date(event.timestamp).toLocaleTimeString()}</span>
        <span className={`flex-shrink-0 w-14 ${sevColor[event.severity] || "text-gray-500"}`}>{event.severity}</span>
        <span className="text-gray-900 font-medium flex-shrink-0">{event.eventType}</span>
        <span className="text-gray-500 truncate flex-1 min-w-0" title={event.message || undefined}>{shortenBuildHashInMessage(event.message)}</span>
        {hasDetails && (
          <button onClick={() => setExpanded(!expanded)} className="text-gray-400 hover:text-blue-600 flex-shrink-0 ml-1">
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
