"use client";

import { sessionUrl } from "@/lib/routes";
import Link from "next/link";
import { Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { GlobalAdminBanner } from "@/components/GlobalAdminBanner";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { StatsCard } from "./components/StatsCards";
import { WelcomeMessage } from "./components/WelcomeMessage";
import { ActivelyDevelopedBanner } from "./components/ActivelyDevelopedBanner";
import { SessionTable } from "./components/SessionTable";
import { TenantFilterBar } from "./components/TenantFilterBar";
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
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { formatDuration } from "@/lib/formatting";
import { hasTenantReadScope } from "@/lib/tenantScope";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";

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
  const { user, getAccessToken, isActivationPending, hasGlobalScope } = useAuth();
  const { addNotification } = useNotifications();
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
  // `?ruleId=` deep-links the fleet-context filter — reached from a session-detail analysis
  // card's "View sessions" link. The hit set (sessions where the rule fired, last 14 days)
  // loads once from the backend; until it resolves the list stays unfiltered.
  const initialRuleId = searchParams?.get("ruleId") ?? null;
  const [ruleFilter, setRuleFilter] = useState<{ ruleId: string; sessionIds: Set<string> | null } | null>(
    initialRuleId ? { ruleId: initialRuleId, sessionIds: null } : null
  );
  const [tenantIdFilter, setTenantIdFilter] = useState(initialTenantFilter);
  // Mirrors the last filter value the user actually submitted (Submit / Clear).
  // Drives the stats refetch — server-side stats follow the submitted scope so
  // typing into the filter input doesn't trigger a backend round-trip per keystroke.
  const [submittedTenantIdFilter, setSubmittedTenantIdFilter] = useState(initialTenantFilter);
  const { adminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();

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
    showBlockConfirm, blockTargets, blockingDevice, blockedDevicesSet, setBlockedDevicesSet,
    blockDevices, confirmBlock, cancelBlock,
  } = useBlockDevice(getAccessToken, addNotification, adminMode, crossTenant);

  const {
    sessions, loading, hasMore, loadingMore, loadingAll,
    refetch, refetchWith, loadMore, loadAll, searchAll, removeSession,
  } = useDashboardSessions({
    user, tenantId, globalAdminMode: crossTenant, joinGlobalAdmins, tenantIdFilter, adminMode,
    getAccessToken, addNotification, setBlockedDevicesSet, signalR,
  });

  const {
    showDeleteConfirm, deleteTargets, pendingDeletions,
    deleteSessions, confirmDelete, cancelDelete,
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
    ruleFilterId: ruleFilter?.ruleId ?? null,
    ruleSessionIds: ruleFilter?.sessionIds ?? null,
  });

  // Resolve the rule filter's hit set once. Failure degrades to an empty set (the
  // chip then reads "0 enrollments") — same fail-soft posture as the backend read.
  useEffect(() => {
    if (!ruleFilter || ruleFilter.sessionIds !== null) return;
    let cancelled = false;
    const fetchHits = async () => {
      const ids = new Set<string>();
      try {
        const response = await authenticatedFetch(
          api.metrics.ruleHitSessions(ruleFilter.ruleId, 14, initialTenantFilter || undefined),
          getAccessToken
        );
        if (response.ok) {
          const data = await response.json();
          if (Array.isArray(data.sessionIds)) {
            for (const id of data.sessionIds) ids.add(String(id));
          }
        }
      } catch {
        // Fail-soft: empty set below.
      }
      if (!cancelled) {
        setRuleFilter((prev) =>
          prev && prev.sessionIds === null ? { ...prev, sessionIds: ids } : prev
        );
      }
    };
    fetchHits();
    return () => { cancelled = true; };
  }, [ruleFilter, initialTenantFilter, getAccessToken]);

  const clearRuleFilter = useCallback(() => {
    setRuleFilter(null);
    if (typeof window !== "undefined") {
      const url = new URL(window.location.href);
      url.searchParams.delete("ruleId");
      window.history.replaceState(null, "", url.toString());
    }
  }, []);

  // Stats cards: server-side aggregation so the numbers don't drift with whatever
  // the client has paginated into view. Refreshes on SignalR newSession/newevents
  // (debounced) and on SignalR reconnect to recover from any missed messages.
  const isRegularUser = !!user && !hasTenantReadScope(user);
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
    if (user && !hasTenantReadScope(user)) {
      router.replace("/progress");
    }
  }, [user, router]);

  const { serialValidationEnabled, proContactMissing } = useTenantSecurityConfig(tenantId, user, getAccessToken, addNotification);
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
  }, [user, globalAdminMode, setGlobalAdminMode]);

  // Clear the tenant filter when cross-tenant mode turns off (refetch is owned by useDashboardSessions).
  // Keyed on crossTenant (not raw globalAdminMode) so a delegated ("MSP") admin — whose crossTenant is
  // always on — keeps any `?tenant=` deep-link / typed filter instead of having it wiped on mount.
  // Adjust-during-render (compare-prev) instead of an effect; prev starts at null so the first
  // render replicates the old effect's mount-time run, which cleared a stray `?tenant=`
  // deep-link for users who are not in a cross-tenant view.
  const [prevCrossTenant, setPrevCrossTenant] = useState<boolean | null>(null);
  if (prevCrossTenant !== crossTenant) {
    setPrevCrossTenant(crossTenant);
    if (!crossTenant) {
      setTenantIdFilter("");
      setSubmittedTenantIdFilter("");
    }
  }

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

  // Server-search when search is active and local results are insufficient — fetches only
  // matching sessions (backend q= filter) instead of the former loadAll() full-history walk.
  // Uses a 500ms debounce so rapid typing doesn't trigger unnecessary loads.
  const autoLoadTimerRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => {
    if (autoLoadTimerRef.current) clearTimeout(autoLoadTimerRef.current);

    const query = searchQuery.trim();
    if (!query || query.length < 2) return;
    if (loading || loadingMore || !hasMore) return;
    // Skip duration queries — those are local-only filters
    if (/^[><]=?\s*\d+$/.test(query)) return;
    if (filteredSessions.length >= 8) return;

    autoLoadTimerRef.current = setTimeout(() => {
      searchAll(query);
    }, 500);

    return () => { if (autoLoadTimerRef.current) clearTimeout(autoLoadTimerRef.current); };
  }, [searchQuery, filteredSessions.length, hasMore, loading, loadingMore, searchAll]);

  const applyTenantIdFilter = (value: string) => {
    setTenantIdFilter(value);
    // Emptying the input while a filter is submitted acts as an implicit clear:
    // without this, deleting the text only widens the client-side view over the
    // already-loaded (filtered) sessions — the backend scope would stay on the
    // old tenant until the user hits Filter again on an empty box.
    if (!value.trim() && submittedTenantIdFilter.trim()) {
      setSubmittedTenantIdFilter("");
      refetchWith("");
    }
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
          {/* Feedback & bug report banner (session-dismissable, telemetry-instrumented) */}
          <ActivelyDevelopedBanner />

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

          {/* Pro-requires-contact nag (amber, non-blocking): Pro tenants keep every feature,
              but should be reachable. Admins only — they own the Contact section; delegated
              admins are excluded (the link would target their own tenant, not the viewed one). */}
          {proContactMissing && !isDelegated && (user?.isTenantAdmin || user?.isGlobalAdmin) && (
            <div className="mb-6 bg-amber-50 border border-amber-300 rounded-lg px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 dark:bg-amber-950/30 dark:border-amber-700/50">
              <div className="flex items-start gap-3">
                <svg className="w-4 h-4 text-amber-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                </svg>
                <p className="text-sm text-amber-800 dark:text-amber-300">
                  <span className="font-semibold">Your Pro tenant has no contact address.</span>{" "}
                  Set one so we can reach you about service or security matters — it is used for
                  nothing else and never shared.
                </p>
              </div>
              <Link
                href="/settings/tenant/contact"
                className="shrink-0 inline-flex items-center gap-2 bg-amber-600 text-white font-medium text-sm px-3 py-1.5 rounded-lg hover:bg-amber-700 transition-colors"
              >
                Set contact address
              </Link>
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
            {/* Median, not mean — a few overnight/WhiteGlove outliers dominate the average
                of the right-skewed duration distribution; P90 keeps the tail visible. */}
            <StatsCard
              title="Median Duration"
              value={
                dashboardStats
                  ? dashboardStats.medianDurationMinutes > 0
                    ? `${dashboardStats.medianDurationMinutes} min`
                    : "—"
                  : "..."
              }
              description={
                dashboardStats && dashboardStats.p90DurationMinutes > 0
                  ? `P90 ${formatDuration(dashboardStats.p90DurationMinutes * 60)} · last 7 days`
                  : "Last 7 days"
              }
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

          {/* Zero sessions in a cross-tenant view: keep the tenant filter on screen so a
              GA/MSP admin drilling through tenants can clear it or type the next tenant
              without leaving the page (the SessionTable — which normally hosts this bar —
              is not rendered when the filtered fetch came back empty). */}
          {!loading && sessions.length === 0 && crossTenant && (
            <div className="mt-4 bg-white shadow rounded-lg p-6">
              <TenantFilterBar
                tenantIdFilter={tenantIdFilter}
                onChange={applyTenantIdFilter}
                onSubmit={submitTenantIdFilter}
                onClear={clearTenantIdFilter}
                tenantList={tenantList}
              />
              {submittedTenantIdFilter.trim() && (
                <p className="text-sm text-gray-500">
                  No sessions found for this tenant yet. Clear the filter or enter another tenant to continue.
                </p>
              )}
            </div>
          )}

          {/* Welcome message - only show when no sessions */}
          {!loading && sessions.length === 0 && <WelcomeMessage />}

          {/* Fleet-context rule filter chip (from a session-detail "View sessions" link). */}
          {ruleFilter && (
            <div className="mt-4 flex items-center">
              <span
                className="inline-flex items-center gap-2 px-3 py-1.5 rounded-full bg-amber-50 border border-amber-300 text-sm text-amber-800"
                title="Shows loaded sessions where this rule fired in the last 14 days. Only sessions already loaded are filtered — use Load more to widen."
              >
                <span className="font-medium">Rule:</span>
                <span className="font-mono">{ruleFilter.ruleId}</span>
                <span className="text-amber-600">
                  {ruleFilter.sessionIds === null
                    ? "loading…"
                    : `${ruleFilter.sessionIds.size} enrollment${ruleFilter.sessionIds.size === 1 ? "" : "s"} · last 14 days`}
                </span>
                <button
                  onClick={clearRuleFilter}
                  aria-label="Clear rule filter"
                  className="ml-1 text-amber-500 hover:text-amber-700 font-bold leading-none"
                >
                  ×
                </button>
              </span>
            </div>
          )}

          {/* Sessions List — skeleton fills the slot on the initial fetch so
              the page has no blank gap; once data (or the empty states above)
              resolve, it never reappears. */}
          {loading && sessions.length === 0 && (
            <TableSkeleton wrapped columns={7} rows={8} className="mt-4" />
          )}
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
              loadingAll={loadingAll}
              onLoadAll={loadAll}
              onSearchAll={() => searchAll(searchQuery, { force: true })}
              adminMode={adminMode}
              globalAdminMode={crossTenant}
              tenantIdFilter={tenantIdFilter}
              onTenantIdFilterChange={applyTenantIdFilter}
              onTenantIdFilterSubmit={submitTenantIdFilter}
              onTenantIdFilterClear={clearTenantIdFilter}
              tenantList={tenantList}
              blockedDevicesSet={blockedDevicesSet}
              isActivationPending={isActivationPending}
              user={user}
              columnFilters={columnFilters}
              onColumnFiltersChange={setColumnFilters}
              onDeleteSessions={deleteSessions}
              pendingDeletions={pendingDeletions}
              onBlockDevices={blockDevices}
              fullWidth={fullWidth}
              onToggleFullWidth={toggleFullWidth}
              sessionLinkTarget={
                crossTenant
                  ? (s) => sessionUrl(s.sessionId, { tenantId: s.tenantId || undefined })
                  : undefined
              }
            />
          )}
        </div>
      </main>

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && (
        <DeleteConfirmModal
          targets={deleteTargets}
          onConfirm={confirmDelete}
          onCancel={cancelDelete}
        />
      )}

      {/* Block Device Confirmation Modal */}
      {showBlockConfirm && (
        <BlockConfirmModal
          targets={blockTargets}
          blockingDevice={blockingDevice}
          onConfirm={confirmBlock}
          onCancel={cancelBlock}
        />
      )}
    </div>
    </ProtectedRoute>
  );
}
