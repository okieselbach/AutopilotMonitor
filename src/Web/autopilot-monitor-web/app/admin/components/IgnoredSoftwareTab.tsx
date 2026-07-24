"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import TruncatedLabel from "@/components/TruncatedLabel";
import { trackEvent } from "@/lib/appInsights";
import type { AutoResolveResult, IgnoredSoftwareEntry } from "./SoftwareMappingTypes";

interface IgnoredSoftwareTabProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  refreshTrigger: number;
  onRestored: () => void;
  onMappingChanged: () => void;
  onCountChanged: (count: number) => void;
}

const pageSize = 20;

export function IgnoredSoftwareTab({
  getAccessToken,
  setError,
  refreshTrigger,
  onRestored,
  onMappingChanged,
  onCountChanged,
}: IgnoredSoftwareTabProps) {
  const [ignoredEntries, setIgnoredEntries] = useState<IgnoredSoftwareEntry[]>([]);
  const [ignoredLoading, setIgnoredLoading] = useState(false);
  const [ignoredLoaded, setIgnoredLoaded] = useState(false);
  const [ignoredPage, setIgnoredPage] = useState(0);
  const [restoringRow, setRestoringRow] = useState<string | null>(null);

  // --- Fetching ---

  const fetchIgnoredSoftware = useCallback(async () => {
    try {
      setIgnoredLoading(true);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load ignored software: ${response.statusText}`);
      const data = await response.json();
      const items: IgnoredSoftwareEntry[] = data.items || [];
      setIgnoredEntries(items);
      setIgnoredLoaded(true);
      setIgnoredPage(0);
      onCountChanged(items.length);
    } catch (err) {
      console.error("Error fetching ignored software:", err);
      setError(err instanceof Error ? err.message : "Failed to load ignored software");
    } finally {
      setIgnoredLoading(false);
    }
  }, [getAccessToken, setError, onCountChanged]);

  // Lazy load on mount
  useEffect(() => {
    if (!ignoredLoaded && !ignoredLoading) {
      fetchIgnoredSoftware();
    }
  }, [ignoredLoaded, ignoredLoading, fetchIgnoredSoftware]);

  // Reset when refreshTrigger changes (e.g. unmapped tab ignored an item)
  useEffect(() => {
    if (refreshTrigger > 0) {
      setIgnoredLoaded(false);
    }
  }, [refreshTrigger]);

  // --- Handlers ---

  const handleRestoreSoftware = async (entry: IgnoredSoftwareEntry) => {
    const key = `${entry.softwareName}::${entry.publisher}`;
    try {
      setRestoringRow(key);
      setError(null);
      const response = await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
        method: "DELETE",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ softwareName: entry.softwareName, publisher: entry.publisher || "" }),
      });
      if (!response.ok) throw new Error(`Failed to restore software: ${response.statusText}`);
      setIgnoredEntries((prev) => {
        const next = prev.filter((e) => `${e.softwareName}::${e.publisher}` !== key);
        onCountChanged(next.length);
        return next;
      });
      onRestored();
    } catch (err) {
      console.error("Error restoring software:", err);
      setError(err instanceof Error ? err.message : "Failed to restore software");
    } finally {
      setRestoringRow(null);
    }
  };

  const handleCheckNvdFromIgnored = async (entry: IgnoredSoftwareEntry) => {
    const key = `${entry.softwareName}::${entry.publisher}`;
    try {
      setRestoringRow(key);
      setError(null);
      // First, call auto-resolve for this single item
      const resolveResponse = await authenticatedFetch(api.vulnerability.cpeAutoResolve(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          items: [{ softwareName: entry.softwareName, publisher: entry.publisher || "", normalizedVendor: entry.publisher || "" }],
        }),
      });
      if (!resolveResponse.ok) throw new Error(`NVD check failed: ${resolveResponse.statusText}`);
      const data: AutoResolveResult = await resolveResponse.json();

      if (data.totalResolved > 0) {
        // Resolved! Remove from ignore list and refresh
        await authenticatedFetch(api.vulnerability.ignoredSoftware(), getAccessToken, {
          method: "DELETE",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ softwareName: entry.softwareName, publisher: entry.publisher || "" }),
        });
        setIgnoredEntries((prev) => {
          const next = prev.filter((e) => `${e.softwareName}::${e.publisher}` !== key);
          onCountChanged(next.length);
          return next;
        });
        onMappingChanged();
        setError(null);
        trackEvent("ignored_nvd_check_resolved", { softwareName: entry.softwareName, cpeUri: data.resolved[0]?.cpeUri });
      } else {
        setError(`No CPE match found for "${entry.softwareName}" (${data.failed[0]?.reason || "no match"})`);
      }
    } catch (err) {
      console.error("Error checking NVD:", err);
      setError(err instanceof Error ? err.message : "NVD check failed");
    } finally {
      setRestoringRow(null);
    }
  };

  // --- Helpers ---

  const ignoredTotalPages = Math.ceil(ignoredEntries.length / pageSize);
  const paginatedIgnoredEntries = ignoredEntries.slice(ignoredPage * pageSize, (ignoredPage + 1) * pageSize);

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

  // --- Render ---

  return (
    <>
      {ignoredLoading ? (
        <div className="flex items-center justify-center py-8">
          <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
          <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading ignored software...</span>
        </div>
      ) : ignoredEntries.length === 0 ? (
        <div className="text-center py-8 text-gray-500 dark:text-gray-400">
          <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
          </svg>
          <p className="text-sm">No ignored software. Items you ignore from the Unmapped tab will appear here.</p>
        </div>
      ) : (
        <>
          {/* Stats bar */}
          <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
            <span className="text-gray-600 dark:text-gray-300">
              <span className="font-semibold text-gray-700 dark:text-gray-200">{ignoredEntries.length}</span> ignored software entries
            </span>
            <button
              onClick={fetchIgnoredSoftware}
              className="ml-auto text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
              </svg>
              Refresh
            </button>
          </div>

          {/* Table */}
          <div>
            <table className="w-full table-fixed divide-y divide-gray-200 dark:divide-gray-700">
              <colgroup>
                <col />
                <col style={{ width: "20%" }} />
                <col className="w-24" />
                <col style={{ width: "210px" }} />
              </colgroup>
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Software Name</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Publisher</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Ignored At</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {paginatedIgnoredEntries.map((entry) => {
                  const key = `${entry.softwareName}::${entry.publisher}`;
                  const isRestoring = restoringRow === key;

                  return (
                    <tr key={key} className="hover:bg-gray-50 dark:hover:bg-gray-700/30 transition-colors">
                      <td className="px-3 py-3">
                        <TruncatedLabel text={entry.softwareName} className="block text-sm text-gray-900 dark:text-gray-100" />
                      </td>
                      <td className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 truncate">
                        {entry.publisher || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                      </td>
                      <td className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 whitespace-nowrap">
                        {formatDate(entry.ignoredAt)}
                      </td>
                      <td className="px-3 py-3 text-sm">
                        <div className="flex items-center gap-2 whitespace-nowrap">
                          <button
                            onClick={() => handleRestoreSoftware(entry)}
                            disabled={isRestoring}
                            className="text-xs px-2.5 py-1 rounded-md font-medium bg-gray-100 text-gray-600 hover:bg-amber-100 hover:text-amber-700 dark:bg-gray-700 dark:text-gray-400 dark:hover:bg-amber-900/50 dark:hover:text-amber-400 disabled:opacity-40 transition-colors"
                          >
                            {isRestoring ? "..." : "Restore"}
                          </button>
                          <button
                            onClick={() => handleCheckNvdFromIgnored(entry)}
                            disabled={isRestoring}
                            className="text-xs px-2.5 py-1 rounded-md font-medium bg-blue-50 text-blue-600 hover:bg-blue-100 dark:bg-blue-900/20 dark:text-blue-400 dark:hover:bg-blue-900/40 disabled:opacity-40 transition-colors"
                          >
                            {isRestoring ? "..." : "Check NVD"}
                          </button>
                          <a
                            href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="text-xs text-gray-500 hover:text-amber-600 dark:text-gray-400 dark:hover:text-amber-400 transition-colors"
                            title="Search NVD manually"
                          >
                            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                            </svg>
                          </a>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Ignored Pagination */}
          {ignoredTotalPages > 1 && (
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {ignoredPage * pageSize + 1}&ndash;{Math.min((ignoredPage + 1) * pageSize, ignoredEntries.length)} of {ignoredEntries.length}
              </span>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setIgnoredPage((p) => p - 1)}
                  disabled={ignoredPage === 0}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <span className="text-xs text-gray-500 dark:text-gray-400">
                  {ignoredPage + 1} / {ignoredTotalPages}
                </span>
                <button
                  onClick={() => setIgnoredPage((p) => p + 1)}
                  disabled={(ignoredPage + 1) * pageSize >= ignoredEntries.length}
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
