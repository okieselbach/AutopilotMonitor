"use client";

import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
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
  const { user, logout, getAccessToken, isPreviewBlocked } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  const [tenantIdFilter, setTenantIdFilter] = useState("");
  // Mirrors the last filter value the user actually submitted (Submit / Clear).
  // Drives the stats refetch — server-side stats follow the submitted scope so
  // typing into the filter input doesn't trigger a backend round-trip per keystroke.
  const [submittedTenantIdFilter, setSubmittedTenantIdFilter] = useState("");
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();

  const signalR = useSignalR();
  const { tenantId } = useTenant();

  const {
    showBlockConfirm, sessionToBlock, blockingDevice, blockedDevicesSet, setBlockedDevicesSet,
    blockDevice, confirmBlock, cancelBlock,
  } = useBlockDevice(getAccessToken, addNotification, adminMode, globalAdminMode);

  const {
    sessions, loading, hasMore, loadingMore,
    refetch, refetchWith, loadMore, loadAll, removeSession,
  } = useDashboardSessions({
    user, tenantId, globalAdminMode, tenantIdFilter, adminMode,
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
    globalAdminMode,
    tenantIdFilter,
    hasMore,
    loadingMore,
    loadMore,
  });

  // Stats cards: server-side aggregation so the numbers don't drift with whatever
  // the client has paginated into view. Refreshes on SignalR newSession/newevents
  // (debounced) and on SignalR reconnect to recover from any missed messages.
  const isRegularUser = !!user && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && user.role !== "Operator";
  const { stats: dashboardStats } = useDashboardStats({
    tenantId,
    globalAdminMode,
    submittedTenantIdFilter,
    getAccessToken,
    addNotification,
    signalR,
    disabled: isRegularUser,
  });

  // Redirect users without own-tenant/platform scope away from the session list. A delegated ("MSP") admin
  // manages OTHER tenants and belongs on the fleet overview, not the end-user progress portal; everyone else
  // without scope goes to /progress. A read-only Global Reader has cross-tenant read scope → stays.
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && user.role !== 'Operator') {
      router.replace(user.isDelegated ? "/fleet" : "/progress");
    }
  }, [user, router]);

  const serialValidationEnabled = useTenantSecurityConfig(tenantId, user, getAccessToken, addNotification);
  const tenantList = useTenantList(globalAdminMode, getAccessToken);

  // Disable global-scope mode for users without platform scope. A read-only Global Reader keeps it
  // (their cross-tenant view is read-only-safe; writes are gated separately + backend-enforced).
  useEffect(() => {
    if (user && !user.isGlobalAdmin && !user.isGlobalReader && globalAdminMode) {
      console.log('[Home] User has no platform scope, disabling global mode');
      setGlobalAdminMode(false);
    }
  }, [user, globalAdminMode]);

  // Clear tenant filter when leaving Global Admin mode (refetch is owned by useDashboardSessions)
  useEffect(() => {
    if (!globalAdminMode) {
      setTenantIdFilter("");
      setSubmittedTenantIdFilter("");
    }
  }, [globalAdminMode]);

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
      {/* Main content */}
      <main className={mainClassName}>
        <div className="px-4 sm:px-0">
          {/* Feedback & bug report banner */}
          <div className="mb-4 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
            <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
            </svg>
            <p className="text-sm text-blue-800 dark:text-blue-300">
              <span className="font-semibold">Private Preview.</span>{" "}
              The platform is under active development.{" "}
              If something looks off, check the{" "}
              <Link
                href="/changelog"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Private Preview Changelog
              </Link>{" "}
              or{" "}
              <Link
                href="/docs/known-issues"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Known Issues
              </Link>
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

          {/* Agent V2 rollout banner */}
          <div className="mb-4 bg-amber-50 border border-amber-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-amber-950/30 dark:border-amber-700/50">
            <svg className="w-4 h-4 text-amber-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
            </svg>
            <div className="text-sm text-amber-900 dark:text-amber-200 space-y-2">
              <p>
                <span className="font-semibold">Agent V2 rolling out.</span>{" "}
                The agent has been rebuilt on a cleaner internal architecture for more reliable session detection,
                stricter completion logic, and easier diagnostics. Early rollout may still surface a few unexpected
                behaviors.
              </p>
              <ul className="list-disc pl-5 space-y-1">
                <li>
                  <span className="font-medium">Action — update the Intune bootstrap script.</span>{" "}
                  Replace <code className="text-xs bg-amber-100 dark:bg-amber-900/50 px-1 py-0.5 rounded">Install-AutopilotMonitor.ps1</code>{" "}
                  in your Intune tenant with the latest version from the repository.
                </li>
                <li>
                  <span className="font-medium">Notice anything off?</span>{" "}
                  Use the{" "}
                  <span className="italic">Report Session</span>{" "}
                  button on a session,{" "}
                  <Link
                    href="/settings/tenant/support"
                    className="underline font-medium hover:text-amber-700 dark:hover:text-amber-100"
                  >
                    Submit Logs
                  </Link>{" "}
                  for session-less diagnostics,{" "}
                  <a
                    href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="underline font-medium hover:text-amber-700 dark:hover:text-amber-100"
                  >
                    open a GitHub issue
                  </a>
                  , or{" "}
                  <a
                    href="https://www.linkedin.com/in/oliver-kieselbach/"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="underline font-medium hover:text-amber-700 dark:hover:text-amber-100"
                  >
                    DM me on LinkedIn
                  </a>
                  .
                </li>
              </ul>
            </div>
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
              globalAdminMode={globalAdminMode}
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
