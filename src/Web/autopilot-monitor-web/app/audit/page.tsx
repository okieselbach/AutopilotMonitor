'use client';

import { Fragment, useCallback, useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from '@/lib/api';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation } from "@/lib/paginationLink";
import { useAggregatedAdminScope } from "@/hooks";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";


interface AuditLogEntry {
  id: string;
  tenantId: string;
  action: string;
  entityType: string;
  entityId: string;
  performedBy: string;
  timestamp: string;
  details: string;
}

type ActionFilter = 'DEFAULT' | 'ALL' | 'DELETE' | 'UPDATE' | 'CREATE';
type EntityTypeFilter = string;

// Maintenance-driven actions that are usually noise in the default view.
// Operators rarely care about per-session deletion bookkeeping unless they
// are explicitly auditing cleanup runs.
const NOISY_ACTIONS = new Set(['deletion_started', 'deletion_completed']);

const PAGE_SIZE = 15;

function defaultIsoDateFrom(): string {
  // 30 days ago, ISO 8601 UTC, midnight — matches the backend's default
  // window so first paint without a manual filter shows the same data set.
  const d = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000);
  d.setUTCHours(0, 0, 0, 0);
  return d.toISOString();
}

function defaultIsoDateTo(): string {
  return new Date().toISOString();
}

function isoToDateInputValue(iso: string): string {
  // <input type="date"> wants yyyy-MM-dd — we display the date portion of UTC.
  return iso.slice(0, 10);
}

function dateInputToIsoStart(value: string): string {
  // Treat the picker as a UTC midnight on that calendar date.
  return value ? `${value}T00:00:00.000Z` : '';
}

function dateInputToIsoEnd(value: string): string {
  // Inclusive upper bound: end of that calendar day in UTC.
  return value ? `${value}T23:59:59.999Z` : '';
}

