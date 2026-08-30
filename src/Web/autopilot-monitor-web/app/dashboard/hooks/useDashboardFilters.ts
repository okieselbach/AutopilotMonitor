"use client";

import { useEffect, useMemo, useState } from "react";
import { trackEvent } from "@/lib/appInsights";
import type { Session } from "../types";

const SESSIONS_PER_PAGE_KEY = "sessionsPerPage";
const DEFAULT_SESSIONS_PER_PAGE = 10;

// Deterministic serialization of the column filters (sorted keys, sorted set values)
// so structurally identical filter content always yields the same page-reset key
// regardless of insertion order.
function serializeColumnFilters(filters: Record<string, Set<string>>): string {
  return JSON.stringify(
    Object.keys(filters)
      .sort()
      .map((key) => [key, Array.from(filters[key]).sort()])
  );
}

interface UseDashboardFiltersParams {
  sessions: Session[];
  blockedDevicesSet: Set<string>;
  tenantId: string | null | undefined;
  globalAdminMode: boolean;
  tenantIdFilter: string;
  hasMore: boolean;
  loadingMore: boolean;
  loadMore: () => void;
  // Deep-link seed (e.g. Fleet Health "Health by Device Model" → dashboard filtered
  // to Failed + that model). Applied once as the initial state, then user-owned.
  initialSearchQuery?: string;
  initialStatusFilter?: string | null;
  // Fleet-context deep link (`?ruleId=`): restrict to sessions where that rule fired.
  // `ruleSessionIds === null` means the hit set is still loading — no filtering yet,
  // so the list never flashes empty before the fetch resolves. The id string keys the
  // page reset (the Set identity alone would reset on every fetch object).
  ruleFilterId?: string | null;
  ruleSessionIds?: Set<string> | null;
}

export interface UseDashboardFiltersReturn {
  searchQuery: string;
  setSearchQuery: (value: string) => void;
  statusFilter: string | null;
  setStatusFilter: (value: string | null) => void;
  sortColumn: keyof Session | null;
  sortDirection: "asc" | "desc";
  handleSort: (column: keyof Session) => void;
  columnFilters: Record<string, Set<string>>;
  setColumnFilters: React.Dispatch<React.SetStateAction<Record<string, Set<string>>>>;
  currentPage: number;
  sessionsPerPage: number;
  handleSessionsPerPageChange: (value: number) => void;
  handlePreviousPage: () => void;
  handleNextPage: () => void;
  effectiveSessions: Session[];
  filteredSessions: Session[];
  sortedSessions: Session[];
  paginatedSessions: Session[];
  totalPages: number;
}

/**
 * Owns the dashboard's filtering, sorting, and pagination. Pure derivation —
 * no fetches, no side effects beyond localStorage (page size) and an
 * Application Insights event for search usage. Stats cards moved off this
 * hook to <c>useDashboardStats</c> (server-aggregated).
 */
