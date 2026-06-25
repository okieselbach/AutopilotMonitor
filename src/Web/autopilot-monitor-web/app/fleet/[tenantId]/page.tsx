"use client";

import { useMemo } from "react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { useTenantList } from "@/hooks/useTenantList";
import { SessionStatusBadge } from "@/components/SessionStatusBadge";
import { formatDateTime, formatDurationShort } from "@/utils/sessionFormat";
import type { Session } from "@/types/session";
import { useFleetSummaries } from "../hooks/useFleetSummaries";
import { useTenantSessions } from "./hooks/useTenantSessions";
import type { FleetSummary } from "../lib/fleetRollup";

const DAYS = 30;

function ArrowLeftIcon({ className = "h-4 w-4" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5 3 12m0 0 7.5-7.5M3 12h18" />
    </svg>
  );
}

/**
 * Fleet drill-in — one managed tenant's read-only overview for a delegated ("MSP") admin.
 *
 * The tenant is an explicit route param, so every read is a single-tenant `/global/*?tenantId=` call
 * (Phase 2a) the backend bounds to the caller's delegated scope (GlobalReadOrDelegatedSubset). There is no
 * all-tenants path here. As client-side defense in depth we also refuse a tenantId the caller doesn't
 * manage (unless they hold full platform scope) BEFORE issuing any fetch.
 *
 * Stats + session list, and each row opens the full session-detail page for the managed tenant via
 * `/sessions/{id}?tenantId=`. No new backend endpoint is needed: the detail reads are MemberRead + QueryParam,
 * which the delegated scope already rescues; the detail page renders read-only for a delegated viewer
 * (mutations hidden). Inspector stays Global-Admin-only.
 */
export default function FleetTenantPage() {
  const params = useParams<{ tenantId: string }>();
  const tenantId = decodeURIComponent(params.tenantId);

  const { user, hasGlobalScope, hasFleetScope } = useAuth();

  const allowed = useMemo(() => {
    if (hasGlobalScope) return true; // GA / Global Reader may view any tenant
    const managed = new Set((user?.delegatedTenantIds ?? []).map((t) => t.toLowerCase()));
    return managed.has(tenantId.toLowerCase());
  }, [hasGlobalScope, user?.delegatedTenantIds, tenantId]);

  // Gate every data fetch on `allowed` — an empty id makes the hooks no-op, so an unmanaged tenant never
  // triggers a request even though the backend would reject it anyway.
  const effectiveTenantId = allowed ? tenantId : "";

  const tenants = useTenantList(hasFleetScope);
  const domainName = useMemo(
    () => tenants.find((t) => t.tenantId.toLowerCase() === tenantId.toLowerCase())?.domainName || "",
    [tenants, tenantId]
  );

  const { summaries } = useFleetSummaries(effectiveTenantId ? [effectiveTenantId] : [], DAYS);
  const summary: FleetSummary | undefined = summaries[effectiveTenantId];

  const { sessions, loading, loadingMore, hasMore, error, loadMore } = useTenantSessions(effectiveTenantId, DAYS);

  return (
    <div className="mx-auto max-w-7xl p-4 sm:p-6 lg:p-8">
      <Link
        href="/fleet"
        className="mb-4 inline-flex items-center gap-1.5 text-sm text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200"
      >
        <ArrowLeftIcon className="h-4 w-4" />
        Back to fleet
      </Link>

      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900 dark:text-white">
          {domainName || "Managed tenant"}
        </h1>
        <p className="mt-1 break-all font-mono text-xs text-gray-400 dark:text-gray-500">{tenantId}</p>
      </div>

      {!allowed ? (
        <div className="rounded-lg border border-dashed border-gray-300 p-10 text-center text-gray-500 dark:border-gray-700 dark:text-gray-400">
          You don&apos;t manage this tenant.
        </div>
      ) : (
        <>
          {/* Stat tiles — terminal-only success rate, matching the fleet cards. */}
          <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
            <StatTile label="Active sessions" value={summary ? String(summary.activeCount) : "—"} />
            <StatTile
              label={`Sessions · ${DAYS}d`}
              value={summary ? String(summary.totalLastNDays) : "—"}
            />
            <StatTile
              label="Failed"
              value={summary ? String(summary.failedLastNDays) : "—"}
              tone={!!summary && summary.failedLastNDays > 0 ? "danger" : "default"}
            />
            <StatTile
              label="Success rate"
              value={summary ? `${summary.successRatePct}%` : "—"}
              tone={
                !!summary &&
                successTone(summary.successRatePct, summary.succeededLastNDays + summary.failedLastNDays) ===
                  "danger"
                  ? "danger"
                  : "default"
              }
            />
          </div>

          {/* Recent sessions */}
          <div className="rounded-lg border border-gray-200 bg-white shadow-sm dark:border-gray-700 dark:bg-gray-800">
            <div className="flex items-center justify-between border-b border-gray-100 px-5 py-3 dark:border-gray-700">
              <h2 className="text-sm font-semibold text-gray-900 dark:text-white">
                Recent sessions · last {DAYS} days
              </h2>
              {loading && <span className="text-xs text-gray-400">Loading…</span>}
            </div>

            {error ? (
              <div className="px-5 py-10 text-center text-sm text-red-600 dark:text-red-400">
                Couldn&apos;t load sessions for this tenant.
              </div>
            ) : !loading && sessions.length === 0 ? (
              <div className="px-5 py-10 text-center text-sm text-gray-500 dark:text-gray-400">
                No sessions in the last {DAYS} days.
              </div>
            ) : (
              <SessionList
                sessions={sessions}
                hasMore={hasMore}
                loadingMore={loadingMore}
                onLoadMore={loadMore}
                tenantId={tenantId}
              />
            )}
          </div>
        </>
      )}
    </div>
  );
}

