"use client";

import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { GlobalAdminBanner } from "@/components/GlobalAdminBanner";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { StatsCard } from "./components/StatsCards";
import { WelcomeMessage } from "./components/WelcomeMessage";
import { SessionTable } from "./components/SessionTable";
import { DeleteConfirmModal, BlockConfirmModal } from "./components/ConfirmationModals";
import TipOfTheDay from "./components/TipOfTheDay";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useDeleteSession } from "./hooks/useDeleteSession";
import { useBlockDevice } from "./hooks/useBlockDevice";
import { useTenantSecurityConfig } from "./hooks/useTenantSecurityConfig";
import { useTenantList } from "./hooks/useTenantList";
import { delegatedScopedTenantList, upnDomain } from "@/utils/homeTenantScope";
import { useDashboardFilters } from "./hooks/useDashboardFilters";
import { useDashboardSessions } from "./hooks/useDashboardSessions";
import { useDashboardStats } from "./hooks/useDashboardStats";

export default function Home() {
  // useSearchParams() in HomeContent requires a Suspense boundary for static prerender.
  return (
    <Suspense fallback={null}>
      <HomeContent />
    </Suspense>
  );
}

const FULL_WIDTH_STORAGE_KEY = "dashboard_fullWidth";

// Canonical status-filter values (mirrors the status badges in SessionTable). Guards the
// `?status=` deep-link so only a real bucket seeds the filter.
const VALID_STATUS_FILTERS = new Set([
  "Succeeded", "InProgress", "Pending", "Stalled", "AwaitingUser", "Failed", "Incomplete",
]);

function HomeContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  // Full-width layout: URL `?span=full|wide|max` overrides + persists; `?span=default|normal|off` clears.
  // Without a URL override, falls back to the last user choice from localStorage.
  const [fullWidth, setFullWidth] = useState<boolean>(() => {
    const span = searchParams?.get("span")?.toLowerCase();
    if (span === "full" || span === "wide" || span === "max") return true;
    if (span === "default" || span === "normal" || span === "off") return false;
    if (typeof window !== "undefined") {
      try {
        return localStorage.getItem(FULL_WIDTH_STORAGE_KEY) === "1";
      } catch { /* ignore */ }
    }
    return false;
  });

  // Persist any URL-driven override on first mount so it survives subsequent visits without the param.
  useEffect(() => {
    const span = searchParams?.get("span")?.toLowerCase();
    if (span === "full" || span === "wide" || span === "max") {
      try { localStorage.setItem(FULL_WIDTH_STORAGE_KEY, "1"); } catch { /* ignore */ }
    } else if (span === "default" || span === "normal" || span === "off") {
      try { localStorage.setItem(FULL_WIDTH_STORAGE_KEY, "0"); } catch { /* ignore */ }
    }
    // Run once on mount; URL re-sync happens via toggleFullWidth.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const toggleFullWidth = useCallback(() => {
    setFullWidth((prev) => {
      const next = !prev;
      try { localStorage.setItem(FULL_WIDTH_STORAGE_KEY, next ? "1" : "0"); } catch { /* ignore */ }
      if (typeof window !== "undefined") {
        const url = new URL(window.location.href);
        if (next) url.searchParams.set("span", "full");
        else url.searchParams.delete("span");
        window.history.replaceState(null, "", url.toString());
      }
      return next;
    });
  }, []);

  const mainClassName = fullWidth
    ? "w-full px-4 sm:px-6 lg:px-8 py-4"
    : "max-w-7xl mx-auto py-4 sm:px-6 lg:px-8";
  const { user, logout, getAccessToken, isPreviewBlocked, hasGlobalScope } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  // `?tenant=<id>` deep-links a cross-tenant view onto one tenant — used by the /fleet card grid to drill
  // a managed tenant into this dashboard. Ignored for non-cross-tenant users (the filter is unused there).
  const initialTenantFilter = searchParams?.get("tenant") ?? "";
  // `?search=` + `?status=` deep-link a pre-filtered session list — used by Fleet Health's
  // "Health by Device Model" rows to drill a model into the dashboard filtered to Failed.
  // Status is validated against the known set so a junk param can't hide every session.
  const initialSearchQuery = searchParams?.get("search") ?? "";
  const rawStatusParam = searchParams?.get("status");
  const initialStatusFilter = rawStatusParam && VALID_STATUS_FILTERS.has(rawStatusParam)
    ? rawStatusParam
    : null;
  const [tenantIdFilter, setTenantIdFilter] = useState(initialTenantFilter);
  // Mirrors the last filter value the user actually submitted (Submit / Clear).
  // Drives the stats refetch — server-side stats follow the submitted scope so
  // typing into the filter input doesn't trigger a backend round-trip per keystroke.
  const [submittedTenantIdFilter, setSubmittedTenantIdFilter] = useState(initialTenantFilter);
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();

  const signalR = useSignalR();
  const { tenantId } = useTenant();

  // A delegated ("MSP") admin browses cross-tenant bounded to their managed subset. Cross-tenant mode drives
  // the /global endpoints + tenant filter UI for a real GA in GA mode OR a delegated admin; an empty filter
  // is the bounded aggregate (backend restricts it to the managed tenants). The global-admins SignalR
  // broadcast group stays real-GA-only — a delegated caller has no platform scope and would be rejected.
  const isDelegated = user?.isDelegated ?? false;
  const crossTenant = (globalAdminMode && hasGlobalScope) || isDelegated;
  const joinGlobalAdmins = globalAdminMode && hasGlobalScope;

  const {
    showBlockConfirm, sessionToBlock, blockingDevice, blockedDevicesSet, setBlockedDevicesSet,
    blockDevice, confirmBlock, cancelBlock,
  } = useBlockDevice(getAccessToken, addNotification, adminMode, crossTenant);

  const {
    sessions, loading, hasMore, loadingMore,
    refetch, refetchWith, loadMore, loadAll, removeSession,
  } = useDashboardSessions({
    user, tenantId, globalAdminMode: crossTenant, joinGlobalAdmins, tenantIdFilter, adminMode,
    getAccessToken, addNotification, setBlockedDevicesSet, signalR,
  });

  const {
    showDeleteConfirm, sessionToDelete, pendingDeletions,
    deleteSession, confirmDelete, cancelDelete,
  } = useDeleteSession(getAccessToken, addNotification, adminMode, removeSession);

  const {
    searchQuery, setSearchQuery,
    statusFilter, setStatusFilter,
    sortColumn, sortDirection, handleSort,
    columnFilters, setColumnFilters,
    currentPage, sessionsPerPage, handleSessionsPerPageChange,
    handlePreviousPage, handleNextPage,
    effectiveSessions, filteredSessions, sortedSessions, paginatedSessions,
    totalPages,
  } = useDashboardFilters({
    sessions,
    blockedDevicesSet,
    tenantId,
    globalAdminMode: crossTenant,
    tenantIdFilter,
    hasMore,
    loadingMore,
    loadMore,
    initialSearchQuery,
    initialStatusFilter,
  });

  // Stats cards: server-side aggregation so the numbers don't drift with whatever
  // the client has paginated into view. Refreshes on SignalR newSession/newevents
  // (debounced) and on SignalR reconnect to recover from any missed messages.
  const isRegularUser = !!user && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && !user.isDelegated && user.role !== "Operator";
  const { stats: dashboardStats } = useDashboardStats({
    tenantId,
    globalAdminMode: crossTenant,
    submittedTenantIdFilter,
    // Delegated ("MSP") reader (no platform scope): bound the stats filter to the managed set, mirroring the
    // session list. A delegated user who is ALSO GA/Reader stays unbounded.
    isDelegatedScope: isDelegated && !hasGlobalScope,
    delegatedTenantIds: user?.delegatedTenantIds,
    getAccessToken,
    addNotification,
    signalR,
    disabled: isRegularUser,
  });

  // Redirect users without own-tenant/platform/delegated scope away from the session list to /progress. A
  // delegated ("MSP") admin now STAYS on the dashboard (cross-tenant bounded session browser); their /fleet
  // card grid is the landing overview but they may browse sessions here. A read-only Global Reader stays too.
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && !user.isDelegated && user.role !== 'Operator') {
      router.replace("/progress");
    }
  }, [user, router]);

  const serialValidationEnabled = useTenantSecurityConfig(tenantId, user, getAccessToken, addNotification);
  const rawTenantList = useTenantList(crossTenant, getAccessToken);
  // Delegated: bound the tenant filter's autocomplete to the managed subset (defense in depth on top of the
  // backend-bounded config/all), plus the caller's own HOME tenant when they hold a member role there —
  // home-tenant reads route via the member path (see utils/homeTenantScope.ts). GA/Reader: the full list.
  const tenantList = useMemo(() => {
    if (!isDelegated || hasGlobalScope) return rawTenantList;
    return delegatedScopedTenantList(
      rawTenantList, user?.delegatedTenantIds, user?.tenantId, upnDomain(user?.upn), !!user?.role);
  }, [rawTenantList, isDelegated, hasGlobalScope, user?.delegatedTenantIds, user?.tenantId, user?.upn, user?.role]);

  // Disable global-scope mode for users without platform scope. A read-only Global Reader keeps it
  // (their cross-tenant view is read-only-safe; writes are gated separately + backend-enforced).
  useEffect(() => {
    if (user && !user.isGlobalAdmin && !user.isGlobalReader && globalAdminMode) {
      console.log('[Home] User has no platform scope, disabling global mode');
      setGlobalAdminMode(false);
    }
  }, [user, globalAdminMode]);

  // Clear the tenant filter when cross-tenant mode turns off (refetch is owned by useDashboardSessions).
  // Keyed on crossTenant (not raw globalAdminMode) so a delegated ("MSP") admin — whose crossTenant is
  // always on — keeps any `?tenant=` deep-link / typed filter instead of having it wiped on mount.
  useEffect(() => {
    if (!crossTenant) {
      setTenantIdFilter("");
      setSubmittedTenantIdFilter("");
    }
  }, [crossTenant]);

  // Auto-load more when the user needs more sessions than currently loaded
  // (e.g. increased sessionsPerPage, paginated forward, or applied a sort/column filter
  // that would benefit from the full dataset). Cheap server roundtrip, paid only on demand.
  useEffect(() => {
    if (loading || loadingMore || !hasMore) return;
    const needed = currentPage * sessionsPerPage;
    if (sessions.length < needed) {
      loadMore();
    }
  }, [sessions.length, currentPage, sessionsPerPage, hasMore, loading, loadingMore, loadMore]);

  // Auto-load ALL remaining sessions when search is active and local results are insufficient.
  // Uses a 500ms debounce so rapid typing doesn't trigger unnecessary loads.
  const autoLoadTimerRef = useRef<ReturnType<typeof setTimeout>>();
  useEffect(() => {
    if (autoLoadTimerRef.current) clearTimeout(autoLoadTimerRef.current);

    const query = searchQuery.trim();
    if (!query || query.length < 2) return;
    if (loading || loadingMore || !hasMore) return;
    // Skip duration queries — those are local-only filters
    if (/^[><]=?\s*\d+$/.test(query)) return;
    if (filteredSessions.length >= 8) return;

    autoLoadTimerRef.current = setTimeout(() => {
      loadAll();
    }, 500);

    return () => { if (autoLoadTimerRef.current) clearTimeout(autoLoadTimerRef.current); };
  }, [searchQuery, filteredSessions.length, hasMore, loading, loadingMore, loadAll]);

  const applyTenantIdFilter = (value: string) => {
    setTenantIdFilter(value);
  };

  const submitTenantIdFilter = () => {
    setSubmittedTenantIdFilter(tenantIdFilter);
    refetch();
  };

  const clearTenantIdFilter = () => {
    setTenantIdFilter("");
    setSubmittedTenantIdFilter("");
    refetchWith("");
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
      {/* Delegated ("MSP") admin: blue cross-tenant banner. Empty filter = bounded aggregate over the
          managed tenants; a selected tenant = drill-in. (GA gets no banner here, as before.) */}
      <GlobalAdminBanner
        show={isDelegated}
        delegated
        subtitle={submittedTenantIdFilter ? "viewing one managed tenant" : "aggregating across your managed tenants"}
      />
      {/* Main content */}
      <main className={mainClassName}>
        <div className="px-4 sm:px-0">
          {/* Scheduled maintenance banner — TEMPORARY, remove after 2026-07-20 */}
          <div className="mb-4 bg-amber-50 border border-amber-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-amber-950/30 dark:border-amber-700/50">
            <svg className="w-4 h-4 text-amber-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <p className="text-sm text-amber-800 dark:text-amber-300">
              <span className="font-semibold">Scheduled infrastructure maintenance.</span>{" "}
              The platform is <span className="font-medium">not available</span> this weekend, from{" "}
              <span className="font-medium">Sat 18 Jul, 00:00</span> until{" "}
              <span className="font-medium">Mon 20 Jul, 00:00 CEST (UTC+2)</span> (expected).{" "}
              The portal cannot be reached and the ingestion API is offline. Agents continue collecting
              locally and re-sync once the platform is back online — no enrollment data is lost.
              If the work finishes early, the platform will come back sooner; the completion will be
              announced here and in the{" "}
              <a
                href="https://docs.autopilotmonitor.com/troubleshooting/service-announcements"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-amber-600 dark:hover:text-amber-200"
              >
                Service Announcements
              </a>
              .
            </p>
          </div>

          {/* Feedback & bug report banner */}
          <div className="mb-4 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
            <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
            </svg>
            <p className="text-sm text-blue-800 dark:text-blue-300">
              <span className="font-semibold">Private Preview.</span>{" "}
              The platform is under active development.{" "}
              If something looks off, check the{" "}
              <a
                href="https://docs.autopilotmonitor.com/changelog/platform-changelog"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Private Preview Changelog
              </a>{" "}
              or{" "}
              <a
                href="https://docs.autopilotmonitor.com/troubleshooting/service-announcements"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Known Issues
              </a>
              .{" "}
              Feedback or bug report?{" "}
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Open a GitHub issue
              </a>
              {" "}or message me on{" "}
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                LinkedIn
              </a>
              .
            </p>
          </div>

          {serialValidationEnabled === false && (
            <div className="mb-6 bg-red-600 border-2 border-red-700 rounded-xl p-5 shadow-lg">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex items-start gap-3">
                  <svg className="w-6 h-6 text-white mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                  </svg>
                  <div>
                    <p className="text-base font-bold text-white">Action required: Autopilot Device Validation is disabled</p>
                    <p className="text-sm text-red-100 mt-0.5">
                      Agent ingestion is blocked. Enable Autopilot Device Validation in Settings to start monitoring devices.
                    </p>
                  </div>
                </div>
                <a
                  href="/settings"
                  className="shrink-0 inline-flex items-center gap-2 bg-white text-red-700 font-semibold text-sm px-4 py-2 rounded-lg hover:bg-red-50 transition-colors"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  </svg>
                  Open Settings
                </a>
              </div>
            </div>
          )}

          {/* Stats cards — server-aggregated (see useDashboardStats).
              `dashboardStats === null` covers both initial load and post-scope-change
              reset; show a non-zero placeholder so a fetch error doesn't masquerade
              as legitimate zeros. */}
          <div className="grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-5 mb-2">
            <StatsCard
              title="Active Sessions"
              value={dashboardStats ? dashboardStats.activeCount.toString() : "..."}
              description="Currently enrolling"
              color="blue"
            />
            <StatsCard
              title="Success Rate"
              value={dashboardStats ? `${dashboardStats.successRatePct}%` : "..."}
              description="Last 7 days"
              color="green"
            />
            <StatsCard
              title="Avg. Duration"
              value={dashboardStats ? `${dashboardStats.avgDurationMinutes} min` : "..."}
              description="Last 7 days"
              color="purple"
            />
            <StatsCard
              title="Total Today"
              value={dashboardStats ? dashboardStats.totalToday.toString() : "..."}
              description="Started today"
              color="indigo"
            />
            <StatsCard
              title="Failed Today"
              value={dashboardStats ? dashboardStats.failedToday.toString() : "..."}
              description="Needs attention"
              color="red"
            />
          </div>

          <TipOfTheDay />

          {/* Welcome message - only show when no sessions */}
          {!loading && sessions.length === 0 && <WelcomeMessage />}

          {/* Sessions List */}
          {sessions.length > 0 && (
            <SessionTable
              sessions={effectiveSessions}
              filteredSessions={filteredSessions}
              sortedSessions={sortedSessions}
              paginatedSessions={paginatedSessions}
              searchQuery={searchQuery}
              onSearchQueryChange={setSearchQuery}
              statusFilter={statusFilter}
              onStatusFilterChange={setStatusFilter}
              sortColumn={sortColumn}
              sortDirection={sortDirection}
              onSort={handleSort}
              currentPage={currentPage}
              totalPages={totalPages}
              onPreviousPage={handlePreviousPage}
              onNextPage={handleNextPage}
              sessionsPerPage={sessionsPerPage}
              onSessionsPerPageChange={handleSessionsPerPageChange}
              hasMore={hasMore}
              loadingMore={loadingMore}
              onLoadAll={loadAll}
              adminMode={adminMode}
              globalAdminMode={crossTenant}
              tenantIdFilter={tenantIdFilter}
              onTenantIdFilterChange={applyTenantIdFilter}
              onTenantIdFilterSubmit={submitTenantIdFilter}
              onTenantIdFilterClear={clearTenantIdFilter}
              tenantList={tenantList}
              blockedDevicesSet={blockedDevicesSet}
              isPreviewBlocked={isPreviewBlocked}
              user={user}
              columnFilters={columnFilters}
              onColumnFiltersChange={setColumnFilters}
              onDeleteSession={deleteSession}
              pendingDeletions={pendingDeletions}
              onBlockDevice={blockDevice}
              fullWidth={fullWidth}
              onToggleFullWidth={toggleFullWidth}
              sessionLinkTarget={
                crossTenant
                  ? (s) =>
                      s.tenantId
                        ? `/sessions/${s.sessionId}?tenantId=${encodeURIComponent(s.tenantId)}`
                        : `/sessions/${s.sessionId}`
                  : undefined
              }
            />
          )}
        </div>
      </main>

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && sessionToDelete && (
        <DeleteConfirmModal
          sessionToDelete={sessionToDelete}
          onConfirm={confirmDelete}
          onCancel={cancelDelete}
        />
      )}

      {/* Block Device Confirmation Modal */}
      {showBlockConfirm && sessionToBlock && (
        <BlockConfirmModal
          sessionToBlock={sessionToBlock}
          blockingDevice={blockingDevice}
          onConfirm={confirmBlock}
          onCancel={cancelBlock}
        />
      )}
    </div>
    </ProtectedRoute>
  );
}
