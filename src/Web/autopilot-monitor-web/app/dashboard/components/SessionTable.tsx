"use client";

import { useRouter } from "next/navigation";
import { useState, useEffect, useRef, useMemo, useDeferredValue } from "react";
import { Session } from "../types";
import { trackEvent } from "@/lib/appInsights";
import { fuzzyContains } from "@/utils/fuzzy";
import { buildUniqueValuesByField } from "./uniqueValuesByField";
import { SessionStatusBadge } from "@/components/SessionStatusBadge";

// Column definition for the session table
interface ColumnDef {
  key: string;
  label: string;
  defaultVisible: boolean;
  adminOnly?: boolean;
  globalOnly?: boolean;
  sortKey?: keyof Session;
  // Session field used for column-level filtering; if set, column is filterable
  filterKey?: keyof Session;
}

const ALL_COLUMNS: ColumnDef[] = [
  { key: "device", label: "Device", defaultVisible: true, sortKey: "deviceName" },
  { key: "tenantId", label: "Tenant ID", defaultVisible: false, globalOnly: true },
  { key: "model", label: "Model", defaultVisible: true, sortKey: "model", filterKey: "manufacturer" },
  { key: "status", label: "Status", defaultVisible: true, sortKey: "status", filterKey: "status" },
  { key: "eventCount", label: "Events", defaultVisible: true, sortKey: "eventCount" },
  { key: "duration", label: "Duration", defaultVisible: true, sortKey: "durationSeconds" },
  { key: "started", label: "Started", defaultVisible: true, sortKey: "startedAt" },
  { key: "country", label: "Country", defaultVisible: false, sortKey: "geoCountry", filterKey: "geoCountry" },
  { key: "agentVersion", label: "Agent Version", defaultVisible: false, sortKey: "agentVersion", filterKey: "agentVersion" },
  { key: "osName", label: "OS Name", defaultVisible: false, sortKey: "osName", filterKey: "osName" },
  { key: "osBuild", label: "OS Build", defaultVisible: false, sortKey: "osBuild", filterKey: "osBuild" },
  { key: "osDisplayVersion", label: "OS Version", defaultVisible: false, sortKey: "osDisplayVersion", filterKey: "osDisplayVersion" },
  { key: "osEdition", label: "OS Edition", defaultVisible: false, sortKey: "osEdition", filterKey: "osEdition" },
  { key: "osLanguage", label: "OS Language", defaultVisible: false, sortKey: "osLanguage", filterKey: "osLanguage" },
  { key: "actions", label: "Actions", defaultVisible: true, adminOnly: true },
];

const STORAGE_KEY = "sessionTable_visibleColumns";

function getInitialVisibleColumns(): Set<string> {
  if (typeof window !== "undefined") {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored) {
        return new Set(JSON.parse(stored));
      }
    } catch { /* ignore */ }
  }
  return new Set(ALL_COLUMNS.filter((c) => c.defaultVisible).map((c) => c.key));
}

interface SessionTableProps {
  sessions: Session[];
  filteredSessions: Session[];
  sortedSessions: Session[];
  paginatedSessions: Session[];
  searchQuery: string;
  onSearchQueryChange: (query: string) => void;
  statusFilter: string | null;
  onStatusFilterChange: (status: string | null) => void;
  sortColumn: keyof Session | null;
  sortDirection: "asc" | "desc";
  onSort: (column: keyof Session) => void;
  currentPage: number;
  totalPages: number;
  onPreviousPage: () => void;
  onNextPage: () => void;
  sessionsPerPage: number;
  onSessionsPerPageChange: (value: number) => void;
  hasMore: boolean;
  loadingMore: boolean;
  onLoadAll: () => void;
  adminMode: boolean;
  globalAdminMode: boolean;
  tenantIdFilter: string;
  onTenantIdFilterChange: (value: string) => void;
  onTenantIdFilterSubmit: () => void;
  onTenantIdFilterClear: () => void;
  tenantList: { tenantId: string; domainName: string }[];
  blockedDevicesSet: Set<string>;
  isPreviewBlocked: boolean;
  user: { isGlobalAdmin?: boolean } | null;
  columnFilters: Record<string, Set<string>>;
  onColumnFiltersChange: (filters: Record<string, Set<string>>) => void;
  onDeleteSession: (sessionId: string, tenantId: string, deviceName?: string) => void;
  /**
   * Sessions currently awaiting the cascade worker's `sessionDeleted` SignalR notification.
   * When this set contains a session id, the action cell renders a spinner instead of the
   * delete button so the user can see that the click was accepted and the row is being
   * drained server-side.
   */
  pendingDeletions: ReadonlySet<string>;
  onBlockDevice: (serialNumber: string, tenantId: string, deviceName?: string) => void;
  fullWidth: boolean;
  onToggleFullWidth: () => void;
}