export default function AuditPage() {
  const router = useRouter();

  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const [logs, setLogs] = useState<AuditLogEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [actionFilter, setActionFilter] = useState<ActionFilter>('DEFAULT');
  const [entityTypeFilter, setEntityTypeFilter] = useState<EntityTypeFilter>('ALL');
  const [expandedRow, setExpandedRow] = useState<string | null>(null);

  // Date window — both default to the last 30 days (matches backend default).
  const [dateFromIso, setDateFromIso] = useState<string>(defaultIsoDateFrom());
  const [dateToIso, setDateToIso] = useState<string>(defaultIsoDateTo());

  // Pattern B1 click-next replace state
  const [continuation, setContinuation] = useState<string | null>(null);
  const [nextLink, setNextLink] = useState<string | null>(null);
  const [continuationStack, setContinuationStack] = useState<Array<string | null>>([]);
  const [pageNumber, setPageNumber] = useState(1);

  // Cross-tenant scope: a GA/Reader gets the "All tenants" aggregate AND a per-tenant drill-down; a
  // delegated ("MSP") admin gets the per-tenant dropdown only (no aggregate). selectedTenantId is the audit
  // tenant filter ("" = GA aggregate). scopeKey changes on tenant/GA-mode change → drives the refetch.
  // GA/Reader starts in the aggregated "All tenants" view; a delegated ("MSP") admin has no aggregate and
  // still defaults to its first managed tenant via the normal tenant switcher.
  const scope = useAggregatedAdminScope({ defaultAggregated: true });
  const { isGlobalAdmin: crossTenant, routeGlobal, selectedTenantId, scopeInitialized, scopeKey } = scope;

  // The DEFAULT view ("All (excl. deletions)") is resolved server-side: the
  // backend drops per-session deletion bookkeeping and back-fills the page, so
  // a cleanup-heavy window still returns a full page of real entries instead of
  // an all-deletions page that the client would strip down to nothing.
  const excludeDeletions = actionFilter === 'DEFAULT';

  const fetchPage = useCallback(async (
    nextContinuation: string | null,
    isInitial: boolean,
  ) => {
    try {
      if (isInitial) setLoading(true); else setRefreshing(true);

      const opts = {
        dateFrom: dateFromIso,
        dateTo: dateToIso,
        pageSize: PAGE_SIZE,
        continuation: nextContinuation ?? undefined,
        excludeDeletions,
      };
      // Cross-tenant: globalLogs with the selected tenant ("" → GA aggregate over all tenants; a managed
      // tenant for a delegated caller, validated server-side). Own-tenant member — including a delegated
      // caller viewing their HOME tenant (routeGlobal false): the tenant-scoped logs.
      const endpoint = routeGlobal
        ? api.audit.globalLogs({ ...opts, tenantId: selectedTenantId || undefined })
        : api.audit.logs(opts);
      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (!response.ok) {
        addNotification('error', 'Backend Error', `Failed to load audit logs: ${response.statusText}`, 'audit-fetch-error');
        return;
      }
      const data = await response.json();
      if (!data.success) {
        addNotification('error', 'Backend Error', data.message || 'Failed to load audit logs', 'audit-fetch-error');
        return;
      }
      setLogs(data.logs || []);
      setNextLink(data.nextLink ?? null);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Error fetching audit logs:', err);
        addNotification('error', 'Backend Not Reachable', 'Unable to load audit logs. Please check your connection.', 'audit-fetch-error');
      }
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [addNotification, dateFromIso, dateToIso, getAccessToken, routeGlobal, selectedTenantId, excludeDeletions]);

  // Initial / window-change fetch resets pagination state.
  // fetchPage is intentionally excluded from deps: its identity churns whenever
  // MSAL refreshes the `accounts` array (getAccessToken → useCallback → fetchPage),
  // which happens after every authenticatedFetch. With fetchPage in deps the
  // effect fires immediately after each successful Next click, resets the
  // pagination state, and races a fresh page-1 fetch against the in-flight
  // page-N fetch — giving the visible "Next does nothing" symptom. The
  // useCallback closure is rebuilt from the same window deps the effect
  // already tracks, so the latest fetchPage is invoked when the effect runs.
  useEffect(() => {
    // Wait until the scope's default selection settles (own tenant, or the first managed tenant for a
    // delegated caller) so we don't fire a wasted request in the wrong scope. scopeKey changes on a
    // tenant/GA-mode switch → refetch from page 1.
    if (!scopeInitialized) return;
    setContinuation(null);
    setContinuationStack([]);
    setPageNumber(1);
    fetchPage(null, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeKey, scopeInitialized, dateFromIso, dateToIso, excludeDeletions]);

  const handleRefresh = () => {
    fetchPage(continuation, false);
  };

  const handleNextPage = () => {
    const nextCont = extractContinuation(nextLink);
    if (!nextCont) return;
    setContinuationStack(stack => [...stack, continuation]);
    setContinuation(nextCont);
    setPageNumber(n => n + 1);
    fetchPage(nextCont, false);
  };

  const handlePrevPage = () => {
    if (continuationStack.length === 0) return;
    const prev = continuationStack[continuationStack.length - 1];
    setContinuationStack(stack => stack.slice(0, -1));
    setContinuation(prev ?? null);
    setPageNumber(n => Math.max(1, n - 1));
    fetchPage(prev ?? null, false);
  };

  // Client-side filters operate on the current page only — Pattern B1 shows one
  // backend page at a time, so global counts are intentionally not surfaced.
  const entityTypes = ['ALL', ...Array.from(new Set(logs.map(l => l.entityType).filter(Boolean)))];
  const filteredLogs = logs.filter(log => {
    if (actionFilter === 'DEFAULT') {
      if (NOISY_ACTIONS.has(log.action)) return false;
    } else if (actionFilter !== 'ALL' && log.action !== actionFilter) {
      return false;
    }
    if (entityTypeFilter !== 'ALL' && log.entityType !== entityTypeFilter) return false;
    if (searchQuery) {
      const q = searchQuery.toLowerCase();
      return (
        log.performedBy?.toLowerCase().includes(q) ||
        log.entityId?.toLowerCase().includes(q) ||
        log.entityType?.toLowerCase().includes(q) ||
        log.action?.toLowerCase().includes(q) ||
        log.details?.toLowerCase().includes(q)
      );
    }
    return true;
  });

  const formatTimestamp = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  const getActionBadge = (action: string) => {
    switch (action?.toUpperCase()) {
      case 'DELETE':
        return 'bg-red-100 text-red-800';
      case 'CREATE':
        return 'bg-green-100 text-green-800';
      case 'UPDATE':
        return 'bg-blue-100 text-blue-800';
      default:
        return 'bg-gray-100 text-gray-800';
    }
  };

  const parseDetails = (details: string): Record<string, string> | null => {
    if (!details) return null;
    try {
      return JSON.parse(details);
    } catch {
      return null;
    }
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 dark:from-gray-900 dark:to-gray-800 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600 dark:text-gray-400">Loading audit logs...</p>
        </div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner
          show={crossTenant}
          delegated={scope.isDelegatedScope}
          subtitle={globalAdminSubtitle(scope, "aggregating data across all tenants")}
        />
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <div>
                  <h1 className="text-2xl font-normal text-gray-900">Audit Log</h1>
                  <p className="text-sm text-gray-600 mt-1">
                    {filteredLogs.length} {filteredLogs.length === 1 ? 'entry' : 'entries'} on this page
                    {filteredLogs.length !== logs.length && ` (filtered from ${logs.length})`}
                  </p>
                </div>
              </div>
              <div className="flex items-center gap-3">
                <TenantScopeSelector scope={scope} allowAggregated />
                <button
                  onClick={handleRefresh}
                  disabled={refreshing}
                  className="px-4 py-2 bg-white border border-gray-200 text-gray-700 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                <svg className={`h-5 w-5 ${refreshing ? 'animate-spin' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
                <span>{refreshing ? 'Refreshing...' : 'Refresh'}</span>
                </button>
              </div>
            </div>
          </div>
        </header>
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">

          {/* Date window + filters */}
          <div className="mb-4 flex flex-wrap items-center gap-3">
            {/* Date pickers */}
            <div className="flex items-center gap-2">
              <label className="text-sm text-gray-600 dark:text-gray-400">From:</label>
              <input
                type="date"
                value={isoToDateInputValue(dateFromIso)}
                onChange={(e) => setDateFromIso(dateInputToIsoStart(e.target.value))}
                className="px-3 py-2 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
              <label className="text-sm text-gray-600 dark:text-gray-400">To:</label>
              <input
                type="date"
                value={isoToDateInputValue(dateToIso)}
                onChange={(e) => setDateToIso(dateInputToIsoEnd(e.target.value))}
                className="px-3 py-2 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>

            {/* Search */}
            <div className="relative flex-1 min-w-[200px]">
              <svg className="absolute left-3 top-1/2 transform -translate-y-1/2 h-5 w-5 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
              <input
                type="text"
                placeholder="Search by user, entity, action or details (this page only)..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full pl-10 pr-4 py-2 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg text-gray-900 dark:text-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
              />
            </div>

            {/* Action Filter */}
            <select
              value={actionFilter}
              onChange={(e) => setActionFilter(e.target.value as ActionFilter)}
              className="px-3 py-2 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              <option value="DEFAULT">All (excl. deletions)</option>
              <option value="ALL">All Actions</option>
              <option value="CREATE">Create</option>
              <option value="UPDATE">Update</option>
              <option value="DELETE">Delete</option>
            </select>

            {/* Entity Type Filter */}
            <select
              value={entityTypeFilter}
              onChange={(e) => setEntityTypeFilter(e.target.value)}
              className="px-3 py-2 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              {entityTypes.map(type => (
                <option key={type} value={type}>
                  {type === 'ALL' ? 'All Entity Types' : type}
                </option>
              ))}
            </select>
          </div>

          {/* Audit Table */}
          <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
            {filteredLogs.length === 0 ? (
              <div className="p-12 text-center">
                <svg className="h-16 w-16 text-gray-300 dark:text-gray-600 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                <p className="text-gray-500 dark:text-gray-400 text-lg">No audit log entries found</p>
                {(searchQuery || actionFilter !== 'DEFAULT' || entityTypeFilter !== 'ALL') && (
                  <p className="text-gray-400 dark:text-gray-500 text-sm mt-2">Try adjusting your filters</p>
                )}
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                  <thead className="bg-gray-50 dark:bg-gray-750">
                    <tr>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Timestamp
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Action
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Entity Type
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Entity ID
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Performed By
                      </th>
                      <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">
                        Details
                      </th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                    {filteredLogs.map((log) => {
                      const details = parseDetails(log.details);
                      const isExpanded = expandedRow === log.id;

                      return (
                        <Fragment key={log.id}>
                          <tr
                            className={`hover:bg-gray-50 dark:hover:bg-gray-750 transition-colors cursor-pointer ${isExpanded ? 'bg-gray-50 dark:bg-gray-750' : ''}`}
                            onClick={() => setExpandedRow(isExpanded ? null : log.id)}
                          >
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-700 dark:text-gray-300">
                              {formatTimestamp(log.timestamp)}
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap">
                              <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${getActionBadge(log.action)}`}>
                                {log.action}
                              </span>
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-700 dark:text-gray-300">
                              {log.entityType}
                            </td>
                            <td className="px-6 py-4 text-sm text-gray-700 dark:text-gray-300 max-w-[200px] truncate" title={log.entityId}>
                              {log.entityId}
                            </td>
                            <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-700 dark:text-gray-300">
                              {log.performedBy}
                            </td>
                            <td className="px-6 py-4 text-sm text-gray-500 dark:text-gray-400">
                              {details ? (
                                <button
                                  onClick={(e) => {
                                    e.stopPropagation();
                                    setExpandedRow(isExpanded ? null : log.id);
                                  }}
                                  className="text-blue-600 dark:text-blue-400 hover:text-blue-800 dark:hover:text-blue-300 text-xs font-medium"
                                >
                                  {isExpanded ? 'Hide details' : 'Show details'}
                                </button>
                              ) : (
                                <span className="text-gray-400 dark:text-gray-500">-</span>
                              )}
                            </td>
                          </tr>
                          {isExpanded && details && (
                            <tr className="bg-gray-50 dark:bg-gray-750">
                              <td colSpan={6} className="px-6 py-4">
                                <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm max-w-2xl">
                                  {Object.entries(details).map(([key, value]) => (
                                    <Fragment key={key}>
                                      <span className="font-medium text-gray-600 dark:text-gray-300 whitespace-nowrap">{key}:</span>
                                      <span className="text-gray-800 dark:text-gray-200 break-words">{value}</span>
                                    </Fragment>
                                  ))}
                                </div>
                              </td>
                            </tr>
                          )}
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            )}
          </div>

          {/* Pagination Controls (Pattern B1) */}
          <div className="mt-4 flex items-center justify-between">
            <div className="text-sm text-gray-700">
              Page {pageNumber}
              {nextLink ? '' : ' (last)'}
            </div>
            <div className="flex gap-2">
              <button
                onClick={handlePrevPage}
                disabled={continuationStack.length === 0 || refreshing}
                className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                &larr; Previous
              </button>
              <button
                onClick={handleNextPage}
                disabled={!nextLink || refreshing}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
              >
                Next &rarr;
              </button>
            </div>
          </div>
        </div>
      </div>
    </ProtectedRoute>
  );
}
