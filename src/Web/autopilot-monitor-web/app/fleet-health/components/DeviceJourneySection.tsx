"use client";

import React, { useMemo } from "react";
import Link from "next/link";
import TruncatedLabel from "../../../components/TruncatedLabel";

/**
 * F2 First-Time-Right section (insights spec §F2 "Surfaces"): FTR rate + weekly trend +
 * attempt histogram + "repeat devices" violator table. All counts come from the daily
 * FTR aggregate rows, which are ADDITIVE — window and weekly rates are ratios of summed
 * counts (server sums the window; weeks are grouped here from the same rows). Below the
 * ≥20-completed-journeys gate the rate renders as "insufficient data (n=…)" (truthfulness
 * rule 4); junk-serial exclusions are disclosed; open journeys never count.
 */

export interface DeviceJourneyAttemptBucketDto {
  attempts: number;
  journeyCount: number;
}

export interface DeviceJourneyDailyDto {
  tenantId: string;
  date: string;
  journeyVersion: number;
  completedJourneyCount: number;
  firstTimeRightCount: number;
  attemptHistogram: DeviceJourneyAttemptBucketDto[];
  excludedSessionCount: number;
}

export interface DeviceJourneyTotalsDto {
  completedJourneys: number;
  firstTimeRight: number;
  ftrRatePct: number | null;
  excludedSessions: number;
  attemptHistogram: DeviceJourneyAttemptBucketDto[];
}

export interface RepeatDeviceDto {
  serialNumber: string;
  manufacturer: string;
  model: string;
  attempts: number;
  journeyCount: number;
  lastStatus: string;
  lastSessionId: string;
  lastStartedAt: string;
  lastFailureReason: string;
}

export interface DeviceJourneyResponseDto {
  success: boolean;
  windowDays: number;
  totals: DeviceJourneyTotalsDto;
  daily: DeviceJourneyDailyDto[];
  repeatDevices: RepeatDeviceDto[] | null;
}

/** UI gate for the FTR rate (spec: ≥20 completed journeys). */
const MIN_JOURNEYS_FOR_RATE = 20;

/** Monday-based week key (UTC) so weekly buckets are stable across locales. */
function weekStart(dateStr: string): string {
  const d = new Date(dateStr + "T00:00:00Z");
  const day = d.getUTCDay(); // 0 = Sunday
  const diff = day === 0 ? 6 : day - 1;
  d.setUTCDate(d.getUTCDate() - diff);
  return d.toISOString().slice(0, 10);
}

