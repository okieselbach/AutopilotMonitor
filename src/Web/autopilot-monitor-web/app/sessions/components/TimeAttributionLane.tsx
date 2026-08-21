"use client";

import React from "react";

/**
 * F1 time attribution (insights spec §F1): renders the PRE-COMPUTED breakdown row of a
 * terminal session — segment totals as a proportional stacked bar, the ESP-blocking app
 * intervals, reboot outage and data-quality flags. Everything shown comes verbatim from the
 * SessionTimeBreakdowns row (AttributionVersion-stamped); nothing is re-derived client-side.
 * The exact-partition invariant holds server-side: segments + unattributed == wall clock ==
 * the session's DurationSeconds (WhiteGlove pause excluded by design).
 */

export interface TimeAttributionSpanDto {
  segmentKey: string;
  startUtc: string;
  endUtc: string;
  seconds: number;
}

export interface BlockingAppIntervalDto {
  appId: string;
  appName: string;
  startUtc: string;
  endUtc: string;
  seconds: number;
}

export interface RebootSpanDto {
  startUtc: string;
  endUtc: string;
  seconds: number;
  segmentKey: string;
}

export interface SleepSpanDto {
  startUtc: string;
  endUtc: string;
  seconds: number;
  segmentKey: string;
  /** "sleep" | "hibernate" | "modern_standby" (from the system_sleep_episode payload). */
  kind: string;
}

export interface SessionTimeBreakdownDto {
  tenantId: string;
  sessionId: string;
  attributionVersion: number;
  wallClockSeconds: number;
  segments: TimeAttributionSpanDto[];
  unattributedSeconds: number;
  rebootSeconds: number;
  rebootSpans: RebootSpanDto[];
  /** Optional: rows computed before AttributionVersion 3 lack these. */
  sleepSeconds?: number;
  sleepSpans?: SleepSpanDto[];
  blockingApps: BlockingAppIntervalDto[];
  blockingAppCount: number;
  espAppsOccupancySeconds: number | null;
  /** Flags enum serialized as string, e.g. "None" or "PartialObservation, BlockingSetTruncated". */
  qualityFlags: string;
}

const SEGMENT_META: { key: string; label: string; color: string }[] = [
  { key: "device_prep", label: "Device preparation", color: "bg-slate-400" },
  { key: "esp_apps", label: "Apps (ESP)", color: "bg-blue-500" },
  { key: "identity_hello", label: "Identity & Hello", color: "bg-violet-500" },
  { key: "user_esp", label: "User ESP", color: "bg-indigo-400" },
  { key: "desktop_handoff", label: "Desktop handoff", color: "bg-emerald-500" },
  { key: "unattributed", label: "Unattributed", color: "bg-gray-300" },
];

const FLAG_LABELS: Record<string, string> = {
  ClockSkewDropped: "Clock-skewed data dropped",
  PartialObservation: "Agent started late — early phases underobserved",
  BlockingSetUnknown: "ESP blocking set unknown",
  BlockingSetTruncated: "ESP blocking list truncated",
  WhiteGloveAnchorsIncomplete: "Pre-provisioning window estimated",
  PriorEnrollmentResidue: "Device re-enrolled without wipe — phase timing unreliable",
};

