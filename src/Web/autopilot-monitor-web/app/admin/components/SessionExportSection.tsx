"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { TenantConfiguration } from "./TenantManagementSection";
import { TenantSearchSelect } from "./TenantSearchSelect";
import { SessionExportEvent, generateCsvExport, generateUiExport } from "@/utils/sessionExportUtils";
import { trackEvent } from "@/lib/appInsights";
import { SectionCardHeader } from "@/components/SectionCardHeader";

function downloadFile(content: string, filename: string, mimeType: string) {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

interface SessionExportSectionProps {
  tenants: TenantConfiguration[];
  getAccessToken: () => Promise<string | null>;
}

export function SessionExportSection({
  tenants,
  getAccessToken,
}: SessionExportSectionProps) {
  const [exportSessionId, setExportSessionId] = useState("");
  const [exportTenantId, setExportTenantId] = useState("");
  const [exportLoading, setExportLoading] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const [exportedEvents, setExportedEvents] = useState<SessionExportEvent[] | null>(null);

  const cleanId = (v: string) => v.trim().replace(/^["'\s]+|["'\s]+$/g, "");

  const handleFetchExportEvents = async () => {
    const sid = cleanId(exportSessionId);
    const tid = cleanId(exportTenantId);
    if (!sid || !tid) {
      setExportError("Session ID and Tenant ID are required.");
      return;
    }
    try {
      setExportLoading(true);
      setExportError(null);
      setExportedEvents(null);
      const res = await authenticatedFetch(
        api.sessions.events(sid, tid),
        getAccessToken
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
      const data = await res.json();
      if (!data.success) throw new Error(data.message || "Backend returned error");
      setExportedEvents(data.events ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching export events");
      }
      setExportError(err instanceof Error ? err.message : "Failed to fetch events");
    } finally {
      setExportLoading(false);
    }
  };

  return (
    <div className="bg-gradient-to-br from-teal-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-teal-300 dark:border-teal-700 rounded-lg shadow-lg">
      <SectionCardHeader
        tone="adminTeal"
        iconPath="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"
        title="Session Event Export"
        subtitle="Fetch and export all events directly from storage — use to analyze timeline phase grouping and ordering"
      />
      <div className="p-6">
        <div className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Session ID</label>
              <input
                type="text"
                placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                value={exportSessionId}
                onChange={e => setExportSessionId(e.target.value)}
                className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Tenant ID</label>
              <TenantSearchSelect
                tenants={tenants}
                value={exportTenantId}
                onChange={setExportTenantId}
                focusRingClass="focus:ring-teal-500 focus:border-teal-500"
              />
            </div>
          </div>

          <div className="flex justify-end">
            <button
              onClick={handleFetchExportEvents}
              disabled={exportLoading}
              className="px-5 py-2.5 bg-gradient-to-r from-teal-600 to-cyan-600 text-white rounded-lg hover:from-teal-700 hover:to-cyan-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2 text-sm font-medium"
            >
              {exportLoading ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  <span>Fetching...</span>
                </>
              ) : (
                <>
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                  </svg>
                  <span>Fetch Events</span>
                </>
              )}
            </button>
          </div>

          {exportError && (
            <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-700 rounded-lg p-3 flex items-center space-x-2">
              <svg className="w-4 h-4 text-red-600 dark:text-red-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span className="text-sm text-red-800 dark:text-red-300">{exportError}</span>
            </div>
          )}

          {exportedEvents !== null && (
            <div className="space-y-3">
              <div className="flex items-center space-x-2 text-sm text-teal-800 dark:text-teal-200 bg-teal-50 dark:bg-teal-900/20 border border-teal-200 dark:border-teal-700 rounded-lg p-3">
                <svg className="w-4 h-4 text-teal-600 dark:text-teal-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span>
                  <strong>{exportedEvents.length}</strong> events loaded
                  {" \u00B7 "}
                  {exportedEvents.some(e => e.phase === 2) ? "V1" : "V2"}
                  {" \u00B7 "}
                  session {cleanId(exportSessionId).slice(0, 8)}
                </span>
              </div>
              <div className="flex flex-col sm:flex-row gap-3">
                <button
                  onClick={() => {
                    const sid = cleanId(exportSessionId);
                    const tid = cleanId(exportTenantId);
                    downloadFile(
                      generateUiExport(exportedEvents, sid, tid),
                      `session-${sid.slice(0, 8)}-timeline.txt`,
                      "text/plain;charset=utf-8"
                    );
                    trackEvent("session_exported", { format: "txt" });
                  }}
                  className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  <span>Download Timeline Export (.txt)</span>
                </button>
                <button
                  onClick={() => {
                    const sid = cleanId(exportSessionId);
                    downloadFile(
                      generateCsvExport(exportedEvents),
                      `session-${sid.slice(0, 8)}-events.csv`,
                      "text/csv;charset=utf-8"
                    );
                    trackEvent("session_exported", { format: "csv" });
                  }}
                  className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  <span>Download Raw CSV Export (.csv)</span>
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