type Tone = "default" | "danger";

/** Red emphasis only once there are TERMINAL sessions to judge (success rate is terminal-only). */
function successTone(pct: number, terminalCount: number): Tone {
  if (terminalCount === 0) return "default";
  return pct < 80 ? "danger" : "default";
}

function StatTile({ label, value, tone = "default" }: { label: string; value: string; tone?: Tone }) {
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-800">
      <div className="text-xs font-medium uppercase tracking-wider text-gray-400 dark:text-gray-500">
        {label}
      </div>
      <div
        className={`mt-1 text-2xl font-semibold ${
          tone === "danger" ? "text-red-600 dark:text-red-400" : "text-gray-900 dark:text-white"
        }`}
      >
        {value}
      </div>
    </div>
  );
}

const PHASE_LABELS: Record<number, string> = {
  0: "Setup start",
  1: "Device prep",
  2: "Device setup",
  3: "Apps (device)",
  4: "Account setup",
  5: "Apps (user)",
  6: "Finalizing",
  7: "Complete",
};

function phaseLabel(phase: number): string {
  return PHASE_LABELS[phase] ?? "—";
}

/** Read-only session table for the drill-in. Rows open the full session-detail page for the managed tenant
 * via `?tenantId=` — the backend serves the reads (MemberRead + delegated scope) and the detail page renders
 * read-only for a delegated viewer (write actions hidden). */
function SessionList({
  sessions,
  hasMore,
  loadingMore,
  onLoadMore,
  tenantId,
}: {
  sessions: Session[];
  hasMore: boolean;
  loadingMore: boolean;
  onLoadMore: () => void;
  tenantId: string;
}) {
  const router = useRouter();
  const open = (sessionId: string) =>
    router.push(`/sessions/${sessionId}?tenantId=${encodeURIComponent(tenantId)}`);
  return (
    <div className="overflow-x-auto">
      <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
        <thead>
          <tr className="text-left text-xs font-medium uppercase tracking-wider text-gray-400 dark:text-gray-500">
            <th className="px-5 py-2.5">Device</th>
            <th className="px-5 py-2.5">Model</th>
            <th className="px-5 py-2.5">Status</th>
            <th className="px-5 py-2.5">Phase</th>
            <th className="px-5 py-2.5 text-right">Events</th>
            <th className="px-5 py-2.5 text-right">Duration</th>
            <th className="px-5 py-2.5">Started</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100 dark:divide-gray-700/60">
          {sessions.map((s) => (
            <tr
              key={s.sessionId}
              onClick={() => open(s.sessionId)}
              className="cursor-pointer text-sm hover:bg-gray-50 dark:hover:bg-gray-700/40"
            >
              <td className="px-5 py-3">
                <div className="font-medium text-gray-900 dark:text-white">
                  {s.deviceName || s.serialNumber || "—"}
                </div>
                {s.deviceName && s.serialNumber && (
                  <div className="font-mono text-xs text-gray-400 dark:text-gray-500">{s.serialNumber}</div>
                )}
              </td>
              <td className="px-5 py-3 text-gray-600 dark:text-gray-300">
                {[s.manufacturer, s.model].filter(Boolean).join(" ") || "—"}
              </td>
              <td className="px-5 py-3">
                <SessionStatusBadge status={s.status} failureReason={s.failureReason} />
              </td>
              <td className="px-5 py-3 text-gray-600 dark:text-gray-300">{phaseLabel(s.currentPhase)}</td>
              <td className="px-5 py-3 text-right tabular-nums text-gray-600 dark:text-gray-300">
                {s.eventCount}
              </td>
              <td className="px-5 py-3 text-right tabular-nums text-gray-600 dark:text-gray-300">
                {formatDurationShort(s.durationSeconds)}
              </td>
              <td className="px-5 py-3 whitespace-nowrap text-gray-600 dark:text-gray-300">
                {formatDateTime(s.startedAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {hasMore && (
        <div className="border-t border-gray-100 px-5 py-3 text-center dark:border-gray-700">
          <button
            onClick={onLoadMore}
            disabled={loadingMore}
            className="rounded-md border border-gray-300 px-4 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
          >
            {loadingMore ? "Loading…" : "Load more"}
          </button>
        </div>
      )}
    </div>
  );
}
