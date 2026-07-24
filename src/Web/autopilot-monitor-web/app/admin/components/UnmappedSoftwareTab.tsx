"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import TruncatedLabel from "@/components/TruncatedLabel";
import { trackEvent } from "@/lib/appInsights";
import type { UnmatchedSoftwareEntry, AutoResolveResult } from "./SoftwareMappingTypes";

interface UnmappedSoftwareTabProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  onMappingChanged: () => void;
  onIgnored: () => void;
  onCountChanged: (count: number) => void;
  refreshTrigger: number;
}

export function UnmappedSoftwareTab({
  getAccessToken,
  setError,
  onMappingChanged,
  onIgnored,
  onCountChanged,
  refreshTrigger,
}: UnmappedSoftwareTabProps) {
  // Unmapped state — server-side pagination
  const pageSize = 20;
  const [loading, setLoading] = useState(false);
  const [initialLoaded, setInitialLoaded] = useState(false);
  const [entries, setEntries] = useState<UnmatchedSoftwareEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [currentPage, setCurrentPage] = useState(0);

  // CPE mapping state
  const [expandedMappingRow, setExpandedMappingRow] = useState<string | null>(null);
  const [cpeInputs, setCpeInputs] = useState<Record<string, string>>({});
  const [savingMapping, setSavingMapping] = useState<string | null>(null);
  const [savedMappings, setSavedMappings] = useState<Set<string>>(new Set());

  // Bulk auto-resolve state — selection persists across pages, keyed by softwareName::publisher
  const [selectedEntries, setSelectedEntries] = useState<Map<string, UnmatchedSoftwareEntry>>(new Map());
  const [autoResolving, setAutoResolving] = useState(false);
  const [autoResolveProgress, setAutoResolveProgress] = useState<{
    batchIndex: number;
    totalBatches: number;
    resolvedSoFar: number;
    failedSoFar: number;
  } | null>(null);
  const [autoResolveResults, setAutoResolveResults] = useState<AutoResolveResult | null>(null);

  // Ignore state
  const [ignoringRow, setIgnoringRow] = useState<string | null>(null);

  // Propagate total to parent
  useEffect(() => {
    onCountChanged(total);
  }, [total, onCountChanged]);

  // --- Fetching ---

  // Server-paginated fetch. Returns the server total so callers can react to range changes.
  const fetchPage = useCallback(
    async (page: number): Promise<number | null> => {
      try {
        setLoading(true);
        setError(null);

        const response = await authenticatedFetch(
          api.vulnerability.unmatchedSoftware(page * pageSize, pageSize),
          getAccessToken
        );

        if (!response.ok) {
          throw new Error(`Failed to load unmatched software: ${response.statusText}`);
        }

        const data = await response.json();
        const items: UnmatchedSoftwareEntry[] = data.software || [];
        const serverTotal: number = data.total ?? items.length;
        setEntries(items);
        setTotal(serverTotal);
        setInitialLoaded(true);
        return serverTotal;
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          console.error("Session expired while fetching unmatched software");
        } else {
          console.error("Error fetching unmatched software:", err);
        }
        setError(err instanceof Error ? err.message : "Failed to load unmatched software");
        return null;
      } finally {
        setLoading(false);
      }
    },
    [getAccessToken, setError]
  );

  // Refetch current page; if it slides past the new last page (after deletions), step back.
  const refreshCurrentPage = useCallback(async () => {
    const serverTotal = await fetchPage(currentPage);
    if (serverTotal === null) return;
    const lastPage = Math.max(0, Math.ceil(serverTotal / pageSize) - 1);
    if (currentPage > lastPage) {
      setCurrentPage(lastPage);
    }
  }, [currentPage, fetchPage]);

  // Load on mount + whenever the page index changes
  useEffect(() => {
    fetchPage(currentPage);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentPage]);

  // Refresh when parent signals (e.g. item restored from ignored tab) — reset to page 0
  useEffect(() => {
    if (refreshTrigger > 0) {
      setSavedMappings(new Set());
      setSelectedEntries(new Map());
      if (currentPage !== 0) setCurrentPage(0);
      else fetchPage(0);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshTrigger]);

  // --- Helpers ---

  const totalPages = Math.max(1, Math.ceil(total / pageSize));

  const formatDate = (dateStr: string): string => {
    try {
      return new Date(dateStr).toLocaleDateString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
      });
    } catch {
      return dateStr;
    }
  };

  const getFrequencyBadgeClasses = (frequency: number): string => {
    if (frequency >= 100) return "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300";
    if (frequency >= 50) return "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300";
    if (frequency >= 20) return "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300";
    if (frequency >= 5) return "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-300";
    return "bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300";
  };

  const getRowKey = (entry: UnmatchedSoftwareEntry): string =>
    `${entry.softwareName}::${entry.publisher}`;

  // --- Mapping ---

  const toggleMappingRow = (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    if (expandedMappingRow === key) {
      setExpandedMappingRow(null);
    } else {
      setExpandedMappingRow(key);
      if (!cpeInputs[key]) {
        setCpeInputs((prev) => ({ ...prev, [key]: "" }));
      }
    }
  };

  const handleSaveMapping = async (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    const cpeUri = (cpeInputs[key] || "").trim();
    if (!cpeUri) return;

    try {
      setSavingMapping(key);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.softwareName,
            normalizedVendor: entry.publisher || "",
            cpeUri: cpeUri,
            displayNamePatterns: [entry.softwareName],
            publisherPatterns: entry.publisher ? [entry.publisher] : [],
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to save mapping: ${response.statusText}`);
      }

      setSavedMappings((prev) => new Set(prev).add(key));
      setExpandedMappingRow(null);
      onMappingChanged();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving CPE mapping");
      } else {
        console.error("Error saving CPE mapping:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save CPE mapping");
    } finally {
      setSavingMapping(null);
    }
  };

  // --- Selection ---
  // Selection is keyed by row-key but stores the full entry, so bulk actions can run
  // across pages without needing to re-fetch entries that have scrolled out of view.

  const toggleRowSelection = (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    setSelectedEntries((prev) => {
      const next = new Map(prev);
      if (next.has(key)) next.delete(key);
      else next.set(key, entry);
      return next;
    });
  };

  const selectAllOnPage = () => {
    setSelectedEntries((prev) => {
      const next = new Map(prev);
      for (const e of entries) {
        if (!savedMappings.has(getRowKey(e))) next.set(getRowKey(e), e);
      }
      return next;
    });
  };

  const deselectAll = () => setSelectedEntries(new Map());

  const allOnPageSelected = entries.length > 0 &&
    entries
      .filter((e) => !savedMappings.has(getRowKey(e)))
      .every((e) => selectedEntries.has(getRowKey(e)));

  // --- Auto-resolve ---

  const handleAutoResolveSelected = async () => {
    const selectedList = Array.from(selectedEntries.values());
    if (selectedList.length === 0) return;

    setAutoResolving(true);
    setAutoResolveResults(null);
    setAutoResolveProgress(null);

    const BATCH_SIZE = 25;
    const batches: UnmatchedSoftwareEntry[][] = [];
    for (let i = 0; i < selectedList.length; i += BATCH_SIZE) {
      batches.push(selectedList.slice(i, i + BATCH_SIZE));
    }

    const cumulativeResolved: AutoResolveResult["resolved"] = [];
    const cumulativeFailed: AutoResolveResult["failed"] = [];

    try {
      for (let i = 0; i < batches.length; i++) {
        setAutoResolveProgress({
          batchIndex: i + 1,
          totalBatches: batches.length,
          resolvedSoFar: cumulativeResolved.length,
          failedSoFar: cumulativeFailed.length,
        });

        const response = await authenticatedFetch(
          api.vulnerability.cpeAutoResolve(),
          getAccessToken,
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              items: batches[i].map((e) => ({
                softwareName: e.softwareName,
                publisher: e.publisher,
                normalizedVendor: e.normalizedVendor || e.publisher || "",
              })),
            }),
          }
        );

        if (!response.ok) {
          throw new Error(`Batch ${i + 1} failed: ${response.statusText}`);
        }

        const data: AutoResolveResult = await response.json();
        cumulativeResolved.push(...data.resolved);
        cumulativeFailed.push(...data.failed);

        // Mark resolved items so the row badge flips to "Mapped" without a refetch
        data.resolved.forEach((r) => {
          const entry = selectedList.find((e) => e.softwareName === r.softwareName);
          if (entry) setSavedMappings((prev) => new Set(prev).add(getRowKey(entry)));
        });
      }

      setAutoResolveResults({
        resolved: cumulativeResolved,
        failed: cumulativeFailed,
        totalProcessed: cumulativeResolved.length + cumulativeFailed.length,
        totalResolved: cumulativeResolved.length,
        totalFailed: cumulativeFailed.length,
      });
      setSelectedEntries(new Map());
      onMappingChanged();

      trackEvent("bulk_auto_resolve_completed", {
        totalSelected: selectedList.length.toString(),
        totalResolved: cumulativeResolved.length.toString(),
        totalFailed: cumulativeFailed.length.toString(),
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Auto-resolve failed");
    } finally {
      setAutoResolving(false);
      setAutoResolveProgress(null);
    }
  };

  // --- Ignore ---

  const handleIgnoreSoftware = async (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    try {
      setIgnoringRow(key);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: [{ softwareName: entry.softwareName, publisher: entry.publisher || "", reason: "manual" }],
        }),
      });
      if (!response.ok) throw new Error(`Failed to ignore software: ${response.statusText}`);
      setSelectedEntries((prev) => { const n = new Map(prev); n.delete(key); return n; });
      onIgnored();
      await refreshCurrentPage();
    } catch (err) {
      console.error("Error ignoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to ignore software");
    } finally {
      setIgnoringRow(null);
    }
  };

  const handleBulkIgnoreSelected = async () => {
    const selectedList = Array.from(selectedEntries.values());
    if (selectedList.length === 0) return;

    try {
      setIgnoringRow("bulk");
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: selectedList.map((e) => ({ softwareName: e.softwareName, publisher: e.publisher || "", reason: "manual" })),
        }),
      });
      if (!response.ok) throw new Error(`Failed to ignore software: ${response.statusText}`);
      setSelectedEntries(new Map());
      onIgnored();
      await refreshCurrentPage();
    } catch (err) {
      console.error("Error bulk ignoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to ignore software");
    } finally {
      setIgnoringRow(null);
    }
  };

  // --- Render ---

  // Full-page spinner only on initial load. Subsequent page fetches keep the table visible
  // to avoid flicker; controls are disabled via the `loading` state.
  return (
    <>
      {loading && !initialLoaded ? (
        <div className="flex items-center justify-center py-8">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
          <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading unmatched software...</span>
        </div>
      ) : initialLoaded && total === 0 ? (
        <div className="text-center py-8 text-gray-500 dark:text-gray-400">
          <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
          </svg>
          <p className="text-sm">No unmatched software found. All detected software has been mapped to CPE identifiers.</p>
        </div>
      ) : (
        <>
          {/* Stats bar */}
          <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
            <span className="text-gray-600 dark:text-gray-300">
              <span className="font-semibold text-amber-600 dark:text-amber-400">{total}</span> unmatched software entries
            </span>
            <button
              onClick={() => {
                setSavedMappings(new Set());
                setSelectedEntries(new Map());
                if (currentPage !== 0) setCurrentPage(0);
                else fetchPage(0);
              }}
              disabled={autoResolving || loading}
              className="ml-auto text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5 disabled:opacity-40"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
              </svg>
              Refresh
            </button>
          </div>

          {/* Selection toolbar */}
          {(selectedEntries.size > 0 || autoResolving) && (
            <div className="flex flex-wrap items-center gap-3 pb-3 text-sm">
              <span className="text-gray-600 dark:text-gray-300 font-medium">
                {selectedEntries.size} selected
              </span>
              <button
                onClick={allOnPageSelected ? deselectAll : selectAllOnPage}
                disabled={autoResolving}
                className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-40 transition-colors"
              >
                {allOnPageSelected ? "Deselect Page" : "Select Page"}
              </button>
              {selectedEntries.size > 0 && (
                <button
                  onClick={deselectAll}
                  disabled={autoResolving}
                  className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-40 transition-colors"
                >
                  Clear
                </button>
              )}
              <button
                onClick={handleBulkIgnoreSelected}
                disabled={autoResolving || ignoringRow === "bulk" || selectedEntries.size === 0}
                className="text-xs px-2.5 py-1 rounded-md border border-gray-300 dark:border-gray-600 text-gray-600 dark:text-gray-300 hover:bg-red-50 hover:text-red-600 hover:border-red-300 dark:hover:bg-red-900/20 dark:hover:text-red-400 disabled:opacity-40 transition-colors"
              >
                {ignoringRow === "bulk" ? "Ignoring..." : "Ignore Selected"}
              </button>
              <button
                onClick={handleAutoResolveSelected}
                disabled={autoResolving || selectedEntries.size === 0}
                className="ml-auto text-xs px-3 py-1.5 rounded-md font-medium bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5"
              >
                {autoResolving ? (
                  <>
                    <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                    Auto-Mapping...
                  </>
                ) : (
                  <>
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9.813 15.904L9 18.75l-.813-2.846a4.5 4.5 0 00-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 003.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 003.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 00-3.09 3.09z" />
                    </svg>
                    Auto-Map Selected
                  </>
                )}
              </button>
            </div>
          )}

          {/* Auto-resolve progress */}
          {autoResolving && autoResolveProgress && (
            <div className="mb-3 px-4 py-3 bg-blue-50 dark:bg-blue-900/20 border border-blue-200 dark:border-blue-800 rounded-md text-sm">
              <div className="flex items-center gap-2 text-blue-700 dark:text-blue-300">
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-blue-600" />
                <span>
                  Auto-resolving... Batch {autoResolveProgress.batchIndex}/{autoResolveProgress.totalBatches}
                  {autoResolveProgress.resolvedSoFar > 0 && <> &middot; Resolved: {autoResolveProgress.resolvedSoFar}</>}
                  {autoResolveProgress.failedSoFar > 0 && <> &middot; Failed: {autoResolveProgress.failedSoFar}</>}
                </span>
              </div>
            </div>
          )}

          {/* Auto-resolve results */}
          {autoResolveResults && !autoResolving && (
            <div className={`mb-3 px-4 py-3 rounded-md text-sm border ${
              autoResolveResults.totalFailed === 0
                ? "bg-green-50 dark:bg-green-900/20 border-green-200 dark:border-green-800"
                : "bg-amber-50 dark:bg-amber-900/20 border-amber-200 dark:border-amber-800"
            }`}>
              <div className="flex items-center justify-between">
                <span className={autoResolveResults.totalFailed === 0 ? "text-green-700 dark:text-green-300" : "text-amber-700 dark:text-amber-300"}>
                  Auto-resolve complete: <strong>{autoResolveResults.totalResolved}</strong> resolved,{" "}
                  <strong>{autoResolveResults.totalFailed}</strong> failed
                </span>
                <button
                  onClick={() => setAutoResolveResults(null)}
                  className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
              {autoResolveResults.failed.length > 0 && (
                <div className="mt-2 text-xs text-gray-600 dark:text-gray-400">
                  <span className="font-medium">Failed items:</span>{" "}
                  {autoResolveResults.failed.map((f) => `${f.softwareName} (${f.reason})`).join(", ")}
                </div>
              )}
            </div>
          )}

          {/* Table */}
          <div className="overflow-x-auto">
            <table className="w-full table-fixed divide-y divide-gray-200 dark:divide-gray-700">
              <colgroup>
                <col className="w-10" />
                <col />
                <col style={{ width: "18%" }} />
                <col className="w-24" />
                <col className="w-24" />
                <col className="w-20" />
                <col className="w-32" />
              </colgroup>
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-2 py-3">
                    <input
                      type="checkbox"
                      checked={allOnPageSelected && entries.length > 0}
                      onChange={allOnPageSelected ? deselectAll : selectAllOnPage}
                      disabled={autoResolving}
                      className="h-4 w-4 rounded border-gray-300 text-amber-600 focus:ring-amber-500 disabled:opacity-40"
                    />
                  </th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Software Name</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Publisher</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Frequency</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Last Seen</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Session</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {entries.map((entry) => {
                  const key = getRowKey(entry);
                  const isMapped = savedMappings.has(key);
                  const isMappingExpanded = expandedMappingRow === key;
                  const isSaving = savingMapping === key;

                  return (
                    <tr key={key} className={`group hover:bg-amber-50 dark:hover:bg-amber-900/10 transition-colors ${isMappingExpanded ? "bg-amber-50/50 dark:bg-amber-900/5" : ""}`}>
                      <td colSpan={7} className="p-0">
                        <div className="flex items-center">
                            <div className="w-10 px-2 py-3 flex items-center justify-center flex-shrink-0">
                              {!isMapped && (
                                <input
                                  type="checkbox"
                                  checked={selectedEntries.has(key)}
                                  onChange={() => toggleRowSelection(entry)}
                                  disabled={autoResolving}
                                  className="h-4 w-4 rounded border-gray-300 text-amber-600 focus:ring-amber-500 disabled:opacity-40"
                                />
                              )}
                            </div>
                            <TruncatedLabel text={entry.softwareName} className="px-3 py-3 text-sm text-gray-900 dark:text-gray-100 flex-1" />
                            <div className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 truncate" style={{ width: "18%", flexShrink: 0 }}>
                              {entry.publisher || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                            </div>
                            <div className="px-3 py-3 text-sm flex-shrink-0 w-24">
                              <span className={`inline-flex items-center whitespace-nowrap px-2 py-0.5 rounded-full text-xs font-semibold ${getFrequencyBadgeClasses(entry.frequency)}`}>
                                {entry.frequency} sess.
                              </span>
                            </div>
                            <div className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 whitespace-nowrap flex-shrink-0 w-24">
                              {formatDate(entry.lastSeenAt)}
                            </div>
                            <div className="px-3 py-3 text-sm flex-shrink-0 w-20">
                              <a
                                href={`/sessions/${entry.exampleSessionId}`}
                                className="text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 font-mono text-xs hover:underline"
                                onClick={(e) => e.stopPropagation()}
                              >
                                {entry.exampleSessionId.length > 8
                                  ? `${entry.exampleSessionId.slice(0, 8)}...`
                                  : entry.exampleSessionId}
                              </a>
                            </div>
                            <div className="px-3 py-3 text-sm flex-shrink-0 w-32 flex flex-nowrap items-center gap-1.5">
                              {isMapped ? (
                                <span className="inline-flex items-center gap-1 text-xs text-green-600 dark:text-green-400 font-medium">
                                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2}>
                                    <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                                  </svg>
                                  Mapped
                                </span>
                              ) : (
                                <>
                                  <button
                                    onClick={() => toggleMappingRow(entry)}
                                    className={`text-xs px-3 py-1 rounded-md font-medium whitespace-nowrap flex-shrink-0 transition-colors ${
                                      isMappingExpanded
                                        ? "bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-200"
                                        : "bg-amber-100 text-amber-700 hover:bg-amber-200 dark:bg-amber-900/50 dark:text-amber-400 dark:hover:bg-amber-800/50"
                                    }`}
                                  >
                                    Map
                                  </button>
                                  <a
                                    href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                    target="_blank"
                                    rel="noopener noreferrer"
                                    className="text-xs text-gray-500 hover:text-amber-600 dark:text-gray-400 dark:hover:text-amber-400 transition-colors"
                                    title="Search NVD for CPE"
                                    onClick={(e) => e.stopPropagation()}
                                  >
                                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                      <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                    </svg>
                                  </a>
                                  <button
                                    onClick={() => handleIgnoreSoftware(entry)}
                                    disabled={ignoringRow === key}
                                    className="text-xs text-gray-400 hover:text-red-500 dark:text-gray-500 dark:hover:text-red-400 transition-colors"
                                    title="Ignore — never auto-resolve"
                                  >
                                    {ignoringRow === key ? (
                                      <div className="animate-spin rounded-full h-3.5 w-3.5 border-b-2 border-gray-400" />
                                    ) : (
                                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                        <path strokeLinecap="round" strokeLinejoin="round" d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                                      </svg>
                                    )}
                                  </button>
                                </>
                              )}
                            </div>
                          </div>

                          {/* Inline mapping form */}
                          {isMappingExpanded && (
                            <div className="px-4 pb-3 pt-1 border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/80 dark:bg-amber-900/10">
                              <div className="flex items-center gap-3">
                                <label className="text-xs text-gray-500 dark:text-gray-400 whitespace-nowrap flex-shrink-0">
                                  CPE URI:
                                </label>
                                <input
                                  type="text"
                                  placeholder="cpe:2.3:a:vendor:product:*:*:*:*:*:*:*:*"
                                  value={cpeInputs[key] || ""}
                                  onChange={(e) =>
                                    setCpeInputs((prev) => ({ ...prev, [key]: e.target.value }))
                                  }
                                  onKeyDown={(e) => {
                                    if (e.key === "Enter" && (cpeInputs[key] || "").trim()) {
                                      handleSaveMapping(entry);
                                    }
                                    if (e.key === "Escape") {
                                      setExpandedMappingRow(null);
                                    }
                                  }}
                                  className="flex-1 px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                  autoFocus
                                  disabled={isSaving}
                                />
                                <button
                                  onClick={() => handleSaveMapping(entry)}
                                  disabled={isSaving || !(cpeInputs[key] || "").trim()}
                                  className="px-3 py-1.5 text-xs font-medium rounded-md bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5 flex-shrink-0"
                                >
                                  {isSaving ? (
                                    <>
                                      <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                                      Saving...
                                    </>
                                  ) : (
                                    "Save Mapping"
                                  )}
                                </button>
                                <button
                                  onClick={() => setExpandedMappingRow(null)}
                                  disabled={isSaving}
                                  className="px-2 py-1.5 text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors flex-shrink-0"
                                >
                                  Cancel
                                </button>
                                <a
                                  href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                  target="_blank"
                                  rel="noopener noreferrer"
                                  className="text-xs text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 whitespace-nowrap flex items-center gap-1 flex-shrink-0"
                                  onClick={(e) => e.stopPropagation()}
                                >
                                  Search NVD
                                  <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                    <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                  </svg>
                                </a>
                              </div>
                            </div>
                          )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Pagination — server-side; range computed from total */}
          {totalPages > 1 && (
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {total === 0 ? 0 : currentPage * pageSize + 1}&ndash;
                {Math.min((currentPage + 1) * pageSize, total)} of {total}
              </span>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setCurrentPage((p) => Math.max(0, p - 1))}
                  disabled={currentPage === 0 || loading}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <span className="text-xs text-gray-500 dark:text-gray-400">
                  {currentPage + 1} / {totalPages}
                </span>
                <button
                  onClick={() => setCurrentPage((p) => p + 1)}
                  disabled={currentPage + 1 >= totalPages || loading}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Next
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </>
  );
}
