"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { selectPriorSessions, type DeviceSessionRefDto } from "./deviceHistoryPrior";

export type { DeviceSessionRefDto } from "./deviceHistoryPrior";

/**
 * F2 device-history banner (insights spec §F2 "Surfaces"): shown when the device behind this
 * session has PRIOR terminal sessions — "Attempt N for this device · View history". The attempt
 * number is SERVER-computed (journey semantics live in the backend calculator; live sessions get
 * their would-be position via the virtual-attempt rule). The expandable list renders the chain
 * refs verbatim: durations are the sessions' authoritative DurationSeconds (never recomputed
 * from timestamps; Incomplete honestly has none). Fetch is fail-soft — no history, no banner.
 */

export interface DeviceHistoryDto {
  tenantId: string;
  serialKey: string;
  serialNumber: string;
  manufacturer: string;
  model: string;
  chain: DeviceSessionRefDto[];
  currentJourneyAttempts: number;
  journeyCount: number;
  journeyVersion: number;
}

interface DeviceHistoryResponse {
  success: boolean;
  history: DeviceHistoryDto | null;
  attemptNumber: number | null;
}

function formatDuration(seconds: number | null): string {
  if (seconds === null || seconds === undefined) return "—";
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.round(s / 60)} min`;
  return `${Math.floor(s / 3600)}h ${Math.round((s % 3600) / 60)}m`;
}

const STATUS_PILL: Record<string, string> = {
  Succeeded: "bg-green-100 text-green-800",
  Failed: "bg-red-100 text-red-800",
  Incomplete: "bg-slate-100 text-slate-600",
};

export default function DeviceHistoryBanner({
  sessionId,
  sessionStartedAt,
  serialNumber,
  effectiveTenantId,
  linkTenantId,
  getAccessToken,
}: {
  sessionId: string;
  /** The viewed session's StartedAt — anchor for the "previous enrollments" count when the session is not (yet) in the chain. */
  sessionStartedAt?: string;
  serialNumber: string | undefined;
  /** Tenant used for the API read (resolved session tenant / GA override); undefined = own tenant. */
  effectiveTenantId?: string;
  /** Carried into session links so a GA/MSP drill-in stays in the viewed tenant. */
  linkTenantId?: string;
  getAccessToken: () => Promise<string | null>;
}) {
  const [data, setData] = useState<DeviceHistoryResponse | null>(null);
  const [expanded, setExpanded] = useState(false);

  useEffect(() => {
    if (!sessionId || !serialNumber) {
      setData(null);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const response = await authenticatedFetch(
          api.metrics.deviceHistory(serialNumber, sessionId, effectiveTenantId),
          getAccessToken
        );
        if (!response.ok) return;
        const json = (await response.json()) as DeviceHistoryResponse;
        if (!cancelled) setData(json);
      } catch {
        // fail-soft: no banner
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, serialNumber, effectiveTenantId]);

  const chain = data?.history?.chain ?? [];
  const priorSessions = selectPriorSessions(chain, sessionId, sessionStartedAt);
  if (priorSessions.length === 0) return null;

  const attemptNumber = data?.attemptNumber;
  const sessionHref = (id: string) =>
    `/sessions/${id}${linkTenantId ? `?tenantId=${encodeURIComponent(linkTenantId)}` : ""}`;

  return (
    <div className="bg-amber-50 border border-amber-200 rounded-lg px-4 py-3 mb-6">
      <div className="flex items-center justify-between flex-wrap gap-2">
        <div className="flex items-center gap-2 text-sm text-amber-900">
          <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <span>
            {attemptNumber != null ? (
              <>
                <span className="font-medium">Attempt {attemptNumber}</span> for this device
              </>
            ) : (
              <span className="font-medium">This device has enrolled before</span>
            )}
            <span className="text-amber-700">
              {" "}· {priorSessions.length} previous enrollment{priorSessions.length === 1 ? "" : "s"} recorded
            </span>
          </span>
        </div>
        <button
          onClick={() => setExpanded(!expanded)}
          className="text-sm text-amber-800 hover:text-amber-900 underline underline-offset-2"
        >
          {expanded ? "Hide history" : "View history"}
        </button>
      </div>

      {expanded && (
        <div className="mt-3 border-t border-amber-200 pt-2">
          <div className="text-xs text-amber-700 mb-2" title="The 20 most recent terminal enrollments of this device (by serial number). Open sessions are not attempts. Durations are the sessions' recorded enrollment durations (pre-provisioning pause excluded).">
            Terminal enrollments of {data?.history?.serialNumber || serialNumber}
            {data?.history?.model ? ` · ${data.history.model}` : ""}
          </div>
          <div className="space-y-1">
            {[...chain].reverse().map((r) => {
              const isCurrent = r.sessionId === sessionId;
              const started = new Date(r.startedAt);
              return (
                <div
                  key={r.sessionId}
                  className={`flex items-center flex-wrap gap-x-3 gap-y-1 text-sm rounded px-2 py-1 ${isCurrent ? "bg-amber-100/70" : ""}`}
                >
                  <span className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${STATUS_PILL[r.status] ?? "bg-gray-100 text-gray-600"}`}>
                    {r.status}
                  </span>
                  <span className="text-gray-700">{started.toLocaleString()}</span>
                  <span className="text-gray-500">{formatDuration(r.durationSeconds)}</span>
                  {r.isPreProvisioned && (
                    <span className="text-xs text-violet-700 bg-violet-50 px-1.5 py-0.5 rounded">Pre-provisioning</span>
                  )}
                  {r.enrollmentType === "v2" && (
                    <span className="text-xs text-blue-700 bg-blue-50 px-1.5 py-0.5 rounded">Device Preparation</span>
                  )}
                  {r.adminMarked && (
                    <span className="text-xs text-gray-500 bg-gray-100 px-1.5 py-0.5 rounded"
                      title="An administrator set this session's final status manually">admin-marked</span>
                  )}
                  {isCurrent ? (
                    <span className="text-xs text-amber-800">this session</span>
                  ) : (
                    <Link href={sessionHref(r.sessionId)} className="text-xs text-blue-600 hover:text-blue-800 underline underline-offset-2">
                      open
                    </Link>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
