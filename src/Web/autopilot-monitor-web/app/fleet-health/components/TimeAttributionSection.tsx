"use client";

import React from "react";

/**
 * F1 fleet time attribution (insights spec §F1 "Surfaces"): renders the sweep-maintained
 * rolling 30-day aggregate rows — per enrollment class (never mixed): stacked segment medians
 * plus the top ESP-blocking apps with their what-if bounds. Below the ≥20-clean-sessions gate
 * the class renders as "insufficient data (n=…)" instead of a small-n median (truthfulness
 * rule 4); flagged/missing exclusions are disclosed, and the what-if wording is always
 * "up to" — it is an upper bound, never a promise.
 */

export interface TimeAttributionSegmentStatDto {
  segmentKey: string;
  medianSeconds: number;
  p75Seconds: number;
  p90Seconds: number;
}

export interface TimeAttributionBlockingAppStatDto {
  appId: string;
  appName: string;
  sessionCount: number;
  medianSeconds: number;
  p75Seconds: number;
  medianSavingSeconds: number;
  p75SavingSeconds: number;
}

export interface TimeAttributionAggregateDto {
  tenantId: string;
  date: string;
  enrollmentClass: string;
  attributionVersion: number;
  cleanSessionCount: number;
  flaggedExcludedCount: number;
  missingBreakdownCount: number;
  segmentStats: TimeAttributionSegmentStatDto[];
  topBlockingApps: TimeAttributionBlockingAppStatDto[];
}

export interface TimeAttributionResponseDto {
  success: boolean;
  windowDays: number;
  classes: TimeAttributionAggregateDto[];
  daily: TimeAttributionAggregateDto[];
}

/** UI gate for rendering class statistics (spec: ≥20 clean sessions per class). */
const MIN_SESSIONS_FOR_PANEL = 20;

const SEGMENT_META: { key: string; label: string; color: string }[] = [
  { key: "device_prep", label: "Device preparation", color: "bg-slate-400" },
  { key: "esp_apps", label: "Apps (ESP)", color: "bg-blue-500" },
  { key: "identity_hello", label: "Identity & Hello", color: "bg-violet-500" },
  { key: "user_esp", label: "User ESP", color: "bg-indigo-400" },
  { key: "desktop_handoff", label: "Desktop handoff", color: "bg-emerald-500" },
  { key: "unattributed", label: "Unattributed", color: "bg-gray-300" },
];

const CLASS_LABELS: Record<string, string> = {
  user_driven: "User-driven",
  whiteglove: "Pre-provisioning (WhiteGlove)",
  self_deploying: "Self-deploying",
  device_preparation: "Device Preparation (v2)",
};

