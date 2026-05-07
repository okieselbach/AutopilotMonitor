"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation } from "@/lib/paginationLink";
import { isGuid } from "@/utils/inputValidation";
import { trackEvent } from "@/lib/appInsights";

const PAGE_SIZE = 20;

interface SessionReport {
  reportId: string;
  tenantId: string;
  sessionId: string;
  comment: string;
  email: string;
  blobName: string;
  submittedBy: string;
  submittedAt: string;
  adminNote?: string;
}

interface SessionReportsSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
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

export function SessionReportsSection({
  getAccessToken,
  setError,
}: SessionReportsSectionProps) {
  const [reports, setReports] = useState<SessionReport[]>([]);
  const [loading, setLoading] = useState(false);
  const [selectedReport, setSelectedReport] = useState<SessionReport | null>(null);
  const [downloadingBlob, setDownloadingBlob] = useState<string | null>(null);
  const [adminNoteValue, setAdminNoteValue] = useState("");
  const [savingNote, setSavingNote] = useState(false);
  const [noteSaveResult, setNoteSaveResult] = useState<"saved" | string | null>(null);

  // Pattern B1 click-next replace state — backend pagination
  const [tenantFilterInput, setTenantFilterInput] = useState("");
  const [tenantFilterApplied, setTenantFilterApplied] = useState<string | undefined>(undefined);
  const [continuation, setContinuation] = useState<string | null>(null);
  const [nextLink, setNextLink] = useState<string | null>(null);
  const [continuationStack, setContinuationStack] = useState<Array<string | null>>([]);
  const [pageNumber, setPageNumber] = useState(1);

  useEffect(() => {
    if (selectedReport) {
      setAdminNoteValue(selectedReport.adminNote ?? "");
      setNoteSaveResult(null);
    }
  }, [selectedReport]);

  const handleDownload = async (blobName: string) => {
    try {
      setDownloadingBlob(blobName);

      const res = await authenticatedFetch(
        api.reports.downloadUrl(blobName),
        getAccessToken
      );
      if (!res.ok) throw new Error(`Failed to get download URL: ${res.statusText}`);
      const data = await res.json();
      if (!data.downloadUrl) throw new Error("No download URL returned");

      trackEvent("session_report_downloaded");
      const a = document.createElement("a");
      a.href = data.downloadUrl;
      a.download = blobName;
      a.click();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while downloading report");
      }
      setError(err instanceof Error ? err.message : "Failed to download report");
    } finally {
      setDownloadingBlob(null);
    }
  };

  const handleSaveAdminNote = async () => {
    if (!selectedReport) return;
    try {
      setSavingNote(true);
      setNoteSaveResult(null);

      const res = await authenticatedFetch(
        api.reports.note(selectedReport.reportId),
        getAccessToken,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ adminNote: adminNoteValue }),
        }
      );
      if (!res.ok) throw new Error(`Failed to save note: ${res.statusText}`);

      // Update local state
      const updated = { ...selectedReport, adminNote: adminNoteValue };
      setSelectedReport(updated);
      setReports(prev => prev.map(r => r.reportId === selectedReport.reportId ? updated : r));
      setNoteSaveResult("saved");
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving admin note");
      }
      setNoteSaveResult(err instanceof Error ? err.message : "Failed to save note");
    } finally {
      setSavingNote(false);
    }
  };

  const fetchReports = useCallback(async (cursor: string | null, filterTenantId: string | undefined) => {
    try {
      setLoading(true);

      const res = await authenticatedFetch(
        api.reports.list({
          tenantId: filterTenantId,
          pageSize: PAGE_SIZE,
          continuation: cursor ?? undefined,
        }),
        getAccessToken,
      );

      if (res.status === 404) {
        // Table/container doesn't exist yet — no reports submitted so far
        setReports([]);
        setNextLink(null);
        return;
      }
      if (!res.ok) throw new Error(`Failed to load reports: ${res.statusText}`);
      const data = await res.json();
      setReports(data.reports ?? []);
      setNextLink(data.nextLink ?? null);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while loading reports");
      }
      setError(err instanceof Error ? err.message : "Failed to load reports");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  // Initial load + reload whenever the applied tenant filter changes.
  // fetchReports is intentionally excluded from deps: getAccessToken's identity
  // churns on every MSAL accounts-array refresh, which happens after each
  // authenticatedFetch — leaving fetchReports in deps causes the effect to
  // re-fire after every successful page-N click, race a page-1 fetch against
  // it, and snap the user back to page 1.
  useEffect(() => {
    setContinuation(null);
    setContinuationStack([]);
    setPageNumber(1);
    fetchReports(null, tenantFilterApplied);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantFilterApplied]);

  const handleApplyTenantFilter = () => {
    const trimmed = tenantFilterInput.trim();
    if (trimmed && !isGuid(trimmed)) {
      setError("Tenant ID must be a valid GUID");
      return;
    }
    setError(null);
    setTenantFilterApplied(trimmed || undefined);
  };

  const handleClearTenantFilter = () => {
    setTenantFilterInput("");
    setTenantFilterApplied(undefined);
  };

  const handleNextPage = () => {
    const nextCont = extractContinuation(nextLink);
    if (!nextCont) return;
    setContinuationStack(stack => [...stack, continuation]);
    setContinuation(nextCont);
    setPageNumber(n => n + 1);
    fetchReports(nextCont, tenantFilterApplied);
  };

  const handlePrevPage = () => {
    if (continuationStack.length === 0) return;
    const prev = continuationStack[continuationStack.length - 1];
    setContinuationStack(stack => stack.slice(0, -1));
    setContinuation(prev ?? null);
    setPageNumber(n => Math.max(1, n - 1));
    fetchReports(prev ?? null, tenantFilterApplied);
  };

  const handleRefresh = () => fetchReports(continuation, tenantFilterApplied);

  return (
    <div className="bg-gradient-to-br from-indigo-50 to-purple-50 dark:from-gray-800 dark:to-gray-800 border-2 border-indigo-300 dark:border-indigo-700 rounded-lg shadow-lg">
      {/* Section Header */}
      <div className="p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-purple-100 dark:from-indigo-900/40 dark:to-purple-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600 dark:text-indigo-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
          </svg>
          <div>
            <div className="flex items-center gap-2">
              <h2 className="text-xl font-semibold text-indigo-900 dark:text-indigo-100">Session Reports</h2>
            </div>
            <p className="text-sm text-indigo-600 dark:text-indigo-300 mt-1">
              Sessions reported by Tenant Admins for analysis
            </p>
          </div>
        </div>
      </div>

      {/* Tenant filter + refresh */}
      <div className="px-6 pt-4 pb-2 flex items-center gap-2 flex-wrap">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Filter:</span>
        <input
          type="text"
          placeholder="Tenant ID (GUID)"
          value={tenantFilterInput}
          onChange={(e) => setTenantFilterInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") handleApplyTenantFilter();
          }}
          className="w-72 px-2 py-1 text-xs font-mono border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
        />
        <button
          onClick={handleApplyTenantFilter}
          disabled={loading}
          className="px-2.5 py-1 text-xs font-medium rounded-md border border-indigo-300 dark:border-indigo-700 text-indigo-700 dark:text-indigo-300 hover:bg-indigo-100 dark:hover:bg-indigo-900/40 disabled:opacity-40 transition-colors"
        >
          Apply
        </button>
        {tenantFilterApplied && (
          <button
            onClick={handleClearTenantFilter}
            disabled={loading}
            className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 transition-colors"
          >
            Clear
          </button>
        )}
        {tenantFilterApplied && (
          <span className="text-xs text-gray-500 dark:text-gray-400">
            scoped to <span className="font-mono">{tenantFilterApplied.slice(0, 8)}…</span>
          </span>
        )}
        <button
          onClick={handleRefresh}
          disabled={loading}
          className="ml-auto px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 transition-colors"
        >
          {loading ? "Loading..." : "Refresh"}
        </button>
      </div>

      {/* Reports Table */}
      <div className="p-6 pt-2">
        {loading ? (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-indigo-600"></div>
            <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading reports...</span>
          </div>
        ) : reports.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M20 13V6a2 2 0 00-2-2H6a2 2 0 00-2 2v7m16 0v5a2 2 0 01-2 2H6a2 2 0 01-2-2v-5m16 0h-2.586a1 1 0 00-.707.293l-2.414 2.414a1 1 0 01-.707.293h-3.172a1 1 0 01-.707-.293l-2.414-2.414A1 1 0 006.586 13H4" />
            </svg>
            <p className="text-sm">No session reports yet.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Date</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Session</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Tenant</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Submitted By</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Comment</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Admin Note</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {reports.map(r => (
                  <tr
                    key={r.reportId}
                    onClick={() => setSelectedReport(r)}
                    className="hover:bg-indigo-50 dark:hover:bg-indigo-900/20 cursor-pointer transition-colors"
                  >
                    <td className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 whitespace-nowrap">
                      {new Date(r.submittedAt).toLocaleString()}
                    </td>
                    <td className="px-4 py-3 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.sessionId.length > 8 ? `${r.sessionId.slice(0, 8)}...` : r.sessionId}
                    </td>
                    <td className="px-4 py-3 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.tenantId.length > 8 ? `${r.tenantId.slice(0, 8)}...` : r.tenantId}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300">
                      {r.submittedBy}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300 truncate max-w-xs">
                      {r.comment || <span className="text-gray-400 italic">no comment</span>}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300">
                      {r.adminNote ? (
                        <div className="flex items-center gap-1.5">
                          <svg className="w-4 h-4 text-indigo-500 dark:text-indigo-400 shrink-0" fill="currentColor" viewBox="0 0 24 24">
                            <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 000-1.41l-2.34-2.34a1 1 0 00-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" />
                          </svg>
                          <span className="truncate max-w-[140px]">{r.adminNote}</span>
                        </div>
                      ) : (
                        <span className="text-gray-300 dark:text-gray-600">—</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>

            {/* Pagination (Pattern B1 — backend-driven) */}
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md">
              <span className="text-xs text-gray-500 dark:text-gray-400">
                {reports.length} on page {pageNumber}{nextLink ? "" : " (last)"}
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

      {/* Report Detail Modal */}
      {selectedReport && (
        <div
          className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4"
          onClick={() => setSelectedReport(null)}
        >
          <div
            className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
            onClick={e => e.stopPropagation()}
          >
            <div className="p-6">
              <div className="flex items-center justify-between mb-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Report Details</h3>
                <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300">
                  {selectedReport.reportId}
                </span>
              </div>

              <dl className="space-y-3 text-sm">
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Session ID</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                    {selectedReport.sessionId}
                    <CopyButton value={selectedReport.sessionId} />
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Tenant ID</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                    {selectedReport.tenantId}
                    <CopyButton value={selectedReport.tenantId} />
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Submitted By</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                    {selectedReport.submittedBy}
                    <CopyButton value={selectedReport.submittedBy} />
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Submitted At</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{new Date(selectedReport.submittedAt).toLocaleString()}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Email</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5 flex items-center">
                    {selectedReport.email
                      ? <><span>{selectedReport.email}</span><CopyButton value={selectedReport.email} /></>
                      : <span className="text-gray-400 italic">not provided</span>}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Comment</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5 whitespace-pre-wrap">
                    {selectedReport.comment || <span className="text-gray-400 italic">no comment</span>}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Blob Name</dt>
                  <dd className="font-mono text-xs text-gray-700 dark:text-gray-300 mt-0.5 break-all bg-gray-50 dark:bg-gray-700/50 rounded p-2">
                    {selectedReport.blobName}
                  </dd>
                </div>

                {/* Admin Note */}
                <div className="pt-1 border-t border-gray-100 dark:border-gray-700">
                  <dt className="font-medium text-gray-500 dark:text-gray-400 flex items-center gap-1.5">
                    <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
                      <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 000-1.41l-2.34-2.34a1 1 0 00-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" />
                    </svg>
                    Admin Note
                  </dt>
                  <dd className="mt-1.5">
                    <textarea
                      value={adminNoteValue}
                      onChange={e => { setAdminNoteValue(e.target.value); setNoteSaveResult(null); }}
                      rows={3}
                      placeholder="Add an internal note about this report..."
                      className="w-full text-sm rounded-md border border-gray-300 dark:border-gray-600 bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 px-3 py-2 focus:outline-none focus:ring-2 focus:ring-indigo-500 resize-none placeholder:text-gray-400"
                    />
                    <div className="flex items-center justify-between mt-1.5">
                      <div className="text-xs">
                        {noteSaveResult === "saved" && (
                          <span className="text-green-600 dark:text-green-400 flex items-center gap-1">
                            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                            </svg>
                            Saved
                          </span>
                        )}
                        {noteSaveResult && noteSaveResult !== "saved" && (
                          <span className="text-red-600 dark:text-red-400">{noteSaveResult}</span>
                        )}
                      </div>
                      <button
                        onClick={handleSaveAdminNote}
                        disabled={savingNote}
                        className="flex items-center gap-1.5 px-3 py-1.5 bg-indigo-600 hover:bg-indigo-700 disabled:bg-indigo-400 text-white rounded-md transition-colors text-xs font-medium"
                      >
                        {savingNote ? (
                          <>
                            <svg className="animate-spin h-3 w-3" fill="none" viewBox="0 0 24 24">
                              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                            </svg>
                            Saving...
                          </>
                        ) : "Save Note"}
                      </button>
                    </div>
                  </dd>
                </div>
              </dl>

              <div className="mt-6 flex items-center justify-between">
                <button
                  onClick={() => handleDownload(selectedReport.blobName)}
                  disabled={downloadingBlob === selectedReport.blobName}
                  className="flex items-center gap-2 px-4 py-2 bg-indigo-600 hover:bg-indigo-700 disabled:bg-indigo-400 text-white rounded-md transition-colors text-sm font-medium"
                >
                  {downloadingBlob === selectedReport.blobName ? (
                    <>
                      <svg className="animate-spin h-4 w-4" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                      </svg>
                      Preparing...
                    </>
                  ) : (
                    <>
                      <svg className="h-4 w-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                      </svg>
                      Download ZIP
                    </>
                  )}
                </button>
                <button
                  onClick={() => setSelectedReport(null)}
                  className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors"
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
