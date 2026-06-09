"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation } from "@/lib/paginationLink";
import { extractSessionId, buildAutoReason } from "./opsEventSessionHelpers";

interface OpsEvent {
  id: string;
  category: string;
  eventType: string;
  severity: string;
  tenantId: string | null;
  userId: string | null;
  message: string;
  details: string | null;
  timestamp: string;
}

interface OpsEventsSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  opsEventRetentionDays: number;
  setOpsEventRetentionDays: (value: number) => void;
  onSaveConfig: () => Promise<void>;
  savingConfig: boolean;
}

const SEVERITY_STYLES: Record<string, { badge: string; row: string }> = {
  Info:     { badge: "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300",       row: "" },
  Warning:  { badge: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",   row: "" },
  Error:    { badge: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",           row: "bg-red-50/30 dark:bg-red-900/10" },
  Critical: { badge: "bg-red-200 text-red-900 dark:bg-red-800/60 dark:text-red-200 font-bold", row: "bg-red-50/50 dark:bg-red-900/20" },
};

const CATEGORY_STYLES: Record<string, string> = {
  Consent:     "bg-indigo-100 text-indigo-800 dark:bg-indigo-900/40 dark:text-indigo-300",
  Maintenance: "bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300",
  Security:    "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300",
  Tenant:      "bg-teal-100 text-teal-800 dark:bg-teal-900/40 dark:text-teal-300",
  Agent:       "bg-cyan-100 text-cyan-800 dark:bg-cyan-900/40 dark:text-cyan-300",
};

const ALL_CATEGORIES = ["Consent", "Maintenance", "Security", "Tenant", "Agent"];

const PAGE_SIZE = 20;

function defaultIsoDateFrom(): string {
  const d = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000);
  d.setUTCHours(0, 0, 0, 0);
  return d.toISOString();
}

function defaultIsoDateTo(): string {
  return new Date().toISOString();
}

function isoToDateInputValue(iso: string): string {
  return iso.slice(0, 10);
}

function dateInputToIsoStart(value: string): string {
  return value ? `${value}T00:00:00.000Z` : "";
}

function dateInputToIsoEnd(value: string): string {
  return value ? `${value}T23:59:59.999Z` : "";
}

function SeverityBadge({ severity }: { severity: string }) {
  const style = SEVERITY_STYLES[severity] ?? SEVERITY_STYLES.Info;
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${style.badge}`}>
      {severity}
    </span>
  );
}

function CategoryBadge({ category }: { category: string }) {
  const style = CATEGORY_STYLES[category] ?? "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300";
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${style}`}>
      {category}
    </span>
  );
}

function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  const handleCopy = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };
  return (
    <button
      onClick={handleCopy}
      title="Copy to clipboard"
      className="ml-1.5 p-0.5 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition-colors"
    >
      {copied ? (
        <svg className="w-3.5 h-3.5 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
        </svg>
      ) : (
        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
        </svg>
      )}
    </button>
  );
}