function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60 > 0 ? `${s % 60}s` : ""}`.trim();
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
}

export default function TimeAttributionLane({ breakdown }: { breakdown: SessionTimeBreakdownDto }) {
  const wallClock = breakdown.wallClockSeconds;
  if (!wallClock || wallClock <= 0) return null;

  // Segment totals in canonical order; unattributed is the explicit remainder — the bar
  // always sums to 100% of the session's authoritative duration (never normalized away).
  const totals = new Map<string, number>();
  for (const span of breakdown.segments) {
    totals.set(span.segmentKey, (totals.get(span.segmentKey) ?? 0) + span.seconds);
  }
  totals.set("unattributed", breakdown.unattributedSeconds);

  const parts = SEGMENT_META
    .map(meta => ({ ...meta, seconds: totals.get(meta.key) ?? 0 }))
    .filter(p => p.seconds > 0);

  const flags = breakdown.qualityFlags
    .split(",")
    .map(f => f.trim())
    .filter(f => f && f !== "None");

  const blockingSetUnknown = flags.includes("BlockingSetUnknown");
  const apps = [...breakdown.blockingApps].sort((a, b) => b.seconds - a.seconds);
  const maxAppSeconds = apps.length > 0 ? apps[0].seconds : 0;

  return (
    <div className="mt-6 border-t border-gray-100 pt-4">
      <div className="flex items-center justify-between flex-wrap gap-2 mb-2">
        <h3 className="text-sm font-semibold text-gray-700">
          Time attribution
          <span className="ml-2 text-xs font-normal text-gray-400">
            {formatDuration(wallClock)} enrollment time
          </span>
        </h3>
        <span className="inline-flex items-center gap-2">
          {(breakdown.sleepSeconds ?? 0) > 0 && (
            <span
              className="text-xs text-indigo-700 bg-indigo-50 border border-indigo-200 rounded-full px-2 py-0.5"
              title="The device was asleep (standby/hibernate) for this long inside the enrollment window — the wall clock keeps the pause; this chip discloses it. Like reboots, sleep overlaps the segment it started in and is not a separate slice of the bar."
            >
              🌙 {(breakdown.sleepSpans ?? []).length} standby · {formatDuration(breakdown.sleepSeconds ?? 0)}
            </span>
          )}
          {breakdown.rebootSeconds > 0 && (
            <span
              className="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded-full px-2 py-0.5"
              title="Reboot outage overlaps the segment it started in — it is not a separate slice of the bar."
            >
              ⟳ {breakdown.rebootSpans.length} reboot{breakdown.rebootSpans.length !== 1 ? "s" : ""} · {formatDuration(breakdown.rebootSeconds)}
            </span>
          )}
        </span>
      </div>

      {/* Proportional stacked bar over the authoritative wall clock */}
      <div className="w-full h-5 rounded overflow-hidden flex" role="img" aria-label="Enrollment time by segment">
        {parts.map(p => (
          <div
            key={p.key}
            className={`${p.color} h-full`}
            style={{ width: `${(p.seconds / wallClock) * 100}%` }}
            title={`${p.label}: ${formatDuration(p.seconds)}`}
          />
        ))}
      </div>

      {/* Legend with per-segment totals */}
      <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1">
        {parts.map(p => (
          <span key={p.key} className="inline-flex items-center text-xs text-gray-600">
            <span className={`w-2.5 h-2.5 rounded-sm mr-1.5 ${p.color}`} />
            {p.label}
            <span className="ml-1 text-gray-400">{formatDuration(p.seconds)}</span>
          </span>
        ))}
      </div>

      {/* ESP-blocking apps (positive evidence only) */}
      <div className="mt-4">
        <div className="text-xs font-semibold text-gray-600 mb-1.5">
          ESP-blocking apps
          {/* Loose check: the backend omits null fields (WhenWritingNull), so the client sees undefined. */}
          {breakdown.espAppsOccupancySeconds != null && (
            <span className="ml-2 font-normal text-gray-400" title="Overlap-merged install time of ESP-blocking apps within the apps phase.">
              critical path {formatDuration(breakdown.espAppsOccupancySeconds)}
            </span>
          )}
        </div>
        {blockingSetUnknown ? (
          <p className="text-xs text-gray-400 italic">
            The ESP blocking set was not observed for this session — per-app blocking attribution is unknown (not zero).
          </p>
        ) : apps.length === 0 ? (
          <p className="text-xs text-gray-400 italic">
            {breakdown.blockingAppCount > 0
              ? `${breakdown.blockingAppCount} blocking app${breakdown.blockingAppCount !== 1 ? "s" : ""} in the ESP set, none with a fully observed install interval.`
              : "No ESP-blocking app installs observed in this session."}
          </p>
        ) : (
          <div className="space-y-1">
            {apps.map(app => (
              <div key={app.appId} className="flex items-center gap-2 text-xs">
                <span className="w-44 truncate text-gray-700" title={app.appName || app.appId}>
                  {app.appName || app.appId}
                </span>
                <div className="flex-1 h-2 bg-gray-100 rounded">
                  <div
                    className="h-2 rounded bg-blue-400"
                    style={{ width: `${maxAppSeconds > 0 ? Math.max(2, (app.seconds / maxAppSeconds) * 100) : 0}%` }}
                  />
                </div>
                <span className="w-16 text-right text-gray-500">{formatDuration(app.seconds)}</span>
              </div>
            ))}
            {breakdown.blockingAppCount > apps.length && (
              <p className="text-[11px] text-gray-400">
                +{breakdown.blockingAppCount - apps.length} more blocking app{breakdown.blockingAppCount - apps.length !== 1 ? "s" : ""} without a fully observed interval.
              </p>
            )}
          </div>
        )}
      </div>

      {/* Data-quality disclosure */}
      {flags.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {flags.map(flag => (
            <span key={flag} className="text-[11px] text-gray-500 bg-gray-50 border border-gray-200 rounded-full px-2 py-0.5">
              {FLAG_LABELS[flag] ?? flag}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}
