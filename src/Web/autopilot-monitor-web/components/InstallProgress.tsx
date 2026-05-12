"use client";

import { useEffect, useMemo, useState } from "react";
import { getErrorCodeEntry, formatErrorCode } from "@/utils/errorCodeMap";

interface InstallEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

interface SummaryStats {
  totalApps?: number;
  installing?: number;
  installed?: number;
  failed?: number;
  likelyStuck?: number;
}

interface InstallProgressProps {
  events: InstallEvent[];
  summaryStats?: SummaryStats | null;
}

interface InstallItem {
  appName: string;
  appId: string;
  state: "Installing" | "Installed" | "Failed" | "Postponed" | "Skipped";
  startedAt?: string;
  completedAt?: string;
  durationMs?: number;
  isCompleted: boolean;
  isError: boolean;
  errorDetail?: string;
  errorPatternId?: string;
  exitCode?: string;
  hresultFromWin32?: string;
  // c117946b debrief (2026-05-12) — when the agent promotes an app from Installing
  // to Error on terminal ESP-Apps-failure, it tags the event with
  // failureType=esp_apps_timeout + confidence=presumed. UI renders these as a
  // hedged "Likely stuck" badge with explanatory wording, separate from a hard
  // "Failed" label, because we genuinely don't know the final outcome.
  failureType?: string;
  confidence?: string;
  isLikelyStuck: boolean;
  firstSeenIndex: number;
  eventData?: Record<string, any>;
}