export function OpsEventsSection({
  getAccessToken,
  setError,
  opsEventRetentionDays,
  setOpsEventRetentionDays,
  onSaveConfig,
  savingConfig,
}: OpsEventsSectionProps) {
  const [events, setEvents] = useState<OpsEvent[]>([]);
  const [loading, setLoading] = useState(false);
  const [selectedEvent, setSelectedEvent] = useState<OpsEvent | null>(null);
  const [categoryFilter, setCategoryFilter] = useState<string>("");
  const [searchQuery, setSearchQuery] = useState("");

  const [dateFromIso, setDateFromIso] = useState<string>(defaultIsoDateFrom());
  const [dateToIso, setDateToIso] = useState<string>(defaultIsoDateTo());

  // Pattern B1 click-next replace state — backend pagination
  const [continuation, setContinuation] = useState<string | null>(null);
  const [nextLink, setNextLink] = useState<string | null>(null);
  const [continuationStack, setContinuationStack] = useState<Array<string | null>>([]);
  const [pageNumber, setPageNumber] = useState(1);

  const fetchEvents = useCallback(async (cursor: string | null) => {
    try {
      setLoading(true);
      const res = await authenticatedFetch(
        api.opsEvents.list(categoryFilter || undefined, {
          dateFrom: dateFromIso,
          dateTo: dateToIso,
          pageSize: PAGE_SIZE,
          continuation: cursor ?? undefined,
        }),
        getAccessToken
      );
      if (res.status === 404) {
        setEvents([]);
        setNextLink(null);
        return;
      }
      if (!res.ok) throw new Error(`Failed to load ops events: ${res.statusText}`);
      const data = await res.json();
      setEvents(data.events ?? []);
      setNextLink(data.nextLink ?? null);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while loading ops events");
      }
      setError(err instanceof Error ? err.message : "Failed to load ops events");
    } finally {
      setLoading(false);
    }
  }, [categoryFilter, dateFromIso, dateToIso, getAccessToken, setError]);

  // Reset pagination + refetch whenever the filter window or category changes.
  // fetchEvents is intentionally excluded from deps: getAccessToken's identity
  // churns on every MSAL accounts-array refresh, which happens after each
  // authenticatedFetch — leaving fetchEvents in deps causes the effect to
  // re-fire after every successful page-N click, race a page-1 fetch against
  // it, and snap the user back to page 1.
  useEffect(() => {
    setContinuation(null);
    setContinuationStack([]);
    setPageNumber(1);
    fetchEvents(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [categoryFilter, dateFromIso, dateToIso]);

  const handleNextPage = () => {
    const nextCont = extractContinuation(nextLink);
    if (!nextCont) return;
    setContinuationStack(stack => [...stack, continuation]);
    setContinuation(nextCont);
    setPageNumber(n => n + 1);
    fetchEvents(nextCont);
  };

  const handlePrevPage = () => {
    if (continuationStack.length === 0) return;
    const prev = continuationStack[continuationStack.length - 1];
    setContinuationStack(stack => stack.slice(0, -1));
    setContinuation(prev ?? null);
    setPageNumber(n => Math.max(1, n - 1));
    fetchEvents(prev ?? null);
  };

  const handleRefresh = () => fetchEvents(continuation);

  // Search filter operates on the current backend page only — Pattern B1 shows
  // one page at a time, so cross-page totals are intentionally not surfaced.
  const filteredEvents = searchQuery.trim()
    ? events.filter((e) => {
        const q = searchQuery.toLowerCase();
        return (
          e.eventType.toLowerCase().includes(q) ||
          e.message.toLowerCase().includes(q) ||
          (e.details?.toLowerCase().includes(q) ?? false) ||
          (e.tenantId?.toLowerCase().includes(q) ?? false) ||
          (e.userId?.toLowerCase().includes(q) ?? false) ||
          e.severity.toLowerCase().includes(q) ||
          e.category.toLowerCase().includes(q)
        );
      })
    : events;

  // Summary stats
  const categoryCounts = filteredEvents.reduce<Record<string, number>>((acc, e) => {
    acc[e.category] = (acc[e.category] ?? 0) + 1;
    return acc;
  }, {});

  const severityCounts = filteredEvents.reduce<Record<string, number>>((acc, e) => {
    acc[e.severity] = (acc[e.severity] ?? 0) + 1;
    return acc;
  }, {});

  const errorCount = (severityCounts["Error"] ?? 0) + (severityCounts["Critical"] ?? 0);

  return (
    <div className="bg-gradient-to-br from-slate-50 to-gray-50 dark:from-gray-800 dark:to-gray-800 border-2 border-slate-300 dark:border-slate-700 rounded-lg shadow-lg">
      {/* Header */}
      <div className="p-6 border-b border-slate-200 dark:border-slate-700 bg-gradient-to-r from-slate-100 to-gray-100 dark:from-slate-900/40 dark:to-gray-900/40">
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-slate-600 dark:text-slate-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Operational Events</h2>
              <p className="text-sm text-slate-600 dark:text-slate-300 mt-1">
                Vital infrastructure events across consent, maintenance, security, and tenant lifecycle.
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <Link
              href="/admin/settings/alerts"
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 transition-colors"
            >
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
              </svg>
              Configure Alerts
            </Link>
            <button
              onClick={handleRefresh}
              disabled={loading}
              className="px-3 py-1.5 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 transition-colors"
            >
              {loading ? "Loading..." : "Refresh"}
            </button>
          </div>
        </div>
      </div>

      {/* Summary Cards */}
      {!loading && filteredEvents.length > 0 && (
        <div className="p-4 border-b border-slate-200 dark:border-slate-700 bg-slate-50/50 dark:bg-gray-800/50">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <div className="bg-white dark:bg-gray-700 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
              <div className="text-2xl font-bold text-gray-900 dark:text-gray-100">{filteredEvents.length}</div>
              <div className="text-xs text-gray-500 dark:text-gray-400">Total Events</div>
            </div>
            <div className={`bg-white dark:bg-gray-700 rounded-lg p-3 border ${errorCount > 0 ? 'border-red-300 dark:border-red-600' : 'border-gray-200 dark:border-gray-600'}`}>
              <div className={`text-2xl font-bold ${errorCount > 0 ? 'text-red-600 dark:text-red-400' : 'text-gray-900 dark:text-gray-100'}`}>
                {errorCount}
              </div>
              <div className="text-xs text-gray-500 dark:text-gray-400">Errors / Critical</div>
            </div>
            <div className="col-span-2 bg-white dark:bg-gray-700 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
              <div className="text-xs text-gray-500 dark:text-gray-400 mb-1.5">By Category</div>
              <div className="flex flex-wrap gap-1.5">
                {Object.entries(categoryCounts)
                  .sort(([, a], [, b]) => b - a)
                  .map(([cat, count]) => (
                    <span key={cat} className="text-xs">
                      <CategoryBadge category={cat} /> <span className="text-gray-500 dark:text-gray-400 ml-0.5">{count}</span>
                    </span>
                  ))}
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Retention Config */}
      <div className="px-6 pt-4 pb-2">
        <div className="flex items-center gap-3 bg-white dark:bg-gray-700 border border-gray-200 dark:border-gray-600 rounded-lg p-3">
          <label className="text-sm font-medium text-gray-700 dark:text-gray-300 whitespace-nowrap">
            Retention:
          </label>
          <input
            type="number"
            min="1"
            max="365"
            value={opsEventRetentionDays}
            onChange={(e) => setOpsEventRetentionDays(parseInt(e.target.value) || 90)}
            className="w-20 px-2 py-1 text-sm border border-gray-300 dark:border-gray-600 rounded bg-white dark:bg-gray-600 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-slate-500"
          />
          <span className="text-sm text-gray-500 dark:text-gray-400">days</span>
          <button
            onClick={onSaveConfig}
            disabled={savingConfig}
            className="ml-auto px-3 py-1 text-xs font-medium rounded-md bg-slate-600 text-white hover:bg-slate-700 disabled:opacity-50 transition-colors"
          >
            {savingConfig ? "Saving..." : "Save"}
          </button>
        </div>
      </div>

      {/* Date window */}
      <div className="px-6 pt-4 pb-2 flex items-center gap-3 flex-wrap">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Window:</span>
        <label className="text-xs text-gray-600 dark:text-gray-400">From</label>
        <input
          type="date"
          value={isoToDateInputValue(dateFromIso)}
          onChange={(e) => setDateFromIso(dateInputToIsoStart(e.target.value))}
          className="px-2 py-1 text-xs border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-slate-500 focus:border-slate-500"
        />
        <label className="text-xs text-gray-600 dark:text-gray-400">To</label>
        <input
          type="date"
          value={isoToDateInputValue(dateToIso)}
          onChange={(e) => setDateToIso(dateInputToIsoEnd(e.target.value))}
          className="px-2 py-1 text-xs border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-slate-500 focus:border-slate-500"
        />
      </div>

      {/* Search & Category Filter */}
      <div className="px-6 pt-4 pb-2 flex items-center gap-2 flex-wrap">
        <div className="relative">
          <svg className="absolute left-2.5 top-1/2 -translate-y-1/2 w-3.5 h-3.5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            type="text"
            placeholder="Search this page..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-52 pl-8 pr-7 py-1 text-xs border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-slate-500 focus:border-slate-500"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery("")}
              className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
            >
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
        <div className="w-px h-5 bg-gray-300 dark:bg-gray-600 mx-1" />
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Filter:</span>
        <button
          onClick={() => setCategoryFilter("")}
          className={`px-2.5 py-1 text-xs font-medium rounded-md border transition-colors ${
            categoryFilter === ""
              ? "bg-slate-700 text-white border-slate-700 dark:bg-slate-500 dark:border-slate-500"
              : "border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
          }`}
        >
          All
        </button>
        {ALL_CATEGORIES.map(cat => (
          <button
            key={cat}
            onClick={() => setCategoryFilter(cat)}
            className={`px-2.5 py-1 text-xs font-medium rounded-md border transition-colors ${
              categoryFilter === cat
                ? "bg-slate-700 text-white border-slate-700 dark:bg-slate-500 dark:border-slate-500"
                : "border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600"
            }`}
          >
            {cat}
          </button>
        ))}
      </div>

      {/* Events Table */}
      <div className="p-6 pt-2">
        {loading ? (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-slate-600"></div>
            <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading ops events...</span>
          </div>
        ) : filteredEvents.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <p className="text-sm">
              {searchQuery ? `No events matching "${searchQuery}".` : "No operational events recorded yet."}
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Time</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Severity</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Category</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Event</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Message</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Tenant</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {filteredEvents.map((evt, idx) => {
                  const rowStyle = SEVERITY_STYLES[evt.severity]?.row ?? "";
                  return (
                    <tr
                      key={evt.id || `${evt.timestamp}-${idx}`}
                      onClick={() => setSelectedEvent(evt)}
                      className={`hover:bg-slate-50 dark:hover:bg-slate-900/20 cursor-pointer transition-colors ${rowStyle}`}
                    >
                      <td className="px-3 py-2.5 text-sm text-gray-900 dark:text-gray-100 whitespace-nowrap">
                        {new Date(evt.timestamp).toLocaleString()}
                      </td>
                      <td className="px-3 py-2.5 text-sm">
                        <SeverityBadge severity={evt.severity} />
                      </td>
                      <td className="px-3 py-2.5 text-sm">
                        <CategoryBadge category={evt.category} />
                      </td>
                      <td className="px-3 py-2.5 text-sm font-mono text-gray-700 dark:text-gray-300 whitespace-nowrap">
                        {evt.eventType}
                      </td>
                      <td className="px-3 py-2.5 text-sm text-gray-700 dark:text-gray-300 max-w-[300px] truncate">
                        {evt.message}
                      </td>
                      <td className="px-3 py-2.5 text-sm font-mono text-gray-700 dark:text-gray-300">
                        {evt.tenantId ? (evt.tenantId.length > 8 ? `${evt.tenantId.slice(0, 8)}...` : evt.tenantId) : <span className="text-gray-300 dark:text-gray-600">-</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>

            {/* Pagination (Pattern B1 — backend-driven) */}
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md">
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {filteredEvents.length} on page {pageNumber}{searchQuery && ` (filtered)`}{nextLink ? "" : " (last)"}
              </span>
              <div className="flex items-center gap-2">
                <button
                  onClick={handlePrevPage}
                  disabled={continuationStack.length === 0 || loading}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <button
                  onClick={handleNextPage}
                  disabled={!nextLink || loading}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Next
                </button>
              </div>
            </div>
          </div>
        )}
      </div>

      {/* Detail Modal */}
      {selectedEvent && (
        <div
          className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4"
          onClick={() => setSelectedEvent(null)}
        >
          <div
            className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
            onClick={e => e.stopPropagation()}
          >
            <div className="p-6">
              <div className="flex items-center justify-between mb-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Event Details</h3>
                <div className="flex gap-1.5">
                  <SeverityBadge severity={selectedEvent.severity} />
                  <CategoryBadge category={selectedEvent.category} />
                </div>
              </div>

              <dl className="space-y-3 text-sm">
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Event Type</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5">{selectedEvent.eventType}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Timestamp</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">
                    {new Date(selectedEvent.timestamp).toLocaleString()}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Message</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedEvent.message}</dd>
                </div>
                {selectedEvent.tenantId && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Tenant ID</dt>
                    <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                      {selectedEvent.tenantId}
                      <CopyButton value={selectedEvent.tenantId} />
                    </dd>
                  </div>
                )}
                {selectedEvent.userId && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">User</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                      {selectedEvent.userId}
                      <CopyButton value={selectedEvent.userId} />
                    </dd>
                  </div>
                )}
                {selectedEvent.details && (
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Details</dt>
                    <dd className="text-gray-900 dark:text-gray-100 mt-0.5 text-xs bg-gray-50 dark:bg-gray-700 rounded p-2 font-mono break-all whitespace-pre-wrap">
                      {(() => {
                        try {
                          return JSON.stringify(JSON.parse(selectedEvent.details!), null, 2);
                        } catch {
                          return selectedEvent.details;
                        }
                      })()}
                    </dd>
                  </div>
                )}
              </dl>

              {(() => {
                const sessionId = extractSessionId(selectedEvent.details);
                if (!sessionId) return null;
                const reason = buildAutoReason(selectedEvent.eventType, sessionId);
                const baseHref = `/admin/security/device-block?sessionId=${encodeURIComponent(sessionId)}&reason=${encodeURIComponent(reason)}`;
                return (
                  <div className="mt-5 pt-4 border-t border-gray-200 dark:border-gray-700">
                    <p className="text-xs text-gray-500 dark:text-gray-400 mb-2">
                      Review the session, or quick-action against the device behind it:
                    </p>
                    <div className="flex flex-wrap gap-2">
                      <Link
                        href={`/sessions/${encodeURIComponent(sessionId)}`}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="inline-flex items-center px-3 py-1.5 rounded-md text-xs font-medium bg-blue-100 text-blue-800 hover:bg-blue-200 dark:bg-blue-900/40 dark:text-blue-200 dark:hover:bg-blue-900/60 border border-blue-300 dark:border-blue-700"
                      >
                        View session
                      </Link>
                      <Link
                        href={`${baseHref}&action=Block`}
                        onClick={() => setSelectedEvent(null)}
                        className="inline-flex items-center px-3 py-1.5 rounded-md text-xs font-medium bg-orange-100 text-orange-800 hover:bg-orange-200 dark:bg-orange-900/40 dark:text-orange-200 dark:hover:bg-orange-900/60 border border-orange-300 dark:border-orange-700"
                      >
                        Block this device
                      </Link>
                      <Link
                        href={`${baseHref}&action=Kill`}
                        onClick={() => setSelectedEvent(null)}
                        className="inline-flex items-center px-3 py-1.5 rounded-md text-xs font-medium bg-red-700 text-white hover:bg-red-800 dark:bg-red-700 dark:hover:bg-red-800"
                      >
                        Kill this device
                      </Link>
                    </div>
                  </div>
                );
              })()}

              <div className="mt-6 flex justify-end">
                <button
                  onClick={() => setSelectedEvent(null)}
                  className="px-4 py-2 text-sm font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 transition-colors"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