export function SessionTable({
  sessions,
  filteredSessions,
  sortedSessions,
  paginatedSessions,
  searchQuery,
  onSearchQueryChange,
  statusFilter,
  onStatusFilterChange,
  sortColumn,
  sortDirection,
  onSort,
  currentPage,
  totalPages,
  onPreviousPage,
  onNextPage,
  sessionsPerPage,
  onSessionsPerPageChange,
  hasMore,
  loadingMore,
  onLoadAll,
  adminMode,
  globalAdminMode,
  tenantIdFilter,
  onTenantIdFilterChange,
  onTenantIdFilterSubmit,
  onTenantIdFilterClear,
  tenantList,
  blockedDevicesSet,
  isPreviewBlocked,
  user,
  columnFilters,
  onColumnFiltersChange,
  onDeleteSession,
  pendingDeletions,
  onBlockDevice,
  fullWidth,
  onToggleFullWidth,
}: SessionTableProps) {
  const router = useRouter();
  const [visibleColumns, setVisibleColumns] = useState<Set<string>>(getInitialVisibleColumns);
  const [showColumnSelector, setShowColumnSelector] = useState(false);
  const [openFilterColumn, setOpenFilterColumn] = useState<string | null>(null);
  const columnSelectorRef = useRef<HTMLDivElement>(null);
  const filterDropdownRef = useRef<HTMLDivElement>(null);
  const tenantDropdownRef = useRef<HTMLDivElement>(null);
  const [showTenantSuggestions, setShowTenantSuggestions] = useState(false);
  const [tenantSelectedIndex, setTenantSelectedIndex] = useState(-1);
  const searchDropdownRef = useRef<HTMLDivElement>(null);
  const [showSearchSuggestions, setShowSearchSuggestions] = useState(false);
  const [searchSelectedIndex, setSearchSelectedIndex] = useState(-1);

  // Fuzzy-match sessions by multiple fields for search dropdown (two-phase: exact then Levenshtein)
  interface SearchSuggestion {
    session: Session;
    matchedField: string;
    matchedValue: string;
    isExact: boolean;
  }

  const SEARCH_FIELDS: { key: keyof Session; label: string }[] = [
    { key: "deviceName", label: "Device" },
    { key: "serialNumber", label: "Serial" },
    { key: "model", label: "Model" },
    { key: "manufacturer", label: "Manufacturer" },
    { key: "sessionId", label: "Session ID" },
    { key: "geoCountry", label: "Country" },
    { key: "geoRegion", label: "Region" },
    { key: "geoCity", label: "City" },
    { key: "agentVersion", label: "Agent Version" },
    { key: "osName", label: "OS Name" },
    { key: "osBuild", label: "OS Build" },
    { key: "osDisplayVersion", label: "OS Version" },
    { key: "osEdition", label: "OS Edition" },
    { key: "status", label: "Status" },
  ];

  // Defer expensive suggestion scans so rapid typing keeps the input responsive.
  const deferredSearchQuery = useDeferredValue(searchQuery);
  const deferredTenantIdFilter = useDeferredValue(tenantIdFilter);

  const searchSuggestions = useMemo<SearchSuggestion[]>(() => {
    const q = deferredSearchQuery.trim().toLowerCase();
    if (q.length < 2 || sessions.length === 0) return [];
    if (/^[><]=?\s*\d+$/.test(q)) return [];

    const exactResults: SearchSuggestion[] = [];
    const fuzzyResults: SearchSuggestion[] = [];
    const seen = new Set<string>();

    // Phase 1: exact substring matches (highest priority)
    for (const session of sessions) {
      if (exactResults.length >= 8) break;
      if (seen.has(session.sessionId)) continue;
      for (const f of SEARCH_FIELDS) {
        const val = session[f.key];
        if (val != null && String(val).toLowerCase().includes(q)) {
          seen.add(session.sessionId);
          exactResults.push({ session, matchedField: f.label, matchedValue: String(val), isExact: true });
          break;
        }
      }
    }

    // Phase 2: Levenshtein fuzzy matches (fill remaining slots, min 3 chars for fuzzy)
    if (exactResults.length < 8 && q.length >= 3) {
      const maxDist = q.length <= 4 ? 1 : 2;
      for (const session of sessions) {
        if (exactResults.length + fuzzyResults.length >= 8) break;
        if (seen.has(session.sessionId)) continue;
        for (const f of SEARCH_FIELDS) {
          const val = session[f.key];
          if (val != null && fuzzyContains(String(val), q, maxDist)) {
            seen.add(session.sessionId);
            fuzzyResults.push({ session, matchedField: f.label, matchedValue: String(val), isExact: false });
            break;
          }
        }
      }
    }

    return [...exactResults, ...fuzzyResults];
    // SEARCH_FIELDS is a stable literal — intentionally omitted from deps.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deferredSearchQuery, sessions]);

  // Fuzzy-match tenants by domain name or tenant ID
  const tenantSuggestions = useMemo(() => {
    const q = deferredTenantIdFilter.trim().toLowerCase();
    if (q.length < 2 || tenantList.length === 0) return [];
    return tenantList
      .filter((t) => t.domainName.toLowerCase().includes(q) || t.tenantId.toLowerCase().includes(q))
      .slice(0, 8);
  }, [deferredTenantIdFilter, tenantList]);

  const tenantDomainById = useMemo(
    () => new Map(tenantList.map((t) => [t.tenantId, t.domainName])),
    [tenantList],
  );

  // Persist visible columns to localStorage
  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...visibleColumns]));
  }, [visibleColumns]);

  // Close dropdowns on outside click
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (columnSelectorRef.current && !columnSelectorRef.current.contains(e.target as Node)) {
        setShowColumnSelector(false);
      }
      if (filterDropdownRef.current && !filterDropdownRef.current.contains(e.target as Node)) {
        setOpenFilterColumn(null);
      }
      if (tenantDropdownRef.current && !tenantDropdownRef.current.contains(e.target as Node)) {
        setShowTenantSuggestions(false);
      }
      if (searchDropdownRef.current && !searchDropdownRef.current.contains(e.target as Node)) {
        setShowSearchSuggestions(false);
      }
    }
    if (showColumnSelector || openFilterColumn || showTenantSuggestions || showSearchSuggestions) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [showColumnSelector, openFilterColumn, showTenantSuggestions, showSearchSuggestions]);

  // Compute unique values for every filterable field in a single pass over sessions.
  // Memoized so that header re-renders pay only a map lookup, not N × O(sessions).
  const uniqueValuesByField = useMemo(
    () =>
      buildUniqueValuesByField(
        sessions,
        ALL_COLUMNS.map((c) => c.filterKey).filter((k): k is keyof Session => !!k),
      ),
    [sessions],
  );

  const activeFilterCount = Object.values(columnFilters).reduce(
    (sum, s) => sum + (s.size > 0 ? 1 : 0), 0
  );

  const toggleColumn = (key: string) => {
    setVisibleColumns((prev) => {
      const next = new Set(prev);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
  };

  const resetColumns = () => {
    setVisibleColumns(new Set(ALL_COLUMNS.filter((c) => c.defaultVisible).map((c) => c.key)));
  };

  // Filter columns based on mode and visibility
  const activeColumns = ALL_COLUMNS.filter((col) => {
    if (col.adminOnly && !adminMode) return false;
    if (col.globalOnly && !globalAdminMode) return false;
    return visibleColumns.has(col.key);
  });

  // Columns available for the selector (exclude mode-gated ones that aren't applicable)
  const selectableColumns = ALL_COLUMNS.filter((col) => {
    if (col.adminOnly && !adminMode) return false;
    if (col.globalOnly && !globalAdminMode) return false;
    return true;
  });

  const colSpan = activeColumns.length;

  return (
    <div className="mt-4 bg-white shadow rounded-lg p-6">
      <div className="flex items-center justify-between mb-4 gap-3 flex-wrap">
        <h2 className="text-xl font-semibold text-gray-900">
          Sessions ({sessions.length}{hasMore ? '+' : ''})
          {filteredSessions.length !== sessions.length && (
            <span className="text-sm text-gray-500 ml-2">
              ({filteredSessions.length} filtered)
            </span>
          )}
        </h2>
        <div className="flex items-center gap-2">
          {/* Preview Status Badge - only shown when blocked */}
          {isPreviewBlocked && (
            <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-amber-100 text-amber-800 border border-amber-200 shrink-0">
              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Preview approval pending
            </span>
          )}

          {/* Page Size Selector */}
          <select
            value={sessionsPerPage}
            onChange={(e) => onSessionsPerPageChange(Number(e.target.value))}
            className="px-2 py-1.5 rounded-lg text-xs font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 border border-gray-200 transition-colors cursor-pointer focus:outline-none focus:ring-2 focus:ring-blue-500"
            title="Sessions per page"
          >
            {[10, 15, 20, 50, 100].map((n) => (
              <option key={n} value={n}>{n} per page</option>
            ))}
          </select>

          {/* Full-width toggle */}
          <button
            onClick={onToggleFullWidth}
            className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium border transition-colors ${
              fullWidth
                ? "text-blue-700 bg-blue-50 border-blue-200 hover:bg-blue-100"
                : "text-gray-600 bg-gray-100 hover:bg-gray-200 border-gray-200"
            }`}
            title={fullWidth ? "Switch to default width" : "Expand to full width"}
            aria-pressed={fullWidth}
          >
            {fullWidth ? (
              // collapse: arrows pointing inward (>-<)
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 12h18M3 8l4 4-4 4M21 8l-4 4 4 4" />
              </svg>
            ) : (
              // expand: arrows pointing outward (<-->)
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 12h18M7 8l-4 4 4 4M17 8l4 4-4 4" />
              </svg>
            )}
            <span className="hidden sm:inline">{fullWidth ? "Default" : "Full width"}</span>
          </button>

          {/* Column Selector */}
          <div className="relative" ref={columnSelectorRef}>
            <button
              onClick={() => setShowColumnSelector(!showColumnSelector)}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium text-gray-600 bg-gray-100 hover:bg-gray-200 border border-gray-200 transition-colors"
              title="Configure visible columns"
            >
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17V7m0 10a2 2 0 01-2 2H5a2 2 0 01-2-2V7a2 2 0 012-2h2a2 2 0 012 2m0 10a2 2 0 002 2h2a2 2 0 002-2M9 7a2 2 0 012-2h2a2 2 0 012 2m0 10V7m0 10a2 2 0 002 2h2a2 2 0 002-2V7a2 2 0 00-2-2h-2a2 2 0 00-2 2" />
              </svg>
              Columns
            </button>
            {showColumnSelector && (
              <div className="absolute left-0 sm:left-auto sm:right-0 top-full mt-1 w-56 bg-white rounded-lg shadow-lg border border-gray-200 z-50 py-2">
                <div className="px-3 py-1.5 text-xs font-semibold text-gray-400 uppercase tracking-wider flex items-center justify-between">
                  <span>Toggle Columns</span>
                  <button
                    onClick={resetColumns}
                    className="text-blue-500 hover:text-blue-700 normal-case font-medium tracking-normal"
                  >
                    Reset
                  </button>
                </div>
                <div className="border-t border-gray-100 mt-1 pt-1">
                  {selectableColumns.map((col) => (
                    <label
                      key={col.key}
                      className="flex items-center gap-2 px-3 py-1.5 hover:bg-gray-50 cursor-pointer text-sm text-gray-700"
                    >
                      <input
                        type="checkbox"
                        checked={visibleColumns.has(col.key)}
                        onChange={() => toggleColumn(col.key)}
                        className="rounded border-gray-300 text-blue-600 focus:ring-blue-500 h-3.5 w-3.5"
                      />
                      {col.label}
                    </label>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Tenant ID Filter (Global Admin only) */}
      {globalAdminMode && (
        <div className="mb-3 flex items-center gap-2">
          <div className="relative flex-1" ref={tenantDropdownRef}>
            <input
              type="text"
              placeholder="Filter by Tenant ID or domain name"
              value={tenantIdFilter}
              onChange={(e) => {
                onTenantIdFilterChange(e.target.value);
                setShowTenantSuggestions(true);
                setTenantSelectedIndex(-1);
              }}
              onFocus={() => {
                if (tenantSuggestions.length > 0) setShowTenantSuggestions(true);
              }}
              onKeyDown={(e) => {
                if (showTenantSuggestions && tenantSuggestions.length > 0) {
                  if (e.key === "ArrowDown") {
                    e.preventDefault();
                    setTenantSelectedIndex((i) => Math.min(i + 1, tenantSuggestions.length - 1));
                    return;
                  }
                  if (e.key === "ArrowUp") {
                    e.preventDefault();
                    setTenantSelectedIndex((i) => Math.max(i - 1, -1));
                    return;
                  }
                  if (e.key === "Enter" && tenantSelectedIndex >= 0) {
                    e.preventDefault();
                    const selected = tenantSuggestions[tenantSelectedIndex];
                    onTenantIdFilterChange(selected.tenantId);
                    setShowTenantSuggestions(false);
                    setTenantSelectedIndex(-1);
                    return;
                  }
                  if (e.key === "Escape") {
                    setShowTenantSuggestions(false);
                    return;
                  }
                }
                if (e.key === "Enter") {
                  setShowTenantSuggestions(false);
                  onTenantIdFilterSubmit();
                }
              }}
              className="w-full px-4 py-2 pr-10 border border-purple-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors font-mono text-sm"
            />
            {tenantIdFilter && (
              <button
                onClick={() => {
                  onTenantIdFilterClear();
                  setShowTenantSuggestions(false);
                }}
                className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                title="Clear tenant filter"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                </svg>
              </button>
            )}
            {/* Tenant suggestions dropdown */}
            {showTenantSuggestions && tenantSuggestions.length > 0 && (
              <div className="absolute z-50 mt-1 w-full bg-white border border-purple-200 rounded-lg shadow-lg max-h-64 overflow-y-auto">
                {tenantSuggestions.map((t, idx) => (
                  <button
                    key={t.tenantId}
                    onClick={() => {
                      onTenantIdFilterChange(t.tenantId);
                      setShowTenantSuggestions(false);
                      setTenantSelectedIndex(-1);
                    }}
                    className={`w-full text-left px-4 py-2.5 flex flex-col gap-0.5 transition-colors ${
                      idx === tenantSelectedIndex
                        ? "bg-purple-100"
                        : "hover:bg-purple-50"
                    }`}
                  >
                    <span className="text-sm font-medium text-gray-900">{t.domainName}</span>
                    <span className="text-xs text-gray-500 font-mono">{t.tenantId}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
          <button
            onClick={() => {
              setShowTenantSuggestions(false);
              onTenantIdFilterSubmit();
            }}
            className="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 transition-colors text-sm font-medium shrink-0"
          >
            Filter
          </button>
        </div>
      )}

      {/* Search Input with Fuzzy Suggestions */}
      <div className="mb-4 relative" ref={searchDropdownRef}>
        <input
          type="text"
          placeholder="Search by device, serial, model, status, session ID, country, or duration (e.g., >30 for >30min)"
          value={searchQuery}
          onChange={(e) => {
            onSearchQueryChange(e.target.value);
            setShowSearchSuggestions(true);
            setSearchSelectedIndex(-1);
          }}
          onFocus={() => {
            if (searchSuggestions.length > 0) setShowSearchSuggestions(true);
          }}
          onKeyDown={(e) => {
            if (showSearchSuggestions && searchSuggestions.length > 0) {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setSearchSelectedIndex((i) => Math.min(i + 1, searchSuggestions.length - 1));
                return;
              }
              if (e.key === "ArrowUp") {
                e.preventDefault();
                setSearchSelectedIndex((i) => Math.max(i - 1, -1));
                return;
              }
              if (e.key === "Enter" && searchSelectedIndex >= 0) {
                e.preventDefault();
                const selected = searchSuggestions[searchSelectedIndex];
                setShowSearchSuggestions(false);
                setSearchSelectedIndex(-1);
                router.push(`/sessions/${selected.session.sessionId}`);
                return;
              }
              if (e.key === "Escape") {
                setShowSearchSuggestions(false);
                return;
              }
            }
          }}
          className="w-full px-4 py-2 pr-10 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
        />
        {searchQuery && (
          <button
            onClick={() => {
              onSearchQueryChange("");
              setShowSearchSuggestions(false);
            }}
            className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
            title="Clear search"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        )}
        {/* Search suggestions dropdown */}
        {showSearchSuggestions && searchSuggestions.length > 0 && (
          <div className="absolute z-50 mt-1 w-full bg-white border border-gray-200 rounded-lg shadow-lg max-h-80 overflow-y-auto">
            {searchSuggestions.map((s, idx) => {
              const statusColors: Record<string, string> = {
                Succeeded: "text-green-600",
                Failed: "text-red-600",
                InProgress: "text-blue-600",
                Pending: "text-amber-600",
                Stalled: "text-orange-600",
              };
              return (
                <button
                  key={s.session.sessionId}
                  onClick={() => {
                    setShowSearchSuggestions(false);
                    setSearchSelectedIndex(-1);
                    router.push(`/sessions/${s.session.sessionId}`);
                  }}
                  className={`w-full text-left px-4 py-2.5 flex items-center gap-3 transition-colors ${
                    idx === searchSelectedIndex ? "bg-blue-50" : "hover:bg-gray-50"
                  }`}
                >
                  <span className="text-gray-400 flex-shrink-0">
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                    </svg>
                  </span>
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <p className="text-sm font-medium text-gray-900 truncate">
                        {s.session.deviceName || s.session.serialNumber || s.session.sessionId}
                      </p>
                      <span className={`text-[10px] font-semibold ${statusColors[s.session.status] ?? "text-gray-500"}`}>
                        {s.session.status}
                      </span>
                      {!s.isExact && (
                        <span className="text-[9px] font-medium text-amber-600 bg-amber-50 px-1.5 py-0.5 rounded-full">
                          fuzzy
                        </span>
                      )}
                    </div>
                    <p className="text-xs text-gray-500 truncate">
                      <span className="text-gray-400">Matched: </span>
                      <span className="font-medium text-blue-600">{s.matchedField}</span>
                      <span className="text-gray-300 mx-1">&middot;</span>
                      {s.matchedValue}
                      {s.session.serialNumber && s.matchedField !== "Serial" && (
                        <span><span className="text-gray-300 mx-1">&middot;</span>{s.session.serialNumber}</span>
                      )}
                    </p>
                  </div>
                </button>
              );
            })}
            {/* Hint when more sessions may exist on the server */}
            {hasMore && searchSuggestions.length < 8 && (
              <div className="border-t border-gray-100 px-4 py-2.5 flex items-center justify-between">
                {loadingMore ? (
                  <span className="text-xs text-gray-400 flex items-center gap-1.5">
                    <svg className="w-3 h-3 animate-spin" viewBox="0 0 24 24" fill="none">
                      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                    </svg>
                    Searching... ({sessions.length} sessions loaded)
                  </span>
                ) : (
                  <>
                    <span className="text-xs text-gray-400">
                      Searching {sessions.length} loaded sessions
                    </span>
                    <button
                      onClick={() => {
                        setShowSearchSuggestions(false);
                        onLoadAll();
                      }}
                      className="text-xs font-medium text-blue-600 hover:text-blue-800 transition-colors"
                    >
                      Search all sessions
                    </button>
                  </>
                )}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Status Filter Badges */}
      <div className="mb-4 flex items-center gap-2 flex-wrap">
        {(["Succeeded", "InProgress", "Pending", "Stalled", "Failed"] as const).map((status) => {
          const config: Record<string, { bg: string; bgActive: string; text: string; label: string }> = {
            Succeeded: { bg: "bg-green-50 text-green-700 border-green-200 hover:bg-green-100", bgActive: "bg-green-600 text-white border-green-600", text: "text-green-600", label: "Succeeded" },
            InProgress: { bg: "bg-blue-50 text-blue-700 border-blue-200 hover:bg-blue-100", bgActive: "bg-blue-600 text-white border-blue-600", text: "text-blue-600", label: "In Progress" },
            Pending: { bg: "bg-amber-50 text-amber-700 border-amber-200 hover:bg-amber-100", bgActive: "bg-amber-500 text-white border-amber-500", text: "text-amber-600", label: "Pending" },
            Stalled: { bg: "bg-orange-50 text-orange-700 border-orange-200 hover:bg-orange-100", bgActive: "bg-orange-600 text-white border-orange-600", text: "text-orange-600", label: "Stalled" },
            Failed: { bg: "bg-red-50 text-red-700 border-red-200 hover:bg-red-100", bgActive: "bg-red-600 text-white border-red-600", text: "text-red-600", label: "Failed" },
          };
          const c = config[status];
          const count = sessions.filter(s => s.status === status).length;
          const isActive = statusFilter === status;
          return (
            <button
              key={status}
              onClick={() => { if (!isActive) trackEvent("session_filter_applied", { filterType: "status", value: status }); onStatusFilterChange(isActive ? null : status); }}
              className={`inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold border transition-colors cursor-pointer ${isActive ? c.bgActive : c.bg}`}
            >
              {c.label}
              <span className={`rounded-full px-1.5 py-0.5 text-[10px] font-bold leading-none ${isActive ? "bg-white/25" : "bg-black/5"}`}>
                {count}
              </span>
            </button>
          );
        })}
        {statusFilter && (
          <button
            onClick={() => onStatusFilterChange(null)}
            className="inline-flex items-center gap-1 px-2 py-1 rounded-full text-xs text-gray-500 hover:text-gray-700 hover:bg-gray-100 transition-colors cursor-pointer"
            title="Clear filter"
          >
            <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
            Clear
          </button>
        )}
        {activeFilterCount > 0 && (
          <span className="inline-flex items-center gap-1.5 ml-2 px-2.5 py-1 rounded-full text-xs font-medium bg-blue-50 text-blue-700 border border-blue-200">
            <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
            </svg>
            {activeFilterCount} column filter{activeFilterCount > 1 ? "s" : ""} active
            <button
              onClick={() => onColumnFiltersChange({})}
              className="ml-0.5 hover:text-blue-900 transition-colors"
              title="Clear all column filters"
            >
              <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </span>
        )}
      </div>

      <div className="overflow-x-auto">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              {activeColumns.map((col) => {
                if (col.sortKey) {
                  return (
                    <SortableHeader
                      key={col.key}
                      column={col.sortKey}
                      currentSort={sortColumn}
                      direction={sortDirection}
                      onSort={onSort}
                      className={["eventCount", "duration", "started", "country", "agentVersion", "osName", "osBuild", "osDisplayVersion", "osEdition", "osLanguage"].includes(col.key) ? "px-3" : undefined}
                      filterKey={col.filterKey}
                      filterValues={col.filterKey ? uniqueValuesByField[col.filterKey] : undefined}
                      activeFilter={col.filterKey ? columnFilters[col.filterKey] : undefined}
                      isFilterOpen={openFilterColumn === col.key}
                      onFilterToggle={() => setOpenFilterColumn(openFilterColumn === col.key ? null : col.key)}
                      onFilterChange={(field, values) => {
                        onColumnFiltersChange({ ...columnFilters, [field]: values });
                      }}
                      filterDropdownRef={openFilterColumn === col.key ? filterDropdownRef : undefined}
                    >
                      {col.label}
                    </SortableHeader>
                  );
                }
                // Non-sortable headers (tenantId, actions)
                if (col.key === "actions") {
                  return (
                    <th key={col.key} scope="col" className="pl-3 pr-4 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                      Actions
                    </th>
                  );
                }
                return (
                  <th key={col.key} scope="col" className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                    {col.label}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {paginatedSessions.length === 0 ? (
              <tr>
                <td colSpan={colSpan} className="px-6 py-8 text-center text-gray-500">
                  {loadingMore ? (
                    <span className="inline-flex items-center gap-2">
                      <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                      </svg>
                      Loading sessions...
                    </span>
                  ) : (
                    "No sessions found matching your search."
                  )}
                </td>
              </tr>
            ) : (
              paginatedSessions.map((session) => (
              <tr
                key={session.sessionId}
                onClick={() => { trackEvent("session_opened", { sessionId: session.sessionId, status: session.status ?? "" }); router.push(`/sessions/${session.sessionId}`); }}
                className="hover:bg-gray-50 cursor-pointer transition-colors"
              >
                {activeColumns.map((col) => (
                  <SessionCell
                    key={col.key}
                    columnKey={col.key}
                    session={session}
                    adminMode={adminMode}
                    globalAdminMode={globalAdminMode}
                    blockedDevicesSet={blockedDevicesSet}
                    user={user}
                    onDeleteSession={onDeleteSession}
                    isDeletionPending={pendingDeletions.has(session.sessionId)}
                    onBlockDevice={onBlockDevice}
                    tenantDomainById={tenantDomainById}
                  />
                ))}
              </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Pagination Controls — single Prev/Next surface that transparently fetches
          the next server batch when the user pages past locally-loaded sessions.
          Renders whenever there's somewhere to navigate (more local pages OR more
          on the server). */}
      {(totalPages > 1 || hasMore) && (
        <div className="mt-4 flex items-center justify-between">
          <div className="text-sm text-gray-700">
            Page {currentPage} of {totalPages}{hasMore ? '+' : ''} ({sortedSessions.length}{hasMore ? '+' : ''} total sessions)
          </div>
          <div className="flex gap-2">
            <button
              onClick={onPreviousPage}
              disabled={currentPage === 1 || loadingMore}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              ← Previous
            </button>
            <button
              onClick={onNextPage}
              disabled={(currentPage >= totalPages && !hasMore) || loadingMore}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed inline-flex items-center gap-2"
            >
              {loadingMore && (
                <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
              )}
              Next →
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function SessionCell({
  columnKey,
  session,
  adminMode,
  globalAdminMode,
  blockedDevicesSet,
  user,
  onDeleteSession,
  isDeletionPending,
  onBlockDevice,
  tenantDomainById,
}: {
  columnKey: string;
  session: Session;
  adminMode: boolean;
  globalAdminMode: boolean;
  blockedDevicesSet: Set<string>;
  user: { isGlobalAdmin?: boolean } | null;
  onDeleteSession: (sessionId: string, tenantId: string, deviceName?: string) => void;
  /** True when the V2 cascade has been queued for this session and we're awaiting `sessionDeleted`. */
  isDeletionPending: boolean;
  onBlockDevice: (serialNumber: string, tenantId: string, deviceName?: string) => void;
  tenantDomainById: Map<string, string>;
}) {
  switch (columnKey) {
    case "device":
      return (
        <td className="px-6 py-4 whitespace-nowrap">
          <div className="text-sm font-medium text-gray-900">
            {session.deviceName || session.serialNumber}
          </div>
          <div className="text-sm text-gray-500">
            {session.serialNumber}
          </div>
        </td>
      );

    case "tenantId": {
      const tenantDomain = tenantDomainById.get(session.tenantId);
      return (
        <td className="px-6 py-4 whitespace-nowrap">
          <button
            onClick={(e) => {
              e.stopPropagation();
              navigator.clipboard.writeText(session.tenantId);
            }}
            className="group flex items-center gap-1 text-xs font-mono text-gray-600 hover:text-blue-600 transition-colors"
            title={session.tenantId}
          >
            <span>{session.tenantId.split('-').slice(0, 2).join('-')}...</span>
            <svg className="w-3 h-3 opacity-0 group-hover:opacity-100 transition-opacity flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
            </svg>
          </button>
          {tenantDomain && (
            <div
              className="mt-0.5 text-xs text-gray-500 truncate max-w-[220px]"
              title={tenantDomain}
            >
              {tenantDomain}
            </div>
          )}
        </td>
      );
    }

    case "model":
      return (
        <td className="px-6 py-4 whitespace-nowrap">
          <div className="text-sm font-medium text-gray-900">
            {session.manufacturer || "Unknown manufacturer"}
          </div>
          <div className="text-sm text-gray-500">
            {session.model || "Unknown model"}
          </div>
        </td>
      );

    case "status":
      return (
        <td className="px-6 py-4 whitespace-nowrap">
          <div className="flex items-center gap-1.5">
            <SessionStatusBadge status={session.status} failureReason={session.failureReason} adminMarkedAction={session.adminMarkedAction} />
            {session.isHybridJoin && (
              <span
                className="px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full bg-purple-100 text-purple-800"
                title="Hybrid Azure AD Join"
              >
                Hybrid
              </span>
            )}
            {blockedDevicesSet.has(`${session.tenantId}:${session.serialNumber}`) && (
              <span
                className="px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full bg-orange-100 text-orange-800"
                title="Device is currently blocked"
              >
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                </svg>
                Blocked
              </span>
            )}
          </div>
        </td>
      );

    case "eventCount":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.eventCount}
        </td>
      );

    case "duration":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {Math.round(session.durationSeconds / 60)} min
        </td>
      );

    case "started":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {new Date(session.startedAt).toLocaleString()}
        </td>
      );

    case "country":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.geoCountry ? (
            <span title={[session.geoCity, session.geoRegion, session.geoCountry].filter(Boolean).join(", ")}>
              {session.geoCountry}
            </span>
          ) : (
            <span className="text-gray-300">—</span>
          )}
        </td>
      );

    case "agentVersion": {
      const fullVersion = session.agentVersion || "";
      const shortVersion = fullVersion.split("+")[0];
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500 font-mono">
          {shortVersion ? (
            <span title={fullVersion}>{shortVersion}</span>
          ) : (
            <span className="text-gray-300">—</span>
          )}
        </td>
      );
    }

    case "osName":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.osName || <span className="text-gray-300">—</span>}
        </td>
      );

    case "osBuild":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500 font-mono">
          {session.osBuild || <span className="text-gray-300">—</span>}
        </td>
      );

    case "osDisplayVersion":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.osDisplayVersion || <span className="text-gray-300">—</span>}
        </td>
      );

    case "osEdition":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.osEdition || <span className="text-gray-300">—</span>}
        </td>
      );

    case "osLanguage":
      return (
        <td className="px-3 py-4 whitespace-nowrap text-sm text-gray-500">
          {session.osLanguage || <span className="text-gray-300">—</span>}
        </td>
      );

    case "actions":
      return (
        <td className="pl-3 pr-4 py-4 whitespace-nowrap text-right text-sm font-medium">
          <div className="flex items-center justify-end gap-2">
            {globalAdminMode && user?.isGlobalAdmin && !isDeletionPending && (
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  onBlockDevice(session.serialNumber, session.tenantId, session.deviceName || session.serialNumber);
                }}
                className="text-orange-500 hover:text-orange-700 transition-colors"
                title="Device blocken"
              >
                <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                </svg>
              </button>
            )}
            {isDeletionPending ? (
              // V2 cascade in flight — render a disabled spinner so the user knows the click
              // was accepted and the row will disappear when the worker fires `sessionDeleted`.
              // Plan §5 PR5 finding 3.
              <span
                className="inline-flex items-center text-gray-400"
                title="Deletion queued — cascade worker is draining this session"
                aria-label="Deletion in progress"
              >
                <svg className="animate-spin h-5 w-5" viewBox="0 0 24 24" fill="none" stroke="currentColor">
                  <circle cx="12" cy="12" r="10" strokeWidth="3" className="opacity-25" />
                  <path d="M4 12a8 8 0 018-8" strokeWidth="3" className="opacity-75" />
                </svg>
              </span>
            ) : (
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  onDeleteSession(session.sessionId, session.tenantId, session.deviceName || session.serialNumber);
                }}
                className="text-red-600 hover:text-red-900 transition-colors"
                title="Delete session"
              >
                <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                </svg>
              </button>
            )}
          </div>
        </td>
      );

    default:
      return <td className="px-3 py-4" />;
  }
}

function SortableHeader({
  column,
  currentSort,
  direction,
  onSort,
  children,
  className,
  filterKey,
  filterValues,
  activeFilter,
  isFilterOpen,
  onFilterToggle,
  onFilterChange,
  filterDropdownRef,
}: {
  column: keyof Session;
  currentSort: keyof Session | null;
  direction: "asc" | "desc";
  onSort: (column: keyof Session) => void;
  children: React.ReactNode;
  className?: string;
  filterKey?: keyof Session;
  filterValues?: string[];
  activeFilter?: Set<string>;
  isFilterOpen?: boolean;
  onFilterToggle?: () => void;
  onFilterChange?: (field: keyof Session, values: Set<string>) => void;
  filterDropdownRef?: React.Ref<HTMLDivElement>;
}) {
  const isActive = currentSort === column;
  const hasActiveFilter = activeFilter && activeFilter.size > 0;
  const [filterSearch, setFilterSearch] = useState("");

  const filteredFilterValues = filterValues?.filter(v =>
    v.toLowerCase().includes(filterSearch.toLowerCase())
  );

  return (
    <th
      className={`py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider select-none relative ${className ?? "px-6"}`}
    >
      <div className="flex items-center gap-1">
        <button
          onClick={() => onSort(column)}
          className="flex items-center gap-1 hover:text-gray-700 transition-colors cursor-pointer"
        >
          {children}
          <span className="text-gray-400">
            {isActive ? (direction === "asc" ? "↑" : "↓") : "↕"}
          </span>
        </button>
        {filterKey && onFilterToggle && (
          <button
            onClick={(e) => {
              e.stopPropagation();
              onFilterToggle();
            }}
            className={`p-0.5 rounded transition-colors ${hasActiveFilter ? "text-blue-600 hover:text-blue-800" : "text-gray-300 hover:text-gray-500"}`}
            title={hasActiveFilter ? `Filtered (${activeFilter!.size} selected)` : "Filter this column"}
          >
            <svg className="w-3 h-3" fill={hasActiveFilter ? "currentColor" : "none"} stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
            </svg>
          </button>
        )}
      </div>

      {/* Filter Dropdown */}
      {isFilterOpen && filterKey && filterValues && onFilterChange && (
        <div
          ref={filterDropdownRef}
          className="absolute left-0 top-full mt-1 w-52 bg-white rounded-lg shadow-lg border border-gray-200 z-50 py-2"
          onClick={(e) => e.stopPropagation()}
        >
          {/* Search within filter values */}
          {filterValues.length > 8 && (
            <div className="px-2 pb-2">
              <input
                type="text"
                placeholder="Search..."
                value={filterSearch}
                onChange={(e) => setFilterSearch(e.target.value)}
                className="w-full px-2 py-1 text-xs border border-gray-200 rounded text-gray-700 placeholder-gray-400 focus:outline-none focus:ring-1 focus:ring-blue-500"
                onClick={(e) => e.stopPropagation()}
              />
            </div>
          )}
          <div className="max-h-48 overflow-y-auto">
            {(filteredFilterValues ?? filterValues).map((value) => {
              const isChecked = activeFilter?.has(value) ?? false;
              return (
                <label
                  key={value}
                  className="flex items-center gap-2 px-3 py-1 hover:bg-gray-50 cursor-pointer text-xs text-gray-700 normal-case tracking-normal font-normal"
                >
                  <input
                    type="checkbox"
                    checked={isChecked}
                    onChange={() => {
                      const next = new Set(activeFilter ?? []);
                      if (isChecked) {
                        next.delete(value);
                      } else {
                        next.add(value);
                      }
                      onFilterChange(filterKey, next);
                    }}
                    className="rounded border-gray-300 text-blue-600 focus:ring-blue-500 h-3 w-3"
                  />
                  <span className="truncate">{value}</span>
                </label>
              );
            })}
            {filteredFilterValues?.length === 0 && (
              <div className="px-3 py-2 text-xs text-gray-400">No matches</div>
            )}
          </div>
          {hasActiveFilter && (
            <div className="border-t border-gray-100 mt-1 pt-1 px-3">
              <button
                onClick={() => onFilterChange(filterKey, new Set())}
                className="text-xs text-blue-500 hover:text-blue-700 font-medium normal-case tracking-normal"
              >
                Clear filter
              </button>
            </div>
          )}
        </div>
      )}
    </th>
  );
}