export function useDashboardFilters({
  sessions,
  blockedDevicesSet,
  tenantId,
  globalAdminMode,
  tenantIdFilter,
  hasMore,
  loadingMore,
  loadMore,
  initialSearchQuery = "",
  initialStatusFilter = null,
  ruleFilterId = null,
  ruleSessionIds = null,
}: UseDashboardFiltersParams): UseDashboardFiltersReturn {
  const [searchQuery, setSearchQuery] = useState(initialSearchQuery);
  const [statusFilter, setStatusFilter] = useState<string | null>(initialStatusFilter);
  const [sortColumn, setSortColumn] = useState<keyof Session | null>(null);
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("asc");
  const [columnFilters, setColumnFilters] = useState<Record<string, Set<string>>>({});
  const [currentPage, setCurrentPage] = useState(1);
  const [sessionsPerPage, setSessionsPerPage] = useState(() => {
    if (typeof window !== "undefined") {
      const stored = localStorage.getItem(SESSIONS_PER_PAGE_KEY);
      if (stored) return parseInt(stored, 10);
    }
    return DEFAULT_SESSIONS_PER_PAGE;
  });

  // Track search usage (debounced — fires 1s after user stops typing).
  // Skip empty query so initial mount and "clear search" don't pollute the event.
  useEffect(() => {
    if (!searchQuery) return;
    const timer = setTimeout(() => {
      trackEvent("session_searched", { queryLength: searchQuery.length });
    }, 1000);
    return () => clearTimeout(timer);
  }, [searchQuery]);

  // Reset to page 1 whenever the displayed set or its ordering changes —
  // includes external scope changes (tenant switch, global-admin toggle, tenant filter).
  // React's adjust-during-render pattern (compare the previous key, set state while
  // rendering) instead of an effect, so the stale page never paints first.
  const pageResetKey = JSON.stringify([
    searchQuery, statusFilter, sortColumn, sortDirection,
    serializeColumnFilters(columnFilters), sessionsPerPage,
    globalAdminMode, tenantIdFilter, tenantId ?? null,
    ruleFilterId, ruleSessionIds === null ? -1 : ruleSessionIds.size,
  ]);
  const [prevPageResetKey, setPrevPageResetKey] = useState(pageResetKey);
  if (prevPageResetKey !== pageResetKey) {
    setPrevPageResetKey(pageResetKey);
    setCurrentPage(1);
  }

  const handleSessionsPerPageChange = (value: number) => {
    setSessionsPerPage(value);
    if (typeof window !== "undefined") {
      localStorage.setItem(SESSIONS_PER_PAGE_KEY, String(value));
    }
  };

  const handleSort = (column: keyof Session) => {
    if (sortColumn === column) {
      setSortDirection(sortDirection === "asc" ? "desc" : "asc");
    } else {
      setSortColumn(column);
      setSortDirection("asc");
    }
  };

  // Client-side tenant filter: ensures sessions from other tenants are never displayed,
  // regardless of how they entered the sessions state (SignalR, race conditions, etc.)
  const effectiveSessions = useMemo(() => {
    if (globalAdminMode) {
      const trimmed = tenantIdFilter.trim();
      return trimmed ? sessions.filter((s) => s.tenantId === trimmed) : sessions;
    }
    return tenantId ? sessions.filter((s) => s.tenantId === tenantId) : sessions;
  }, [sessions, globalAdminMode, tenantIdFilter, tenantId]);

  const filteredSessions = useMemo(() => {
    return effectiveSessions.filter((session) => {
      // Fleet-context rule filter: only while the hit set is loaded (null = still
      // loading → unfiltered, so the table never flashes empty before the fetch).
      if (ruleSessionIds && !ruleSessionIds.has(session.sessionId)) return false;

      if (statusFilter && session.status !== statusFilter) return false;

      for (const [field, allowedValues] of Object.entries(columnFilters)) {
        if (allowedValues.size === 0) continue;
        const value = String(session[field as keyof Session] ?? "");
        if (!allowedValues.has(value)) return false;
      }

      if (!searchQuery.trim()) return true;

      const query = searchQuery.toLowerCase().trim();

      const durationMatch = query.match(/^([><]=?)\s*(\d+)$/);
      if (durationMatch) {
        const operator = durationMatch[1];
        const value = parseInt(durationMatch[2]);
        const durationMinutes = Math.round((session.durationSeconds ?? 0) / 60);

        if (operator === ">") return durationMinutes > value;
        if (operator === ">=") return durationMinutes >= value;
        if (operator === "<") return durationMinutes < value;
        if (operator === "<=") return durationMinutes <= value;
      }

      const searchableText = [
        session.deviceName,
        session.serialNumber,
        session.manufacturer,
        session.model,
        session.status,
        session.sessionId,
        new Date(session.startedAt).toLocaleString(),
        `${Math.round((session.durationSeconds ?? 0) / 60)} min`,
        blockedDevicesSet.has(`${session.tenantId}:${session.serialNumber}`) ? "blocked" : "",
        session.geoCountry,
        session.geoRegion,
        session.geoCity,
        session.agentVersion,
        session.osName,
        session.osBuild,
        session.osDisplayVersion,
        session.osEdition,
        session.osLanguage,
      ].join(" ").toLowerCase();

      return searchableText.includes(query);
    });
  }, [effectiveSessions, ruleSessionIds, statusFilter, columnFilters, searchQuery, blockedDevicesSet]);

  const sortedSessions = useMemo(() => {
    if (!sortColumn) return filteredSessions;
    return [...filteredSessions].sort((a, b) => {
      const rawA: Session[typeof sortColumn] = a[sortColumn];
      const rawB: Session[typeof sortColumn] = b[sortColumn];

      let aValue: number | string | boolean | null | undefined;
      let bValue: number | string | boolean | null | undefined;

      if (sortColumn === "startedAt") {
        aValue = new Date(rawA as string).getTime();
        bValue = new Date(rawB as string).getTime();
      } else {
        aValue = rawA;
        bValue = rawB;
      }

      if (aValue == null || bValue == null) return 0;
      if (aValue < bValue) return sortDirection === "asc" ? -1 : 1;
      if (aValue > bValue) return sortDirection === "asc" ? 1 : -1;
      return 0;
    });
  }, [filteredSessions, sortColumn, sortDirection]);

  const totalPages = Math.ceil(sortedSessions.length / sessionsPerPage);

  // Snap currentPage back to the last valid page when a load finished that did NOT
  // grow totalPages (e.g. an active filter narrowed the new batch to zero matches).
  // Without this the user would be stuck on an empty paginated slice with no way
  // to recover except clicking Prev. Clamped at read (not written back via an
  // effect): gated on !loadingMore so a legitimate overshoot is never reverted
  // mid-fetch, and the moment the load finishes the clamp applies in the very
  // same render — an effect-based snap-back could otherwise race handleNextPage
  // (loadingMore still false when the effect ran) and cancel the advance.
  const effectivePage =
    !loadingMore && totalPages > 0 && currentPage > totalPages ? totalPages : currentPage;

  const paginatedSessions = useMemo(() => {
    const startIndex = (effectivePage - 1) * sessionsPerPage;
    return sortedSessions.slice(startIndex, startIndex + sessionsPerPage);
  }, [sortedSessions, effectivePage, sessionsPerPage]);

  // Handlers step from effectivePage (the clamped value every consumer sees) so a
  // raw overshoot left in state can never compound or make Prev a visual no-op.
  const handlePreviousPage = () => setCurrentPage(Math.max(1, effectivePage - 1));
  // When advancing past locally-loaded pages, fire loadMore() in the SAME event
  // handler so React batches setLoadingMore(true) with setCurrentPage(...): the
  // clamp above then sees loadingMore === true on the next render and lets the
  // overshoot page stand while the fetch is in flight.
  const handleNextPage = () => {
    if (effectivePage < totalPages) {
      setCurrentPage(effectivePage + 1);
      return;
    }
    if (hasMore && !loadingMore) {
      loadMore();
      setCurrentPage(effectivePage + 1);
    }
  };

  return {
    searchQuery, setSearchQuery,
    statusFilter, setStatusFilter,
    sortColumn, sortDirection, handleSort,
    columnFilters, setColumnFilters,
    currentPage: effectivePage, sessionsPerPage, handleSessionsPerPageChange,
    handlePreviousPage, handleNextPage,
    effectiveSessions, filteredSessions, sortedSessions, paginatedSessions,
    totalPages,
  };
}
