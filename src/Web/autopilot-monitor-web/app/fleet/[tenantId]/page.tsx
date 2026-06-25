"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { useTenantList } from "@/hooks/useTenantList";
import { SessionTable } from "@/app/dashboard/components/SessionTable";
import { useDashboardFilters } from "@/app/dashboard/hooks/useDashboardFilters";
import { useFleetSummaries } from "../hooks/useFleetSummaries";
import { useTenantSessions } from "./hooks/useTenantSessions";
import type { FleetSummary } from "../lib/fleetRollup";

const DAYS = 30;
const FLEET_FULL_WIDTH_KEY = "fleet_fullWidth";
// Stable empty refs so the read-only SessionTable / filter memos don't churn on every render.
const EMPTY_SET: ReadonlySet<string> = new Set();
const NOOP = () => {};

function ArrowLeftIcon({ className = "h-4 w-4" }: { className?: string }) {
  return (
    <svg className={className} fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor" aria-hidden="true">
      <path strokeLinecap="round" strokeLinejoin="round" d="M10.5 19.5 3 12m0 0 7.5-7.5M3 12h18" />
    </svg>
  );
}

/**
 * Fleet drill-in — one managed tenant's read-only session browser for a delegated ("MSP") admin.
 *
 * The tenant is an explicit route param, so every read is a single-tenant `/global/*?tenantId=` call the
 * backend bounds to the caller's delegated scope (GlobalReadOrDelegatedSubset). No all-tenants path here; as
 * defense in depth we also refuse a tenantId the caller doesn't manage (unless full platform scope) BEFORE
 * issuing any fetch.
 *
 * Reuses the dashboard's session browser (useDashboardFilters + SessionTable) for full parity — search, sort,
 * status filter, column selection, page-size, full-width, Next/Prev pagination (which pulls more pages from
 * the server as you advance). Rendered READ-ONLY: adminMode/globalAdminMode=false and user=null hide the
 * delete/block actions and the cross-tenant filter box; rows open `/sessions/{id}?tenantId=` so the detail
 * page loads the managed tenant's session (also read-only). Inspector stays Global-Admin-only.
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

  // Full-width toggle (own localStorage key; read post-mount to avoid an SSR hydration mismatch).
  const [fullWidth, setFullWidth] = useState(false);
  useEffect(() => {
    try { setFullWidth(localStorage.getItem(FLEET_FULL_WIDTH_KEY) === "1"); } catch { /* ignore */ }
  }, []);
  const toggleFullWidth = useCallback(() => {
    setFullWidth((prev) => {
      const next = !prev;
      try { localStorage.setItem(FLEET_FULL_WIDTH_KEY, next ? "1" : "0"); } catch { /* ignore */ }
      return next;
    });
  }, []);

  // Reuse the dashboard's filter/sort/pagination engine. tenantId=null + globalAdminMode=false ⇒ no per-tenant
  // re-filter (the data is already single-tenant) and the global-only columns / tenant-filter box stay hidden.
  const filters = useDashboardFilters({
    sessions,
    blockedDevicesSet: EMPTY_SET as Set<string>,
    tenantId: null,
    globalAdminMode: false,
    tenantIdFilter: "",
    hasMore,
    loadingMore,
    loadMore,
  });

  const linkTarget = useCallback(
    (sessionId: string) => `/sessions/${sessionId}?tenantId=${encodeURIComponent(tenantId)}`,
    [tenantId]
  );

  const containerClass = fullWidth
    ? "w-full px-4 sm:px-6 lg:px-8 py-4"
    : "mx-auto max-w-7xl p-4 sm:p-6 lg:p-8";

  return (
    <div className={containerClass}>
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
            <StatTile label={`Sessions · ${DAYS}d`} value={summary ? String(summary.totalLastNDays) : "—"} />
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
                successTone(summary.successRatePct, summary.succeededLastNDays + summary.failedLastNDays) === "danger"
                  ? "danger"
                  : "default"
              }
            />
          </div>

          {/* Session browser — same component the dashboard uses, read-only. */}
          {error ? (
            <div className="rounded-lg border border-gray-200 bg-white p-10 text-center text-sm text-red-600 shadow-sm dark:border-gray-700 dark:bg-gray-800 dark:text-red-400">
              Couldn&apos;t load sessions for this tenant.
            </div>
          ) : loading && sessions.length === 0 ? (
            <div className="flex items-center justify-center rounded-lg border border-gray-200 bg-white p-12 shadow-sm dark:border-gray-700 dark:bg-gray-800">
              <div className="h-6 w-6 animate-spin rounded-full border-b-2 border-blue-600" />
              <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading sessions…</span>
            </div>
          ) : sessions.length === 0 ? (
            <div className="rounded-lg border border-dashed border-gray-300 p-10 text-center text-sm text-gray-500 dark:border-gray-700 dark:text-gray-400">
              No sessions in the last {DAYS} days.
            </div>
          ) : (
            <SessionTable
              sessions={filters.effectiveSessions}
              filteredSessions={filters.filteredSessions}
              sortedSessions={filters.sortedSessions}
              paginatedSessions={filters.paginatedSessions}
              searchQuery={filters.searchQuery}
              onSearchQueryChange={filters.setSearchQuery}
              statusFilter={filters.statusFilter}
              onStatusFilterChange={filters.setStatusFilter}
              sortColumn={filters.sortColumn}
              sortDirection={filters.sortDirection}
              onSort={filters.handleSort}
              currentPage={filters.currentPage}
              totalPages={filters.totalPages}
              onPreviousPage={filters.handlePreviousPage}
              onNextPage={filters.handleNextPage}
              sessionsPerPage={filters.sessionsPerPage}
              onSessionsPerPageChange={filters.handleSessionsPerPageChange}
              hasMore={hasMore}
              loadingMore={loadingMore}
              onLoadAll={loadMore}
              adminMode={false}
              globalAdminMode={false}
              tenantIdFilter=""
              onTenantIdFilterChange={NOOP}
              onTenantIdFilterSubmit={NOOP}
              onTenantIdFilterClear={NOOP}
              tenantList={[]}
              blockedDevicesSet={EMPTY_SET as Set<string>}
              isPreviewBlocked={false}
              user={null}
              columnFilters={filters.columnFilters}
              onColumnFiltersChange={filters.setColumnFilters}
              onDeleteSession={NOOP}
              pendingDeletions={EMPTY_SET}
              onBlockDevice={NOOP}
              fullWidth={fullWidth}
              onToggleFullWidth={toggleFullWidth}
              sessionLinkTarget={linkTarget}
            />
          )}
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