function formatDuration(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) return `${minutes}m ${remainingSeconds}s`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours}h ${remainingMinutes}m`;
}

export default function InstallProgress({ events, summaryStats }: InstallProgressProps) {
  const installs = useMemo(() => {
    if (events.length === 0) return [];

    const sortedEvents = [...events].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );

    const installMap = new Map<string, InstallItem>();
    let insertionIndex = 0;

    for (const evt of sortedEvents) {
      const d = evt.data;
      if (!d) continue;

      const appName = d.appName ?? d.app_name ?? d.appId ?? d.app_id ?? "Unknown App";
      if (appName === "Unknown App") continue;

      const appId = d.appId ?? d.app_id ?? appName;
      const existing = installMap.get(appName);
      const eventTs = evt.timestamp;

      if (evt.eventType === "app_install_started") {
        // Don't reset an app that already completed — later batch re-scans
        // would overwrite the real duration with near-zero timestamps.
        // Allow restart after failure (retry).
        if (existing?.state === "Installed") continue;
        installMap.set(appName, {
          appName,
          appId,
          state: "Installing",
          startedAt: eventTs,
          isCompleted: false,
          isError: false,
          isLikelyStuck: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_completed") {
        // Keep the first valid completion — don't let batch re-scans overwrite.
        if (existing?.state === "Installed" && existing.durationMs != null) continue;
        const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
        const endTime = new Date(eventTs).getTime();
        const duration = startTime ? endTime - startTime : undefined;

        installMap.set(appName, {
          appName,
          appId,
          state: "Installed",
          startedAt: existing?.startedAt,
          completedAt: eventTs,
          durationMs: duration,
          isCompleted: true,
          isError: false,
          isLikelyStuck: false,
          exitCode: d.exitCode ?? d.exit_code,
          hresultFromWin32: d.hresultFromWin32 ?? d.hresult_from_win32,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_failed") {
        // Don't downgrade from Installed to Failed.
        if (existing?.state === "Installed") continue;
        const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
        const endTime = new Date(eventTs).getTime();
        const duration = startTime ? endTime - startTime : undefined;

        const failureType = (d.failureType ?? d.failure_type) as string | undefined;
        const confidence = (d.confidence) as string | undefined;
        const isLikelyStuck = failureType === "esp_apps_timeout";

        installMap.set(appName, {
          appName,
          appId,
          state: "Failed",
          startedAt: existing?.startedAt,
          completedAt: eventTs,
          durationMs: duration,
          isCompleted: true,
          isError: true,
          isLikelyStuck,
          failureType,
          confidence,
          errorDetail: d.errorDetail ?? d.error_detail,
          errorPatternId: d.errorPatternId ?? d.error_pattern_id,
          exitCode: d.exitCode ?? d.exit_code,
          hresultFromWin32: d.hresultFromWin32 ?? d.hresult_from_win32,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_postponed") {
        // Don't downgrade from Installed to Postponed.
        if (existing?.state === "Installed") continue;
        const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
        const endTime = new Date(eventTs).getTime();
        const duration = startTime ? endTime - startTime : undefined;

        installMap.set(appName, {
          appName,
          appId,
          state: "Postponed",
          startedAt: existing?.startedAt,
          completedAt: eventTs,
          durationMs: duration,
          isCompleted: true,
          isError: false,
          isLikelyStuck: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_skipped") {
        // Don't overwrite terminal states.
        if (existing?.state === "Installed" || existing?.state === "Failed" || existing?.state === "Postponed") continue;
        installMap.set(appName, {
          appName,
          appId,
          state: "Skipped",
          isCompleted: true,
          isError: false,
          isLikelyStuck: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      }
    }

    return Array.from(installMap.values()).sort((a, b) => a.firstSeenIndex - b.firstSeenIndex);
  }, [events]);

  const [expanded, setExpanded] = useState(true);
  const [showSkipped, setShowSkipped] = useState(false);

  // Sum of individual install durations (actual time spent installing, not wall-clock)
  // Must be before the early return to keep hooks in stable order across renders.
  const totalDuration = useMemo(() => {
    let sum = 0;
    let hasAny = false;
    for (const item of installs) {
      if (item.durationMs != null && item.durationMs > 0) {
        sum += item.durationMs;
        hasAny = true;
      }
    }
    return hasAny ? sum : null;
  }, [installs]);

  const filteredInstalls = useMemo(() => {
    return showSkipped ? installs : installs.filter(d => d.state !== "Skipped");
  }, [installs, showSkipped]);

  if (installs.length === 0) return null;

  const activeCount = installs.filter(d => d.state === "Installing").length;
  const completedCount = installs.filter(d => d.state === "Installed").length;
  // Separate confirmed failures from likely-stuck (esp_apps_timeout) so the user
  // sees an honest count of "this really failed" vs "we don't actually know".
  const failedCount = installs.filter(d => d.state === "Failed" && !d.isLikelyStuck).length;
  const likelyStuckCount = installs.filter(d => d.isLikelyStuck).length;
  const postponedCount = installs.filter(d => d.state === "Postponed").length;
  const skippedCount = installs.filter(d => d.state === "Skipped").length;

  // Use summary stats for "X of Y" if available, fall back to local event counts
  const totalFromSummary = summaryStats?.totalApps;
  const installedFromSummary = summaryStats?.installed;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-indigo-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Install Progress</h2>
          {totalFromSummary != null && installedFromSummary != null && (
            <span className="text-xs text-gray-400">
              ({installedFromSummary} of {totalFromSummary} installed)
            </span>
          )}
          {totalDuration != null && (
            <span className="text-xs text-gray-400">
              — Total: {formatDuration(totalDuration)}
            </span>
          )}
          <div className="flex items-center space-x-2 text-xs">
            {activeCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-indigo-100 text-indigo-700 font-medium">
                {activeCount} active
              </span>
            )}
            {completedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-green-100 text-green-700 font-medium">
                {completedCount} completed
              </span>
            )}
            {failedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                {failedCount} failed
              </span>
            )}
            {likelyStuckCount > 0 && (
              <span
                className="px-2 py-0.5 rounded-full bg-orange-100 text-orange-700 font-medium"
                title="ESP timed out while these apps were still installing — final status couldn't be confirmed."
              >
                {likelyStuckCount} likely stuck
              </span>
            )}
            {postponedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-amber-100 text-amber-700 font-medium">
                {postponedCount} postponed
              </span>
            )}
            {skippedCount > 0 && (
              <button
                onClick={(e) => { e.stopPropagation(); setShowSkipped(!showSkipped); }}
                className={`px-2 py-0.5 rounded-full font-medium transition-colors ${showSkipped ? "bg-gray-200 text-gray-700" : "bg-gray-100 text-gray-400"}`}
                title={showSkipped ? "Hide skipped apps" : "Show skipped apps"}
              >
                {skippedCount} skipped {showSkipped ? "▾" : "▸"}
              </button>
            )}
          </div>
        </div>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {expanded && <div className="space-y-3 mt-4">
        {filteredInstalls.map((item) => (
          <InstallItemRow key={item.appName} item={item} />
        ))}
      </div>}
    </div>
  );
}

function InstallItemRow({ item }: { item: InstallItem }) {
  const [showDetails, setShowDetails] = useState(false);
  const [elapsedMs, setElapsedMs] = useState<number | null>(null);

  useEffect(() => {
    if (item.state !== "Installing" || !item.startedAt) {
      setElapsedMs(null);
      return;
    }
    const startTime = new Date(item.startedAt).getTime();
    const tick = () => setElapsedMs(Date.now() - startTime);
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [item.state, item.startedAt]);

  const containerClass = item.state === "Skipped"
    ? "bg-gray-50 border border-gray-300"
    : item.isLikelyStuck
      ? "bg-orange-50 border border-orange-200"
      : item.isError
        ? "bg-red-50 border border-red-200"
        : item.isCompleted
          ? "bg-green-50 border border-green-200"
          : "bg-gray-50 border border-gray-200";

  return (
    <div className={`rounded-lg p-3 ${containerClass}`}>
      <div className="flex items-center justify-between mb-1">
        <div className="flex items-center space-x-2 min-w-0">
          {item.state === "Skipped" ? (
            <svg className="w-4 h-4 text-gray-400 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 5l7 7-7 7M5 5l7 7-7 7" />
            </svg>
          ) : item.isLikelyStuck ? (
            // Question-mark inside circle — explicit "we don't actually know" iconography,
            // distinct from the hard X used for confirmed failures.
            <svg className="w-4 h-4 text-orange-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          ) : item.isError ? (
            <svg className="w-4 h-4 text-red-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          ) : item.isCompleted ? (
            <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          ) : (
            <svg className="w-4 h-4 text-indigo-500 flex-shrink-0 animate-pulse" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
            </svg>
          )}
          <span className={`text-sm font-medium truncate ${item.state === "Skipped" ? "text-gray-500" : "text-gray-900"}`}>
            {item.appName}
          </span>
          {item.state === "Skipped" && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600 font-medium">Skipped</span>
          )}
          {item.state === "Failed" && item.isLikelyStuck && (
            <span
              className="text-xs px-2 py-0.5 rounded-full bg-orange-200 text-orange-800 font-medium"
              title="ESP timed out while this app was still installing — final status couldn't be confirmed. Treat as a strong hint to investigate, not a confirmed failure."
            >
              Likely stuck
            </span>
          )}
          {item.state === "Failed" && !item.isLikelyStuck && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-red-200 text-red-700 font-medium">Failed</span>
          )}
          {item.state === "Postponed" && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-amber-200 text-amber-700 font-medium">Postponed</span>
          )}
        </div>
        <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
          {elapsedMs != null && elapsedMs > 0 && (
            <span className="font-medium text-indigo-600 tabular-nums">{formatDuration(elapsedMs)}</span>
          )}
          {elapsedMs == null && item.durationMs != null && item.durationMs > 0 && (
            <span className="font-medium">{formatDuration(item.durationMs)}</span>
          )}
          {item.eventData && Object.keys(item.eventData).length > 0 && (
            <button
              onClick={() => setShowDetails(!showDetails)}
              className="text-xs text-blue-600 hover:text-blue-800"
            >
              {showDetails ? 'Hide' : 'Details'}
            </button>
          )}
        </div>
      </div>

      {/* Exit code / HRESULT display — only when non-zero */}
      {(item.exitCode && item.exitCode !== "0") || (item.hresultFromWin32 && item.hresultFromWin32 !== "0") || (item.isError && item.errorDetail) ? (
        <div className="mt-1 space-y-0.5">
          {item.exitCode && item.exitCode !== "0" && (() => {
            const entry = getErrorCodeEntry(item.exitCode);
            const badgeBg = item.isError ? "bg-red-100 text-red-800" : "bg-amber-100 text-amber-800";
            const descColor = item.isError ? "text-red-600" : "text-amber-600";
            return (
              <div className="flex items-center gap-2 text-xs">
                <span className={`px-1.5 py-0.5 rounded font-mono font-medium ${badgeBg}`}>
                  Exit: {formatErrorCode(item.exitCode)}
                </span>
                {entry && (
                  <span className={descColor} title={`${entry.source} (${entry.confidence} confidence)`}>
                    {entry.description}
                  </span>
                )}
              </div>
            );
          })()}
          {item.hresultFromWin32 && item.hresultFromWin32 !== "0" && (() => {
            const entry = getErrorCodeEntry(item.hresultFromWin32);
            const badgeBg = item.isError ? "bg-red-100 text-red-800" : "bg-amber-100 text-amber-800";
            const descColor = item.isError ? "text-red-600" : "text-amber-600";
            return (
              <div className="flex items-center gap-2 text-xs">
                <span className={`px-1.5 py-0.5 rounded font-mono font-medium ${badgeBg}`}>
                  HRESULT: {formatErrorCode(item.hresultFromWin32)}
                </span>
                {entry && (
                  <span className={descColor} title={`${entry.source} (${entry.confidence} confidence)`}>
                    {entry.description}
                  </span>
                )}
              </div>
            );
          })()}
          {item.isError && item.errorDetail && !item.isLikelyStuck && (
            <div className="text-xs text-red-600">{item.errorDetail}</div>
          )}
          {item.isLikelyStuck && (
            <div className="text-xs text-orange-700">
              ESP timed out while this app was still installing — final status couldn&apos;t be confirmed.
            </div>
          )}
        </div>
      ) : null}

      {/* Event details (expandable) */}
      {showDetails && item.eventData && (
        <div className="mt-3 p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
          <pre>{JSON.stringify(item.eventData, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}