export default function DeviceJourneySection({
  data,
  linkTenantId,
}: {
  data: DeviceJourneyResponseDto | null;
  /** Carried into session links so a GA scoped to one tenant lands in that tenant. */
  linkTenantId?: string;
}) {
  const weekly = useMemo(() => {
    const byWeek = new Map<string, { completed: number; ftr: number }>();
    for (const row of data?.daily ?? []) {
      const key = weekStart(row.date);
      const bucket = byWeek.get(key) ?? { completed: 0, ftr: 0 };
      bucket.completed += row.completedJourneyCount;
      bucket.ftr += row.firstTimeRightCount;
      byWeek.set(key, bucket);
    }
    return [...byWeek.entries()]
      .sort((a, b) => a[0].localeCompare(b[0]))
      .map(([week, b]) => ({
        week,
        completed: b.completed,
        ratePct: b.completed > 0 ? Math.round((b.ftr / b.completed) * 100) : null,
      }));
  }, [data]);

  if (!data || !data.success) return null;
  const totals = data.totals;
  const gated = totals.completedJourneys < MIN_JOURNEYS_FOR_RATE;
  const maxHistogramCount = Math.max(1, ...totals.attemptHistogram.map((b) => b.journeyCount));
  const repeatDevices = data.repeatDevices;

  const sessionHref = (id: string) =>
    `/sessions/${id}${linkTenantId ? `?tenantId=${encodeURIComponent(linkTenantId)}` : ""}`;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-8">
      <h2 className="text-lg font-semibold text-gray-900 mb-1">
        First-time-right
        <span
          className="ml-2 text-xs font-normal text-gray-400"
          title="Share of completed device journeys that succeeded on the first attempt. A journey groups a device's terminal enrollment attempts (by serial number) until the first success; open journeys (no success yet) and placeholder serials are excluded and disclosed. A pre-provisioning (WhiteGlove) enrollment is ONE attempt."
        >
          last {data.windowDays} days · wipe-and-retry visibility
        </span>
      </h2>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-6 mt-4">
        {/* FTR rate + weekly trend */}
        <div>
          {gated ? (
            <div>
              <div className="text-3xl font-semibold text-gray-400">—</div>
              <div className="text-sm text-gray-400 mt-1">
                insufficient data (n={totals.completedJourneys}, needs {MIN_JOURNEYS_FOR_RATE} completed journeys)
              </div>
            </div>
          ) : (
            <div>
              <div className="text-3xl font-semibold text-gray-900">
                {totals.ftrRatePct != null ? `${totals.ftrRatePct.toFixed(1)}%` : "—"}
              </div>
              <div className="text-sm text-gray-500 mt-1">
                {totals.firstTimeRight} of {totals.completedJourneys} completed journeys succeeded first try
              </div>
            </div>
          )}
          {totals.excludedSessions > 0 && (
            <div className="text-xs text-gray-400 mt-1">
              {totals.excludedSessions} session{totals.excludedSessions === 1 ? "" : "s"} excluded (placeholder serials)
            </div>
          )}

          {weekly.filter((w) => w.completed > 0).length >= 2 && (
            <div className="mt-4">
              <div className="text-xs text-gray-400 mb-1.5">Weekly trend</div>
              <div className="flex items-end gap-1 h-16">
                {weekly.map((w) => (
                  <div key={w.week} className="flex-1 flex flex-col items-center justify-end h-full group relative">
                    {w.ratePct != null && (
                      <div className="absolute -top-7 bg-gray-900 text-white text-xs px-2 py-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity whitespace-nowrap z-10">
                        wk of {w.week}: {w.ratePct}% (n={w.completed})
                      </div>
                    )}
                    <div
                      className={`w-full rounded-t ${w.ratePct != null ? "bg-blue-500" : "bg-gray-100"}`}
                      style={{ height: `${w.ratePct ?? 0}%`, minHeight: w.ratePct != null ? "3px" : "1px" }}
                    />
                  </div>
                ))}
              </div>
              <div className="text-[10px] text-gray-400 mt-1">
                first-time-right % per week (bar height = rate)
              </div>
            </div>
          )}
        </div>

        {/* Attempt histogram */}
        <div>
          <div className="text-xs text-gray-400 mb-1.5">Attempts until success</div>
          {totals.attemptHistogram.length === 0 ? (
            <div className="text-sm text-gray-400 py-4">No completed journeys in this window.</div>
          ) : (
            <div className="space-y-2">
              {totals.attemptHistogram.map((b) => (
                <div key={b.attempts} className="flex items-center space-x-3">
                  <span className="text-xs text-gray-500 w-16 flex-shrink-0">
                    {b.attempts} attempt{b.attempts === 1 ? "" : "s"}
                  </span>
                  <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full ${b.attempts === 1 ? "bg-green-500" : "bg-amber-400"}`}
                      style={{ width: `${(b.journeyCount / maxHistogramCount) * 100}%`, minWidth: "2px" }}
                    />
                  </div>
                  <span className="text-xs text-gray-500 w-8 text-right flex-shrink-0">{b.journeyCount}</span>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Repeat devices */}
        <div>
          <div className="text-xs text-gray-400 mb-1.5"
            title="Devices whose current journey took 2 or more terminal attempts, newest activity first. The failure reason is from the most recent failed attempt.">
            Repeat devices
          </div>
          {repeatDevices === null ? (
            <div className="text-sm text-gray-400 py-4">
              Select a tenant to see per-device detail — the aggregated view has no device drill-down.
            </div>
          ) : repeatDevices.length === 0 ? (
            <div className="text-sm text-gray-400 py-4">No devices needed more than one attempt in this window.</div>
          ) : (
            <div className="space-y-2">
              {repeatDevices.map((d) => (
                <div key={d.serialNumber} className="text-sm">
                  <div className="flex items-center justify-between gap-2">
                    <Link
                      href={sessionHref(d.lastSessionId)}
                      className="text-gray-700 hover:text-blue-600 font-mono text-xs truncate"
                      title={`Open the latest enrollment of ${d.serialNumber}`}
                    >
                      {d.serialNumber}
                    </Link>
                    <span className="text-xs font-medium text-amber-700 flex-shrink-0">
                      {d.attempts} attempts
                    </span>
                  </div>
                  <div className="text-xs text-gray-400 flex items-center gap-2">
                    <TruncatedLabel text={d.model || d.manufacturer || "unknown model"} className="max-w-[160px]" />
                    <span className={d.lastStatus === "Succeeded" ? "text-green-600" : d.lastStatus === "Failed" ? "text-red-500" : "text-gray-400"}>
                      last: {d.lastStatus}
                    </span>
                  </div>
                  {d.lastFailureReason && (
                    <div className="text-xs text-gray-400">
                      <TruncatedLabel text={d.lastFailureReason} className="max-w-full" />
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
