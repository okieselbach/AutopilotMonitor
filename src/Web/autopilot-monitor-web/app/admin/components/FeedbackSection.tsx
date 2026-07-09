"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface FeedbackEntry {
  type: "InApp" | "Offboarding" | string; // server returns these two; future-proof for additional kinds
  upn: string;
  tenantId: string;
  displayName: string;
  rating: number | null;
  comment: string | null;
  dismissed: boolean;
  submitted: boolean;
  interactedAt: string | null;
  // Offboarding-only — null for InApp entries.
  historyRowKey: string | null;
  domainName: string | null;
}

type FeedbackTab = "InApp" | "Offboarding";
type InAppFilter = "all" | "submitted" | "dismissed";

interface FeedbackSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

export function FeedbackSection({ getAccessToken, setError }: FeedbackSectionProps) {
  const [loading, setLoading] = useState(false);
  const [entries, setEntries] = useState<FeedbackEntry[]>([]);
  const [activeTab, setActiveTab] = useState<FeedbackTab>("InApp");
  const [inAppFilter, setInAppFilter] = useState<InAppFilter>("all");
  const [currentPage, setCurrentPage] = useState(0);
  const entriesPerPage = 5;

  const fetchFeedback = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const response = await authenticatedFetch(api.feedback.all(), getAccessToken);

      if (!response.ok) {
        throw new Error(`Failed to load feedback: ${response.statusText}`);
      }

      const data = await response.json();
      // Sort by interactedAt descending (newest first)
      const sorted = (data.feedback || []).sort((a: FeedbackEntry, b: FeedbackEntry) => {
        const dateA = a.interactedAt ? new Date(a.interactedAt).getTime() : 0;
        const dateB = b.interactedAt ? new Date(b.interactedAt).getTime() : 0;
        return dateB - dateA;
      });
      setEntries(sorted);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching feedback");
      } else {
        console.error("Error fetching feedback:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load feedback");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    fetchFeedback();
  }, [fetchFeedback]);

  // Partition by tab. Server returns mixed; client splits + filters per active tab so the
  // two lists never share render code (different shapes: In-App has stars, Offboarding has
  // a domain + history-row reference instead).
  const inAppEntries = entries.filter(e => e.type === "InApp");
  const offboardingEntries = entries.filter(e => e.type === "Offboarding");

  // Stats — In-App only (Offboarding tab has its own count below).
  const submittedEntries = inAppEntries.filter(e => e.submitted);
  const dismissedEntries = inAppEntries.filter(e => e.dismissed && !e.submitted);
  const avgRating = submittedEntries.length > 0
    ? (submittedEntries.reduce((sum, e) => sum + (e.rating || 0), 0) / submittedEntries.length).toFixed(1)
    : "—";

  // In-App tab honours the Submitted/Dismissed filter (click the stat to narrow the list).
  const filteredInAppEntries = inAppFilter === "submitted"
    ? submittedEntries
    : inAppFilter === "dismissed"
      ? dismissedEntries
      : inAppEntries;

  // Pagination over the currently-active tab.
  const activeEntries = activeTab === "InApp" ? filteredInAppEntries : offboardingEntries;
  const totalPages = Math.ceil(activeEntries.length / entriesPerPage);
  const paginatedEntries = activeEntries.slice(
    currentPage * entriesPerPage,
    (currentPage + 1) * entriesPerPage
  );

  const switchTab = (tab: FeedbackTab) => {
    setActiveTab(tab);
    setInAppFilter("all");
    setCurrentPage(0);
  };

  // Click a stat to filter; click the active one again to clear back to "all".
  const toggleInAppFilter = (filter: Exclude<InAppFilter, "all">) => {
    setInAppFilter(prev => (prev === filter ? "all" : filter));
    setCurrentPage(0);
  };

  const formatTimeAgo = (dateStr: string | null): string => {
    if (!dateStr) return "unknown";
    const diff = Date.now() - new Date(dateStr).getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    const weeks = Math.floor(days / 7);
    return `${weeks}w ago`;
  };

  const renderStars = (rating: number | null) => {
    if (rating == null) return null;
    return (
      <span className="inline-flex gap-0.5">
        {[1, 2, 3, 4, 5].map(s => (
          <svg
            key={s}
            className={`w-4 h-4 ${s <= rating ? "text-yellow-400" : "text-gray-300 dark:text-gray-600"}`}
            fill="currentColor"
            viewBox="0 0 20 20"
          >
            <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
          </svg>
        ))}
      </span>
    );
  };

  return (
    <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden border border-purple-200 dark:border-purple-800">
      {/* Header */}
      <div className="w-full px-6 py-4 flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 bg-purple-100 dark:bg-purple-900 rounded-lg flex items-center justify-center">
            <svg className="w-4 h-4 text-purple-600 dark:text-purple-400" fill="currentColor" viewBox="0 0 20 20">
              <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
            </svg>
          </div>
          <div className="text-left">
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">User Feedback</h2>
            <p className="text-sm text-gray-500 dark:text-gray-400">In-app feedback from tenant admins and operators</p>
          </div>
        </div>
      </div>

      {/* Tabs */}
      <div className="px-6 border-t border-gray-200 dark:border-gray-700 flex gap-2 pt-3">
        <button
          onClick={() => switchTab("InApp")}
          className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
            activeTab === "InApp"
              ? "bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-200"
              : "text-gray-600 hover:text-gray-900 dark:text-gray-400 dark:hover:text-gray-200"
          }`}
        >
          In-App Feedback ({inAppEntries.length})
        </button>
        <button
          onClick={() => switchTab("Offboarding")}
          className={`px-3 py-1.5 text-sm font-medium rounded-md transition-colors ${
            activeTab === "Offboarding"
              ? "bg-purple-100 text-purple-700 dark:bg-purple-900 dark:text-purple-200"
              : "text-gray-600 hover:text-gray-900 dark:text-gray-400 dark:hover:text-gray-200"
          }`}
        >
          Offboarding Feedback ({offboardingEntries.length})
        </button>
      </div>

      {/* Content */}
      <div className="px-6 pb-6 border-t border-gray-200 dark:border-gray-700">
        {loading ? (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-purple-600" />
          </div>
        ) : activeEntries.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            {activeTab === "InApp" ? "No in-app feedback received yet" : "No offboarding feedback received yet"}
          </div>
        ) : (
          <>
            {/* Stats — only meaningful for In-App; Offboarding tab gets a simpler summary */}
            <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
              {activeTab === "InApp" ? (
                <>
                  <button
                    onClick={() => toggleInAppFilter("submitted")}
                    aria-pressed={inAppFilter === "submitted"}
                    title={inAppFilter === "submitted" ? "Show all" : "Show only submitted"}
                    className={`text-gray-600 dark:text-gray-300 px-2 py-0.5 rounded-md transition-colors ${
                      inAppFilter === "submitted"
                        ? "bg-purple-100 dark:bg-purple-900 ring-1 ring-purple-300 dark:ring-purple-700"
                        : "hover:bg-gray-100 dark:hover:bg-gray-700"
                    }`}
                  >
                    <span className="font-semibold text-purple-600 dark:text-purple-400">{submittedEntries.length}</span> Submitted
                  </button>
                  <span className="text-gray-400">|</span>
                  <button
                    onClick={() => toggleInAppFilter("dismissed")}
                    aria-pressed={inAppFilter === "dismissed"}
                    title={inAppFilter === "dismissed" ? "Show all" : "Show only dismissed"}
                    className={`text-gray-600 dark:text-gray-300 px-2 py-0.5 rounded-md transition-colors ${
                      inAppFilter === "dismissed"
                        ? "bg-gray-200 dark:bg-gray-700 ring-1 ring-gray-300 dark:ring-gray-600"
                        : "hover:bg-gray-100 dark:hover:bg-gray-700"
                    }`}
                  >
                    <span className="font-semibold text-gray-500">{dismissedEntries.length}</span> Dismissed
                  </button>
                  <span className="text-gray-400">|</span>
                  <span className="text-gray-600 dark:text-gray-300">
                    Avg <span className="font-semibold text-yellow-500">{avgRating}</span>
                  </span>
                  {inAppFilter !== "all" && (
                    <button
                      onClick={() => setInAppFilter("all")}
                      className="text-xs text-purple-600 hover:text-purple-700 dark:text-purple-400 dark:hover:text-purple-300 underline"
                    >
                      Clear filter
                    </button>
                  )}
                </>
              ) : (
                <span className="text-gray-600 dark:text-gray-300">
                  <span className="font-semibold text-purple-600 dark:text-purple-400">{offboardingEntries.length}</span> Departing tenants left a comment
                </span>
              )}
              <button
                onClick={fetchFeedback}
                className="ml-auto text-sm text-purple-600 hover:text-purple-700 dark:text-purple-400 dark:hover:text-purple-300"
              >
                Refresh
              </button>
            </div>

            {/* Entries — distinct render path per tab so the Offboarding shape (domain + comment-only)
                doesn't get distorted into the In-App star UI. */}
            {activeTab === "InApp" ? (
              <div className="space-y-2">
                {paginatedEntries.map((entry) => (
                  <div
                    key={entry.upn}
                    className={`border rounded-lg p-3 transition-all ${
                      entry.dismissed && !entry.submitted
                        ? "bg-gray-50 dark:bg-gray-750 border-gray-200 dark:border-gray-700 opacity-60"
                        : "bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700"
                    }`}
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate max-w-[200px]">
                          {entry.upn}
                        </span>
                        {entry.submitted ? renderStars(entry.rating) : (
                          <span className="text-xs text-gray-400 dark:text-gray-500 italic">dismissed</span>
                        )}
                      </div>
                      <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
                        <span>{formatTimeAgo(entry.interactedAt)}</span>
                        <span className="hidden sm:inline">·</span>
                        <span className="hidden sm:inline truncate max-w-[120px]">
                          {entry.tenantId.substring(0, 8)}...
                        </span>
                      </div>
                    </div>
                    {entry.comment && (
                      <p className="mt-1 text-sm text-gray-600 dark:text-gray-300 italic">
                        &quot;{entry.comment}&quot;
                      </p>
                    )}
                  </div>
                ))}
              </div>
            ) : (
              <div className="space-y-2">
                {paginatedEntries.map((entry) => (
                  <div
                    key={entry.historyRowKey ?? `${entry.tenantId}-${entry.interactedAt}`}
                    className="border border-red-200 dark:border-red-800 rounded-lg p-3 bg-red-50/30 dark:bg-red-950/20"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                      <div className="flex items-center gap-2 min-w-0">
                        <span className="text-xs font-medium px-2 py-0.5 rounded bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-200 flex-shrink-0">
                          Offboarded
                        </span>
                        <span className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate">
                          {entry.domainName || entry.tenantId.substring(0, 8) + "…"}
                        </span>
                      </div>
                      <div className="flex items-center gap-2 text-xs text-gray-500 dark:text-gray-400">
                        <span>{formatTimeAgo(entry.interactedAt)}</span>
                        <span className="hidden sm:inline">·</span>
                        <span className="hidden sm:inline truncate max-w-[200px]">
                          {entry.upn}
                        </span>
                      </div>
                    </div>
                    {entry.comment && (
                      <p className="mt-2 text-sm text-gray-700 dark:text-gray-300 whitespace-pre-wrap">
                        &quot;{entry.comment}&quot;
                      </p>
                    )}
                  </div>
                ))}
              </div>
            )}

            {/* Pagination */}
            {totalPages > 1 && (
              <div className="flex items-center justify-between pt-4 border-t border-gray-200 dark:border-gray-700 mt-4">
                <button
                  onClick={() => setCurrentPage(p => Math.max(0, p - 1))}
                  disabled={currentPage === 0}
                  className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-650 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <span className="text-sm text-gray-600 dark:text-gray-400">
                  Page {currentPage + 1} of {totalPages}
                </span>
                <button
                  onClick={() => setCurrentPage(p => Math.min(totalPages - 1, p + 1))}
                  disabled={currentPage >= totalPages - 1}
                  className="px-4 py-2 text-sm font-medium text-gray-700 dark:text-gray-300 bg-white dark:bg-gray-700 border border-gray-300 dark:border-gray-600 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-650 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Next
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
