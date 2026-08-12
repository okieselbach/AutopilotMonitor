"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { CpeMappingEntry } from "./SoftwareMappingTypes";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";

interface MappedSoftwareTabProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  refreshTrigger: number;
  onCountChanged: (count: number) => void;
}

const pageSize = 20;

export function MappedSoftwareTab({
  getAccessToken,
  setError,
  refreshTrigger,
  onCountChanged,
}: MappedSoftwareTabProps) {
  const [mappedEntries, setMappedEntries] = useState<CpeMappingEntry[]>([]);
  // true: a fetch fires on mount (TableSkeleton convention — avoids an empty-state flash)
  const [mappedLoading, setMappedLoading] = useState(true);
  const [mappedLoaded, setMappedLoaded] = useState(false);
  const [mappedPage, setMappedPage] = useState(0);
  const [searchQuery, setSearchQuery] = useState("");

  // Edit state
  const [editingRow, setEditingRow] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<{
    cpeUri: string;
    cpeVendor: string;
    cpeProduct: string;
    category: string;
    displayNamePatterns: string;
    publisherPatterns: string;
    excludePatterns: string;
  }>({ cpeUri: "", cpeVendor: "", cpeProduct: "", category: "", displayNamePatterns: "", publisherPatterns: "", excludePatterns: "" });
  const [savingEdit, setSavingEdit] = useState(false);
  const [deletingRow, setDeletingRow] = useState<string | null>(null);

  // Track previous refreshTrigger to detect changes
  const prevRefreshTrigger = useRef(refreshTrigger);

  // --- Fetching ---

  const fetchCpeMappings = useCallback(async () => {
    try {
      setMappedLoading(true);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMappings(),
        getAccessToken
      );

      if (!response.ok) {
        throw new Error(`Failed to load CPE mappings: ${response.statusText}`);
      }

      const data = await response.json();
      const mappings = data.mappings || [];
      setMappedEntries(mappings);
      setMappedLoaded(true);
      setMappedPage(0);
      onCountChanged(mappings.length);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching CPE mappings");
      } else {
        console.error("Error fetching CPE mappings:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load CPE mappings");
    } finally {
      setMappedLoading(false);
    }
  }, [getAccessToken, setError, onCountChanged]);

  // Lazy load on first render
  useEffect(() => {
    if (!mappedLoaded) {
      const run = async () => {
        await fetchCpeMappings();
      };
      void run();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [mappedLoaded]);

  // Reset loaded state when refreshTrigger changes (external signal to reload)
  useEffect(() => {
    if (refreshTrigger !== prevRefreshTrigger.current) {
      prevRefreshTrigger.current = refreshTrigger;
      setMappedLoaded(false);
    }
  }, [refreshTrigger]);

  // --- Helpers ---

  const getMappedRowKey = (entry: CpeMappingEntry): string =>
    `${entry.source}::${entry.normalizedVendor}::${entry.normalizedProduct}`;

  // Jump back to page 0 when the search changes (adjust-during-render pattern,
  // see react.dev "storing information from previous renders").
  const [prevSearchQuery, setPrevSearchQuery] = useState(searchQuery);
  if (prevSearchQuery !== searchQuery) {
    setPrevSearchQuery(searchQuery);
    setMappedPage(0);
  }

  const filteredMappedEntries = mappedEntries.filter((m) => {
    if (!searchQuery.trim()) return true;
    const q = searchQuery.toLowerCase();
    return (
      m.normalizedProduct.toLowerCase().includes(q) ||
      m.normalizedVendor.toLowerCase().includes(q) ||
      m.cpeUri.toLowerCase().includes(q) ||
      m.cpeVendor.toLowerCase().includes(q) ||
      m.cpeProduct.toLowerCase().includes(q) ||
      m.category.toLowerCase().includes(q) ||
      m.displayNamePatterns.some((p) => p.toLowerCase().includes(q)) ||
      m.publisherPatterns.some((p) => p.toLowerCase().includes(q)) ||
      (m.excludePatterns || []).some((p) => p.toLowerCase().includes(q))
    );
  });

  const mappedTotalPages = Math.ceil(filteredMappedEntries.length / pageSize);
  const paginatedMappedEntries = filteredMappedEntries.slice(
    mappedPage * pageSize,
    (mappedPage + 1) * pageSize
  );

  const getSourceBadge = (source: string) => {
    switch (source) {
      case "custom":
        return "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300";
      case "community":
        return "bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300";
      default:
        return "bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300";
    }
  };

  const startEditing = (entry: CpeMappingEntry) => {
    const key = getMappedRowKey(entry);
    setEditingRow(key);
    setEditForm({
      cpeUri: entry.cpeUri,
      cpeVendor: entry.cpeVendor,
      cpeProduct: entry.cpeProduct,
      category: entry.category,
      displayNamePatterns: entry.displayNamePatterns.join(", "),
      publisherPatterns: entry.publisherPatterns.join(", "),
      excludePatterns: (entry.excludePatterns || []).join(", "),
    });
  };

  const handleSaveEdit = async (entry: CpeMappingEntry) => {
    const cpeUri = editForm.cpeUri.trim();
    if (!cpeUri) return;

    try {
      setSavingEdit(true);
      setError(null);

      const displayNamePatterns = editForm.displayNamePatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);
      const publisherPatterns = editForm.publisherPatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);
      const excludePatterns = editForm.excludePatterns
        .split(",")
        .map((s) => s.trim())
        .filter(Boolean);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.normalizedProduct,
            normalizedVendor: entry.normalizedVendor,
            cpeUri: cpeUri,
            cpeVendor: editForm.cpeVendor.trim(),
            cpeProduct: editForm.cpeProduct.trim(),
            category: editForm.category.trim() || "custom",
            displayNamePatterns: displayNamePatterns.length > 0 ? displayNamePatterns : entry.displayNamePatterns,
            publisherPatterns: publisherPatterns.length > 0 ? publisherPatterns : entry.publisherPatterns,
            excludePatterns: excludePatterns,
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to save mapping: ${response.statusText}`);
      }

      setEditingRow(null);
      // Reload to reflect changes
      await fetchCpeMappings();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving CPE mapping edit");
      } else {
        console.error("Error saving CPE mapping edit:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save CPE mapping");
    } finally {
      setSavingEdit(false);
    }
  };

  const handleDeleteMapping = async (entry: CpeMappingEntry) => {
    if (!confirm(`Delete mapping for "${entry.normalizedProduct}"? This cannot be undone.`)) return;

    const key = getMappedRowKey(entry);
    try {
      setDeletingRow(key);
      setError(null);

      const response = await authenticatedFetch(
        api.vulnerability.cpeMapping(),
        getAccessToken,
        {
          method: "DELETE",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.normalizedProduct,
            normalizedVendor: entry.normalizedVendor,
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to delete mapping: ${response.statusText}`);
      }

      setMappedEntries((prev) => {
        const updated = prev.filter((m) => getMappedRowKey(m) !== key);
        onCountChanged(updated.length);
        return updated;
      });
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while deleting CPE mapping");
      } else {
        console.error("Error deleting CPE mapping:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to delete CPE mapping");
    } finally {
      setDeletingRow(null);
    }
  };

  // --- JSON Export ---

  const handleExportJson = () => {
    const dataToExport = filteredMappedEntries;
    const exportData = {
      version: "1.0.0",
      lastUpdated: new Date().toISOString().split("T")[0],
      mappings: dataToExport.map((m) => ({
        normalizedVendor: m.normalizedVendor,
        normalizedProduct: m.normalizedProduct,
        displayNamePatterns: m.displayNamePatterns,
        publisherPatterns: m.publisherPatterns,
        excludePatterns: m.excludePatterns || [],
        cpeVendor: m.cpeVendor,
        cpeProduct: m.cpeProduct,
        cpeUri: m.cpeUri,
        category: m.category,
      })),
    };
    const blob = new Blob([JSON.stringify(exportData, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `cpe-mapping-export-${exportData.lastUpdated}.json`;
    a.click();
    URL.revokeObjectURL(url);
    trackEvent("software_mapping_exported");
  };

  // --- Render ---

  // Skeleton only on initial load; refetches (search, refresh) keep the table visible.
  if (mappedLoading && !mappedLoaded) {
    return <TableSkeleton columns={5} rows={8} className="pt-4" />;
  }

  return (
    <>
      {/* Search bar + Export + Refresh */}
      <div className="flex flex-wrap items-center gap-3 py-4">
        <div className="relative flex-1 min-w-[200px] max-w-md">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-5.197-5.197m0 0A7.5 7.5 0 105.196 5.196a7.5 7.5 0 0010.607 10.607z" />
          </svg>
          <input
            type="text"
            placeholder="Search by product, vendor, CPE URI, category..."
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
            className="w-full pl-9 pr-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
          />
          {searchQuery && (
            <button
              onClick={() => setSearchQuery("")}
              className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
        <span className="text-sm text-gray-600 dark:text-gray-300">
          <span className="font-semibold text-amber-600 dark:text-amber-400">{filteredMappedEntries.length}</span>
          {searchQuery ? ` of ${mappedEntries.length}` : ""} mappings
        </span>
        <div className="ml-auto flex items-center gap-2">
          <button
            onClick={handleExportJson}
            disabled={filteredMappedEntries.length === 0}
            className="text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5 disabled:opacity-40 disabled:cursor-not-allowed"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M3 16.5v2.25A2.25 2.25 0 005.25 21h13.5A2.25 2.25 0 0021 18.75V16.5M16.5 12L12 16.5m0 0L7.5 12m4.5 4.5V3" />
            </svg>
            Export JSON
          </button>
          <button
            onClick={fetchCpeMappings}
            className="text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5"
          >
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
            </svg>
            Refresh
          </button>
        </div>
      </div>

      {filteredMappedEntries.length === 0 ? (
        <div className="text-center py-8 text-gray-500 dark:text-gray-400">
          <p className="text-sm">
            {searchQuery
              ? "No mappings match your search."
              : "No CPE mappings found. Seed mappings may need to be imported."}
          </p>
        </div>
      ) : (
        <>
          {/* Mapped Table */}
          <div className="overflow-x-auto">
            <table className="w-full table-fixed divide-y divide-gray-200 dark:divide-gray-700">
              <colgroup>
                <col />
                <col style={{ width: "14%" }} />
                <col style={{ width: "30%" }} />
                <col className="w-24" />
                <col className="w-20" />
                <col className="w-24" />
              </colgroup>
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Product</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Vendor</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">CPE URI</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Category</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Source</th>
                  <th className="px-3 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {paginatedMappedEntries.map((entry) => {
                  const key = getMappedRowKey(entry);
                  const isEditing = editingRow === key;

                  return (
                    <tr key={key} className="group">
                      <td colSpan={6} className="p-0">
                        <div className={`hover:bg-amber-50/50 dark:hover:bg-amber-900/5 transition-colors ${isEditing ? "bg-amber-50/50 dark:bg-amber-900/5" : ""}`}>
                          <div className="flex">
                            <div className="px-3 py-3 text-sm text-gray-900 dark:text-gray-100 flex-1 min-w-0 truncate" title={entry.normalizedProduct}>
                              {entry.normalizedProduct}
                            </div>
                            <div className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 truncate" style={{ width: "14%", flexShrink: 0 }} title={entry.normalizedVendor}>
                              {entry.normalizedVendor || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                            </div>
                            <div className="px-3 py-3 text-sm text-gray-700 dark:text-gray-300 font-mono text-xs truncate" style={{ width: "30%", flexShrink: 0 }} title={entry.cpeUri}>
                              {entry.cpeUri}
                            </div>
                            <div className="px-3 py-3 text-sm text-gray-600 dark:text-gray-400 flex-shrink-0 w-24 truncate">
                              {entry.category || <span className="italic text-gray-300 dark:text-gray-600">-</span>}
                            </div>
                            <div className="px-3 py-3 text-sm flex-shrink-0 w-20">
                              <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${getSourceBadge(entry.source)}`}>
                                {entry.source}
                              </span>
                            </div>
                            <div className="px-3 py-3 text-sm flex-shrink-0 w-24 flex items-center gap-2">
                              <button
                                onClick={() => isEditing ? setEditingRow(null) : startEditing(entry)}
                                className={`text-xs px-2.5 py-1 rounded-md font-medium transition-colors ${
                                  isEditing
                                    ? "bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-200"
                                    : "bg-gray-100 text-gray-600 hover:bg-amber-100 hover:text-amber-700 dark:bg-gray-700 dark:text-gray-400 dark:hover:bg-amber-900/50 dark:hover:text-amber-400"
                                }`}
                              >
                                {isEditing ? "Close" : "Edit"}
                              </button>
                              {entry.source === "custom" && (
                                <button
                                  onClick={() => handleDeleteMapping(entry)}
                                  disabled={deletingRow === key}
                                  className="text-xs px-2.5 py-1 rounded-md font-medium transition-colors bg-red-50 text-red-600 hover:bg-red-100 dark:bg-red-900/20 dark:text-red-400 dark:hover:bg-red-900/40 disabled:opacity-40 disabled:cursor-not-allowed"
                                >
                                  {deletingRow === key ? "..." : "Delete"}
                                </button>
                              )}
                            </div>
                          </div>

                          {/* Inline edit form */}
                          {isEditing && (
                            <div className="px-4 pb-3 pt-2 border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/80 dark:bg-amber-900/10">
                              <div className="grid grid-cols-2 gap-3 mb-3">
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE URI</label>
                                  <input
                                    type="text"
                                    value={editForm.cpeUri}
                                    onChange={(e) => setEditForm((f) => ({ ...f, cpeUri: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Category</label>
                                  <input
                                    type="text"
                                    value={editForm.category}
                                    onChange={(e) => setEditForm((f) => ({ ...f, category: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE Vendor</label>
                                  <input
                                    type="text"
                                    value={editForm.cpeVendor}
                                    onChange={(e) => setEditForm((f) => ({ ...f, cpeVendor: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">CPE Product</label>
                                  <input
                                    type="text"
                                    value={editForm.cpeProduct}
                                    onChange={(e) => setEditForm((f) => ({ ...f, cpeProduct: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Display Name Patterns (comma-separated)</label>
                                  <input
                                    type="text"
                                    value={editForm.displayNamePatterns}
                                    onChange={(e) => setEditForm((f) => ({ ...f, displayNamePatterns: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div>
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Publisher Patterns (comma-separated)</label>
                                  <input
                                    type="text"
                                    value={editForm.publisherPatterns}
                                    onChange={(e) => setEditForm((f) => ({ ...f, publisherPatterns: e.target.value }))}
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                                <div className="col-span-2">
                                  <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Exclude Patterns (comma-separated)</label>
                                  <input
                                    type="text"
                                    value={editForm.excludePatterns}
                                    onChange={(e) => setEditForm((f) => ({ ...f, excludePatterns: e.target.value }))}
                                    placeholder="e.g. edge update, webview helper"
                                    className="w-full px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                    disabled={savingEdit}
                                  />
                                </div>
                              </div>
                              <div className="flex items-center gap-3">
                                <button
                                  onClick={() => handleSaveEdit(entry)}
                                  disabled={savingEdit || !editForm.cpeUri.trim()}
                                  className="px-3 py-1.5 text-xs font-medium rounded-md bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5"
                                >
                                  {savingEdit ? (
                                    <>
                                      <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                                      Saving...
                                    </>
                                  ) : (
                                    "Save Changes"
                                  )}
                                </button>
                                <button
                                  onClick={() => setEditingRow(null)}
                                  disabled={savingEdit}
                                  className="px-2 py-1.5 text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors"
                                >
                                  Cancel
                                </button>
                                {entry.source === "seed" && (
                                  <span className="text-xs text-gray-400 dark:text-gray-500 italic ml-auto">
                                    Editing a seed entry will create a custom override
                                  </span>
                                )}
                              </div>
                            </div>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>

          {/* Mapped Pagination */}
          {mappedTotalPages > 1 && (
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {mappedPage * pageSize + 1}&ndash;{Math.min((mappedPage + 1) * pageSize, filteredMappedEntries.length)} of {filteredMappedEntries.length}
              </span>
              <div className="flex items-center gap-2">
                <button
                  onClick={() => setMappedPage((p) => p - 1)}
                  disabled={mappedPage === 0}
                  className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <span className="text-xs text-gray-500 dark:text-gray-400">
                  {mappedPage + 1} / {mappedTotalPages}
                </span>
                <button
                  onClick={() => setMappedPage((p) => p + 1)}
                  disabled={(mappedPage + 1) * pageSize >= filteredMappedEntries.length}
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