function formatMinutes(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.round(s / 60)}m`;
  return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
}

export default function TimeAttributionSection({ data }: { data: TimeAttributionResponseDto | null }) {
  if (!data || !data.success) return null;
  const classes = data.classes ?? [];

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-8">
      <h2 className="text-lg font-semibold text-gray-900 mb-1">
        Time attribution
        <span
          className="ml-2 text-xs font-normal text-gray-400"
          title="Median time per enrollment segment across clean terminal sessions of the last 30 days. Sessions with data-quality flags are excluded and counted below. Wall clock = the session's authoritative duration (pre-provisioning pause excluded)."
        >
          last {data.windowDays} days · medians per enrollment class
        </span>
      </h2>

      {classes.length === 0 ? (
        <div className="text-center py-6 text-gray-400 text-sm">
          No attribution data yet — breakdowns appear after the next maintenance run following terminal enrollments.
        </div>
      ) : (
        <div className="space-y-6 mt-4">
          {classes.map(cls => {
            const excludedNote = [
              cls.flaggedExcludedCount > 0 ? `${cls.flaggedExcludedCount} flagged excluded` : null,
              cls.missingBreakdownCount > 0 ? `${cls.missingBreakdownCount} without breakdown` : null,
            ].filter(Boolean).join(" · ");

            if (cls.cleanSessionCount < MIN_SESSIONS_FOR_PANEL) {
              return (
                <div key={cls.enrollmentClass}>
                  <div className="flex items-baseline justify-between flex-wrap gap-1">
                    <span className="text-sm font-medium text-gray-700">
                      {CLASS_LABELS[cls.enrollmentClass] ?? cls.enrollmentClass}
                    </span>
                    <span className="text-xs text-gray-400">
                      insufficient data (n={cls.cleanSessionCount}, needs {MIN_SESSIONS_FOR_PANEL})
                      {excludedNote ? ` · ${excludedNote}` : ""}
                    </span>
                  </div>
                </div>
              );
            }

            const parts = SEGMENT_META
              .map(meta => ({
                ...meta,
                stat: cls.segmentStats.find(s => s.segmentKey === meta.key),
              }))
              .filter(p => p.stat && p.stat.medianSeconds > 0) as
                ({ key: string; label: string; color: string; stat: TimeAttributionSegmentStatDto })[];
            const stackTotal = parts.reduce((sum, p) => sum + p.stat.medianSeconds, 0);

            return (
              <div key={cls.enrollmentClass}>
                <div className="flex items-baseline justify-between flex-wrap gap-1 mb-1.5">
                  <span className="text-sm font-medium text-gray-700">
                    {CLASS_LABELS[cls.enrollmentClass] ?? cls.enrollmentClass}
                    <span className="ml-2 text-xs font-normal text-gray-400">
                      n={cls.cleanSessionCount} · median total ≈ {formatMinutes(stackTotal)}
                    </span>
                  </span>
                  {excludedNote && <span className="text-xs text-gray-400">{excludedNote}</span>}
                </div>
                {stackTotal > 0 && (
                  <div className="w-full h-4 rounded overflow-hidden flex" role="img"
                    aria-label={`Median enrollment time by segment (${cls.enrollmentClass})`}>
                    {parts.map(p => (
                      <div
                        key={p.key}
                        className={`${p.color} h-full`}
                        style={{ width: `${(p.stat.medianSeconds / stackTotal) * 100}%` }}
                        title={`${p.label}: median ${formatMinutes(p.stat.medianSeconds)} · p90 ${formatMinutes(p.stat.p90Seconds)}`}
                      />
                    ))}
                  </div>
                )}
                <div className="mt-1.5 flex flex-wrap gap-x-4 gap-y-0.5">
                  {parts.map(p => (
                    <span key={p.key} className="inline-flex items-center text-xs text-gray-500">
                      <span className={`w-2 h-2 rounded-sm mr-1 ${p.color}`} />
                      {p.label} <span className="ml-1 text-gray-400">{formatMinutes(p.stat.medianSeconds)}</span>
                    </span>
                  ))}
                </div>
              </div>
            );
          })}

          {/* Top ESP-blocking apps — one table, rows stay class-scoped (no cross-class math) */}
          {(() => {
            const rows = classes
              .filter(c => c.cleanSessionCount >= MIN_SESSIONS_FOR_PANEL)
              .flatMap(c => c.topBlockingApps.map(app => ({ cls: c.enrollmentClass, app })))
              .sort((a, b) => b.app.medianSeconds - a.app.medianSeconds)
              .slice(0, 10);
            if (rows.length === 0) return null;
            return (
              <div>
                <h3 className="text-sm font-semibold text-gray-700 mb-2">
                  Top time-consuming blocking apps
                  <span className="ml-2 text-xs font-normal text-gray-400"
                    title="ESP-blocking membership is positive evidence from the device's own tracking lists. Savings are an upper bound from removing the app off the critical path — real savings may be lower.">
                    what-if savings are “up to” bounds
                  </span>
                </h3>
                <div className="overflow-x-auto">
                  <table className="min-w-full text-sm">
                    <thead>
                      <tr className="text-left text-xs text-gray-400 border-b border-gray-100">
                        <th className="py-1.5 pr-4 font-medium">App</th>
                        <th className="py-1.5 pr-4 font-medium">Class</th>
                        <th className="py-1.5 pr-4 font-medium text-right">Sessions</th>
                        <th className="py-1.5 pr-4 font-medium text-right">Median install</th>
                        <th className="py-1.5 font-medium text-right">Removing it saves</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map(({ cls, app }) => (
                        <tr key={`${cls}|${app.appId}`} className="border-b border-gray-50">
                          <td className="py-1.5 pr-4 text-gray-700 max-w-[220px] truncate" title={app.appName || app.appId}>
                            {app.appName || app.appId}
                          </td>
                          <td className="py-1.5 pr-4 text-xs text-gray-400">{CLASS_LABELS[cls] ?? cls}</td>
                          <td className="py-1.5 pr-4 text-right text-gray-500">{app.sessionCount}</td>
                          <td className="py-1.5 pr-4 text-right text-gray-700">{formatMinutes(app.medianSeconds)}</td>
                          <td className="py-1.5 text-right text-gray-700">
                            up to {formatMinutes(app.medianSavingSeconds)}
                            <span className="text-xs text-gray-400"> (p75 {formatMinutes(app.p75SavingSeconds)})</span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </div>
            );
          })()}
        </div>
      )}
    </div>
  );
}
